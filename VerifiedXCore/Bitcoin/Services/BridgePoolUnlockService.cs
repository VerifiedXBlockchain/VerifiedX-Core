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
        private static IEnumerable<List<PoolUnlockAllocation>?> PlanCandidates(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs, List<VBTCBridgeLockState>? pool = null)
        {
            yield return Fitting(ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs, passOverDust: true, largestFirst: false, pool));
            yield return Fitting(ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs, passOverDust: false, largestFirst: false, pool));
            yield return Fitting(ComputeAllocationPlan(exitAmountBtc, excludeContractUIDs, passOverDust: false, largestFirst: true, pool));
        }

        /// <summary>Pairs of vaults one search tries at most before it plans on more vaults (each pair is three plans in memory).</summary>
        private const int MaxPairsTried = 16;

        /// <summary>The locks a plan may draw on, oldest first, read once for every plan one search tries.</summary>
        private static List<VBTCBridgeLockState> Pool() => VBTCBridgeLockState.GetAvailableLocksFIFO() ?? new List<VBTCBridgeLockState>();

        /// <summary>
        /// What each vault's locks hold for a plan (locks a plan can name, in whole satoshis), vaults in the order of their
        /// oldest lock, leaving out <paramref name="leftOut"/>.
        /// </summary>
        private static List<(string Vault, decimal Total)> VaultTotals(HashSet<string> leftOut, List<VBTCBridgeLockState> pool) =>
            pool
                .Where(l => !leftOut.Contains(l.SmartContractUID) && Nameable(l.LockId) && NameableText(l.SmartContractUID))
                .GroupBy(l => l.SmartContractUID, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Sum(l => Math.Round(l.RemainingAmount, 8, MidpointRounding.ToZero))))
                .ToList();

        /// <summary>
        /// Plans on as few vaults as can pay, each set's locks tried as <see cref="PlanCandidates"/> tries the pool: one
        /// vault alone, for each vault whose locks hold the whole amount (and at least its minimum part,
        /// <paramref name="minOf"/>), vaults in the order of their oldest lock; then pairs of vaults that hold the amount
        /// between them and whose minimums fit in it, largest first, at most <see cref="MaxPairsTried"/> (an unlock's
        /// minimum differs by vault, so the two largest may not both reach theirs while another pair can); then the vaults
        /// that hold the most, largest first, until together they hold the amount (no fewer vaults can). An exit to
        /// Bitcoin pays a miner fee for each vault it draws on, and an unlock leaves its recipient a position on each: the
        /// plan followed the queue across vaults when fewer could pay it, and the holder paid a fee a vault.
        /// </summary>
        private static IEnumerable<List<PoolUnlockAllocation>?> FewVaultCandidates(decimal amountBtc, HashSet<string> leftOut, List<VBTCBridgeLockState> pool, Func<string, decimal> minOf)
        {
            var totals = VaultTotals(leftOut, pool);
            IEnumerable<List<PoolUnlockAllocation>?> On(ICollection<string> vaults) =>
                PlanCandidates(amountBtc, new HashSet<string>(leftOut.Concat(totals.Select(t => t.Vault).Where(v => !vaults.Contains(v))), StringComparer.Ordinal), pool);
            foreach (var (vault, total) in totals)
            {
                if (total < amountBtc || amountBtc < minOf(vault)) continue;
                foreach (var candidate in On(new[] { vault }))
                    yield return candidate;
            }
            var minimum = totals.ToDictionary(t => t.Vault, t => minOf(t.Vault), StringComparer.Ordinal);
            var byTotal = totals.OrderByDescending(t => t.Total).ToList(); // stable: equal totals keep queue order
            var pairs = 0;
            for (var i = 0; i < byTotal.Count && pairs < MaxPairsTried; i++)
                for (var j = i + 1; j < byTotal.Count && pairs < MaxPairsTried; j++)
                {
                    if (byTotal[i].Total + byTotal[j].Total < amountBtc) break; // the rest are smaller still
                    if (byTotal[i].Total >= amountBtc && amountBtc >= minimum[byTotal[i].Vault]) break; // the vault alone was tried
                    if (minimum[byTotal[i].Vault] + minimum[byTotal[j].Vault] > amountBtc) continue;
                    pairs++;
                    foreach (var candidate in On(new[] { byTotal[i].Vault, byTotal[j].Vault }))
                        yield return candidate;
                }
            var fewest = new List<string>();
            decimal held = 0M;
            foreach (var (vault, total) in byTotal)
            {
                if (held >= amountBtc) break;
                fewest.Add(vault);
                held += total;
            }
            if (fewest.Count > 2 && held >= amountBtc)
                foreach (var candidate in On(fewest))
                    yield return candidate;
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

        private static List<PoolUnlockAllocation>? ComputeAllocationPlan(decimal exitAmountBtc, HashSet<string>? excludeContractUIDs, bool passOverDust, bool largestFirst, List<VBTCBridgeLockState>? pool = null)
        {
            var available = pool ?? VBTCBridgeLockState.GetAvailableLocksFIFO();
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
        /// vaults: consensus refuses them, and vBTC handed out on a frozen vault could never be moved. It leaves out a vault
        /// whose key cannot sign (BtcVaultKeys.CannotSignRefusal): vBTC handed out there could not be withdrawn or bridged. It also leaves out
        /// a watched vault whose ledger is full and does not name the destination: consensus refuses that credit
        /// (BtcVaultLedger.CreditRefusal, fourth review round 6). From Globals.VaultMinimumPositionHeight a watched vault
        /// whose share would leave the destination less than the vault's smallest position is left out too, and the plan
        /// made again without it (BtcVaultLedger.UnlockShareRefusal): consensus refuses that share. Null when no plan
        /// is left: a burn too small to give its destination a position is not paid from a watched vault.
        /// </summary>
        public static List<PoolUnlockAllocation>? ComputeUnlockAllocationPlan(decimal amountBtc, long height, string? destination = null)
        {
            var pool = Pool();
            var vaults = pool.Select(l => l.SmartContractUID).Distinct(StringComparer.Ordinal).ToList();
            var leftOut = vaults
                .Where(c => Attestation.BtcLegacyFreeze.Active(height) && Attestation.BtcLegacyFreeze.IsLegacyVault(c)
                    || destination != null && Attestation.BtcVaultLedger.CreditRefusal(SmartContractStateTrei.GetSmartContractState(c), destination) != null
                    || Attestation.BtcVaultKeys.CannotSignRefusal(c) != null)
                .ToHashSet(StringComparer.Ordinal);
            if (destination == null || height < Globals.VaultMinimumPositionHeight)
                return FewVaultCandidates(amountBtc, leftOut, pool, _ => 0M).FirstOrDefault(p => p != null)
                    ?? PlanCandidates(amountBtc, leftOut.Count == 0 ? null : leftOut, pool).FirstOrDefault(p => p != null);
            // As for an exit's parts: each kind of plan is tried, and a split moved between its vaults, before a vault whose
            // share is too small is left out.
            decimal MinOf(string vault) => Attestation.BtcVaultLedger.UnlockShareMinimum(vault, destination, height);
            List<string> TooSmall(List<PoolUnlockAllocation> plan) => plan.GroupBy(a => a.SmartContractUID, StringComparer.Ordinal)
                .Where(g => g.Sum(a => a.UnlockAmount) < MinOf(g.Key)).Select(g => g.Key).ToList();
            foreach (var (vault, total) in VaultTotals(leftOut, pool))
                if (total < MinOf(vault)) leftOut.Add(vault);
            foreach (var few in FewVaultCandidates(amountBtc, leftOut, pool, MinOf))
            {
                if (few == null) continue;
                if (TooSmall(few).Count == 0) return few;
                if (Rebalanced(few, MinOf, pool) is { } lifted && TooSmall(lifted).Count == 0) return lifted;
            }
            while (true)
            {
                List<PoolUnlockAllocation>? first = null;
                foreach (var candidate in PlanCandidates(amountBtc, leftOut, pool))
                {
                    if (candidate == null) continue;
                    first ??= candidate;
                    if (TooSmall(candidate).Count == 0) return candidate;
                    if (Rebalanced(candidate, MinOf, pool) is { } moved && TooSmall(moved).Count == 0) return moved;
                }
                if (first == null) return null;
                foreach (var vault in TooSmall(first)) leftOut.Add(vault); // each pass leaves out at least one more vault
            }
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

            var pool = Pool();
            var contracts = pool.Select(l => l.SmartContractUID).Distinct(StringComparer.Ordinal).ToList();
            var watched = contracts.Where(c => Attestation.BtcAttestBalance.IsAttestedVault(c)).ToHashSet(StringComparer.Ordinal);
            var older = contracts.Where(c => !watched.Contains(c)).ToHashSet(StringComparer.Ordinal);
            var paidTo = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(btcDestination) && Attestation.BtcCanonicalWithdrawal.TryDestinationScript(btcDestination, out var script))
                foreach (var vault in watched)
                    if (Attestation.BtcAttestStore.ScriptRowsForVault(vault).Any(k => k.ScriptHex == script))
                        paidTo.Add(vault);
            // A vault whose key cannot sign is left out: its part could never be paid, and the exit would wait out its
            // window and fail (and from BridgeAllocationsSignableHeight consensus refuses it).
            var cannotSign = watched.Where(c => Attestation.BtcVaultKeys.CannotSignRefusal(c) != null);
            HashSet<string> Without(HashSet<string> kind) =>
                new HashSet<string>((excludeContractUIDs ?? new HashSet<string>()).Concat(kind).Concat(paidTo).Concat(cannotSign), StringComparer.Ordinal);

            if (!Attestation.BtcLegacyFreeze.Active(height))
            {
                var fromOlder = PlanCandidates(exitAmountBtc, Without(watched), pool).FirstOrDefault(p => p != null);
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
            // A vault whose locks hold less than the smallest part altogether can pay no part, and is left out from the
            // start. Vaults were left out by the share the first plan gave them, and one that could pay the whole exit
            // by itself was dropped with the vault that could not.
            static decimal MinPart(string _) => Attestation.BtcWithdrawals.MinExitPartSats / Attestation.BtcAttestBalance.SatsPerBtc;
            foreach (var (vault, total) in VaultTotals(leftOut, pool))
                if (total < MinPart(vault)) leftOut.Add(vault);
            // As few vaults as can pay it: a miner fee each.
            foreach (var few in FewVaultCandidates(exitAmountBtc, leftOut, pool, MinPart))
            {
                if (few == null) continue;
                if (TooSmall(few).Count == 0) return few;
                if (Rebalanced(few, MinPart, pool) is { } lifted && TooSmall(lifted).Count == 0) return lifted;
            }
            while (true)
            {
                List<PoolUnlockAllocation>? first = null;
                foreach (var candidate in PlanCandidates(exitAmountBtc, leftOut, pool))
                {
                    if (candidate == null) continue;
                    first ??= candidate;
                    if (TooSmall(candidate).Count == 0) return candidate;
                    if (Rebalanced(candidate, MinPart, pool) is { } moved && TooSmall(moved).Count == 0) return moved;
                }
                if (first == null) return null;
                foreach (var vault in TooSmall(first)) leftOut.Add(vault);
            }
        }

        /// <summary>
        /// A plan with a vault's part under its minimum (<paramref name="minOf"/>: an exit's smallest part, or what an
        /// unlock must give its destination there), made valid by moving the shortfall to that vault from another vault
        /// whose part stays at or above its own minimum. A plan of one vault's whole locks and a little of the next was
        /// given up although such a split existed (fifth review, round 29). The shortfall is taken from as many other
        /// vaults as it needs, each down to its own minimum and no further: one vault had to spare it all, and three
        /// vaults of 30,000 sats could not pay an exit of 80,000 although 27,000, 27,000 and 26,000 could. The short vault
        /// draws on its planned locks first, then on its other locks in queue order: held to the locks already planned, a
        /// vault whose next lock would have lifted it was given up. In whole satoshis. Null when no such move is found,
        /// or the plan would not fit one transaction.
        /// </summary>
        private static List<PoolUnlockAllocation>? Rebalanced(List<PoolUnlockAllocation> plan, Func<string, decimal> minOf, List<VBTCBridgeLockState> pool)
        {
            const decimal sat = 0.00000001M;
            static decimal Up(decimal btc) => Math.Ceiling(btc / sat) * sat;
            static decimal Down(decimal btc) => Math.Floor(btc / sat) * sat;
            var available = pool
                .Where(l => Nameable(l.LockId) && NameableText(l.SmartContractUID)).ToList();
            var remaining = available
                .GroupBy(l => l.LockId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Math.Round(g.First().RemainingAmount, 8, MidpointRounding.ToZero), StringComparer.Ordinal);
            var moved = plan.Select(a => new PoolUnlockAllocation { LockId = a.LockId, UnlockAmount = a.UnlockAmount, SmartContractUID = a.SmartContractUID }).ToList();
            decimal Share(string vault) => moved.Where(a => a.SmartContractUID == vault).Sum(a => a.UnlockAmount);
            foreach (var small in moved.Select(a => a.SmartContractUID).Distinct(StringComparer.Ordinal).Where(v => Share(v) < minOf(v)).ToList())
            {
                var need = Up(minOf(small) - Share(small));
                var planned = moved.Where(a => a.SmartContractUID == small).Select(a => a.LockId).ToHashSet(StringComparer.Ordinal);
                var unplanned = available.Where(l => l.SmartContractUID == small && !planned.Contains(l.LockId) && remaining[l.LockId] > 0M)
                    .GroupBy(l => l.LockId, StringComparer.Ordinal).Select(g => g.First()).ToList();
                var room = moved.Where(a => a.SmartContractUID == small).Sum(a => (remaining.TryGetValue(a.LockId, out var r) ? r : a.UnlockAmount) - a.UnlockAmount)
                    + unplanned.Sum(l => remaining[l.LockId]);
                if (room < need) return null;
                var donors = moved.Select(a => a.SmartContractUID).Distinct(StringComparer.Ordinal)
                    .Where(v => v != small).Select(v => (Vault: v, Spare: Down(Share(v) - minOf(v)))).Where(d => d.Spare > 0M).ToList();
                if (donors.Sum(d => d.Spare) < need) return null;
                var take = need;
                foreach (var (donor, spare) in donors)
                {
                    var fromDonor = Math.Min(take, spare);
                    var left = fromDonor;
                    foreach (var a in moved.Where(a => a.SmartContractUID == donor).Reverse())
                    {
                        var part = Math.Min(left, a.UnlockAmount);
                        a.UnlockAmount -= part;
                        left -= part;
                        if (left == 0M) break;
                    }
                    take -= fromDonor;
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
                foreach (var l in unplanned)
                {
                    if (give == 0M) break;
                    var part = Math.Min(give, remaining[l.LockId]);
                    moved.Add(new PoolUnlockAllocation { LockId = l.LockId, UnlockAmount = part, SmartContractUID = small });
                    give -= part;
                }
                if (give != 0M) return null;
            }
            return PlanBytes(moved) <= MaxPlanBytes ? moved : null;
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
            IReadOnlyList<CasterConsensusVote>? casterConsensusVotes = null,
            string? vfxCreditAddress = null)
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

                // VfxCreditAddress, the address VfxDestinationAddress names, is what the unlock credits from
                // Globals.PoolUnlockCreditsAddressHeight; without it the payload is written as before.
                var txData = string.IsNullOrEmpty(vfxCreditAddress)
                    ? JsonConvert.SerializeObject(new
                    {
                        Function = "VBTCBridgePoolUnlock()",
                        TotalAmount = totalAmount,
                        TotalAmountSats = totalAmountSats,
                        VfxDestinationAddress = vfxDestinationAddress,
                        ExitBurnTxHash = exitBurnTxHash,
                        Allocations = allocations,
                        CasterConsensusVotes = casterConsensusVotes
                    })
                    : JsonConvert.SerializeObject(new
                    {
                        Function = "VBTCBridgePoolUnlock()",
                        TotalAmount = totalAmount,
                        TotalAmountSats = totalAmountSats,
                        VfxDestinationAddress = vfxDestinationAddress,
                        VfxCreditAddress = vfxCreditAddress,
                        ExitBurnTxHash = exitBurnTxHash,
                        Allocations = allocations,
                        CasterConsensusVotes = casterConsensusVotes
                    });

                var poolUnlockTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = signerAddress,
                    ToAddress = signerAddress,
                    Amount = VerifiedXCore.Services.LedgerIntegrityRules.SpellForNextBlock(0M, legacy: 0.00M),
                    Fee = VerifiedXCore.Services.LedgerIntegrityRules.SpellForNextBlock(0M, legacy: 0.00M),
                    Nonce = AccountStateTrei.GetNextNonce(signerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK,
                    Data = txData
                };

                poolUnlockTx.Fee = VerifiedXCore.Services.LedgerIntegrityRules.SpellForNextBlock(0M, legacy: 0.00M);

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
                    if (await TransactionData.SubmitOwnTransaction(poolUnlockTx, signerAddress, poolUnlockTx.Fee + poolUnlockTx.Amount) is { } notSent)
                    {
                        SCLogUtility.Log($"vBTC V2 Bridge Pool Unlock TX not sent: {notSent}", "BridgePoolUnlockService.CreateBridgePoolUnlockTx()");
                        return (false, notSent);
                    }

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
