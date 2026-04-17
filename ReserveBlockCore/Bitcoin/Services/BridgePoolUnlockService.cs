using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Handles pool-based unlocks for V3 bridge exits.
    /// When a VfxExitBurned event fires on Base, casters reach consensus and then
    /// this service builds + broadcasts a VBTC_V2_BRIDGE_POOL_UNLOCK transaction.
    /// 
    /// FIFO allocation: oldest bridge locks are consumed first. If a lock has more
    /// than needed, only a partial amount is consumed; if less, multiple locks are used.
    /// The allocation plan is deterministic and serialized into the TX so all nodes
    /// process the same state changes.
    /// </summary>
    public static class BridgePoolUnlockService
    {
        /// <summary>
        /// Compute the FIFO allocation plan for a given exit amount.
        /// Returns null if insufficient pool liquidity.
        /// </summary>
        public static List<PoolUnlockAllocation>? ComputeAllocationPlan(decimal exitAmountBtc)
        {
            var available = VBTCBridgeLockState.GetAvailableLocksFIFO();
            if (available == null || available.Count == 0)
                return null;

            var allocations = new List<PoolUnlockAllocation>();
            decimal remaining = exitAmountBtc;

            foreach (var lockRec in available)
            {
                if (remaining <= 0) break;

                decimal useAmount = Math.Min(remaining, lockRec.RemainingAmount);
                if (useAmount <= 0) continue;

                allocations.Add(new PoolUnlockAllocation
                {
                    LockId = lockRec.LockId,
                    UnlockAmount = useAmount
                });

                remaining -= useAmount;
            }

            // Not enough pool liquidity
            if (remaining > 0.00000001M)
                return null;

            return allocations;
        }

        /// <summary>
        /// Build, sign, and broadcast a VBTC_V2_BRIDGE_POOL_UNLOCK transaction.
        /// Called by the caster after consensus on a VfxExitBurned event.
        /// 
        /// The signerAddress is the local node's VFX address that will sign the TX.
        /// The vfxDestinationAddress is where the unlocked vBTC will be credited.
        /// </summary>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgePoolUnlockTx(
            string signerAddress,
            string vfxDestinationAddress,
            decimal totalAmount,
            string exitBurnTxHash,
            List<PoolUnlockAllocation> allocations,
            IReadOnlyList<CasterConsensusVote>? casterConsensusVotes = null)
        {
            try
            {
                var account = AccountData.GetSingleAccount(signerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {signerAddress}", "BridgePoolUnlockService.CreateBridgePoolUnlockTx()");
                    return (false, $"Account not found: {signerAddress}");
                }

                long totalAmountSats = (long)(totalAmount * 100_000_000M);

                var payload = new
                {
                    Function = "VBTCBridgePoolUnlock()",
                    TotalAmount = totalAmount,
                    TotalAmountSats = totalAmountSats,
                    VfxDestinationAddress = vfxDestinationAddress,
                    ExitBurnTxHash = exitBurnTxHash,
                    Allocations = allocations,
                    CasterConsensusVotes = casterConsensusVotes
                };
                var txData = JsonConvert.SerializeObject(payload);

                var poolUnlockTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = signerAddress,
                    ToAddress = signerAddress,
                    Amount = 0.00M,
                    Fee = 0.00M,
                    Nonce = AccountStateTrei.GetNextNonce(signerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK,
                    Data = txData
                };

                poolUnlockTx.Fee = 0.00M;

                poolUnlockTx.Build();
                var txHash = poolUnlockTx.Hash;
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {signerAddress}", "BridgePoolUnlockService.CreateBridgePoolUnlockTx()");
                    return (false, $"Private key was null for account {signerAddress}");
                }

                var signature = ReserveBlockCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed for pool unlock", "BridgePoolUnlockService.CreateBridgePoolUnlockTx()");
                    return (false, "TX Signature Failed");
                }

                poolUnlockTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(poolUnlockTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(poolUnlockTx, true);
                    await AccountData.UpdateLocalBalance(signerAddress, poolUnlockTx.Fee + poolUnlockTx.Amount);
                    await TransactionData.AddToPool(poolUnlockTx);
                    await P2PClient.SendTXMempool(poolUnlockTx);

                    SCLogUtility.Log(
                        $"vBTC V2 Bridge Pool Unlock TX Success. TxHash: {poolUnlockTx.Hash}, Dest: {vfxDestinationAddress}, " +
                        $"TotalAmount: {totalAmount}, Allocations: {allocations.Count}, ExitBurn: {exitBurnTxHash}",
                        "BridgePoolUnlockService.CreateBridgePoolUnlockTx()");

                    return (true, poolUnlockTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Bridge Pool Unlock TX Verify Failed. Result: {result.Item2}", "BridgePoolUnlockService.CreateBridgePoolUnlockTx()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Bridge Pool Unlock Error: {ex.Message}", "BridgePoolUnlockService.CreateBridgePoolUnlockTx()");
                return (false, $"Error: {ex.Message}");
            }
        }
    }
}
