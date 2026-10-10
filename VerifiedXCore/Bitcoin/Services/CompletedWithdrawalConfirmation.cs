using System.Collections.Concurrent;
using NBitcoin;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Fund-loss audit item 8 (validator-local). A vBTC V2 owner's spendable balance is the deposit address's confirmed
    /// BTC plus the owner ledger, which adds back every Completed withdrawal because its escrow debit is already
    /// reflected in the deposit once the Bitcoin transaction confirms. Between the COMPLETE transaction (sent right after
    /// broadcast; it accepts any 64-hex txid) and that confirmation, the deposit still holds the withdrawn coin, so the
    /// owner was overstated by the withdrawn amount - for as long as the owner cared to withhold the Bitcoin
    /// transaction. Block validation never asks Bitcoin (it trusts the producer for owner debits), so the only place
    /// this can be enforced is where validators admit and propose: there, a Completed withdrawal is added back only once
    /// its Bitcoin transaction is confirmed.
    ///
    /// Re-audit (9 Oct 2026): confirmation alone was not enough - COMPLETE names any txid, so a confirmed transaction
    /// that has nothing to do with the withdrawal (someone else's, or a deposit INTO the vault) passed. A Completed
    /// withdrawal now counts only when the confirmed transaction is the withdrawal's: it spends the vault's deposit
    /// address and pays the request's destination. Validators that signed it know this already; every validator can
    /// check it from the chain (raw transaction and its inputs' previous outputs).
    ///
    /// The facts of confirmed transactions are remembered for the life of the process (a confirmation never goes away,
    /// and a transaction's inputs and outputs never change), so each txid costs one set of Electrum answers; every row is
    /// still judged against its own destination, and a txid pays at most one row. No answer is reported as null: the
    /// caller fails closed exactly as it does when the deposit balance itself cannot be read.
    /// </summary>
    public static class CompletedWithdrawalConfirmation
    {
        /// <summary>What the check needs to know about a Bitcoin transaction.</summary>
        public sealed class BtcTxFacts
        {
            /// <summary>0 = known and unconfirmed (mempool); &gt;= 1 confirmed.</summary>
            public int Confirmations { get; set; }
            /// <summary>The addresses whose coins the transaction spends (its inputs' previous outputs). Empty while unconfirmed.</summary>
            public HashSet<string> InputAddresses { get; } = new(StringComparer.Ordinal);
            /// <summary>Where the transaction pays, in BTC. Empty while unconfirmed.</summary>
            public List<(string Address, decimal Btc)> Outputs { get; } = new();
        }

        /// <summary>A Completed withdrawal the owner add-back counts. <paramref name="CompletedAt"/> orders rows that name the same txid.</summary>
        public sealed record Candidate(string BtcTxId, decimal Amount, string? Destination, string SmartContractUID, long CompletedAt = 0, string? RequestHash = null,
            int FeeRate = 0, string? SignedBtcTxId = null);

        /// <summary>Confirmations before a transaction's facts are cached for the process (a reorg deeper than this is not planned for).</summary>
        public const int CacheAfterConfirmations = 6;

        /// <summary>
        /// The most a withdrawal's payout may fall short of its amount: the Bitcoin fee comes out of the amount, bounded by the
        /// request's fee rate over a generous 1,000 vbytes (a two-input Taproot spend is about 200). Never below 0.00001 BTC.
        /// </summary>
        public static decimal FeeAllowanceBtc(int feeRateSatPerVb) => Math.Max(0.00001M, Math.Max(feeRateSatPerVb, 1) * 1000M / 100_000_000M);

        /// <summary>The facts for a txid, or null when the servers gave no usable answer. Replaceable for tests.</summary>
        internal static Func<string, Task<BtcTxFacts?>> LookupTransaction = FetchFactsAsync;

        /// <summary>The vault's deposit address from chain state. Replaceable for tests.</summary>
        internal static Func<string, string?> ResolveDepositAddress = scUID => FrostSigningAuthorization.ResolveDepositAddressForContract(scUID);

        private const int MaxServersPerLookup = 3;
        /// <summary>
        /// Facts of CONFIRMED transactions, by txid (a confirmed transaction's inputs and outputs never change). Only the facts
        /// are cached: every candidate is still judged against its own destination, so a txid that completed one withdrawal
        /// cannot vouch for another (re-audit, 9 Oct 2026).
        /// </summary>
        private static readonly ConcurrentDictionary<string, BtcTxFacts> _confirmedFacts = new(StringComparer.OrdinalIgnoreCase);

        internal static void ResetForTests() => _confirmedFacts.Clear();

        /// <summary>The Completed withdrawals on <paramref name="scUID"/> that the owner add-back counts, with their Bitcoin txids and destinations.</summary>
        public static List<Candidate> Candidates(string scUID, long currentHeight)
        {
            var rows = VBTCWithdrawalRequest.GetCompletedWithdrawalRows(scUID, currentHeight);
            return rows.Where(r => !string.IsNullOrWhiteSpace(r.BTCTxHash))
                .Select(r => new Candidate(r.BTCTxHash!, r.Amount, r.BTCDestination, scUID, r.CompletionTimestamp ?? r.Timestamp, r.TransactionHash, r.FeeRate, r.LastSignedBtcTxId))
                .ToList();
        }

        /// <summary>
        /// Sum of the Completed withdrawals on <paramref name="scUID"/> that may not be added back yet: their Bitcoin
        /// transaction is unconfirmed, or confirmed but not the withdrawal's (does not spend the vault, or does not pay the
        /// destination). Null when a lookup had no answer. 0 when there is nothing to check.
        /// </summary>
        public static async Task<decimal?> NotYetCountedAmountAsync(string scUID, long currentHeight)
        {
            List<Candidate> candidates;
            string? deposit;
            try
            {
                candidates = Candidates(scUID, currentHeight);
                deposit = candidates.Count == 0 ? null : ResolveDepositAddress(scUID);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Completed-withdrawal candidates for {scUID} could not be read: {ex.Message}", "CompletedWithdrawalConfirmation.NotYetCountedAmountAsync()");
                return null;
            }
            return await NotYetCountedAmountAsync(candidates, deposit).ConfigureAwait(false);
        }

        /// <summary>
        /// The same, over an explicit candidate list and vault deposit address (the unit-testable core). One Bitcoin
        /// transaction pays one withdrawal: when several completed rows name the same txid, only the earliest completed
        /// one can count; the others never do (re-audit: a prior withdrawal's txid completed a new one to any destination).
        /// </summary>
        public static async Task<decimal?> NotYetCountedAmountAsync(IEnumerable<Candidate> candidates, string? depositAddress)
        {
            var list = candidates.ToList();
            var firstByTxid = list
                .GroupBy(c => c.BtcTxId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CompletedAt).ThenBy(c => c.RequestHash, StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);

            decimal notCounted = 0M;
            foreach (var c in list)
            {
                if (!ReferenceEquals(firstByTxid[c.BtcTxId], c))
                {
                    notCounted += c.Amount; // a reused txid: this row is never paid by that transaction
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(c.SignedBtcTxId) && !string.Equals(c.SignedBtcTxId.Trim(), c.BtcTxId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    notCounted += c.Amount; // this node signed a different transaction for the withdrawal: the named one is not it
                    continue;
                }
                if (!_confirmedFacts.TryGetValue(c.BtcTxId, out var facts))
                {
                    try { facts = await LookupTransaction(c.BtcTxId).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Lookup of completed withdrawal tx {c.BtcTxId} failed: {ex.Message}", "CompletedWithdrawalConfirmation.NotYetCountedAmountAsync()");
                        facts = null;
                    }
                    if (facts == null)
                        return null; // no answer: the caller fails closed
                    if (facts.Confirmations >= CacheAfterConfirmations)
                        _confirmedFacts[c.BtcTxId] = facts;
                }
                if (facts.Confirmations >= 1 && IsTheWithdrawalsTransaction(facts, c.Destination, depositAddress, c.Amount, c.FeeRate, out _))
                    continue;
                notCounted += c.Amount; // unconfirmed (mempool, withheld), or confirmed but not this withdrawal's transaction
            }
            return notCounted;
        }

        /// <summary>
        /// Whether a completed withdrawal row other than <paramref name="requestHash"/> already names <paramref name="btcTxHash"/>
        /// (any vault). Validator-local: a COMPLETE that reuses a Bitcoin transaction is refused when the rows are known.
        /// </summary>
        public static bool IsBtcTxidAlreadyUsed(string? btcTxHash, string? requestHash, out string? usedBy)
        {
            usedBy = null;
            if (string.IsNullOrWhiteSpace(btcTxHash)) return false;
            try
            {
                var db = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb();
                if (db == null) return false;
                var wanted = btcTxHash.Trim().ToLowerInvariant();
                var other = db.Query().Where(x => x.IsCompleted && x.BTCTxHash != null).ToList()
                    .FirstOrDefault(x => string.Equals(x.BTCTxHash!.Trim().ToLowerInvariant(), wanted, StringComparison.Ordinal)
                                         && !string.Equals(x.TransactionHash, requestHash, StringComparison.Ordinal));
                if (other == null) return false;
                usedBy = other.TransactionHash;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The transaction is the withdrawal's when it spends the vault's deposit address and pays the withdrawal's
        /// destination at least the withdrawal amount less the fee allowance (third review: a small real payout to the same
        /// destination must not complete a large withdrawal). Bech32 addresses compare case-insensitively; base58 exactly.
        /// </summary>
        public static bool IsTheWithdrawalsTransaction(BtcTxFacts facts, string? destination, string? depositAddress, decimal amount, int feeRate, out string reason)
        {
            if (!IsTheWithdrawalsTransaction(facts, destination, depositAddress, out reason))
                return false;
            var paid = facts.Outputs.Where(o => SameAddress(o.Address, destination)).Sum(o => o.Btc);
            var floor = amount - FeeAllowanceBtc(feeRate);
            if (paid < floor)
            {
                reason = $"the transaction pays the destination {paid} BTC; the withdrawal is {amount} BTC (fee allowance {FeeAllowanceBtc(feeRate)})";
                return false;
            }
            return true;
        }

        /// <summary>Spends the vault and pays the destination (any amount). The amount-aware overload is what the add-back uses.</summary>
        public static bool IsTheWithdrawalsTransaction(BtcTxFacts facts, string? destination, string? depositAddress, out string reason)
        {
            reason = "";
            if (facts == null) { reason = "no transaction facts"; return false; }
            if (string.IsNullOrWhiteSpace(depositAddress)) { reason = "the vault's deposit address is unknown"; return false; }
            if (string.IsNullOrWhiteSpace(destination)) { reason = "the withdrawal has no destination"; return false; }
            if (!facts.InputAddresses.Any(a => SameAddress(a, depositAddress)))
            {
                reason = "the transaction does not spend the vault's deposit address";
                return false;
            }
            if (!facts.Outputs.Any(o => o.Btc > 0 && SameAddress(o.Address, destination)))
            {
                reason = "the transaction does not pay the withdrawal's destination";
                return false;
            }
            return true;
        }

        private static string Canon(string a)
        {
            var t = a.Trim();
            var lower = t.ToLowerInvariant();
            return lower.StartsWith("bc1") || lower.StartsWith("tb1") || lower.StartsWith("bcrt1") ? lower : t;
        }

        private static bool SameAddress(string? a, string? b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Canon(a), Canon(b), StringComparison.Ordinal);

        // ── Electrum ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Confirmations first (the existing resilient lookup); for a confirmed transaction the raw body and the previous
        /// output of every input, each from up to <see cref="MaxServersPerLookup"/> distinct servers. Null on any gap.
        /// </summary>
        private static async Task<BtcTxFacts?> FetchFactsAsync(string txid)
        {
            var lookup = await BitcoinTransactionService.GetTransactionConfirmationsResilient(txid).ConfigureAwait(false);
            if (lookup.Confirmations == null)
                return null;
            var facts = new BtcTxFacts { Confirmations = lookup.Confirmations.Value };
            if (facts.Confirmations < 1)
                return facts;

            var network = Globals.BTCNetwork ?? FrostDkgAttestation.ConsensusNetwork;
            var raw = await FetchRawAsync(txid).ConfigureAwait(false);
            if (raw == null)
                return null;
            NBitcoin.Transaction tx;
            try { tx = NBitcoin.Transaction.Parse(raw, network); }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Completed withdrawal tx {txid} could not be parsed: {ex.Message}", "CompletedWithdrawalConfirmation.FetchFactsAsync()");
                return null;
            }
            foreach (var o in tx.Outputs)
            {
                var addr = o.ScriptPubKey.GetDestinationAddress(network)?.ToString();
                if (!string.IsNullOrEmpty(addr))
                    facts.Outputs.Add((addr, o.Value.ToDecimal(MoneyUnit.BTC)));
            }
            var prevCache = new Dictionary<string, NBitcoin.Transaction>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in tx.Inputs)
            {
                var prevId = input.PrevOut.Hash.ToString();
                if (!prevCache.TryGetValue(prevId, out var prev))
                {
                    var prevRaw = await FetchRawAsync(prevId).ConfigureAwait(false);
                    if (prevRaw == null)
                        return null;
                    try { prev = NBitcoin.Transaction.Parse(prevRaw, network); }
                    catch { return null; }
                    prevCache[prevId] = prev;
                }
                if (input.PrevOut.N >= prev.Outputs.Count)
                    return null;
                var addr = prev.Outputs[(int)input.PrevOut.N].ScriptPubKey.GetDestinationAddress(network)?.ToString();
                if (!string.IsNullOrEmpty(addr))
                    facts.InputAddresses.Add(addr);
            }
            return facts;
        }

        /// <summary>The raw transaction hex from the first of up to <see cref="MaxServersPerLookup"/> distinct servers that has it; null otherwise.</summary>
        private static async Task<string?> FetchRawAsync(string txid)
        {
            var tried = 0;
            foreach (var server in ClientService.GetServerCandidates())
            {
                if (tried++ >= MaxServersPerLookup)
                    break;
                var (ok, result) = await ElectrumServerPool.AttemptAsync(server, c => c.GetRawTx(txid)).ConfigureAwait(false);
                if (ok && !string.IsNullOrWhiteSpace(result?.RawTx))
                    return result!.RawTx;
            }
            return null;
        }
    }
}
