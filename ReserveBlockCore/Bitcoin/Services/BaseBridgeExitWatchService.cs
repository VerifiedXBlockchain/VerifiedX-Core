using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Polls Base for <c>ExitBurned</c> (V2), <c>VfxExitBurned</c> (V3), and <c>BTCExitBurned</c> events.
    /// For ExitBurned (V2 legacy): broadcasts <see cref="TransactionType.VBTC_V2_BRIDGE_UNLOCK"/> on VFX.
    /// For VfxExitBurned (V3 pool): feeds into <see cref="BurnExitConsensusService"/> → <see cref="BridgePoolUnlockService"/>.
    /// For BTCExitBurned: feeds into <see cref="BurnExitConsensusService"/> for caster consensus + FROST.
    /// </summary>
    public static class BaseBridgeExitWatchService
    {
        private const int MaxBlockSpan = 4000;
        private static long? _startBlockOverride;

        [Event("ExitBurned")]
        public class ExitBurnedEventDTO : IEventDTO
        {
            [Parameter("address", "burner", 1, true)]
            public string Burner { get; set; } = "";

            [Parameter("uint256", "amount", 2, false)]
            public BigInteger Amount { get; set; }

            [Parameter("string", "vfxLockId", 3, false)]
            public string VfxLockId { get; set; } = "";
        }

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
        }

        /// <summary>Needs VBTCbV2 proxy address and Base RPC.</summary>
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

        /// <summary>Single poll; for API manual trigger.</summary>
        public static async Task<(int Processed, long ScannedToBlock, string Message)> PollOnce()
        {
            if (!IsConfigured)
                return (0, 0, "VBTCb contract address not configured");
            return await PollOnceInternal();
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
                    return (0, latest, "Initialized cursor; no backlog scan on first run");
                }
            }

            var from = state.LastScannedBlock + 1;
            if (from > latest)
                return (0, state.LastScannedBlock, "No new blocks");

            var to = Math.Min(from + MaxBlockSpan - 1, latest);
            var contract = BaseBridgeService.VBTCbContractAddress;

            var eventHandler = web3.Eth.GetEvent<ExitBurnedEventDTO>(contract);
            var filter = eventHandler.CreateFilterInput(
                new BlockParameter(new HexBigInteger(from)),
                new BlockParameter(new HexBigInteger(to)));

            var logs = await eventHandler.GetAllChangesAsync(filter);
            var processed = 0;

            foreach (var ev in logs)
            {
                var log = ev.Log;
                var lockId = ev.Event?.VfxLockId?.Trim() ?? "";
                var burner = ev.Event?.Burner ?? "";
                var amount = ev.Event?.Amount ?? BigInteger.Zero;
                if (string.IsNullOrEmpty(lockId) || string.IsNullOrEmpty(burner)) continue;
                if (amount > long.MaxValue || amount < 0) continue;

                var txHash = log.TransactionHash;
                if (string.IsNullOrEmpty(txHash)) continue;

                // If this node is a caster, delegate to BurnExitConsensusService for coordinated handling
                if (Globals.IsBlockCaster && !BurnExitConsensusService.IsAlreadyProcessed(txHash))
                {
                    var amountDecimal = (decimal)amount / 100_000_000M;
                    _ = BurnExitConsensusService.HandleDetectedBurn(
                        txHash,
                        BurnExitConsensusService.BurnExitType.VfxUnlock,
                        lockId, "", amountDecimal, burner);
                    processed++;
                    continue;
                }

                var chainLock = VBTCBridgeLockState.GetByLockId(lockId);
                if (chainLock == null || chainLock.IsUnlocked)
                    continue;

                var localRecord = BridgeLockRecord.GetByLockId(lockId);
                var walletForOwner = AccountData.GetSingleAccount(chainLock.OwnerAddress) != null;
                if (localRecord == null && !walletForOwner)
                    continue;

                if (localRecord != null && !BridgeLockRecord.ValidateExitBurnMatchesMinted(lockId, burner, (long)amount))
                    continue;

                if (localRecord == null)
                {
                    if (!string.Equals(chainLock.EvmDestination.Trim(), burner.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (chainLock.AmountSats != (long)amount) continue;
                }

                var unlockResult = await VBTCService.CreateBridgeUnlockTx(
                    chainLock.SmartContractUID,
                    chainLock.OwnerAddress,
                    lockId,
                    chainLock.Amount,
                    txHash);

                if (unlockResult.Success)
                {
                    processed++;
                    if (localRecord != null)
                        BridgeLockRecord.TryMarkRedeemingForExit(lockId, txHash);
                    LogUtility.Log($"[BaseBridgeExit] Broadcast VBTC_V2_BRIDGE_UNLOCK for lock {lockId} (burn tx {txHash}) → VFX tx {unlockResult.TxHashOrError}",
                        "BaseBridgeExitWatchService.PollOnceInternal");
                }
                else
                {
                    LogUtility.Log($"[BaseBridgeExit] CreateBridgeUnlockTx failed for lock {lockId}: {unlockResult.TxHashOrError}",
                        "BaseBridgeExitWatchService.PollOnceInternal");
                }
            }

            // --- VfxExitBurned events (V3 burnForVfxExit → pool-based unlock) ---
            try
            {
                var v3Contract = BaseBridgeService.VBTCbV3ContractAddress;
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
                                vfxDest, "", amountDecimal, burner);
                        }

                        processed++;
                    }
                }
            }
            catch (Exception v3Ex)
            {
                LogUtility.Log($"[BaseBridgeExit] VfxExitBurned scan error: {v3Ex.Message}",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }

            // --- BTCExitBurned events (burnForBTCExit → direct BTC withdrawal) ---
            try
            {
                // Also scan the V2 contract if configured
                var v2Contract = BaseBridgeService.VBTCbV2ContractAddress;
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
                            "", btcDest, amountDecimal, burner);
                    }

                    processed++;
                }
            }
            catch (Exception btcEx)
            {
                LogUtility.Log($"[BaseBridgeExit] BTCExitBurned scan error: {btcEx.Message}",
                    "BaseBridgeExitWatchService.PollOnceInternal");
            }

            state.LastScannedBlock = to;
            BridgeExitSyncState.Save(state);

            return (processed, to, processed > 0 ? $"Processed {processed} exit burn(s)" : "OK");
        }
    }
}
