using LiteDB;
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
        /// <summary>The fee rate a sweep is first fixed at, sat/vB: a bridge exit's.</summary>
        public const long SweepFeeRate = ExitFeeRate;

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
            foreach (var old in keys.Take(keys.Count - 1))
            {
                var tx = BtcCanonicalWithdrawal.BuildSweep(old, current, SweepFeeRate, vfxHeight, out var takenFrom, out var reason, db);
                if (tx == null) continue;
                var n = 0;
                string reference;
                do reference = SweepRefOf(scUID, old.Generation, vfxHeight, n++); while (BtcAttestStore.Withdrawal(reference, db) != null);
                var row = new VaultWithdrawalRow
                {
                    Ref = reference, SmartContractUID = scUID, RequestHash = "", Requester = "", AmountSats = tx.InputSats,
                    DestinationScriptHex = current.ScriptHex, FeeRate = SweepFeeRate, RequestVfxHeight = vfxHeight,
                    State = VaultWithdrawalState.Waiting, IsSweep = true, SweepFromScriptHex = old.ScriptHex,
                };
                Pin(row, tx, vfxHeight, db);
                SCLogUtility.Log($"Vault {scUID}: {tx.Inputs.Count} coin(s) of key generation {old.Generation} ({tx.InputSats} sats) fixed on Bitcoin transaction {tx.Txid} to the current key (generation {current.Generation}), {tx.FeeSats} sats of fee"
                    + (takenFrom.Count > 0 ? $"; taken from {string.Join(", ", takenFrom)}, which had stopped moving." : "."), "BtcWithdrawals.PlanSweeps()");
            }
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
