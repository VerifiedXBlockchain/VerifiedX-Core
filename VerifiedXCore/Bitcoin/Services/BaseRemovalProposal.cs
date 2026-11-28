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
    /// The pending removal's event is the one that carries what the contract shows of it: its eta, VFX block height,
    /// validators and casters. An eta alone is not one proposal (a proposal, its cancellation and another proposal fit
    /// in one Base block, and then share it), so events are matched on all four, and of several that match the last one
    /// by block and place in the block is taken: one removal is pending at a time, and a later one replaced an earlier.
    /// What is found is kept under the same four, never under the eta alone.
    ///
    /// The event has no indexed field, so it is found by where it can be. A removal's eta is its block's time plus the
    /// delay then in force, and the contract allows a delay of 48 hours to 30 days: the proposing block lies between
    /// eta - 30 days and eta - 48 hours, whatever the delay has been changed to since. One attempt
    /// (<see cref="SearchAsync"/>) first reads the block at eta less the delay now in force, which is the proposing block
    /// unless the delay was changed; after that it reads the window from its newest block down, a provider's range of
    /// blocks at a time, at most <see cref="PagesPerAttempt"/> reads, and keeps how far it came for the next attempt.
    /// Only blocks up to the block the layer reads at are read. An event found at the likely place is taken once the
    /// blocks above it are read too (none, while the delay is the least the contract allows): a later one would be there.
    ///
    /// The nonce decides whether this node vetoes, so it is not taken on one provider's word. Every provider is asked,
    /// each searching for itself (what one provider says a range holds, or does not hold, is not taken for another's
    /// search), and what a provider's search comes to is that provider's sighting: the event's block, its place in the
    /// block and its nonce. The nonce is known once BaseRpc.RequiredAgreement providers, at different addresses, have
    /// the same sighting. A provider that makes an event up, or leaves the last one out, has a sighting no other
    /// shares; until two agree the nonce is not known yet (<see cref="ProposedUnder"/> is null).
    ///
    /// Attempts are made by the member's own veto loop, once a pass. A signing request reads only what is kept here
    /// (<see cref="ProposedUnder"/>): it never makes this node read Base for the event.
    /// </summary>
    internal static class BaseRemovalProposal
    {
        /// <summary>vBTCb.sol DEFAULT_UPGRADE_DELAY and MAX_UPGRADE_DELAY: the least and the most a removal waits, in seconds.</summary>
        internal const long MinDelaySeconds = 48 * 3600, MaxDelaySeconds = 30L * 24 * 3600;
        /// <summary>Seconds read below the window's oldest end. Its newest end is exact: no proposal waits less than the least delay.</summary>
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

        /// <summary>
        /// The pending removal's event as one provider has it: the block it is in, its place in the block, and the admin
        /// nonce it carries (it carries what the contract shows of the removal, or it would not be this removal's).
        /// <paramref name="At"/> is when the provider's search came to it.
        /// </summary>
        private sealed record Sighting(BigInteger Block, BigInteger LogIndex, BigInteger Nonce, long At = 0);

        /// <summary>How far one provider's search for one removal's event has come. Touched by that provider's attempts only.</summary>
        private sealed class Search
        {
            public bool LikelyRead;
            /// <summary>The event found at the likely place; the pending removal's unless a later one lies above it.</summary>
            public Sighting? Candidate;
            /// <summary>The window's first block, and its last once that one can be read.</summary>
            public BigInteger? Low, Top;
            /// <summary>The blocks of the window read so far, both ends included.</summary>
            public BigInteger? ReadFrom, ReadTo;
            public int Span = MaxSpan, Refused = int.MaxValue;
            /// <summary>When the whole window had been read without the event (0: not so).</summary>
            public long ReadWholeAt;
        }

        /// <summary>By removal (<see cref="IdentityOf"/>): the nonce it was proposed under.</summary>
        private static readonly ConcurrentDictionary<string, BigInteger> Found = new(StringComparer.Ordinal);
        /// <summary>By removal (<see cref="IdentityOf"/>) and provider: how far that provider's search for its event has come.</summary>
        private static readonly ConcurrentDictionary<string, Search> Searches = new(StringComparer.Ordinal);
        /// <summary>By removal and provider: what that provider's search came to.</summary>
        private static readonly ConcurrentDictionary<string, Sighting> Sightings = new(StringComparer.Ordinal);
        /// <summary>By removal: how many attempts asked a provider for it.</summary>
        private static readonly ConcurrentDictionary<string, int> Attempts = new(StringComparer.Ordinal);

        /// <summary>The clock a window read whole is timed by (seconds).</summary>
        internal static Func<long> Clock = () => VerifiedXCore.Utilities.TimeUtil.GetTime();

        /// <summary>Stands in for one attempt in tests of what is done with the answer: the nonce, or null for "not found this time".</summary>
        internal static Func<BaseBridgeService.PendingRemoval, Task<BigInteger?>>? AttemptForTests;

        private static IEnumerable<string> Lower(IEnumerable<string>? addresses) =>
            (addresses ?? Array.Empty<string>()).Select(a => (a ?? "").Trim().ToLowerInvariant());

        /// <summary>
        /// One pending removal as the contract shows it, as one text: the contract, its eta, its VFX block height, and its
        /// validators and casters in the contract's order. What is kept of a removal, here and in the operator's record
        /// (BaseRemovalOverride), is kept under this: two removals that differ in any of it are never taken for one.
        /// </summary>
        internal static string IdentityOf(string? contract, BaseBridgeService.PendingRemoval pending) =>
            Convert.ToHexString(VbtcbTypedData.Keccak(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
                (contract ?? "").Trim().ToLowerInvariant(), pending.Eta, pending.VfxBlockHeight?.ToString() ?? "-",
                string.Join(",", Lower(pending.Validators)), string.Join(",", Lower(pending.Casters))))));

        private static string KeyOf(BaseBridgeService.PendingRemoval pending) => IdentityOf(BaseBridgeService.ContractAddress, pending);

        /// <summary>The nonce this pending removal was proposed under, when a search has found it; null while it is not known. Reads nothing from Base.</summary>
        internal static BigInteger? ProposedUnder(BaseBridgeService.PendingRemoval pending) => Found.TryGetValue(KeyOf(pending), out var nonce) ? nonce : null;

        /// <summary>How many attempts were made for this pending removal (tests).</summary>
        internal static int AttemptsFor(BaseBridgeService.PendingRemoval pending) => Attempts.TryGetValue(KeyOf(pending), out var attempts) ? attempts : 0;

        /// <summary>Test seam: forget what was found and how far every search came.</summary>
        internal static void ForgetForTests()
        {
            Found.Clear();
            Searches.Clear();
            Sightings.Clear();
            Attempts.Clear();
        }

        /// <summary>
        /// One attempt to find the pending removal's event: every provider is asked, each going on with its own search.
        /// The nonce is kept once BaseRpc.RequiredAgreement providers have the same sighting of the event (the same
        /// block, the same place in it, the same nonce); one provider's sighting alone is kept as that and no more.
        /// A provider whose search is done is not asked again while its sighting, or its finding of no event, is younger
        /// than <see cref="ReadAgainSeconds"/>; after that it searches afresh, so a sighting no other provider came to
        /// share does not stand for good. Nothing is thrown.
        /// </summary>
        internal static async Task SearchAsync(BaseBridgeService.PendingRemoval pending)
        {
            if (pending.Eta == 0 || !BaseBridgeService.IsBridgeConfigured) return;
            var key = KeyOf(pending);
            if (Found.ContainsKey(key)) return;
            if (Found.Count >= 64) Found.Clear();
            if (Searches.Count >= 256 || Sightings.Count >= 256 || Attempts.Count >= 64)
            {
                Searches.Clear();
                Sightings.Clear();
                Attempts.Clear();
            }

            if (AttemptForTests != null)
            {
                Attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
                if (await AttemptForTests(pending) is { } told) Found[key] = told;
                return;
            }

            var now = Clock();
            var asked = new int[1];
            await Task.WhenAll(BaseRpc.DistinctProviders().Select(async provider =>
            {
                var mine = key + "|" + provider;
                if (Sightings.TryGetValue(mine, out var seen))
                {
                    if (Math.Abs(now - seen.At) < ReadAgainSeconds) return;       // it has said what it has
                    Sightings.TryRemove(mine, out _);
                    Searches.TryRemove(mine, out _);
                }
                var search = Searches.GetOrAdd(mine, _ => new Search());
                if (search.ReadWholeAt != 0)
                {
                    if (Math.Abs(now - search.ReadWholeAt) < ReadAgainSeconds) return;
                    Searches[mine] = search = new Search();
                }
                System.Threading.Interlocked.Exchange(ref asked[0], 1);
                try
                {
                    if (await BaseRpc.AuthorizingAtAsync(provider, (web3, at) => AttemptAsync(web3, at, search, pending)) is { } sighting)
                        Sightings[mine] = sighting with { At = now };
                }
                catch { }   // this provider could not be read: its search goes on from where it is at the next attempt
            }));
            if (asked[0] != 0) Attempts.AddOrUpdate(key, 1, (_, n) => n + 1);

            var agreed = Sightings.Where(s => s.Key.StartsWith(key + "|", StringComparison.Ordinal))
                .GroupBy(s => (s.Value.Block, s.Value.LogIndex, s.Value.Nonce))
                .FirstOrDefault(g => g.Count() >= BaseRpc.RequiredAgreement);
            if (agreed != null) Found[key] = agreed.Key.Nonce;
        }

        /// <summary>
        /// One provider's part of an attempt: its sighting of the pending removal's event when it has it, null when the
        /// blocks it read hold none (<paramref name="search"/> then says how far the window is read). Throws when the
        /// provider could not be read.
        /// </summary>
        private static async Task<Sighting?> AttemptAsync(Web3 web3, BlockParameter at, Search search, BaseBridgeService.PendingRemoval pending)
        {
            var eta = pending.Eta;
            var (validators, casters) = (Lower(pending.Validators).ToList(), Lower(pending.Casters).ToList());
            var contract = BaseBridgeService.ContractAddress;
            var newest = await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(at)
                ?? throw new InvalidOperationException("the provider gave no block to read up to");
            var last = newest.Number.Value;
            async Task<BigInteger> TimeOf(BigInteger number) =>
                (await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(new HexBigInteger(number)))
                    ?? throw new InvalidOperationException($"the provider gave no block {number}")).Timestamp.Value;
            var proposed = web3.Eth.GetEvent<RemovalProposedEventDTO>(contract);
            // The last event in these blocks, by block and place in it, that carries what the contract shows of the pending removal.
            async Task<Sighting?> EventIn(BigInteger from, BigInteger to)
            {
                var logs = await proposed.GetAllChangesAsync(proposed.CreateFilterInput(new BlockParameter(new HexBigInteger(from)), new BlockParameter(new HexBigInteger(to))));
                var last = logs
                    .Where(l => l.Event.Eta == eta && pending.VfxBlockHeight == l.Event.VfxBlockHeight
                        && Lower(l.Event.Validators).SequenceEqual(validators) && Lower(l.Event.Casters).SequenceEqual(casters))
                    .OrderByDescending(l => l.Log.BlockNumber.Value).ThenByDescending(l => l.Log.LogIndex.Value)
                    .FirstOrDefault();
                return last == null ? null : new Sighting(last.Log.BlockNumber.Value, last.Log.LogIndex.Value, last.Event.AdminNonce);
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
                    var end = BigInteger.Min(last, likely + LikelyBlocks - 1);
                    if (await EventIn(likely, end) is { } there)
                    {
                        // It is the pending removal's unless a later one that matches lies above: only those blocks are still read.
                        search.Candidate = there;
                        search.Low = end + 1;
                    }
                    search.LikelyRead = true;
                }
            }

            // The window every delay the contract allows puts the proposing block in, as blocks.
            search.Low ??= await FirstBlockAtOrAfterAsync(last, eta - MaxDelaySeconds - MarginSeconds, TimeOf);
            if (search.Top == null)
            {
                var pastIt = await FirstBlockAtOrAfterAsync(last, eta - MinDelaySeconds + 1, TimeOf);
                if (pastIt <= last) search.Top = pastIt - 1;                    // else the window's end cannot be read yet
            }
            var (low, high) = (search.Low.Value, search.Top ?? last);

            while (pages > 0 && high >= low)
            {
                BigInteger from, to;
                if (search.ReadTo == null) (to, from) = (high, BigInteger.Max(low, high - search.Span + 1));               // from the newest block down
                else if (search.ReadTo < high)
                {
                    // Blocks that can be read since the last attempt: in one range, so that the last event in them is the
                    // newest. More than one range of them, and the window is read again from its newest block.
                    if (high - search.ReadTo.Value > search.Span) { (search.ReadFrom, search.ReadTo) = (null, null); continue; }
                    (from, to) = (search.ReadTo.Value + 1, high);
                }
                else if (search.ReadFrom > low) (to, from) = (search.ReadFrom.Value - 1, BigInteger.Max(low, search.ReadFrom.Value - search.Span));
                else break;                                                   // the window is read
                pages--;

                Sighting? found;
                try { found = await EventIn(from, to); }
                catch when (search.Span > 1)
                {
                    // A range the provider does not answer: its limit, most likely. Narrower from here on, as the burn watch does.
                    search.Refused = Math.Min(search.Refused, search.Span);
                    search.Span = Math.Max(1, search.Span / 4);
                    continue;
                }
                if (found != null) return found;
                search.ReadTo = search.ReadTo == null ? to : BigInteger.Max(search.ReadTo.Value, to);
                search.ReadFrom = search.ReadFrom == null ? from : BigInteger.Min(search.ReadFrom.Value, from);
                if (search.Span < MaxSpan && search.Span * 2 < search.Refused) search.Span = Math.Min(MaxSpan, search.Span * 2);
            }

            var readWhole = high < low || (search.ReadFrom <= low && search.ReadTo >= high);
            if (readWhole && search.Candidate is { } candidate) return candidate;   // nothing later above it
            if (readWhole && search.Top != null)
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
