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
    /// this can be enforced is where validators admit and propose.
    ///
    /// The rule (fourth review) is the accounting identity itself, with nothing the requester chooses in it. The add-back
    /// of a withdrawal is right exactly when the vault's confirmed balance has fallen by at least the withdrawal's
    /// amount. So a Completed withdrawal is added back only when the Bitcoin transaction its COMPLETE names:
    /// <list type="number">
    /// <item>is confirmed;</item>
    /// <item>took at least the withdrawal's amount OUT of the vault: (the vault's coins it spends) minus (what it sends
    /// back to the vault) &gt;= amount. Every withdrawal transaction the validators sign has exactly this shape: the
    /// destination gets amount less the fee, the change (inputs less amount) returns to the vault;</item>
    /// <item>is the withdrawal's: it pays the destination, and the payout plus the Bitcoin fee actually paid covers the
    /// amount (the earlier floor allowed for a fee computed from the request's fee rate, which the requester sets, so
    /// a large rate brought the floor to nothing);</item>
    /// <item>is named by no earlier Completed row (one transaction pays one withdrawal) and is not a bridge exit's
    /// transaction (an exit's outflow is already carried by its lock debit);</item>
    /// <item>is the transaction this validator signed for the withdrawal, when it signed one (its durable signing record,
    /// held by every signer, not only the node that ran the ceremony).</item>
    /// </list>
    /// With 2 and 4 the sum of the amounts added back on a vault never exceeds what has confirmed out of it.
    ///
    /// The transaction body and its inputs' previous transactions are checked against their txids, so a server cannot
    /// make up a body. Facts of deep transactions are remembered for the life of the process. No answer is reported
    /// as null: the caller fails closed exactly as it does when the deposit balance itself cannot be read.
    /// </summary>
    public static class CompletedWithdrawalConfirmation
    {
        /// <summary>What the check needs to know about a Bitcoin transaction. Amounts in satoshis.</summary>
        public sealed class BtcTxFacts
        {
            /// <summary>0 = known and unconfirmed (mempool); &gt;= 1 confirmed.</summary>
            public int Confirmations { get; set; }
            /// <summary>Each input's previous output: who held the coin (null when the script has no address) and its value. Empty while unconfirmed.</summary>
            public List<(string? Address, long Sats)> Inputs { get; } = new();
            /// <summary>Every output, including those with no address (null). Empty while unconfirmed.</summary>
            public List<(string? Address, long Sats)> Outputs { get; } = new();
            /// <summary>Set when the transaction was not examined (see <see cref="MaxInputs"/>); it then counts for nothing.</summary>
            public string? NotExamined { get; set; }

            public long SpentFrom(string? address) => Inputs.Where(i => SameAddress(i.Address, address)).Sum(i => i.Sats);
            public long PaidTo(string? address) => Outputs.Where(o => SameAddress(o.Address, address)).Sum(o => o.Sats);
            /// <summary>The Bitcoin fee actually paid: inputs less outputs.</summary>
            public long FeeSats => Math.Max(0, Inputs.Sum(i => i.Sats) - Outputs.Sum(o => o.Sats));
        }

        /// <summary>
        /// A Completed withdrawal the owner add-back counts. <paramref name="CompletedAt"/> orders rows that name the same
        /// txid; <paramref name="SignedBtcTxId"/> is the transaction this node signed for it, when it signed one.
        /// </summary>
        public sealed record Candidate(string BtcTxId, decimal Amount, string? Destination, string SmartContractUID, long CompletedAt = 0, string? RequestHash = null,
            string? SignedBtcTxId = null);

        /// <summary>Confirmations before a transaction's facts are cached for the process (a reorg deeper than this is not planned for).</summary>
        public const int CacheAfterConfirmations = 6;

        /// <summary>A withdrawal transaction has a handful of inputs; one with more than this is not examined (each input costs a lookup).</summary>
        public const int MaxInputs = 2_000;

        /// <summary>Rounding between the ledger's decimal amount and Bitcoin's satoshis.</summary>
        private const long ToleranceSats = 1;

        /// <summary>The facts for a txid, or null when the servers gave no usable answer. Replaceable for tests.</summary>
        internal static Func<string, Task<BtcTxFacts?>> LookupTransaction = FetchFactsAsync;

        /// <summary>The vault's deposit address from chain state. Replaceable for tests.</summary>
        internal static Func<string, string?> ResolveDepositAddress = scUID => FrostSigningAuthorization.ResolveDepositAddressForContract(scUID);

        private const int MaxServersPerLookup = 3;
        /// <summary>
        /// Facts of CONFIRMED transactions, by txid (a confirmed transaction's inputs and outputs never change). Only the facts
        /// are cached: every candidate is still judged against its own destination and amount, so a txid that completed
        /// one withdrawal cannot vouch for another (re-audit, 9 Oct 2026).
        /// </summary>
        private static readonly ConcurrentDictionary<string, BtcTxFacts> _confirmedFacts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

        internal static void ResetForTests() { _confirmedFacts.Clear(); _logged.Clear(); }

        public static long ToSats(decimal btc) => (long)decimal.Round(btc * 100_000_000M, 0, MidpointRounding.AwayFromZero);

        private static string Txid(string? t) => (t ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>
        /// The Bitcoin transaction this node signed for the withdrawal: its durable signing record (every validator that
        /// contributed a share holds one), else the txid on its own withdrawal row (the node that ran the ceremony). Null
        /// when this node signed nothing for it.
        /// </summary>
        public static string? SignedTxIdFor(string? scUID, string? requestHash, string? rowLastSignedBtcTxId)
        {
            string? evidence = null;
            try
            {
                if (!string.IsNullOrEmpty(scUID) && !string.IsNullOrEmpty(requestHash))
                    evidence = FrostSignedWithdrawalEvidence.Get(scUID, requestHash)?.BtcTxId;
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(evidence)) return Txid(evidence);
            return string.IsNullOrWhiteSpace(rowLastSignedBtcTxId) ? null : Txid(rowLastSignedBtcTxId);
        }

        /// <summary>
        /// The Bitcoin transactions that paid bridge exits (recorded from the chain on every node when the exit completes).
        /// An exit spends the same vaults; its outflow is carried by the exit's lock debit, never by a withdrawal add-back.
        /// </summary>
        public static HashSet<string> BridgeExitTxIds()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var exit in VBTCBridgeBtcExitState.GetCollection().FindAll())
            {
                if (string.IsNullOrWhiteSpace(exit.BtcTxHash) || exit.IsFailedRecord)
                    continue;
                foreach (var part in exit.BtcTxHash.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    set.Add(Txid(part));
            }
            return set;
        }

        /// <summary>The Completed withdrawals on <paramref name="scUID"/> that the owner add-back counts, with their Bitcoin txids and destinations.</summary>
        public static List<Candidate> Candidates(string scUID, long currentHeight)
        {
            var rows = VBTCWithdrawalRequest.GetCompletedWithdrawalRows(scUID, currentHeight);
            return rows
                .Select(r => new Candidate(r.BTCTxHash ?? string.Empty, r.Amount, r.BTCDestination, scUID, r.CompletionTimestamp ?? r.Timestamp, r.TransactionHash,
                    SignedTxIdFor(scUID, r.TransactionHash, r.LastSignedBtcTxId)))
                .ToList();
        }

        /// <summary>
        /// Sum of the Completed withdrawals on <paramref name="scUID"/> that may not be added back yet (see the class
        /// summary). Null when a lookup had no answer. 0 when there is nothing to check.
        /// </summary>
        public static async Task<decimal?> NotYetCountedAmountAsync(string scUID, long currentHeight)
        {
            List<Candidate> candidates;
            string? deposit;
            HashSet<string> exits;
            try
            {
                candidates = Candidates(scUID, currentHeight);
                if (candidates.Count == 0)
                    return 0M;
                deposit = ResolveDepositAddress(scUID);
                exits = BridgeExitTxIds();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Completed-withdrawal candidates for {scUID} could not be read: {ex.Message}", "CompletedWithdrawalConfirmation.NotYetCountedAmountAsync()");
                return null;
            }
            return await NotYetCountedAmountAsync(candidates, deposit, exits).ConfigureAwait(false);
        }

        /// <summary>
        /// The same, over an explicit candidate list, vault deposit address and set of bridge-exit txids. One Bitcoin
        /// transaction pays one withdrawal: when several completed rows name the same txid, only the earliest completed
        /// one can count; the others never do.
        /// </summary>
        public static async Task<decimal?> NotYetCountedAmountAsync(IEnumerable<Candidate> candidates, string? depositAddress, ISet<string>? bridgeExitTxIds = null)
        {
            var list = candidates.Where(c => c.Amount > 0M).ToList();
            var firstByTxid = list
                .Where(c => Txid(c.BtcTxId).Length > 0)
                .GroupBy(c => Txid(c.BtcTxId), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CompletedAt).ThenBy(c => c.RequestHash, StringComparer.Ordinal).First(), StringComparer.Ordinal);

            decimal notCounted = 0M;
            foreach (var c in list)
            {
                var txid = Txid(c.BtcTxId);
                string? why = null;
                if (txid.Length == 0)
                    why = "the completed row names no Bitcoin transaction";
                else if (!ReferenceEquals(firstByTxid[txid], c))
                    why = $"an earlier completed withdrawal ({firstByTxid[txid].RequestHash}) already names {txid}; a transaction pays one withdrawal";
                else if (bridgeExitTxIds != null && bridgeExitTxIds.Contains(txid))
                    why = $"{txid} is a bridge exit's Bitcoin transaction";
                else if (!string.IsNullOrWhiteSpace(c.SignedBtcTxId) && !string.Equals(Txid(c.SignedBtcTxId), txid, StringComparison.Ordinal))
                    why = $"this node signed {Txid(c.SignedBtcTxId)} for the withdrawal; the COMPLETE names {txid}";

                if (why == null)
                {
                    if (!_confirmedFacts.TryGetValue(txid, out var facts))
                    {
                        try { facts = await LookupTransaction(txid).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            ErrorLogUtility.LogError($"Lookup of completed withdrawal tx {txid} failed: {ex.Message}", "CompletedWithdrawalConfirmation.NotYetCountedAmountAsync()");
                            facts = null;
                        }
                        if (facts == null)
                            return null; // no answer: the caller fails closed
                        if (facts.Confirmations >= CacheAfterConfirmations)
                            _confirmedFacts[txid] = facts;
                    }
                    if (facts.Confirmations < 1)
                    {
                        notCounted += c.Amount; // in the mempool, or withheld: the vault still holds the coin
                        continue;
                    }
                    if (IsTheWithdrawalsTransaction(facts, c.Destination, depositAddress, c.Amount, out why))
                        continue;
                }

                notCounted += c.Amount;
                if (_logged.TryAdd($"{c.SmartContractUID}|{c.RequestHash}|{txid}|{why}", 0))
                    LogUtility.Log($"[VBTC V2] Completed withdrawal {c.RequestHash} on {c.SmartContractUID} ({c.Amount} BTC) is not added back to the owner: {why}.", "CompletedWithdrawalConfirmation");
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
                var wanted = Txid(btcTxHash);
                var other = db.Query().Where(x => x.IsCompleted && x.BTCTxHash != null).ToList()
                    .FirstOrDefault(x => string.Equals(Txid(x.BTCTxHash), wanted, StringComparison.Ordinal)
                                         && !string.Equals(x.TransactionHash, requestHash, StringComparison.Ordinal));
                if (other == null) return false;
                usedBy = other.TransactionHash;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Why this node does not admit a COMPLETE naming <paramref name="btcTxHash"/> for the withdrawal; null when it has no
        /// objection. Admission only (never block acceptance: these read this node's local rows and signing records, and
        /// a fresh or mid-rebuild node must not refuse a block its peers accept). The add-back check above applies the
        /// same conditions to whatever is mined anyway.
        /// </summary>
        public static string? AdmissionRefusal(string? scUID, string? requestHash, string? btcTxHash, string? rowLastSignedBtcTxId)
        {
            var txid = Txid(btcTxHash);
            if (txid.Length == 0) return null;
            if (IsBtcTxidAlreadyUsed(txid, requestHash, out var usedBy))
                return $"Bitcoin transaction {txid} already completed withdrawal {usedBy}; a transaction pays one withdrawal.";
            try
            {
                if (BridgeExitTxIds().Contains(txid))
                    return $"Bitcoin transaction {txid} paid a bridge exit; it cannot complete a withdrawal.";
            }
            catch { }
            var signed = SignedTxIdFor(scUID, requestHash, rowLastSignedBtcTxId);
            if (signed != null && !string.Equals(signed, txid, StringComparison.Ordinal))
                return $"This node signed Bitcoin transaction {signed} for withdrawal {requestHash}; the COMPLETE names {txid}.";
            return null;
        }

        /// <summary>
        /// Conditions 2 and 3 of the class summary, on the facts of a confirmed transaction. Nothing here comes from the
        /// request except its amount and destination. Bech32 addresses compare case-insensitively; base58 exactly.
        /// </summary>
        public static bool IsTheWithdrawalsTransaction(BtcTxFacts facts, string? destination, string? depositAddress, decimal amount, out string reason)
        {
            reason = "";
            if (facts == null) { reason = "no transaction facts"; return false; }
            if (facts.NotExamined != null) { reason = facts.NotExamined; return false; }
            if (string.IsNullOrWhiteSpace(depositAddress)) { reason = "the vault's deposit address is unknown"; return false; }
            if (string.IsNullOrWhiteSpace(destination)) { reason = "the withdrawal has no destination"; return false; }
            if (amount <= 0M) { reason = "the withdrawal has no amount"; return false; }

            var amountSats = ToSats(amount);
            var fromVault = facts.SpentFrom(depositAddress);
            if (fromVault <= 0)
            {
                reason = "the transaction does not spend the vault's deposit address";
                return false;
            }
            var leftVault = fromVault - facts.PaidTo(depositAddress);
            if (leftVault < amountSats - ToleranceSats)
            {
                reason = $"the transaction took {leftVault} sats out of the vault; the withdrawal is {amountSats} sats";
                return false;
            }
            if (SameAddress(destination, depositAddress))
            {
                reason = "the withdrawal's destination is the vault itself";
                return false;
            }
            var paid = facts.PaidTo(destination);
            if (paid <= 0)
            {
                reason = "the transaction does not pay the withdrawal's destination";
                return false;
            }
            if (paid + facts.FeeSats < amountSats - ToleranceSats)
            {
                reason = $"the transaction pays the destination {paid} sats and a fee of {facts.FeeSats} sats; the withdrawal is {amountSats} sats";
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

        // ── From Bitcoin transactions to facts ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The facts of <paramref name="tx"/>, given a way to get the transactions its inputs spend. Null when a previous
        /// transaction is missing, is not the one asked for, or lacks the output. Pure: no network.
        /// </summary>
        internal static BtcTxFacts? BuildFacts(NBitcoin.Transaction tx, Func<uint256, NBitcoin.Transaction?> previous, int confirmations, Network network)
        {
            var facts = new BtcTxFacts { Confirmations = confirmations };
            if (tx.Inputs.Count > MaxInputs)
            {
                facts.NotExamined = $"the transaction has {tx.Inputs.Count} inputs (more than {MaxInputs}); it is not examined";
                return facts;
            }
            foreach (var o in tx.Outputs)
                facts.Outputs.Add((o.ScriptPubKey.GetDestinationAddress(network)?.ToString(), o.Value.Satoshi));
            if (tx.IsCoinBase)
                return facts;
            foreach (var input in tx.Inputs)
            {
                var prev = previous(input.PrevOut.Hash);
                if (prev == null || prev.GetHash() != input.PrevOut.Hash || input.PrevOut.N >= prev.Outputs.Count)
                    return null;
                var spent = prev.Outputs[(int)input.PrevOut.N];
                facts.Inputs.Add((spent.ScriptPubKey.GetDestinationAddress(network)?.ToString(), spent.Value.Satoshi));
            }
            return facts;
        }

        // ── Electrum ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Confirmations first (the existing resilient lookup); for a confirmed transaction the raw body and the previous
        /// transaction of every input, each from up to <see cref="MaxServersPerLookup"/> distinct servers and each checked
        /// against the txid it was asked for. Null on any gap.
        /// </summary>
        private static async Task<BtcTxFacts?> FetchFactsAsync(string txid)
        {
            var lookup = await BitcoinTransactionService.GetTransactionConfirmationsResilient(txid).ConfigureAwait(false);
            if (lookup.Confirmations == null)
                return null;
            if (lookup.Confirmations.Value < 1)
                return new BtcTxFacts { Confirmations = lookup.Confirmations.Value };

            var network = Globals.BTCNetwork ?? FrostDkgAttestation.ConsensusNetwork;
            var tx = await FetchTransactionAsync(txid, network).ConfigureAwait(false);
            if (tx == null)
                return null;
            var prevs = new Dictionary<uint256, NBitcoin.Transaction>();
            if (!tx.IsCoinBase && tx.Inputs.Count <= MaxInputs)
            {
                foreach (var input in tx.Inputs)
                {
                    if (prevs.ContainsKey(input.PrevOut.Hash))
                        continue;
                    var prev = await FetchTransactionAsync(input.PrevOut.Hash.ToString(), network).ConfigureAwait(false);
                    if (prev == null)
                        return null;
                    prevs[input.PrevOut.Hash] = prev;
                }
            }
            return BuildFacts(tx, h => prevs.TryGetValue(h, out var p) ? p : null, lookup.Confirmations.Value, network);
        }

        /// <summary>
        /// The transaction with this txid from the first of up to <see cref="MaxServersPerLookup"/> distinct servers that
        /// returns a body hashing to it; null otherwise. A body that does not hash to the txid is that server's error.
        /// </summary>
        private static async Task<NBitcoin.Transaction?> FetchTransactionAsync(string txid, Network network)
        {
            var tried = 0;
            foreach (var server in ClientService.GetServerCandidates())
            {
                if (tried++ >= MaxServersPerLookup)
                    break;
                var (ok, result) = await ElectrumServerPool.AttemptAsync(server, c => c.GetRawTx(txid)).ConfigureAwait(false);
                if (!ok || string.IsNullOrWhiteSpace(result?.RawTx))
                    continue;
                try
                {
                    var tx = NBitcoin.Transaction.Parse(result!.RawTx, network);
                    if (string.Equals(tx.GetHash().ToString(), txid, StringComparison.OrdinalIgnoreCase))
                        return tx;
                    ErrorLogUtility.LogError($"An Electrum server returned a body for {txid} that hashes to {tx.GetHash()}; ignored.", "CompletedWithdrawalConfirmation.FetchTransactionAsync()");
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Transaction {txid} could not be parsed: {ex.Message}", "CompletedWithdrawalConfirmation.FetchTransactionAsync()");
                }
            }
            return null;
        }
    }
}
