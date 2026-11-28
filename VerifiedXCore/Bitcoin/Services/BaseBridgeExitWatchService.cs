using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Polls Base for <c>VfxExitBurned</c> (V3) and <c>BTCExitBurned</c> events.
    /// For VfxExitBurned (V3 pool): feeds into <see cref="BurnExitConsensusService"/> → <see cref="BridgePoolUnlockService"/>.
    /// For BTCExitBurned: feeds into <see cref="BurnExitConsensusService"/> for caster consensus + FROST.
    /// </summary>
    public static class BaseBridgeExitWatchService
    {
        private const int MaxBlockSpan = 4000;

        // The span read at once shrinks while reads of a range fail and grows back as they succeed: a range kept for a
        // failed read and asked whole every time (a provider refusing wide spans, or a log cap filled with dust burns) held
        // up every later burn on this caster (fifth review, round 21). A single block that fails this many times in a
        // row is passed over and logged for an operator's rescan.
        private static int _span = MaxBlockSpan;
        private static int _singleBlockFailures;
        internal const int SingleBlockFailuresBeforeSkip = 20;

        /// <summary>
        /// What one provider reads at once: the span it is asked next, the widest it read without error and the narrowest
        /// it refused above that (a provider's range cap: 200 blocks on sepolia.base.org). The span settles between them:
        /// doubled into the cap and cut to a quarter on every refusal, it averaged about 95 blocks a 12-second poll, and
        /// with Base at 200 ms blocks a caster behind by an outage gained under one block a second on the chain, so every
        /// burn waited that long (10 November 2026). Each provider has its own, since each has its own cap and its own
        /// bad moments; the watch reads a range no wider than BaseRpc.RequiredAgreement of them take (<see cref="SpanFor"/>).
        /// </summary>
        internal sealed class SpanState
        {
            public int Span = MaxBlockSpan;
            private int _good;
            private int _refused = int.MaxValue;

            /// <summary>The span after a read of <paramref name="span"/> blocks succeeded: back up to the widest that worked, then halfway to the narrowest refused.</summary>
            public int AfterSuccess(int span)
            {
                if (span > _good) _good = span;
                if (span < _good) return Math.Min(span * 2, _good);
                if (_refused == int.MaxValue) return Math.Min(MaxBlockSpan, span * 2);
                return Math.Max(span, (span + _refused) / 2);
            }

            /// <summary>
            /// The span after a read of <paramref name="span"/> blocks failed. Wider than any that worked, it is the provider's
            /// cap and the span goes back to the widest that worked; otherwise (a range dense with logs, or the provider
            /// failing) it shrinks to a quarter, as before.
            /// </summary>
            public int AfterFailure(int span)
            {
                if (span > _good)
                {
                    _refused = Math.Min(_refused, span);
                    if (_good > 0) return _good;
                }
                return Math.Max(1, span / 4);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpanState> Spans = new(StringComparer.Ordinal);

        private static SpanState SpanOf(string provider) => Spans.GetOrAdd(provider, _ => new SpanState());

        /// <summary>
        /// The span a range is read at: the widest that BaseRpc.RequiredAgreement of <paramref name="providers"/> take now,
        /// which is what it takes to pass the range. One provider with a low cap or a bad moment does not narrow it for
        /// the others.
        /// </summary>
        internal static int SpanFor(IEnumerable<string> providers)
        {
            var spans = providers.Select(p => SpanOf(p).Span).OrderByDescending(n => n).ToList();
            return spans.Count >= BaseRpc.RequiredAgreement ? spans[BaseRpc.RequiredAgreement - 1] : spans.DefaultIfEmpty(MaxBlockSpan).Min();
        }

        private static bool _behind;

        /// <summary>Reads at most this many spans in one poll while the cursor is behind Base, one after another.</summary>
        internal const int CatchUpReadsPerPoll = 50;

        /// <summary>
        /// Base blocks read again after a restart, besides every burn still open (<see cref="BridgeExitSyncState.OpenBurns"/>):
        /// about two hours at Base's 200 ms blocks (round 23 sized it at 2 s blocks, which Base left in November 2026).
        /// </summary>
        internal const long RestartRewindBlocks = 36_000;

        /// <summary>The most open burns kept for a restart to read again; one past it is left to <see cref="RestartRewindBlocks"/>.</summary>
        internal const int MaxOpenBurns = 1_000;

        /// <summary>An open burn older than this (one day of Base blocks at 200 ms) is no longer read again after a restart.</summary>
        internal const long MaxOpenBurnAgeBlocks = 432_000;

        /// <summary>How often the open burns are checked against the chain while polling (a restart checks them at once).</summary>
        internal const long OpenBurnPruneSeconds = 600;
        private static long _openBurnsPrunedAt;
        private static bool _started;
        /// <summary>The cursor when this node last was not handling burns (at start, or when it stopped being a synced caster); null while it handles them.</summary>
        private static long? _cursorWhenNotHandling;
        private static long? _startBlockOverride;

        /// <summary>V3 contract event: burnForVfxExit(amount, vfxDestinationAddress)</summary>
        [Event("VfxExitBurned")]
        public class VfxExitBurnedEventDTO : IEventDTO
        {
            [Parameter("address", "burner", 1, true)]
            public string Burner { get; set; } = "";

            [Parameter("uint256", "amount", 2, false)]
            public BigInteger Amount { get; set; }

            [Parameter("string", "vfxDestinationAddress", 3, false)]
            public string VfxDestinationAddress { get; set; } = "";

            [Parameter("uint256", "chainId", 4, false)]
            public BigInteger ChainId { get; set; }
        }

        [Event("BTCExitBurned")]
        public class BTCExitBurnedEventDTO : IEventDTO
        {
            [Parameter("address", "burner", 1, true)]
            public string Burner { get; set; } = "";

            [Parameter("uint256", "amount", 2, false)]
            public BigInteger Amount { get; set; }

            [Parameter("string", "btcDestination", 3, false)]
            public string BtcDestination { get; set; } = "";

            [Parameter("uint256", "chainId", 4, false)]
            public BigInteger ChainId { get; set; }
        }

        /// <summary>Needs VBTCb proxy address and Base RPC.</summary>
        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(BaseBridgeService.VBTCbContractAddress);

        public static void LoadStartBlockOverride()
        {
            var env = Environment.GetEnvironmentVariable("BASE_BRIDGE_EXIT_FROM_BLOCK");
            if (!string.IsNullOrEmpty(env) && long.TryParse(env, out var b) && b >= 0)
                _startBlockOverride = b;
        }

        /// <summary>Background loop entrypoint.</summary>
        public static async Task BridgeExitScanLoop()
        {
            LoadStartBlockOverride();
            // Paused, not ended, while startup sync holds StopAllTimers (PausableLoop).
            await PausableLoop.RunAsync(async () =>
            {
                try
                {
                    if (IsConfigured)
                        await ReadUntilCaughtUpAsync();
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[BaseBridgeExit] Poll error: {ex.Message}", "BaseBridgeExitWatchService.BridgeExitScanLoop");
                }
            }, 12_000, "BaseBridgeExitWatchService.BridgeExitScanLoop");
        }

        /// <summary>
        /// One poll of the loop: reads span after span while the cursor is behind Base (up to <see cref="CatchUpReadsPerPoll"/>),
        /// and stops at the head or at a read that fails, which the next poll takes up again.
        /// </summary>
        internal static async Task ReadUntilCaughtUpAsync()
        {
            for (var i = 0; i < CatchUpReadsPerPoll; i++)
            {
                await PollOnceInternal();
                if (!_behind) return;
                await Task.Delay(200);
            }
        }

        /// <summary>The span the last read asked for.</summary>
        internal static int CurrentSpan => _span;

        /// <summary>Test seam: start the span over as at startup.</summary>
        internal static void ResetSpanForTests()
        {
            _span = MaxBlockSpan;
            Spans.Clear();
            _singleBlockFailures = 0;
            _behind = false;
        }

        private static readonly object CursorLock = new();
        /// <summary>The rescans asked for, each with its number in order and the cursor it set; the last few are kept.</summary>
        private static readonly List<(long Number, long Cursor)> Rescans = new();
        private static long _rescanNumber;
        /// <summary>Which rescans each poll's state was read after: the number of the last one before the read.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BridgeExitSyncState, object> ReadAfter = new();

        /// <summary>The scan state as a poll starts from it. A rescan asked for before this is in it already.</summary>
        internal static BridgeExitSyncState ReadCursorForPoll()
        {
            lock (CursorLock)
            {
                var state = BridgeExitSyncState.GetOrCreate();
                ReadAfter.AddOrUpdate(state, _rescanNumber);
                return state;
            }
        }

        /// <summary>
        /// Saves the scan state a poll holds. A rescan asked for after that poll read the state is kept: the cursor
        /// saved is no later than the one the rescan asked for, so the next poll reads from there. Each poll is judged
        /// by its own read, so another poll starting in between does not make it forget the rescan.
        /// </summary>
        internal static void SaveCursorFromPoll(BridgeExitSyncState state)
        {
            lock (CursorLock)
            {
                var readAfter = ReadAfter.TryGetValue(state, out var number) ? (long)number : _rescanNumber;
                foreach (var rescan in Rescans)
                    if (rescan.Number > readAfter)
                        state.LastScannedBlock = Math.Min(state.LastScannedBlock, rescan.Cursor);
                BridgeExitSyncState.Save(state);
            }
        }

        /// <summary>Tests only: forgets the rescans asked for.</summary>
        internal static void ForgetRescansForTests() { lock (CursorLock) Rescans.Clear(); }

        /// <summary>Reset the scan cursor to rescan from a specific block. Next poll will start from fromBlock.</summary>
        public static (bool Success, string Message) RescanFromBlock(long fromBlock)
        {
            if (fromBlock < 0)
                return (false, "Block number must be >= 0");

            long previousBlock;
            BridgeExitSyncState state;
            lock (CursorLock)
            {
                state = BridgeExitSyncState.GetOrCreate();
                previousBlock = state.LastScannedBlock;
                state.LastScannedBlock = Math.Max(0, fromBlock - 1);
                BridgeExitSyncState.Save(state);
                // A poll already under way read the cursor before this and saves where it got to when it ends: it is told.
                Rescans.Add((++_rescanNumber, state.LastScannedBlock));
                // The list stays short without forgetting what an unfinished poll still needs: the two oldest become
                // one, under the later number and the earlier cursor, so any poll that predates either is still held
                // to the earliest block asked for.
                if (Rescans.Count > 64)
                {
                    Rescans[1] = (Rescans[1].Number, Math.Min(Rescans[0].Cursor, Rescans[1].Cursor));
                    Rescans.RemoveAt(0);
                }
            }

            var msg = $"Reset exit scan cursor from {previousBlock} to {state.LastScannedBlock} (will rescan from block {fromBlock})";
            LogUtility.Log($"[BaseBridgeExit] {msg}", "BaseBridgeExitWatchService.RescanFromBlock");
            return (true, msg);
        }

        /// <summary>Single poll; for API manual trigger.</summary>
        public static async Task<(int Processed, long ScannedToBlock, string Message)> PollOnce()
        {
            if (!IsConfigured)
                return (0, 0, "VBTCb contract address not configured");
            return await PollOnceInternal();
        }

        /// <summary>Whether BaseRpc.RequiredAgreement of <paramref name="providers"/> read the logs of <paramref name="block"/>.</summary>
        private static async Task<bool> CanReadLogs(IReadOnlyList<string> providers, string? contract, long block, long latest)
        {
            if (block > latest || string.IsNullOrEmpty(contract)) return false;
            var probe = new Nethereum.RPC.Eth.DTOs.NewFilterInput
            {
                FromBlock = new BlockParameter(new HexBigInteger(block)),
                ToBlock = new BlockParameter(new HexBigInteger(block)),
                Address = new[] { contract },
            };
            var reads = await BaseRpc.AuthorizingEachAsync(providers, web3 => web3.Eth.Filters.GetLogs.SendRequestAsync(probe));
            return reads.Count(r => r.Read) >= BaseRpc.RequiredAgreement;
        }

        /// <summary>Moves the cursor back when this node has just begun handling burns; see the call in PollOnceInternal.</summary>
        internal static void ConsumeRewind(BridgeExitSyncState state, bool handling)
        {
            if (!_started)
            {
                _started = true;
                _cursorWhenNotHandling = state.LastScannedBlock;
            }
            if (!handling)
            {
                _cursorWhenNotHandling ??= state.LastScannedBlock;
                return;
            }
            if (_cursorWhenNotHandling is not { } since) return;
            _cursorWhenNotHandling = null;
            PruneOpenBurns(state, state.LastScannedBlock); // what the chain redeemed since they were saved is not read again
            // The last stretch, and back to the oldest burn this node handed on that the chain has not redeemed: its work
            // was in memory only, and the stretch alone missed it once it was older.
            var oldestOpen = state.OpenBurns.Count > 0 ? state.OpenBurns.Min(b => b.BaseBlock) - 1 : long.MaxValue;
            var back = Math.Max(0, Math.Min(Math.Min(since, state.LastScannedBlock) - RestartRewindBlocks, oldestOpen));
            if (back >= state.LastScannedBlock) return;
            LogUtility.Log($"[BaseBridgeExit] Now handling burns: reading Base again from block {back + 1} (cursor was {state.LastScannedBlock}).",
                "BaseBridgeExitWatchService.PollOnceInternal");
            state.LastScannedBlock = back;
            SaveCursorFromPoll(state);
        }

        /// <summary>Records a burn this node handed to the casters' consensus, read in Base block <paramref name="baseBlock"/>.</summary>
        internal static void NoteOpenBurn(BridgeExitSyncState state, string burnId, long baseBlock)
        {
            if (string.IsNullOrEmpty(burnId) || state.OpenBurns.Count >= MaxOpenBurns) return;
            if (state.OpenBurns.Any(b => string.Equals(b.BurnId, burnId, StringComparison.Ordinal)) || Redeemed(burnId)) return;
            state.OpenBurns.Add(new OpenBridgeBurn { BurnId = burnId, BaseBlock = baseBlock });
        }

        /// <summary>
        /// Drops the open burns the chain has redeemed (a pool unlock, or an exit that has not failed: a failed one may be
        /// exited again) and those older than <see cref="MaxOpenBurnAgeBlocks"/> behind Base block <paramref name="latest"/>.
        /// </summary>
        internal static void PruneOpenBurns(BridgeExitSyncState state, long latest)
        {
            state.OpenBurns.RemoveAll(b => latest - b.BaseBlock > MaxOpenBurnAgeBlocks || Redeemed(b.BurnId));
        }

        private static bool Redeemed(string burnId)
        {
            try { return VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(burnId); }
            catch { return false; } // kept: read again rather than lost
        }

        /// <summary>Base blocks (about ten minutes) read again once when the path-audit rules begin; see <see cref="ConsumeRulesFlip"/>.</summary>
        internal const long RulesFlipRewindBlocks = 300;
        private static bool? _positionedSeen;

        /// <summary>
        /// When this node sees the path-audit rules begin (a burn of a batched transaction is named by its position from
        /// them, <see cref="BurnIdsOf"/>), the last <see cref="RulesFlipRewindBlocks"/> Base blocks are read once more. A
        /// batch named by its bare hash just before and checked just after was refused as "multiple burn events" and
        /// failed for good; read again it is named by position, new keys its failed record does not block, and a burn the
        /// bare hash already redeemed stays redeemed (VBTCBridgeConsumedBurn.Overlaps) (#294).
        /// </summary>
        internal static void ConsumeRulesFlip(BridgeExitSyncState state)
        {
            var positioned = (Globals.LastBlock?.Height ?? -1) + 1 >= Globals.PathAuditRulesHeight; // as BurnIdsOf judges it
            var before = _positionedSeen;
            _positionedSeen = positioned;
            if (before != false || !positioned) return; // only a flip this process saw
            var back = Math.Max(0, state.LastScannedBlock - RulesFlipRewindBlocks);
            if (back >= state.LastScannedBlock) return;
            LogUtility.Log($"[BaseBridgeExit] The path-audit rules have begun: reading Base again from block {back + 1} (cursor was {state.LastScannedBlock}).",
                "BaseBridgeExitWatchService.PollOnceInternal");
            state.LastScannedBlock = back;
            SaveCursorFromPoll(state);
        }

        /// <summary>Test seam: forget the start and handling history.</summary>
        internal static void ResetRewindForTests()
        {
            _started = false;
            _cursorWhenNotHandling = null;
            _positionedSeen = null;
            _openBurnsPrunedAt = 0;
        }

        /// <summary>A burn log's place: its transaction and its index (logs of one transaction share a block).</summary>
        private static (string TxHash, BigInteger LogIndex) BurnLogKey(FilterLog log) =>
            (log.TransactionHash.ToLowerInvariant(), log.LogIndex?.Value ?? BigInteger.Zero);

        /// <summary>
        /// Each bridge burn log's id (<see cref="BaseBridgeService.BurnId"/>): its transaction's hash, or with its position
        /// among that transaction's bridge burns (both kinds, in emitted order) when it holds several. Before the path-audit
        /// rules, which a position is named under, every burn is its transaction's hash, as it was: a positioned id was
        /// refused by every caster's receipt check and both burns of the transaction failed for good (#291).
        /// </summary>
        internal static Dictionary<(string TxHash, BigInteger LogIndex), string> BurnIdsOf(IEnumerable<FilterLog> logs)
        {
            var positioned = (Globals.LastBlock?.Height ?? -1) + 1 >= Globals.PathAuditRulesHeight; // as BaseBridgeService.BurnAt judges it
            var ids = new Dictionary<(string TxHash, BigInteger LogIndex), string>();
            foreach (var tx in logs.Where(l => l != null && !string.IsNullOrEmpty(l.TransactionHash)).GroupBy(l => l.TransactionHash.ToLowerInvariant()))
            {
                var ordered = tx.GroupBy(BurnLogKey).Select(g => g.First()).OrderBy(l => l.LogIndex?.Value ?? BigInteger.Zero).ToList();
                for (var i = 0; i < ordered.Count; i++)
                    ids[BurnLogKey(ordered[i])] = BaseBridgeService.BurnId(ordered[i].TransactionHash, i, positioned ? ordered.Count : 1);
            }
            return ids;
        }

        private static async Task<(int Processed, long ScannedToBlock, string Message)> PollOnceInternal()
        {
            // The burns read here start an exit: authorizing reads. No single provider decides what the watch reads.
            // Every configured provider that is on Base is asked for its finalized block (the newest one Base will not
            // replace), and the watch reads to the newest block that BaseRpc.RequiredAgreement of them hold final: a burn
            // in a block after it is read once that block is final, so no exit starts from a burn Base can still drop.
            // With fewer providers to read from, nothing is read: that is the providers' failing, not a block's.
            var providers = await BaseRpc.AuthorizingWatchProvidersAsync();
            if (providers.Count < BaseRpc.RequiredAgreement)
                throw new BaseProviderException($"Base cannot be read: {providers.Count} of the configured providers can be read from, and {BaseRpc.RequiredAgreement} are needed");
            var latest = providers.Select(p => p.Final.Number).OrderByDescending(n => n).ElementAt(BaseRpc.RequiredAgreement - 1);
            _behind = false;

            var state = ReadCursorForPoll();

            if (state.LastScannedBlock == 0)
            {
                if (_startBlockOverride.HasValue)
                {
                    state.LastScannedBlock = Math.Max(0, _startBlockOverride.Value - 1);
                    SaveCursorFromPoll(state);
                    LogUtility.Log(
                        $"[BaseBridgeExit] BASE_BRIDGE_EXIT_FROM_BLOCK={_startBlockOverride}; scanning from block {state.LastScannedBlock + 1}",
                        "BaseBridgeExitWatchService.PollOnceInternal");
                }
                else
                {
                    state.LastScannedBlock = Math.Max(0, latest - 1);
                    SaveCursorFromPoll(state);
                    LogUtility.Log($"[BaseBridgeExit] Initialized scan cursor at block {state.LastScannedBlock} (only new burns after this are processed).",
                        "BaseBridgeExitWatchService.PollOnceInternal");
                    _started = true;
                    return (0, latest, "Initialized cursor; no backlog scan on first run");
                }
            }

            // The burns a caster had in hand when it stopped (detected, in consensus, sent but not mined) were only in its
            // memory, and the saved cursor is past them: once the committee had restarted, nothing took them up again
            // (fifth review, round 23). The last stretch is read again; burns the chain has redeemed are passed over.
            // Only once this node handles burns (a synced caster): the watcher starts before the node knows it is a
            // caster, and a first poll read the whole stretch then, handed nothing on and moved past it (round 24). The
            // stretch is counted from where the cursor stood when the node last did not handle burns, so burns passed
            // over while it was not a caster (at start, or after losing its place) are read again too.
            // Each open burn costs a look-up of the exits: pruned every few minutes, not on every range read.
            var pruneNow = TimeUtil.GetTime();
            if (pruneNow - _openBurnsPrunedAt >= OpenBurnPruneSeconds)
            {
                PruneOpenBurns(state, latest);
                _openBurnsPrunedAt = pruneNow;
            }
            ConsumeRewind(state, Globals.IsBlockCaster && Globals.IsChainSynced);
            ConsumeRulesFlip(state);

            var from = state.LastScannedBlock + 1;
            if (from > latest)
                return (0, state.LastScannedBlock, "No new blocks");

            // Every provider final up to the range's end is asked for it (there are BaseRpc.RequiredAgreement at least).
            var span = _span = SpanFor(providers.Where(p => p.Final.Number >= from).Select(p => p.Provider));
            var to = Math.Min(from + span - 1, latest);
            var asked = providers.Where(p => p.Final.Number >= to).Select(p => p.Provider).ToList();
            var contract = BaseBridgeService.VBTCbContractAddress;

            var processed = 0;
            var scanFailed = false;
            // Whether the range could not be passed because of the providers, not because of the range: too few of them
            // were in a position to read it at all (not on Base, or what they served is not Base's). Such a poll says
            // nothing about the block, so it never counts toward passing a block over.
            var providerAtFault = false;
            // Whether a burn that was read could not be handled (this node's failing): the range is narrowed, as before.
            var handlingFailed = false;

            // Both kinds of burn are read before either is handled: a burn's id counts every bridge burn of its transaction
            // (BaseBridgeService.BurnId). A provider that could not read one kind has not read the range.
            var v3Contract = BaseBridgeService.ContractAddress;
            var v2Contract = BaseBridgeService.ContractAddress; // also scan the V2 contract if configured
            var btcExitContract = !string.IsNullOrEmpty(v2Contract) ? v2Contract : contract;
            var reads = await BaseRpc.AuthorizingEachAsync(asked, async web3 =>
            {
                var vfx = new List<Nethereum.Contracts.EventLog<VfxExitBurnedEventDTO>>();
                if (!string.IsNullOrEmpty(v3Contract))
                {
                    var vfxExitHandler = web3.Eth.GetEvent<VfxExitBurnedEventDTO>(v3Contract);
                    vfx = await vfxExitHandler.GetAllChangesAsync(vfxExitHandler.CreateFilterInput(
                        new BlockParameter(new HexBigInteger(from)),
                        new BlockParameter(new HexBigInteger(to))));
                }
                var btcEventHandler = web3.Eth.GetEvent<BTCExitBurnedEventDTO>(btcExitContract);
                var btc = await btcEventHandler.GetAllChangesAsync(btcEventHandler.CreateFilterInput(
                    new BlockParameter(new HexBigInteger(from)),
                    new BlockParameter(new HexBigInteger(to))));
                // The contract writes its own chain's id into every burn: a log that names another chain is not Base's,
                // and this provider has not read the range.
                if (vfx.Any(e => e.Event != null && !BaseRpc.IsExpectedChain(e.Event.ChainId))
                    || btc.Any(e => e.Event != null && !BaseRpc.IsExpectedChain(e.Event.ChainId)))
                    throw new BaseProviderException(BaseBridgeService.BurnOnAnotherChain);
                return (Vfx: vfx, Btc: btc);
            });

            // What any provider serves is a burn to look into: one provider cannot hide a burn from the watch. It is not
            // yet a burn: before anything is signed for it, the casters' check holds it to a final receipt that
            // BaseRpc.RequiredAgreement providers give alike. A burn's id is taken from the logs of the provider that
            // served it, so a log one provider adds to a transaction does not rename the burn the others serve.
            var vfxExitLogs = new List<(Nethereum.Contracts.EventLog<VfxExitBurnedEventDTO> Event, string BurnId)>();
            var btcLogs = new List<(Nethereum.Contracts.EventLog<BTCExitBurnedEventDTO> Event, string BurnId)>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var readBy = 0;
            var rangeFaults = 0;
            foreach (var read in reads)
            {
                var spanState = SpanOf(read.Provider);
                if (!read.Read)
                {
                    if (!read.ProviderAtFault)
                    {
                        rangeFaults++;
                        spanState.Span = spanState.AfterFailure(span);
                    }
                    LogUtility.Log($"[BaseBridgeExit] Burn log query error ({BaseRpc.NameOf(read.Provider)}): {read.Reason}",
                        "BaseBridgeExitWatchService.PollOnceInternal");
                    continue;
                }
                readBy++;
                spanState.Span = spanState.AfterSuccess(span);
                var ids = BurnIdsOf(read.Value.Vfx.Select(e => e.Log).Concat(read.Value.Btc.Select(e => e.Log)));
                foreach (var ev in read.Value.Vfx)
                {
                    if (ev.Log == null || string.IsNullOrEmpty(ev.Log.TransactionHash)) continue;
                    var id = ids[BurnLogKey(ev.Log)];
                    if (taken.Add($"V|{id}|{ev.Event?.Amount}|{ev.Event?.VfxDestinationAddress}|{ev.Event?.Burner?.ToLowerInvariant()}"))
                        vfxExitLogs.Add((ev, id));
                }
                foreach (var ev in read.Value.Btc)
                {
                    if (ev.Log == null || string.IsNullOrEmpty(ev.Log.TransactionHash)) continue;
                    var id = ids[BurnLogKey(ev.Log)];
                    if (taken.Add($"B|{id}|{ev.Event?.Amount}|{ev.Event?.BtcDestination}|{ev.Event?.Burner?.ToLowerInvariant()}"))
                        btcLogs.Add((ev, id));
                }
            }
            // The range is passed only once BaseRpc.RequiredAgreement providers have read it: fewer, and a burn the
            // others would have served could be left behind the cursor.
            if (readBy < BaseRpc.RequiredAgreement)
            {
                scanFailed = true;
                providerAtFault = readBy + rangeFaults < BaseRpc.RequiredAgreement;
            }

            // --- VfxExitBurned events (V3 burnForVfxExit → pool-based unlock) ---
            try
            {
                if (!string.IsNullOrEmpty(v3Contract))
                {
                    foreach (var (ev, burnId) in vfxExitLogs)
                    {
                        var log = ev.Log;
                        var burner = ev.Event?.Burner ?? "";
                        var amount = ev.Event?.Amount ?? BigInteger.Zero;
                        var vfxDest = ev.Event?.VfxDestinationAddress?.Trim() ?? "";
                        if (string.IsNullOrEmpty(burner) || string.IsNullOrEmpty(vfxDest)) continue;
                        if (amount > long.MaxValue || amount <= 0) continue;

                        if (string.IsNullOrEmpty(log.TransactionHash)) continue;
                        var txHash = burnId; // the burn's id: its transaction's hash, with its position when that holds several

                        // Kept for a restart until the chain redeems it, also when this read repeats one whose state was not saved.
                        if (Globals.IsBlockCaster) NoteOpenBurn(state, txHash, (long)(log.BlockNumber?.Value ?? 0));

                        if (BurnExitConsensusService.IsAlreadyProcessed(txHash, BurnExitConsensusService.BurnExitType.VfxPoolUnlock, (decimal)amount / 100_000_000M, burner, "", vfxDest))
                            continue;

                        var amountDecimal = (decimal)amount / 100_000_000M;

                        LogUtility.Log($"[BaseBridgeExit] Detected VfxExitBurned (V3 pool): {txHash}, amount={amountDecimal}, vfxDest={vfxDest}",
                            "BaseBridgeExitWatchService.PollOnceInternal");

                        if (Globals.IsBlockCaster)
                        {
                            _ = BurnExitConsensusService.HandleDetectedBurn(
                                txHash,
                                BurnExitConsensusService.BurnExitType.VfxPoolUnlock,
                                "", "", amountDecimal, burner,
                                vfxDestinationAddress: vfxDest)
                                .ContinueWith(t =>
                                {
                                    if (t.IsFaulted)
                                        ErrorLogUtility.LogError($"[BaseBridgeExit] HandleDetectedBurn (VfxPoolUnlock) faulted for {txHash}: {t.Exception?.InnerException?.Message}",
                                            "BaseBridgeExitWatchService.PollOnceInternal");
                                }, TaskContinuationOptions.OnlyOnFaulted);
                        }

                        processed++;
                    }
                }
            }
            catch (Exception v3Ex)
            {
                scanFailed = true;
                handlingFailed = true;
                LogUtility.Log($"[BaseBridgeExit] VfxExitBurned scan error: {v3Ex.Message}",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }

            // --- BTCExitBurned events (burnForBTCExit → direct BTC withdrawal) ---
            try
            {
                foreach (var (ev, burnId) in btcLogs)
                {
                    var log = ev.Log;
                    var burner = ev.Event?.Burner ?? "";
                    var amount = ev.Event?.Amount ?? BigInteger.Zero;
                    var btcDest = ev.Event?.BtcDestination?.Trim() ?? "";
                    if (string.IsNullOrEmpty(burner) || string.IsNullOrEmpty(btcDest)) continue;
                    if (amount > long.MaxValue || amount <= 0) continue;

                    if (string.IsNullOrEmpty(log.TransactionHash)) continue;
                    var txHash = burnId; // the burn's id: its transaction's hash, with its position when that holds several

                    // Kept for a restart until the chain redeems it, also when this read repeats one whose state was not saved.
                    if (Globals.IsBlockCaster) NoteOpenBurn(state, txHash, (long)(log.BlockNumber?.Value ?? 0));

                    if (BurnExitConsensusService.IsAlreadyProcessed(txHash, BurnExitConsensusService.BurnExitType.BtcExit, (decimal)amount / 100_000_000M, burner, btcDest, ""))
                        continue;

                    var amountDecimal = (decimal)amount / 100_000_000M;

                    LogUtility.Log($"[BaseBridgeExit] Detected BTCExitBurned: {txHash}, amount={amountDecimal}, dest={btcDest}",
                        "BaseBridgeExitWatchService.PollOnceInternal");

                    // Feed into BurnExitConsensusService for caster consensus + FROST
                    if (Globals.IsBlockCaster)
                    {
                        _ = BurnExitConsensusService.HandleDetectedBurn(
                            txHash,
                            BurnExitConsensusService.BurnExitType.BtcExit,
                            "", btcDest, amountDecimal, burner)
                            .ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                    ErrorLogUtility.LogError($"[BaseBridgeExit] HandleDetectedBurn (BtcExit) faulted for {txHash}: {t.Exception?.InnerException?.Message}",
                                        "BaseBridgeExitWatchService.PollOnceInternal");
                            }, TaskContinuationOptions.OnlyOnFaulted);
                    }

                    processed++;
                }
            }
            catch (Exception btcEx)
            {
                scanFailed = true;
                handlingFailed = true;
                LogUtility.Log($"[BaseBridgeExit] BTCExitBurned scan error: {btcEx.Message}",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }

            // A range whose logs could not be read is read again next time: moving past it lost any burn in it on this
            // caster, and on every caster whose reads failed alike (fifth review, round 20). Burns already handled are
            // skipped on the second read.
            if (scanFailed)
            {
                if (handlingFailed)
                    foreach (var provider in asked) SpanOf(provider).Span = SpanOf(provider).AfterFailure(span);
                if (span > 1)
                    return (processed, state.LastScannedBlock, $"Base log query failed; the range is read again next poll, {SpanFor(asked)} block(s) at a time");
                // A block is passed over for failures of its own (a block the providers cannot serve while they serve the
                // next). When too few providers were in a position to read it (not on Base, or their logs are not
                // Base's), the block was not read at all: however often that repeats, the block waits, and is read once
                // enough providers that are right answer. Passed over, every burn in the block was lost with it, and
                // setting the providers right did not bring them back.
                if (providerAtFault)
                    return (processed, state.LastScannedBlock, "Too few Base providers gave an answer that is Base's; the block is read again, not passed over");
                if (++_singleBlockFailures < SingleBlockFailuresBeforeSkip)
                    return (processed, state.LastScannedBlock, "Base log query failed for one block; read again next poll");
                // Passed over only when the providers read the next block: during an outage nothing is skipped, the
                // watcher waits (round 22).
                if (!await CanReadLogs(providers.Where(p => p.Final.Number >= from + 1).Select(p => p.Provider).ToList(), contract, from + 1, latest))
                {
                    _singleBlockFailures = SingleBlockFailuresBeforeSkip - 1;
                    return (processed, state.LastScannedBlock, "Base log queries are failing; waiting for the providers");
                }
                ErrorLogUtility.LogError($"[BaseBridgeExit] Base block {from} could not be read {SingleBlockFailuresBeforeSkip} times; passed over. Rescan it (RescanExitBurns/{from}) once the providers answer.",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }
            _singleBlockFailures = 0;

            state.LastScannedBlock = to;
            SaveCursorFromPoll(state);
            _behind = to < latest;

            return (processed, to, processed > 0 ? $"Processed {processed} exit burn(s)" : "OK");
        }
    }
}
