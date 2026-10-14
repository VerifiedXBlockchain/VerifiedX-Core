using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
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
        /// <param name="exitAmountBtc">Amount of BTC to allocate.</param>
        /// <param name="excludeContractUIDs">Optional set of contract UIDs to skip during allocation
        /// (e.g. blacklisted or known-broken FROST contracts).</param>
        public static List<PoolUnlockAllocation>? ComputeAllocationPlan(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs = null)
        {
            var available = VBTCBridgeLockState.GetAvailableLocksFIFO();
            if (available == null || available.Count == 0)
                return null;

            var allocations = new List<PoolUnlockAllocation>();
            decimal remaining = exitAmountBtc;

            foreach (var lockRec in available)
            {
                if (remaining <= 0) break;

                // Skip locks belonging to excluded contracts
                if (excludeContractUIDs != null && excludeContractUIDs.Contains(lockRec.SmartContractUID))
                    continue;

                decimal useAmount = Math.Min(remaining, lockRec.RemainingAmount);
                if (useAmount <= 0) continue;

                allocations.Add(new PoolUnlockAllocation
                {
                    LockId = lockRec.LockId,
                    UnlockAmount = useAmount,
                    SmartContractUID = lockRec.SmartContractUID
                });

                remaining -= useAmount;
            }

            // Not enough pool liquidity
            if (remaining > 0.00000001M)
                return null;

            return allocations;
        }

        /// <summary>
        /// The allocation plan for an unlock back to VFX. From the enforcement height it leaves out the locks of frozen
        /// vaults: consensus refuses them, and vBTC handed out on a frozen vault could never be moved. It also leaves out
        /// a watched vault whose ledger is full and does not name the destination: consensus refuses that credit
        /// (BtcVaultLedger.CreditRefusal, fourth review round 6).
        /// </summary>
        public static List<PoolUnlockAllocation>? ComputeUnlockAllocationPlan(decimal amountBtc, long height, string? destination = null)
        {
            var vaults = (VBTCBridgeLockState.GetAvailableLocksFIFO() ?? new List<VBTCBridgeLockState>())
                .Select(l => l.SmartContractUID).Distinct(StringComparer.Ordinal).ToList();
            var leftOut = vaults
                .Where(c => Attestation.BtcLegacyFreeze.Active(height) && Attestation.BtcLegacyFreeze.IsLegacyVault(c)
                    || destination != null && Attestation.BtcVaultLedger.CreditRefusal(SmartContractStateTrei.GetSmartContractState(c), destination) != null)
                .ToHashSet(StringComparer.Ordinal);
            return leftOut.Count == 0 ? ComputeAllocationPlan(amountBtc) : ComputeAllocationPlan(amountBtc, leftOut);
        }

        /// <summary>
        /// The allocation plan for an exit that pays Bitcoin. From the recording height an exit draws on one kind of
        /// vault only: older vaults (paid the earlier way, by the handling caster) or vaults the chain watches (paid by
        /// the transaction consensus pins). Older vaults go first while they may still pay, so that they empty before
        /// they are frozen at the enforcement height; from then on only watched vaults are used.
        /// </summary>
        public static List<PoolUnlockAllocation>? ComputeExitAllocationPlan(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs, long height)
        {
            if (!Attestation.BtcAttestRules.Recording(height))
                return ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs);

            var contracts = (VBTCBridgeLockState.GetAvailableLocksFIFO() ?? new List<VBTCBridgeLockState>())
                .Select(l => l.SmartContractUID).Distinct(StringComparer.Ordinal).ToList();
            var watched = contracts.Where(c => Attestation.BtcAttestBalance.IsAttestedVault(c)).ToHashSet(StringComparer.Ordinal);
            var older = contracts.Where(c => !watched.Contains(c)).ToHashSet(StringComparer.Ordinal);
            HashSet<string> Without(HashSet<string> kind) =>
                new HashSet<string>((excludeContractUIDs ?? new HashSet<string>()).Concat(kind), StringComparer.Ordinal);

            if (!Attestation.BtcLegacyFreeze.Active(height))
            {
                var fromOlder = ComputeAllocationPlan(exitAmountBtc, Without(watched));
                if (fromOlder != null) return fromOlder;
            }
            // A vault is never asked for less than a Bitcoin transaction can pay (the exit would be refused). A vault
            // whose share would be that small is left out and the plan made again without it: one small lock at the
            // front of the queue (a remnant, or one made on purpose) used to end the plan for every exit that reached it.
            var leftOut = Without(older);
            while (true)
            {
                var fromWatched = ComputeAllocationPlan(exitAmountBtc, leftOut);
                if (fromWatched == null) return null;
                var tooSmall = fromWatched.GroupBy(a => a.SmartContractUID, StringComparer.Ordinal)
                    .Where(g => (long)(g.Sum(a => a.UnlockAmount) * Attestation.BtcAttestBalance.SatsPerBtc) < Attestation.BtcWithdrawals.MinExitPartSats)
                    .Select(g => g.Key).ToList();
                if (tooSmall.Count == 0) return fromWatched;
                foreach (var vault in tooSmall) leftOut.Add(vault);
            }
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

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
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
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }
    }
}
