using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Admission and block-validation rules for the attestation transactions (plan sections 3.1 and 6). These
    /// transactions have no sender: their authority is the validator signatures inside the data. Everything here reads
    /// chain state only, so the mempool, the block builder and block validation always agree.
    /// </summary>
    public static class BtcAttestTxValidator
    {
        /// <summary>Builds the transaction that carries <paramref name="canonicalData"/> (a report's canonical text).</summary>
        public static Transaction Create(TransactionType type, string canonicalData, long? timestamp = null)
        {
            var tx = new Transaction
            {
                Timestamp = timestamp ?? TimeUtil.GetTime(),
                FromAddress = BtcAttestRules.SystemAddress,
                ToAddress = BtcAttestRules.SystemAddress,
                Amount = 0.0M, // one decimal place, like every transaction: the hash text survives a JSON round trip
                Fee = 0.0M,
                Nonce = 0,
                TransactionType = type,
                Data = canonicalData,
                Signature = BtcAttestRules.SignatureSentinel,
            };
            tx.Build();
            return tx;
        }

        /// <param name="height">The block's height in block validation; the height it would be mined at (tip + 1) at admission.</param>
        /// <param name="admission">True when the transaction is being judged for the mempool or for a block proposal,
        /// false in block validation. Only admission reads the mempool.</param>

        /// <summary>The envelope every attestation transaction has: no sender, no value, no fee, the sentinel signature. Null when it has it.</summary>
        internal static string? EnvelopeError(Transaction tx)
        {
            if (tx.FromAddress != BtcAttestRules.SystemAddress || tx.ToAddress != BtcAttestRules.SystemAddress)
                return $"A Bitcoin attestation transaction is from and to {BtcAttestRules.SystemAddress}.";
            if (tx.Amount != 0M || tx.Fee != 0M || tx.Nonce != 0)
                return "A Bitcoin attestation transaction carries no amount, no fee and nonce 0.";
            if (tx.Signature != BtcAttestRules.SignatureSentinel)
                return "A Bitcoin attestation transaction carries the attestation sentinel as its signature.";
            if (tx.UnlockTime != null)
                return "A Bitcoin attestation transaction has no unlock time.";
            return null;
        }

        public static async Task<(bool, string)> Verify(Transaction tx, long height, bool blockDownloads, bool admission = false, long? blockTimestamp = null)
        {
            if (!BtcAttestRules.Recording(height))
                return (false, "Bitcoin attestation transactions are not accepted before the recording height.");

            // ── The envelope: no sender, no value, no fee ────────────────────────────────────────────────────
            var envelopeError = EnvelopeError(tx);
            if (envelopeError != null)
                return (false, envelopeError);
            if (string.IsNullOrEmpty(tx.Data))
                return (false, "A Bitcoin attestation transaction carries data.");
            if (!TransactionValidatorService.VerifyTXSize(tx, BtcAttestRules.MaxReportTxBytes))
                return (false, $"This transaction is too large. Max size allowed is {BtcAttestRules.MaxReportTxBytes / 1024} kb.");
            if (!string.Equals(tx.Hash, tx.GetHash(), StringComparison.Ordinal))
                return (false, "This transaction's hash is not the hash of its content.");

            var blockTime = admission ? null : LedgerIntegrityRules.BlockTime(height, blockTimestamp);
            if (blockTime != null)
            {
                if (LedgerIntegrityRules.TimestampOutsideItsBlock(tx, blockTime.Value))
                    return (false, LedgerIntegrityRules.TimestampOutsideItsBlockMessage);
            }
            else if (!blockDownloads)
            {
                if (await TransactionData.IsTxTimestampStale(tx, allowHistorical: false))
                    return (false, "The timestamp of this transaction is too old or too far in the future.");
            }

            // A report's transaction has no sender and its timestamp is anybody's to choose, and the mempool keeps the
            // earliest copy of a report. A copy timed at the far edge of what is still accepted would be the earliest,
            // push the honest copies out, and itself be too old to mine a second later. So a copy enters a mempool only
            // while it is fresh; one already held is judged as before (it was fresh when it came).
            if (admission && tx.Timestamp < TimeUtil.GetTime() - MaxAgeAtAdmissionSeconds && !HeldInMempool(tx))
                return (false, $"A Bitcoin attestation transaction is admitted within {MaxAgeAtAdmissionSeconds} seconds of its timestamp.");

            switch (tx.TransactionType)
            {
                case TransactionType.VBTC_V2_BTC_REPORT:
                {
                    if (!BtcReport.TryParse(tx.Data, out var report, out var parseReason) || report == null)
                        return (false, parseReason ?? "The Bitcoin report does not parse.");
                    // A later copy of a report the mempool holds is refused before it is verified: anyone could wrap a
                    // report waiting to be mined anew and have each copy's signatures checked (fourth review, round 5).
                    if (admission && EarlierCopyInMempool(tx, report.Hash(), height, recheckSignatures: false))
                        return (false, "This Bitcoin report is already in the mempool.");
                    var outcome = BtcReportVerifier.Verify(report, height);
                    if (!outcome.Ok)
                        return (false, $"Bitcoin report refused: {outcome.Error}");
                    if (admission && EarlierCopyInMempool(tx, outcome.ReportHash, height))
                        return (false, "This Bitcoin report is already in the mempool.");
                    if (admission) DropLaterCopies(tx, outcome.ReportHash);
                    return (true, "Transaction has been verified.");
                }
                case TransactionType.VBTC_V2_BTC_REORG:
                {
                    if (!BtcReport.TryParse(tx.Data, out var rewind, out var parseReason) || rewind == null)
                        return (false, parseReason ?? "The Bitcoin rewind does not parse.");
                    // A copy of a rewind the mempool holds is refused before it is verified, as a report's is: each copy
                    // cost a full check of its signatures and up to 500 headers (fourth review, round 10).
                    if (admission && EarlierCopyInMempool(tx, rewind.Hash(), height, recheckSignatures: false))
                        return (false, "This Bitcoin rewind is already in the mempool.");
                    var outcome = BtcReportVerifier.VerifyRewind(rewind, height);
                    if (!outcome.Ok)
                        return (false, $"Bitcoin rewind refused: {outcome.Error}");
                    if (admission && EarlierCopyInMempool(tx, outcome.ReportHash, height))
                        return (false, "This Bitcoin rewind is already in the mempool.");
                    if (admission) DropLaterCopies(tx, outcome.ReportHash);
                    return (true, "Transaction has been verified.");
                }
                case TransactionType.VBTC_V2_VAULT_REKEY:
                {
                    if (!BtcRekey.TryParse(tx.Data, out var rekey, out var parseReason) || rekey == null)
                        return (false, parseReason ?? "The re-key does not parse.");
                    var refusal = BtcVaultKeys.Refusal(rekey, height, out _);
                    if (refusal != null)
                        return (false, $"Re-key refused: {refusal}");
                    if (admission && RekeyInMempool(rekey.ContractUID, tx.Hash))
                        return (false, "A re-key of this vault is already in the mempool.");
                    return (true, "Transaction has been verified.");
                }
                default:
                    return (false, "This Bitcoin attestation transaction type is not enabled yet.");
            }
        }

        /// <summary>
        /// A report carries no fee and no sender signature, and the same signed report can be wrapped in any number of
        /// transactions (another timestamp, another choice among the signatures). The mempool keeps one per report: the
        /// earliest by timestamp, then by hash. Every node applies the same order, so they settle on the same copy.
        /// A copy counts only while its own signatures are good: the content is the same report just verified, so that
        /// is all that can differ, and a copy that cannot be mined must not hold a good one out.
        /// A mempool rule only: block validation never reads the mempool.
        /// </summary>
        /// <summary>
        /// The other half of "one per report": when a copy is admitted, the copies of the same report that come after
        /// it in that order are taken out of this node's mempool. They used to stay, so the same signed report wrapped
        /// again and again (each time a second earlier, or with a lower hash) piled up: fee-free, rated highest, taking
        /// the room of a block's proposal and, near the mempool's limit, of ordinary transactions.
        /// </summary>
        private static void DropLaterCopies(Transaction tx, string reportHash)
        {
            try
            {
                var type = tx.TransactionType;
                var pool = TransactionData.GetPool();
                foreach (var other in pool.Find(x => x.TransactionType == type).ToList())
                {
                    if (string.Equals(other.Hash, tx.Hash, StringComparison.Ordinal)) continue;
                    var later = other.Timestamp > tx.Timestamp
                        || (other.Timestamp == tx.Timestamp && string.CompareOrdinal(other.Hash, tx.Hash) > 0);
                    if (!later) continue;
                    if (BtcReport.TryParse(other.Data, out var held, out _) && held != null && held.Hash() == reportHash)
                        pool.DeleteManySafe(x => x.Hash == other.Hash);
                }
            }
            catch { }
        }

        /// <summary>
        /// Whether a re-key of this vault (any one: several leaders may have finished a ceremony) is already waiting in
        /// this node's mempool. The first kept is the one mined; a later one would be refused in its block anyway.
        /// </summary>
        public static bool RekeyInMempool(string vaultUid, string? exceptHash = null)
        {
            try
            {
                foreach (var held in TransactionData.GetPool().Find(x => x.TransactionType == TransactionType.VBTC_V2_VAULT_REKEY).ToList())
                    if (held.Hash != exceptHash && BtcRekey.TryParse(held.Data, out var other, out _) && other != null
                        && string.Equals(other.ContractUID, vaultUid, StringComparison.Ordinal))
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>How old a report's transaction may be when a mempool first takes it, in seconds.</summary>
        public const int MaxAgeAtAdmissionSeconds = 120;

        private static bool HeldInMempool(Transaction tx)
        {
            try { return TransactionData.GetPool().Exists(x => x.Hash == tx.Hash); }
            catch { return false; }
        }

        /// <summary>Whether a copy of this report (or rewind) is waiting in this node's mempool.</summary>
        public static bool ReportInMempool(string reportHash)
        {
            try
            {
                foreach (var held in TransactionData.GetPool().Find(x => x.TransactionType == TransactionType.VBTC_V2_BTC_REPORT || x.TransactionType == TransactionType.VBTC_V2_BTC_REORG).ToList())
                    if (BtcReport.TryParse(held.Data, out var report, out _) && report != null && report.Hash() == reportHash)
                        return true;
            }
            catch { }
            return false;
        }


        /// <summary>Without checking signatures: enough distinct signers of the report's epoch (which must still count) for the copy to stand.</summary>

        private static bool CarriesEnoughSigners(BtcReport held, long height)

        {

            if (held.SignerEpoch < BtcAttestRules.EpochOf(height) - 1) return false;

            var set = BtcAttestStore.SignerSet(held.SignerEpoch);

            if (set == null) return false;

            var members = new HashSet<string>(set.Addresses, StringComparer.Ordinal);

            var signers = (held.Signatures ?? new List<BtcReportSignature>()).Where(s => s?.ValidatorAddress != null && members.Contains(s.ValidatorAddress))

                .Select(s => s.ValidatorAddress).Distinct(StringComparer.Ordinal).Count();

            return signers >= BtcAttestRules.RequiredSignatures(set.Addresses.Count);

        }

        private static bool EarlierCopyInMempool(Transaction tx, string reportHash, long height, bool recheckSignatures = true)
        {
            try
            {
                var type = tx.TransactionType;
                var pending = TransactionData.GetPool().Find(x => x.TransactionType == type).ToList();
                foreach (var other in pending)
                {
                    if (string.Equals(other.Hash, tx.Hash, StringComparison.Ordinal)) continue;
                    // Before verification a held copy also wins a tie of timestamps: variants of one report with other
                    // signature subsets at one timestamp each cost a full check (fourth review, round 6).
                    var earlier = other.Timestamp < tx.Timestamp
                        || (other.Timestamp == tx.Timestamp && (!recheckSignatures || string.CompareOrdinal(other.Hash, tx.Hash) < 0));
                    if (!earlier) continue;
                    if (BtcReport.TryParse(other.Data, out var held, out _) && held != null && held.Hash() == reportHash
                        && (recheckSignatures ? BtcReportVerifier.CheckSignatures(held, reportHash, height) == null
                                              : CarriesEnoughSigners(held, height))) // its signatures were verified when it was taken in
                        return true;
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// What a block may carry of these transactions. A report is verified against the state before the block; two reports
    /// in one block would each be valid on their own and the second could find the first has moved the state under it.
    /// One report per block keeps "valid when validated" equal to "valid when applied" (a Bitcoin block arrives every
    /// ten minutes, a VFX block every twelve seconds).
    /// </summary>
    public static class BtcAttestBlockRules
    {
        public sealed class State
        {
            public string? ReportHash;
            /// <summary>The withdrawals a fee bump of this block has already raised.</summary>
            public readonly HashSet<string> BumpedWithdrawals = new(StringComparer.Ordinal);
            /// <summary>The vaults a re-key of this block has already moved.</summary>
            public readonly HashSet<string> RekeyedVaults = new(StringComparer.Ordinal);
        }

        public const string OneReportReason = "A block carries at most one Bitcoin report or rewind.";
        public const string OneBumpReason = "A block carries at most one fee bump for a withdrawal.";
        public const string OneRekeyReason = "A block carries at most one re-key for a vault.";

        /// <summary>
        /// The same holds for fee bumps of one withdrawal: each is judged against the withdrawal's newest transaction
        /// before the block, and the second would find the first has replaced it.
        /// </summary>
        public static (bool Ok, string Reason) TryRegister(Transaction tx, State state)
        {
            if (tx.TransactionType == TransactionType.VBTC_V2_COIN_PIN)
            {
                var bumped = BtcWithdrawals.FeeBumpRef(tx);
                return bumped == null || state.BumpedWithdrawals.Add(bumped) ? (true, "") : (false, OneBumpReason);
            }
            // Each re-key is judged against the vault's key before the block; a second would find it replaced.
            if (tx.TransactionType == TransactionType.VBTC_V2_VAULT_REKEY)
            {
                var vault = BtcRekey.TryParse(tx.Data, out var rekey, out _) && rekey != null ? rekey.ContractUID : tx.Hash;
                return state.RekeyedVaults.Add(vault) ? (true, "") : (false, OneRekeyReason);
            }
            // A rewind takes the report's place in a block: each is judged against the state before the block.
            if (tx.TransactionType != TransactionType.VBTC_V2_BTC_REPORT && tx.TransactionType != TransactionType.VBTC_V2_BTC_REORG) return (true, "");
            if (state.ReportHash != null) return (false, OneReportReason);
            state.ReportHash = tx.Hash;
            return (true, "");
        }

        /// <summary>
        /// Block building: the first report and the first fee bump of each withdrawal stay; later ones wait in the mempool
        /// for the next block, and so do their sender's later transactions, so the proposal has no nonce gap (as the other
        /// builder filters do; reports and re-keys have no sender).
        /// </summary>
        public static List<Transaction> KeepOnePerBlock(List<Transaction> approved)
        {
            var state = new State();
            var blockedSenders = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<Transaction>();
            foreach (var tx in approved)
            {
                // Reports, rewinds and re-keys share one system name and carry no nonce: one held back says nothing of the others.
                var hasSender = !string.IsNullOrEmpty(tx.FromAddress) && !BtcAttestTx.IsAttestationType(tx.TransactionType);
                if (hasSender && blockedSenders.Contains(tx.FromAddress)) continue;
                if (TryRegister(tx, state).Ok) result.Add(tx);
                else if (hasSender) blockedSenders.Add(tx.FromAddress);
            }
            return result;
        }
    }
}
