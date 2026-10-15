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
        private const int SingleBlockFailuresBeforeSkip = 20;

        /// <summary>Base blocks read again after a restart: about two hours (round 23).</summary>
        internal const long RestartRewindBlocks = 3_600;
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
            while (!Globals.StopAllTimers)
            {
                try
                {
                    if (IsConfigured)
                        await PollOnceInternal();
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[BaseBridgeExit] Poll error: {ex.Message}", "BaseBridgeExitWatchService.BridgeExitScanLoop");
                }

                await Task.Delay(12_000);
            }
        }

        /// <summary>Reset the scan cursor to rescan from a specific block. Next poll will start from fromBlock.</summary>
        public static (bool Success, string Message) RescanFromBlock(long fromBlock)
        {
            if (fromBlock < 0)
                return (false, "Block number must be >= 0");

            var state = BridgeExitSyncState.GetOrCreate();
            var previousBlock = state.LastScannedBlock;
            state.LastScannedBlock = Math.Max(0, fromBlock - 1);
            BridgeExitSyncState.Save(state);

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

        private static async Task<bool> CanReadLogs(Web3 web3, string? contract, long block, long latest)
        {
            if (block > latest || string.IsNullOrEmpty(contract)) return false;
            try
            {
                var probe = new Nethereum.RPC.Eth.DTOs.NewFilterInput
                {
                    FromBlock = new BlockParameter(new HexBigInteger(block)),
                    ToBlock = new BlockParameter(new HexBigInteger(block)),
                    Address = new[] { contract },
                };
                await web3.Eth.Filters.GetLogs.SendRequestAsync(probe);
                return true;
            }
            catch { return false; }
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
            var back = Math.Max(0, Math.Min(since, state.LastScannedBlock) - RestartRewindBlocks);
            if (back >= state.LastScannedBlock) return;
            LogUtility.Log($"[BaseBridgeExit] Now handling burns: reading Base again from block {back + 1} (cursor was {state.LastScannedBlock}).",
                "BaseBridgeExitWatchService.PollOnceInternal");
            state.LastScannedBlock = back;
            BridgeExitSyncState.Save(state);
        }

        /// <summary>Test seam: forget the start and handling history.</summary>
        internal static void ResetRewindForTests()
        {
            _started = false;
            _cursorWhenNotHandling = null;
        }

        private static async Task<(int Processed, long ScannedToBlock, string Message)> PollOnceInternal()
        {
            var web3 = new Web3(BaseBridgeService.BaseRpcUrl);
            var latestHex = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            var latest = (long)latestHex.Value;

            var state = BridgeExitSyncState.GetOrCreate();

            if (state.LastScannedBlock == 0)
            {
                if (_startBlockOverride.HasValue)
                {
                    state.LastScannedBlock = Math.Max(0, _startBlockOverride.Value - 1);
                    BridgeExitSyncState.Save(state);
                    LogUtility.Log(
                        $"[BaseBridgeExit] BASE_BRIDGE_EXIT_FROM_BLOCK={_startBlockOverride}; scanning from block {state.LastScannedBlock + 1}",
                        "BaseBridgeExitWatchService.PollOnceInternal");
                }
                else
                {
                    state.LastScannedBlock = Math.Max(0, latest - 1);
                    BridgeExitSyncState.Save(state);
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
            ConsumeRewind(state, Globals.IsBlockCaster && Globals.IsChainSynced);

            var from = state.LastScannedBlock + 1;
            if (from > latest)
                return (0, state.LastScannedBlock, "No new blocks");

            var to = Math.Min(from + _span - 1, latest);
            var contract = BaseBridgeService.VBTCbContractAddress;

            var processed = 0;
            var scanFailed = false;

            // --- VfxExitBurned events (V3 burnForVfxExit → pool-based unlock) ---
            try
            {
                var v3Contract = BaseBridgeService.ContractAddress;
                if (!string.IsNullOrEmpty(v3Contract))
                {
                    var vfxExitHandler = web3.Eth.GetEvent<VfxExitBurnedEventDTO>(v3Contract);
                    var vfxExitFilter = vfxExitHandler.CreateFilterInput(
                        new BlockParameter(new HexBigInteger(from)),
                        new BlockParameter(new HexBigInteger(to)));

                    var vfxExitLogs = await vfxExitHandler.GetAllChangesAsync(vfxExitFilter);

                    foreach (var ev in vfxExitLogs)
                    {
                        var log = ev.Log;
                        var burner = ev.Event?.Burner ?? "";
                        var amount = ev.Event?.Amount ?? BigInteger.Zero;
                        var vfxDest = ev.Event?.VfxDestinationAddress?.Trim() ?? "";
                        if (string.IsNullOrEmpty(burner) || string.IsNullOrEmpty(vfxDest)) continue;
                        if (amount > long.MaxValue || amount <= 0) continue;

                        var txHash = log.TransactionHash;
                        if (string.IsNullOrEmpty(txHash)) continue;

                        if (BurnExitConsensusService.IsAlreadyProcessed(txHash))
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
                LogUtility.Log($"[BaseBridgeExit] VfxExitBurned scan error: {v3Ex.Message}",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }

            // --- BTCExitBurned events (burnForBTCExit → direct BTC withdrawal) ---
            try
            {
                // Also scan the V2 contract if configured
                var v2Contract = BaseBridgeService.ContractAddress;
                var btcExitContract = !string.IsNullOrEmpty(v2Contract) ? v2Contract : contract;

                var btcEventHandler = web3.Eth.GetEvent<BTCExitBurnedEventDTO>(btcExitContract);
                var btcFilter = btcEventHandler.CreateFilterInput(
                    new BlockParameter(new HexBigInteger(from)),
                    new BlockParameter(new HexBigInteger(to)));

                var btcLogs = await btcEventHandler.GetAllChangesAsync(btcFilter);

                foreach (var ev in btcLogs)
                {
                    var log = ev.Log;
                    var burner = ev.Event?.Burner ?? "";
                    var amount = ev.Event?.Amount ?? BigInteger.Zero;
                    var btcDest = ev.Event?.BtcDestination?.Trim() ?? "";
                    if (string.IsNullOrEmpty(burner) || string.IsNullOrEmpty(btcDest)) continue;
                    if (amount > long.MaxValue || amount <= 0) continue;

                    var txHash = log.TransactionHash;
                    if (string.IsNullOrEmpty(txHash)) continue;

                    if (BurnExitConsensusService.IsAlreadyProcessed(txHash))
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
                LogUtility.Log($"[BaseBridgeExit] BTCExitBurned scan error: {btcEx.Message}",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }

            // A range whose logs could not be read is read again next time: moving past it lost any burn in it on this
            // caster, and on every caster whose reads failed alike (fifth review, round 20). Burns already handled are
            // skipped on the second read.
            if (scanFailed)
            {
                if (_span > 1)
                {
                    _span = Math.Max(1, _span / 4);
                    return (processed, state.LastScannedBlock, $"Base log query failed; the range is read again next poll, {_span} block(s) at a time");
                }
                if (++_singleBlockFailures < SingleBlockFailuresBeforeSkip)
                    return (processed, state.LastScannedBlock, "Base log query failed for one block; read again next poll");
                // Passed over only when the provider reads the next block: during an outage nothing is skipped, the
                // watcher waits (round 22).
                if (!await CanReadLogs(web3, contract, from + 1, latest))
                {
                    _singleBlockFailures = SingleBlockFailuresBeforeSkip - 1;
                    return (processed, state.LastScannedBlock, "Base log queries are failing; waiting for the provider");
                }
                ErrorLogUtility.LogError($"[BaseBridgeExit] Base block {from} could not be read {SingleBlockFailuresBeforeSkip} times; passed over. Rescan it (RescanExitBurns/{from}) once the provider answers.",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }
            else
            {
                _span = Math.Min(MaxBlockSpan, _span * 2);
            }
            _singleBlockFailures = 0;

            state.LastScannedBlock = to;
            BridgeExitSyncState.Save(state);

            return (processed, to, processed > 0 ? $"Processed {processed} exit burn(s)" : "OK");
        }
    }
}
