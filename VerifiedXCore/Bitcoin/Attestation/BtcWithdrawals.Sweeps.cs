using LiteDB;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Moving a vault's coins from a key that was replaced to its current key (docs/PLAN-vBTC-Bitcoin-Attestation.md,
    /// 8.8). A sweep takes the path of a withdrawal: the transaction is worked out from state and pinned, the old key's
    /// holders sign exactly that one, and Bitcoin showing it settles the sweep, its output a coin of the vault under the
    /// new key.
    ///
    /// <list type="bullet">
    /// <item><b>When.</b> When a key is replaced, and every <see cref="BtcAttestRules.TakeRetryBlocks"/> blocks after
    /// that for whatever the old key holds and no transaction holds: change of a withdrawal fixed before the re-key, a
    /// deposit somebody still sent to the old address.</item>
    /// <item><b>What.</b> The old key's free coins (up to <see cref="BtcCanonicalWithdrawal.MaxInputs"/>, largest first)
    /// in one transaction with one output to the current key, at <see cref="SweepFeeRate"/>. A withdrawal fixed on the
    /// old key's coins that has stopped moving gives its coins up to the sweep as it would to another withdrawal: the
    /// two conflict on Bitcoin, and if the sweep confirms the withdrawal is void and refunded in full.</item>
    /// <item><b>Its fee</b> is raised by rule like a bridge exit's: doubled after <see cref="BtcAttestRules.StuckPinBlocks"/>
    /// blocks unconfirmed. Nobody can cancel or release a sweep.</item>
    /// <item><b>Who pays.</b> The Bitcoin fee leaves the vault; no ledger row moves, so it falls on the vault's owner's
    /// figure, which is what is left of the vault's Bitcoin after every holder's claim. It is recorded on the vault's
    /// totals (<see cref="VaultDepositTotalRow.SweepFeeSats"/>).</item>
    /// </list>
    /// </summary>
    public static partial class BtcWithdrawals
    {
        /// <summary>The lowest fee rate a sweep is fixed at, sat/vB: a bridge exit's.</summary>
        public const long SweepFeeRate = ExitFeeRate;

        /// <summary>The highest fee rate a sweep is first fixed at, and the highest it is raised to, sat/vB.</summary>
        public const long SweepMaxFeeRate = 1_000;

        /// <summary>VFX blocks a sweep's newest transaction may wait unconfirmed before its fee is doubled (about 72 minutes, some seven Bitcoin blocks).</summary>
        public const int SweepStuckBlocks = 360;

        /// <summary>
        /// The fee rate a new sweep is fixed at: what withdrawals requested in the last thousand blocks pay (the middle
        /// one), between <see cref="SweepFeeRate"/> and <see cref="SweepMaxFeeRate"/>. Requesters choose rates that get
        /// their Bitcoin out, so this follows Bitcoin's fee market from chain state alone; a sweep fixed at a rate
        /// Bitcoin ignores would leave the old key's coins there while its holders keep leaving.
        /// </summary>
        public static long SweepRateAt(long vfxHeight, LiteDatabase? db = null)
        {
            // Only the last thousand blocks' rows are read (indexed): every settled row ever kept was read before, for
            // every re-keyed vault every 30 blocks (fourth review).
            var since = vfxHeight - 1_000;
            var rates = (db ?? BtcAttestStore.Db).GetCollection<VaultWithdrawalRow>(BtcAttestStore.Withdrawals).Find(x => x.RequestVfxHeight > since)
                .Where(w => w.State == VaultWithdrawalState.Pinned || w.State == VaultWithdrawalState.Settled || w.State == VaultWithdrawalState.Waiting)
                .Where(w => !w.IsSweep && !w.IsBridgeExit)
                .Select(w => w.Pins.LastOrDefault(p => !p.Dead)?.FeeRate is > 0 and var r ? r : w.FeeRate)
                .OrderBy(r => r).ToList();
            var middle = rates.Count == 0 ? SweepFeeRate : rates[rates.Count / 2];
            return Math.Clamp(middle, SweepFeeRate, SweepMaxFeeRate);
        }

        /// <summary>
        /// The sats of a withdrawal or exit part of <paramref name="amountSats"/> that stay in the vault to make good what
        /// sweeps cost beyond the owner's figure. The owner's figure is what is left of the vault's Bitcoin after every
        /// other claim; a sweep's fee falls on it, and when the owner holds less than the fees, the vault holds less than
        /// its holders' claims by the difference (the shortfall). Each payment then leaves its share of the shortfall in
        /// the vault (its amount over all claims), and its requester's completed withdrawal is added back to the owner's
        /// figure in full as before, so the shortfall closes as the vault pays out and nobody is left unpaid at the end.
        /// 0 while the owner's figure covers it (always, unless the owner has given away everything).
        /// </summary>
        internal static long ShortfallShare(string scUID, long amountSats, LiteDatabase? db = null)
        {
            if (amountSats <= 0 || BtcAttestStore.Total(scUID, db).SweepFeeSats <= 0) return 0;
            var state = SmartContractStateTrei.GetSmartContractState(scUID);
            if (state == null || string.IsNullOrEmpty(state.OwnerAddress)) return 0;
            var owner = VBTCService.AttestedOwnerSpendable(state, state.OwnerAddress, StateWriteContext.StampHeight > 0 ? StateWriteContext.StampHeight : (Globals.LastBlock?.Height ?? 0));
            if (owner >= 0M) return 0;
            var shortfall = (long)Math.Ceiling(-owner * BtcAttestBalance.SatsPerBtc);
            var held = BtcAttestStore.Total(scUID, db).HeldSats;
            var claims = held + shortfall;
            if (claims <= 0) return 0;
            var share = (long)Math.Ceiling((decimal)shortfall * amountSats / claims);
            return Math.Min(share, shortfall);
        }

        /// <summary>Every sweep's reference starts with this, and the signing ceremony is told it as the withdrawal it signs for.</summary>
        public const string SweepPrefix = "sweep:";

        public static bool IsSweepReference(string? reference) => reference != null && reference.StartsWith(SweepPrefix, StringComparison.Ordinal);

        /// <summary>A sweep's reference: the vault, the generation it moves from, the block, and a number for a second sweep in one block.</summary>
        private static string SweepRefOf(string scUID, int generation, long vfxHeight, int n) => $"{SweepPrefix}{scUID}|g{generation}@{vfxHeight}#{n}";

        /// <summary>
        /// Fixes a sweep for every replaced key of the vault that holds free coins. A function of state and height: every
        /// node fixes the same transactions at the same block.
        /// </summary>
        public static void PlanSweeps(string? scUID, long vfxHeight, LiteDatabase? db = null)
        {
            if (string.IsNullOrEmpty(scUID)) return;
            var keys = BtcAttestStore.ScriptRowsForVault(scUID, db);
            if (keys.Count < 2) return;
            var current = keys[^1];
            long? market = null;
            foreach (var old in keys.Take(keys.Count - 1))
            {
                // A replaced key with no free coin and no coin a withdrawal holds has nothing a sweep could take: the
                // same answer as building one, without reading the market (most re-keyed vaults, most of the time).
                if (!KeyHoldsUnspent(scUID, old, db)) continue;
                var marketRate = market ??= SweepRateAt(vfxHeight, db);
                // A withdrawal on the replaced key that has gone unconfirmed past the stopped window gives its coins up
                // at double its own rate, even when that rate is above the market's: otherwise one that never confirms
                // keeps its coins (with the vault's change) under a key whose holders are leaving. Only when it is
                // actually taken; free coins alone go at the market rate.
                var rate = marketRate;
                var stalled = StalledOnKeyRate(scUID, old, vfxHeight, db);
                List<string> takenFrom = new();
                string? reason = null;
                var tx = stalled > marketRate ? BtcCanonicalWithdrawal.BuildSweep(old, current, stalled, vfxHeight, out takenFrom, out reason, db) : null;
                if (tx != null && takenFrom.Count > 0) rate = stalled;
                else tx = BtcCanonicalWithdrawal.BuildSweep(old, current, rate, vfxHeight, out takenFrom, out reason, db);
                if (tx == null) continue;
                var n = 0;
                string reference;
                do reference = SweepRefOf(scUID, old.Generation, vfxHeight, n++); while (BtcAttestStore.Withdrawal(reference, db) != null);
                var row = new VaultWithdrawalRow
                {
                    Ref = reference, SmartContractUID = scUID, RequestHash = "", Requester = "", AmountSats = tx.InputSats,
                    DestinationScriptHex = current.ScriptHex, FeeRate = rate, RequestVfxHeight = vfxHeight,
                    State = VaultWithdrawalState.Waiting, IsSweep = true, SweepFromScriptHex = old.ScriptHex,
                };
                Pin(row, tx, vfxHeight, db);
                SCLogUtility.Log($"Vault {scUID}: {tx.Inputs.Count} coin(s) of key generation {old.Generation} ({tx.InputSats} sats) fixed on Bitcoin transaction {tx.Txid} to the current key (generation {current.Generation}), {tx.FeeSats} sats of fee"
                    + (takenFrom.Count > 0 ? $"; taken from {string.Join(", ", takenFrom)}, which had stopped moving." : "."), "BtcWithdrawals.PlanSweeps()");
            }
        }

        /// <summary>Whether a key of the vault still holds a free coin, or a coin a withdrawal (not a sweep) has fixed.</summary>
        internal static bool KeyHoldsUnspent(string scUID, VaultDepositScriptRow key, LiteDatabase? db)
        {
            if (BtcAttestStore.FreeCoinsOf(scUID, db).Any(c => BtcCanonicalWithdrawal.IsUnder(c, key, db))) return true;
            return BtcAttestStore.WithdrawalsOf(scUID, VaultWithdrawalState.Pinned, db).Where(r => !r.IsSweep)
                .SelectMany(r => r.Pins.Where(p => !p.Dead).SelectMany(p => p.Inputs))
                .Any(i => BtcAttestStore.Coin(i, db) is { Status: VaultCoinStatus.Pinned } c && BtcCanonicalWithdrawal.IsUnder(c, key, db));
        }

        /// <summary>
        /// The rate at which a sweep of <paramref name="old"/> takes the coins of a withdrawal fixed on that key that has
        /// gone <see cref="BtcAttestRules.StuckPinBlocks"/> blocks without confirming (counted as for a takeover): double
        /// the highest such withdrawal's rate, at most <see cref="SweepMaxFeeRate"/>. 0 when there is none, or when double
        /// its rate would pass the ceiling (it pays enough to be confirmed).
        /// </summary>
        internal static long StalledOnKeyRate(string scUID, VaultDepositScriptRow old, long vfxHeight, LiteDatabase? db)
        {
            long best = 0;
            foreach (var row in BtcAttestStore.WithdrawalsOf(scUID, VaultWithdrawalState.Pinned, db))
            {
                if (row.IsSweep || row.IsBridgeExit) continue;
                var newest = row.Pins.LastOrDefault(p => !p.Dead);
                if (newest == null || vfxHeight < MovingSince(row) + BtcAttestRules.StuckPinBlocks) continue;
                if (!newest.Inputs.Any(i => BtcAttestStore.ScriptRowOfCoin(BtcAttestStore.Coin(i, db), db)?.ScriptHex == old.ScriptHex)) continue;
                var doubled = 2 * (newest.FeeRate > 0 ? newest.FeeRate : row.FeeRate);
                if (doubled <= SweepMaxFeeRate) best = Math.Max(best, doubled);
            }
            return best;
        }

        /// <summary>Every <see cref="BtcAttestRules.TakeRetryBlocks"/> blocks: the sweeps of every vault with a replaced key.</summary>
        private static void PlanAllSweeps(long vfxHeight, LiteDatabase? db)
        {
            if (vfxHeight % BtcAttestRules.TakeRetryBlocks != 0) return;
            var vaults = (db ?? BtcAttestStore.Db).GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts)
                .Find(x => x.RetiredAtVfxHeight > 0).Select(x => x.SmartContractUID).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var vault in vaults)
                PlanSweeps(vault, vfxHeight, db);
        }

        /// <summary>As <see cref="MaySign"/>, for a sweep (its reference is what the ceremony is told it signs for).</summary>
        public static (bool Ok, string Reason) MaySignSweep(string reference, string scUID, string txid)
        {
            var row = BtcAttestStore.Withdrawal(reference);
            if (row == null || !row.IsSweep || !string.Equals(row.SmartContractUID, scUID, StringComparison.Ordinal))
                return (false, "This sweep is not recorded for the vault in state");
            return IsPinnedTransactionOf(reference, txid, Globals.LastBlock?.Height ?? 0);
        }

        /// <summary>A sweep is settled: Bitcoin shows its transaction, and its output is the vault's coin under the current key.</summary>
        private static void SettleSweep(VaultWithdrawalRow row, string txid, long vfxHeight, LiteDatabase? db)
        {
            var pin = row.Pins.FirstOrDefault(p => p.Txid == txid);
            if (pin != null) BtcAttestStore.AddSweepFee(row.SmartContractUID, pin.FeeSats, db);
            SCLogUtility.Log($"Vault {row.SmartContractUID}: sweep {row.Ref} settled by Bitcoin transaction {txid} at block {vfxHeight} ({pin?.FeeSats} sats of fee).", "BtcWithdrawals.SettleSweep()");
        }
    }
}
