using System.Collections.Concurrent;
using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Which admin nonce the contract's pending removal was proposed under. The contract keeps what a pending removal
    /// names and when it can run (its eta), but not the nonce it used; that is in the RemovalProposed event the proposal
    /// emitted, and it is what tells one proposal from another with the same lists (BaseRemovalOverride).
    ///
    /// The event has no indexed field, so it is found by where it can be. A removal's eta is its block's time plus the
    /// delay then in force, and the contract allows a delay of 48 hours to 30 days: the proposing block lies between
    /// eta - 30 days and eta - 48 hours, whatever the delay has been changed to since. One attempt
    /// (<see cref="SearchAsync"/>) first reads the block at eta less the delay now in force, which is the proposing block
    /// unless the delay was changed; after that it reads the window from its newest block down, a provider's range of
    /// blocks at a time, at most <see cref="PagesPerAttempt"/> reads, and keeps how far it came for the next attempt.
    /// Only blocks up to the block the layer reads at are read.
    ///
    /// Attempts are made by the member's own veto loop, once a pass. A signing request reads only what is kept here
    /// (<see cref="ProposedUnder"/>): it never makes this node read Base for the event.
    /// </summary>
    internal static class BaseRemovalProposal
    {
        /// <summary>vBTCb.sol DEFAULT_UPGRADE_DELAY and MAX_UPGRADE_DELAY: the least and the most a removal waits, in seconds.</summary>
        internal const long MinDelaySeconds = 48 * 3600, MaxDelaySeconds = 30L * 24 * 3600;
        /// <summary>Seconds read beyond either end of the window.</summary>
        internal const long MarginSeconds = 3600;
        /// <summary>Blocks read at the likely place, from the first block of that second on (blocks may share a second).</summary>
        internal const int LikelyBlocks = 16;
        /// <summary>Reads of logs one attempt makes at most.</summary>
        internal const int PagesPerAttempt = 40;
        /// <summary>The widest range of blocks asked for at once; narrowed to what the provider answers.</summary>
        internal const int MaxSpan = 2_000;
        /// <summary>Seconds before a window that was read whole without the event is read again from the start.</summary>
        internal const long ReadAgainSeconds = 3600;

        /// <summary>vBTCb.sol RemovalProposed: what a proposeRemoval was made with, and the admin nonce it used.</summary>
        [Event("RemovalProposed")]
        public class RemovalProposedEventDTO : IEventDTO
        {
            [Parameter("address[]", "validators", 1, false)]
            public List<string> Validators { get; set; } = new();

            [Parameter("address[]", "casters", 2, false)]
            public List<string> Casters { get; set; } = new();

            [Parameter("uint256", "vfxBlockHeight", 3, false)]
            public BigInteger VfxBlockHeight { get; set; }

            [Parameter("uint256", "eta", 4, false)]
            public BigInteger Eta { get; set; }

            [Parameter("uint256", "adminNonce", 5, false)]
            public BigInteger AdminNonce { get; set; }
        }

        private const string UPGRADE_DELAY_ABI = @"[
            {""inputs"":[],""name"":""upgradeDelay"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""}
        ]";

        /// <summary>How far the search for one removal's event has come. Touched by the loop only.</summary>
        private sealed class Search
        {
            public bool LikelyRead;
            /// <summary>The window's first block, and its last once that one can be read.</summary>
            public BigInteger? Low, Top;
            /// <summary>The blocks of the window read so far, both ends included.</summary>
            public BigInteger? ReadFrom, ReadTo;
            public int Span = MaxSpan, Refused = int.MaxValue;
            /// <summary>When the whole window had been read without the event (0: not so).</summary>
            public long ReadWholeAt;
            public int Attempts;
        }

        /// <summary>By contract and eta: the nonce that removal was proposed under. It never changes once found.</summary>
        private static readonly ConcurrentDictionary<string, BigInteger> Found = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Search> Searches = new(StringComparer.Ordinal);

        /// <summary>The clock a window read whole is timed by (seconds).</summary>
        internal static Func<long> Clock = () => VerifiedXCore.Utilities.TimeUtil.GetTime();

        /// <summary>Stands in for one attempt in tests of what is done with the answer: the nonce, or null for "not found this time".</summary>
        internal static Func<BaseBridgeService.PendingRemoval, Task<BigInteger?>>? AttemptForTests;

        private static string KeyOf(BigInteger eta) => (BaseBridgeService.ContractAddress ?? "").Trim().ToLowerInvariant() + ":" + eta;

        /// <summary>The nonce the removal with this eta was proposed under, when a search has found it; null while it is not known. Reads nothing from Base.</summary>
        internal static BigInteger? ProposedUnder(BigInteger eta) => Found.TryGetValue(KeyOf(eta), out var nonce) ? nonce : null;

        /// <summary>How many attempts were made for the removal with this eta (tests).</summary>
        internal static int AttemptsFor(BigInteger eta) => Searches.TryGetValue(KeyOf(eta), out var search) ? search.Attempts : 0;

        /// <summary>Test seam: forget what was found and how far every search came.</summary>
        internal static void ForgetForTests()
        {
            Found.Clear();
            Searches.Clear();
        }

        /// <summary>
        /// One attempt to find the pending removal's event, asked of the providers in their order until one could be read.
        /// Nothing is thrown; what was found, or how far the search came, is kept.
        /// </summary>
        internal static async Task SearchAsync(BaseBridgeService.PendingRemoval pending)
        {
            if (pending.Eta == 0 || !BaseBridgeService.IsBridgeConfigured) return;
            var key = KeyOf(pending.Eta);
            if (Found.ContainsKey(key)) return;
            if (Found.Count >= 64) Found.Clear();
            if (Searches.Count >= 64) Searches.Clear();
            var search = Searches.GetOrAdd(key, _ => new Search());
            if (search.ReadWholeAt != 0)
            {
                if (Math.Abs(Clock() - search.ReadWholeAt) < ReadAgainSeconds) return;
                Searches[key] = search = new Search { Attempts = search.Attempts };
            }
            search.Attempts++;

            if (AttemptForTests != null)
            {
                if (await AttemptForTests(pending) is { } told) Found[key] = told;
                return;
            }
            foreach (var provider in BaseRpc.Providers())
            {
                var (span, refused) = (search.Span, search.Refused);
                try
                {
                    if (await BaseRpc.AuthorizingAtAsync(provider, (web3, at) => AttemptAsync(web3, at, search, pending.Eta)) is { } nonce)
                        Found[key] = nonce;
                    return;                                                   // this provider was read: found, or the search moved on
                }
                catch
                {
                    // Not this provider: the next one goes on from the blocks already read, at the range that was in use
                    // before this one failed (its failures say nothing of another provider's limit).
                    (search.Span, search.Refused) = (span, refused);
                }
            }
        }

        /// <summary>
        /// One provider's part of an attempt: the nonce when it has the event, null when the blocks it read hold none
        /// (<paramref name="search"/> then says how far the window is read). Throws when the provider could not be read.
        /// </summary>
        private static async Task<BigInteger?> AttemptAsync(Web3 web3, BlockParameter at, Search search, BigInteger eta)
        {
            var contract = BaseBridgeService.ContractAddress;
            var newest = await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(at)
                ?? throw new InvalidOperationException("the provider gave no block to read up to");
            var last = newest.Number.Value;
            async Task<BigInteger> TimeOf(BigInteger number) =>
                (await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(new HexBigInteger(number)))
                    ?? throw new InvalidOperationException($"the provider gave no block {number}")).Timestamp.Value;
            var proposed = web3.Eth.GetEvent<RemovalProposedEventDTO>(contract);
            async Task<BigInteger?> EventIn(BigInteger from, BigInteger to)
            {
                var logs = await proposed.GetAllChangesAsync(proposed.CreateFilterInput(new BlockParameter(new HexBigInteger(from)), new BlockParameter(new HexBigInteger(to))));
                return logs.FirstOrDefault(l => l.Event.Eta == eta)?.Event.AdminNonce;
            }
            var pages = PagesPerAttempt;

            // The likely place: the block at eta less the delay now in force.
            if (!search.LikelyRead)
            {
                var delay = await web3.Eth.GetContract(UPGRADE_DELAY_ABI, contract).GetFunction("upgradeDelay").CallAsync<BigInteger>(at);
                var likely = await FirstBlockAtOrAfterAsync(last, eta - delay, TimeOf);
                if (likely <= last)
                {
                    pages--;
                    if (await EventIn(likely, BigInteger.Min(last, likely + LikelyBlocks - 1)) is { } there) return there;
                    search.LikelyRead = true;
                }
            }

            // The window every delay the contract allows puts the proposing block in, as blocks.
            search.Low ??= await FirstBlockAtOrAfterAsync(last, eta - MaxDelaySeconds - MarginSeconds, TimeOf);
            if (search.Top == null)
            {
                var pastIt = await FirstBlockAtOrAfterAsync(last, eta - MinDelaySeconds + MarginSeconds + 1, TimeOf);
                if (pastIt <= last) search.Top = pastIt - 1;                    // else the window's end cannot be read yet
            }
            var (low, high) = (search.Low.Value, search.Top ?? last);

            while (pages > 0 && high >= low)
            {
                BigInteger from, to;
                if (search.ReadTo == null) (to, from) = (high, BigInteger.Max(low, high - search.Span + 1));               // from the newest block down
                else if (search.ReadTo < high) (from, to) = (search.ReadTo.Value + 1, BigInteger.Min(high, search.ReadTo.Value + search.Span));   // blocks that can be read since
                else if (search.ReadFrom > low) (to, from) = (search.ReadFrom.Value - 1, BigInteger.Max(low, search.ReadFrom.Value - search.Span));
                else break;                                                   // the window is read
                pages--;

                BigInteger? found;
                try { found = await EventIn(from, to); }
                catch when (search.Span > 1)
                {
                    // A range the provider does not answer: its limit, most likely. Narrower from here on, as the burn watch does.
                    search.Refused = Math.Min(search.Refused, search.Span);
                    search.Span = Math.Max(1, search.Span / 4);
                    continue;
                }
                if (found is { } nonce) return nonce;
                search.ReadTo = search.ReadTo == null ? to : BigInteger.Max(search.ReadTo.Value, to);
                search.ReadFrom = search.ReadFrom == null ? from : BigInteger.Min(search.ReadFrom.Value, from);
                if (search.Span < MaxSpan && search.Span * 2 < search.Refused) search.Span = Math.Min(MaxSpan, search.Span * 2);
            }

            if (search.Top != null && (high < low || (search.ReadFrom <= low && search.ReadTo >= high)))
                search.ReadWholeAt = Clock();                                 // no such event in the window, as this provider has it: read again later
            return null;
        }

        /// <summary>
        /// The lowest block number in 0..<paramref name="latest"/> whose time is not before <paramref name="time"/>
        /// (latest + 1 when every block is earlier), asking for as few block times as a search by halves needs.
        /// </summary>
        internal static async Task<BigInteger> FirstBlockAtOrAfterAsync(BigInteger latest, BigInteger time, Func<BigInteger, Task<BigInteger>> timeOf)
        {
            BigInteger low = 0, high = latest + 1;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (await timeOf(middle) < time) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }
}
