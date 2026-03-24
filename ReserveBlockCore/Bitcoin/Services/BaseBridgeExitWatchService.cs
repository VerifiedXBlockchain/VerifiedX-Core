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
    /// Polls Base for <c>ExitBurned</c> from <see cref="VBTCb"/> <c>burnForExit</c> and broadcasts
    /// <see cref="TransactionType.VBTC_V2_BRIDGE_UNLOCK"/> on VerifiedX for this node's local locks
    /// (after <see cref="VBTCBridgeLockState"/> exists on-chain from <see cref="VBTCService.CreateBridgeLockTx"/>).
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

        /// <summary>Needs contract address + RPC only (relay key not required).</summary>
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

            state.LastScannedBlock = to;
            BridgeExitSyncState.Save(state);

            return (processed, to, processed > 0 ? $"Processed {processed} exit burn(s)" : "OK");
        }
    }
}
