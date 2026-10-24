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
        internal static bool Nameable(string? id) =>
            NameableText(id) && id!.Length <= MaxLockIdLength;

        /// <summary>A vault UID a plan can name: printable ASCII, not padded. Not held to the lock id's 64 (a created UID may run to 85; round 28).</summary>
        internal static bool NameableText(string? id) =>
            !string.IsNullOrWhiteSpace(id) && id == id.Trim() && id.All(c => c >= 0x20 && c <= 0x7E);

        /// <summary>A bridge lock's id at most (from the path-audit rules; older longer ones are left out of plans).</summary>
        public const int MaxLockIdLength = 64;

        /// <summary>
        /// Locks one plan names at most: a plan is one transaction under the size cap (about 150 bytes a lock). A plan over
        /// many tiny locks (made at the front of the queue on purpose, or honest dust) was refused, and every burn
        /// reaching them went unpaid (fifth review, round 27).
        /// </summary>
        public const int MaxPlanAllocations = 100;

        /// <summary>Locks below this are passed over first, and used only when no plan can be made without them.</summary>
        public const decimal PlanDustBtc = 0.0001M;

        public static List<PoolUnlockAllocation>? ComputeAllocationPlan(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs = null) =>
            PlanCandidates(exitAmountBtc, excludeContractUIDs).FirstOrDefault(p => p != null);

        /// <summary>
        /// The plans tried, in order: dust passed over; every lock in order; the largest first (more locks at the front
        /// than one plan names, each just above the dust line, say: round 28).
        /// </summary>
        private static IEnumerable<List<PoolUnlockAllocation>?> PlanCandidates(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs)
        {
            yield return Fitting(ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs, passOverDust: true, largestFirst: false));
            yield return Fitting(ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs, passOverDust: false, largestFirst: false));
            yield return Fitting(ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs, passOverDust: false, largestFirst: true));
        }

        /// <summary>
        /// The bytes a plan's allocations take in a transaction, measured as they are carried: serialized into Data, and
        /// Data escaped again where the transaction's size is measured. A lock id of quotes or backslashes takes four bytes
        /// a character there, and a hundred such locks went over the cap (fifth review, round 29).
        /// </summary>
        internal static int PlanBytes(List<PoolUnlockAllocation> plan) =>
            JsonConvert.SerializeObject(JsonConvert.SerializeObject(plan)).Length;

        /// <summary>Room for allocations in one transaction under the 30 KB cap, after its votes and other fields.</summary>
        internal const int MaxPlanBytes = 24_000;

        private static List<PoolUnlockAllocation>? Fitting(List<PoolUnlockAllocation>? plan) =>
            plan != null && PlanBytes(plan) <= MaxPlanBytes ? plan : null;

        private static List<PoolUnlockAllocation>? ComputeAllocationPlan(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs, bool passOverDust, bool largestFirst)
        {
            var available = VBTCBridgeLockState.GetAvailableLocksFIFO();
            if (available != null && largestFirst)
                available = available.OrderByDescending(l => l.RemainingAmount).ToList();
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

                // A lock whose id or vault cannot be named in a transaction consensus takes (from the path-audit rules:
                // printable ASCII, no spaces around, not blank) is left out: a lock made before those rules with such an
                // id refused every plan that reached it, freezing all unlocks and exits until H2 (fifth review, round 26).
                if (!Nameable(lockRec.LockId) || !NameableText(lockRec.SmartContractUID))
                    continue;
                if (passOverDust && lockRec.RemainingAmount < PlanDustBtc)
                    continue;

                // Whole satoshis, as consensus takes them from the path-audit rules: a lock made before amounts were held
                // to 8 places (VX-01) is drawn down to its last whole satoshi (round 16).
                decimal useAmount = Math.Min(remaining, Math.Round(lockRec.RemainingAmount, 8, MidpointRounding.ToZero));
                if (useAmount <= 0) continue;

                allocations.Add(new PoolUnlockAllocation
                {
                    LockId = lockRec.LockId,
                    UnlockAmount = useAmount,
                    SmartContractUID = lockRec.SmartContractUID
                });
                // More than one transaction can carry: measured as it is carried (the size cap is consensus's only
                // limit). A flat count of 100 refused a burn that needed more locks than that while they fit, and that
                // burn was never paid.
                if (allocations.Count > MaxPlanAllocations && PlanBytes(allocations) > MaxPlanBytes)
                    return null;

                remaining -= useAmount;
            }

            // Not enough pool liquidity (a plan short by any amount is refused by consensus; round 16)
            if (remaining > 0M)
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
        /// they are frozen at the enforcement height; from then on only watched vaults are used. A watched vault whose
        /// deposit address (of any key) is <paramref name="btcDestination"/> is left out: an exit paying a vault it draws
        /// on is refused, and a burn to such an address was refused at every attempt and never paid (fifth review,
        /// round 23).
        /// </summary>
        public static List<PoolUnlockAllocation>? ComputeExitAllocationPlan(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs, long height, string? btcDestination = null)
        {
            if (!Attestation.BtcAttestRules.Recording(height))
                return ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs);

            var contracts = (VBTCBridgeLockState.GetAvailableLocksFIFO() ?? new List<VBTCBridgeLockState>())
                .Select(l => l.SmartContractUID).Distinct(StringComparer.Ordinal).ToList();
            var watched = contracts.Where(c => Attestation.BtcAttestBalance.IsAttestedVault(c)).ToHashSet(StringComparer.Ordinal);
            var older = contracts.Where(c => !watched.Contains(c)).ToHashSet(StringComparer.Ordinal);
            var paidTo = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(btcDestination) && Attestation.BtcCanonicalWithdrawal.TryDestinationScript(btcDestination, out var script))
                foreach (var vault in watched)
                    if (Attestation.BtcAttestStore.ScriptRowsForVault(vault).Any(k => k.ScriptHex == script))
                        paidTo.Add(vault);
            HashSet<string> Without(HashSet<string> kind) =>
                new HashSet<string>((excludeContractUIDs ?? new HashSet<string>()).Concat(kind).Concat(paidTo), StringComparer.Ordinal);

            if (!Attestation.BtcLegacyFreeze.Active(height))
            {
                var fromOlder = ComputeAllocationPlan(exitAmountBtc, Without(watched));
                if (fromOlder != null) return fromOlder;
            }
            // A vault is never asked for less than a Bitcoin transaction can pay (the exit would be refused). A vault
            // whose share would be that small is left out and the plan made again without it: one small lock at the
            // front of the queue (a remnant, or one made on purpose) used to end the plan for every exit that reached it.
            // Each kind of plan is tried before a vault is left out: passing over dust could leave a vault's part under the
            // minimum that its dust would have lifted over it (round 28).
            var leftOut = Without(older);
            static List<string> TooSmall(List<PoolUnlockAllocation> plan) => plan.GroupBy(a => a.SmartContractUID, StringComparer.Ordinal)
                .Where(g => (long)(g.Sum(a => a.UnlockAmount) * Attestation.BtcAttestBalance.SatsPerBtc) < Attestation.BtcWithdrawals.MinExitPartSats)
                .Select(g => g.Key).ToList();
            while (true)
            {
                List<PoolUnlockAllocation>? first = null;
                foreach (var candidate in PlanCandidates(exitAmountBtc, leftOut))
                {
                    if (candidate == null) continue;
                    first ??= candidate;
                    if (TooSmall(candidate).Count == 0) return candidate;
                    if (Rebalanced(candidate) is { } moved && TooSmall(moved).Count == 0) return moved;
                }
                if (first == null) return null;
                foreach (var vault in TooSmall(first)) leftOut.Add(vault);
            }
        }

        /// <summary>
        /// A plan with a vault's part under the exit minimum, made valid by moving the shortfall to that vault from another
        /// vault whose part stays at or above the minimum (and within the locks already planned). A plan of one vault's
        /// whole locks and a little of the next was given up although such a split existed (fifth review, round 29).
        /// Null when no such move is found.
        /// </summary>
        private static List<PoolUnlockAllocation>? Rebalanced(List<PoolUnlockAllocation> plan)
        {
            var min = Attestation.BtcWithdrawals.MinExitPartSats / Attestation.BtcAttestBalance.SatsPerBtc;
            var remaining = (VBTCBridgeLockState.GetAvailableLocksFIFO() ?? new List<VBTCBridgeLockState>())
                .GroupBy(l => l.LockId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Math.Round(g.First().RemainingAmount, 8, MidpointRounding.ToZero), StringComparer.Ordinal);
            var moved = plan.Select(a => new PoolUnlockAllocation { LockId = a.LockId, UnlockAmount = a.UnlockAmount, SmartContractUID = a.SmartContractUID }).ToList();
            decimal Share(string vault) => moved.Where(a => a.SmartContractUID == vault).Sum(a => a.UnlockAmount);
            foreach (var small in moved.Select(a => a.SmartContractUID).Distinct(StringComparer.Ordinal).Where(v => Share(v) < min).ToList())
            {
                var need = min - Share(small);
                var room = moved.Where(a => a.SmartContractUID == small).Sum(a => (remaining.TryGetValue(a.LockId, out var r) ? r : a.UnlockAmount) - a.UnlockAmount);
                if (room < need) return null;
                var donor = moved.Select(a => a.SmartContractUID).Distinct(StringComparer.Ordinal)
                    .FirstOrDefault(v => v != small && Share(v) - need >= min);
                if (donor == null) return null;
                var take = need;
                foreach (var a in moved.Where(a => a.SmartContractUID == donor).Reverse())
                {
                    var part = Math.Min(take, a.UnlockAmount);
                    a.UnlockAmount -= part;
                    take -= part;
                    if (take == 0M) break;
                }
                moved.RemoveAll(a => a.UnlockAmount <= 0M);
                var give = need;
                foreach (var a in moved.Where(a => a.SmartContractUID == small))
                {
                    var part = Math.Min(give, (remaining.TryGetValue(a.LockId, out var r) ? r : a.UnlockAmount) - a.UnlockAmount);
                    a.UnlockAmount += part;
                    give -= part;
                    if (give == 0M) break;
                }
                if (give != 0M) return null;
            }
            return moved;
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
