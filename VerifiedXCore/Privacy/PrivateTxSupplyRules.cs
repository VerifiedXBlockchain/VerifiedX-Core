using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Fund-loss audit item 1, stage 1 (Globals.PrivateTxSupplyRulesHeight). Consensus rules for ZK-authorized private
    /// transactions (unshield, private transfer) that need no proof system and are deterministic on every node:
    ///
    ///  - Supply floor: a transaction may not take more out of its shielded pool than the pool holds. The pool's
    ///    TotalShieldedSupply is maintained from the chain on every node (PrivateTxLedgerService), so this is consensus
    ///    state. Without it a forged unshield with no proof minted VFX from nothing.
    ///  - Structure: at least one nullifier (with its spent position) and a Merkle root. Both were optional, so a
    ///    payload with neither passed every check that keys off them.
    ///  - Binding: the private hash covers the payload but not the outer ToAddress/Amount, so an unshield's recipient
    ///    and amount could be changed on a relayed transaction without changing its hash. The outer fields must equal
    ///    the payload's transparent_output / transparent_amount, which the hash does cover.
    ///
    /// What this stage does NOT do: tie a nullifier to a real note. Until stage 2 (verified PLONK proofs) a forged
    /// unshield can still spend the pool down to zero, never below.
    /// </summary>
    public static class PrivateTxSupplyRules
    {
        public const string SupplyReasonPrefix = "Shielded pool supply";

        public static bool AppliesTo(Transaction tx, long height) =>
            tx != null && PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType) && height >= Globals.PrivateTxSupplyRulesHeight;

        /// <summary>The amount the transaction's apply subtracts from the shielded supply of <paramref name="payload"/>.Asset.</summary>
        public static decimal PoolDebit(Transaction tx, PrivateTxPayload payload)
        {
            var fee = payload.Fee ?? Globals.PrivateTxFixedFee;
            return tx.TransactionType switch
            {
                TransactionType.VFX_UNSHIELD => tx.Amount + fee,
                TransactionType.VFX_PRIVATE_TRANSFER => fee,
                TransactionType.VBTC_V2_UNSHIELD => payload.VbtcTransparentAmount ?? 0M,
                TransactionType.VBTC_V2_PRIVATE_TRANSFER => 0M,
                _ => 0M
            };
        }

        /// <summary>Stateless structure and binding rules. Null when the transaction passes.</summary>
        public static string? StructureError(Transaction tx, PrivateTxPayload payload)
        {
            if (payload.NullsB64 == null || payload.NullsB64.Count == 0)
                return "A private transaction must spend at least one note (nullifier).";
            if (payload.SpentCommitmentTreePositions == null || payload.SpentCommitmentTreePositions.Count != payload.NullsB64.Count)
                return "A private transaction must carry one spent tree position per nullifier.";
            if (string.IsNullOrWhiteSpace(payload.MerkleRootB64))
                return "A private transaction must carry the shielded pool Merkle root it spends against.";

            return OuterFieldsError(tx, payload);
        }

        /// <summary>
        /// The highest block of each network's history scanned for unshields whose outer fields differ from their payload
        /// (FundLossHistoryScanHarness.UnshieldBindingScan: mainnet 7 unshields, testnet 1, none differs). Raise it with a
        /// new scan at the release cut.
        /// </summary>
        public static long UnshieldBindingScannedThrough => Globals.IsTestNet ? 1_025_066L : 7_410_629L;

        /// <summary>
        /// An unshield's outer recipient and amount, which its hash does not cover, are its payload's. From the fund-loss
        /// height for every block (StructureError). Below it, only for a block at or below the scanned
        /// height, on whichever path judges it (round 20): whoever served blocks to a syncing node could change them and have it credit someone else (fifth
        /// review, round 18), and history up to there holds no unshield that differs. Not for live blocks below the gate:
        /// 8.x producers accept a relayed copy with another recipient, and refusing it split the upgrade (round 19).
        /// </summary>
        public static string? OuterFieldsError(Transaction tx, PrivateTxPayload payload)
        {
            if (tx.TransactionType == TransactionType.VFX_UNSHIELD || tx.TransactionType == TransactionType.VBTC_V2_UNSHIELD)
            {
                if (string.IsNullOrWhiteSpace(payload.TransparentOutput) || !string.Equals(payload.TransparentOutput, tx.ToAddress, StringComparison.Ordinal))
                    return "Unshield recipient must equal the payload's transparent_output.";
                if (tx.TransactionType == TransactionType.VFX_UNSHIELD && (payload.TransparentAmount == null || payload.TransparentAmount.Value != tx.Amount))
                    return "Unshield amount must equal the payload's transparent_amount.";
            }
            return null;
        }

        /// <summary>Supply floor against the committed pool supply. Null when the transaction passes.</summary>
        public static string? SupplyError(Transaction tx, PrivateTxPayload payload, decimal committedSupply)
        {
            var debit = PoolDebit(tx, payload);
            if (debit <= 0M)
                return null;
            if (debit > committedSupply)
                return $"{SupplyReasonPrefix}: {payload.Asset} pool holds {committedSupply}; this transaction takes {debit}.";
            return null;
        }

        public const string UnreadableStoreReason = SupplyReasonPrefix + ": privacy store unreadable; rebuild.";

        /// <summary>
        /// Committed shielded supply of an asset as the floor judges it at <paramref name="height"/>: the pool row's
        /// TotalShieldedSupply (0 when there is no row yet) plus, before the proof-rules epoch, the asset's entry in
        /// Globals.ShieldedSupplyCorrections, which adds back what the forged mainnet unshields subtracted from the counter.
        /// 0 when the store cannot be judged (see <see cref="TryCommittedSupply"/>), so no debit passes.
        /// </summary>
        public static decimal CommittedSupply(string asset, long height) =>
            TryCommittedSupply(asset, height, out var supply) ? supply : 0M;

        /// <summary>
        /// False when the pool row cannot be read, or is missing while a correction applies to the asset. The correction
        /// only makes sense on top of the chain's counter: added to a missing or unreadable row it would let the floor
        /// pass the whole corrected amount, so the floor refuses instead and the node needs a rebuild.
        /// </summary>
        public static bool TryCommittedSupply(string asset, long height, out decimal supply)
        {
            supply = 0M;
            ShieldedPoolState? row;
            try { row = ShieldedPoolService.GetState(asset); }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"{UnreadableStoreReason} Reading the {asset} pool row failed: {ex.Message}", "PrivateTxSupplyRules.TryCommittedSupply()");
                return false;
            }
            // The correction undoes the forged debits in the pre-epoch counter; the epoch pool starts from zero.
            if (PrivacyEpoch.ProofRulesActive(height))
            {
                supply = row?.TotalShieldedSupply ?? 0M;
                return true;
            }
            var corrected = Globals.ShieldedSupplyCorrections.TryGetValue(asset ?? "", out var correction);
            if (row == null && corrected)
            {
                ErrorLogUtility.LogError($"{UnreadableStoreReason} The {asset} pool row is missing.", "PrivateTxSupplyRules.TryCommittedSupply()");
                return false;
            }
            supply = (row?.TotalShieldedSupply ?? 0M) + (corrected ? correction : 0M);
            return true;
        }

        /// <summary>All rules of this stage for one transaction at <paramref name="height"/>; (true, "") when inert or passing.</summary>
        public static (bool ok, string message) Check(Transaction tx, PrivateTxPayload payload, long height)
        {
            if (!AppliesTo(tx, height))
                return (true, "");
            var structure = StructureError(tx, payload);
            if (structure != null)
                return (false, structure);
            if (PoolDebit(tx, payload) <= 0M)
                return (true, "");
            if (!TryCommittedSupply(payload.Asset, height, out var committed))
                return (false, UnreadableStoreReason);
            var supply = SupplyError(tx, payload, committed);
            if (supply != null)
                return (false, supply);
            return (true, "");
        }
    }
}
