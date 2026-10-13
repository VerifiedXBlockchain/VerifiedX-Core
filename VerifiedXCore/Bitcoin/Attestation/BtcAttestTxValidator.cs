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
        public static async Task<(bool, string)> Verify(Transaction tx, long height, bool blockDownloads, bool admission = false)
        {
            if (!BtcAttestRules.Recording(height))
                return (false, "Bitcoin attestation transactions are not accepted before the recording height.");

            // ── The envelope: no sender, no value, no fee ────────────────────────────────────────────────────
            if (tx.FromAddress != BtcAttestRules.SystemAddress || tx.ToAddress != BtcAttestRules.SystemAddress)
                return (false, $"A Bitcoin attestation transaction is from and to {BtcAttestRules.SystemAddress}.");
            if (tx.Amount != 0M || tx.Fee != 0M || tx.Nonce != 0)
                return (false, "A Bitcoin attestation transaction carries no amount, no fee and nonce 0.");
            if (tx.Signature != BtcAttestRules.SignatureSentinel)
                return (false, "A Bitcoin attestation transaction carries the attestation sentinel as its signature.");
            if (tx.UnlockTime != null)
                return (false, "A Bitcoin attestation transaction has no unlock time.");
            if (string.IsNullOrEmpty(tx.Data))
                return (false, "A Bitcoin attestation transaction carries data.");
            if (!TransactionValidatorService.VerifyTXSize(tx, BtcAttestRules.MaxReportTxBytes))
                return (false, $"This transaction is too large. Max size allowed is {BtcAttestRules.MaxReportTxBytes / 1024} kb.");
            if (!string.Equals(tx.Hash, tx.GetHash(), StringComparison.Ordinal))
                return (false, "This transaction's hash is not the hash of its content.");

            if (!blockDownloads)
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
                    var outcome = BtcReportVerifier.VerifyRewind(rewind, height);
                    if (!outcome.Ok)
                        return (false, $"Bitcoin rewind refused: {outcome.Error}");
                    if (admission && EarlierCopyInMempool(tx, outcome.ReportHash, height))
                        return (false, "This Bitcoin rewind is already in the mempool.");
                    if (admission) DropLaterCopies(tx, outcome.ReportHash);
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

        private static bool EarlierCopyInMempool(Transaction tx, string reportHash, long height)
        {
            try
            {
                var type = tx.TransactionType;
                var pending = TransactionData.GetPool().Find(x => x.TransactionType == type).ToList();
                foreach (var other in pending)
                {
                    if (string.Equals(other.Hash, tx.Hash, StringComparison.Ordinal)) continue;
                    var earlier = other.Timestamp < tx.Timestamp
                        || (other.Timestamp == tx.Timestamp && string.CompareOrdinal(other.Hash, tx.Hash) < 0);
                    if (!earlier) continue;
                    if (BtcReport.TryParse(other.Data, out var held, out _) && held != null && held.Hash() == reportHash
                        && BtcReportVerifier.CheckSignatures(held, reportHash, height) == null)
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
        }

        public const string OneReportReason = "A block carries at most one Bitcoin report or rewind.";
        public const string OneBumpReason = "A block carries at most one fee bump for a withdrawal.";

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
            // A rewind takes the report's place in a block: each is judged against the state before the block.
            if (tx.TransactionType != TransactionType.VBTC_V2_BTC_REPORT && tx.TransactionType != TransactionType.VBTC_V2_BTC_REORG) return (true, "");
            if (state.ReportHash != null) return (false, OneReportReason);
            state.ReportHash = tx.Hash;
            return (true, "");
        }

        /// <summary>Block building: the first report and the first fee bump of each withdrawal stay; later ones wait in the mempool for the next block.</summary>
        public static List<Transaction> KeepOnePerBlock(List<Transaction> approved)
        {
            var state = new State();
            return approved.Where(tx => TryRegister(tx, state).Ok).ToList();
        }
    }
}
