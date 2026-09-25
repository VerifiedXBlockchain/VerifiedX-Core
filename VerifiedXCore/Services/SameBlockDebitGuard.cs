using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Privacy;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// NEW-07 (found by the second independent review; not in the audit; same end state as VX-01): every per-transaction
    /// balance check reads committed state only, so two transactions in ONE block that debit the same holder on the same
    /// contract each pass on their own and together spend more than the holder has. The only cross-transaction
    /// accounting was for typed VBTC_V2_TRANSFER against itself, and for tokens only in a JArray branch wallets never use.
    ///
    /// This guard keeps one running debit per (ledger, contract, holder) across every transaction type whose apply writes
    /// a debit for that holder, and refuses the transaction that takes the total above the committed balance. The same
    /// State is used by block validation, the block proposal and mempool admission, so an honest producer never builds
    /// a block that validation will refuse.
    ///
    /// Scope, by design:
    ///  - A key is checked only once it carries a second debit in the batch (a single debit is exactly the per-transaction
    ///    check already made), so no block that the per-transaction rules accept with one debit per key changes verdict.
    ///  - Owner balances (V1 and V2) are backed by the deposit address and are not a ledger sum; they are not checked here
    ///    (the V2 owner check needs ElectrumX and is bypassed at block validation today).
    /// </summary>
    public static class SameBlockDebitGuard
    {
        public enum LedgerKind { VbtcV2, VbtcV1, Token, Native }

        /// <summary>ContractUid of the native VFX key (not a contract).</summary>
        public const string NativeUid = "VFX";

        public readonly record struct DebitKey(LedgerKind Kind, string ContractUid, string Holder);

        public const string ReasonPrefix = "Same-block overspend";

        /// <summary>Transaction types StateData routes through the smart-contract Function dispatcher.</summary>
        private static bool IsFunctionDispatchType(TransactionType t) =>
            t == TransactionType.NFT_TX || t == TransactionType.NFT_MINT || t == TransactionType.NFT_BURN
            || t == TransactionType.FTKN_MINT || t == TransactionType.FTKN_TX || t == TransactionType.FTKN_BURN
            || t == TransactionType.TKNZ_MINT || t == TransactionType.TKNZ_TX || t == TransactionType.TKNZ_BURN
            || t == TransactionType.SC_MINT || t == TransactionType.SC_TX || t == TransactionType.SC_BURN
            || t == TransactionType.VBTC_V2_CONTRACT_CREATE || t == TransactionType.TKNZ_WD_ARB || t == TransactionType.TKNZ_WD_OWNER;

        /// <summary>
        /// The ledger debits a transaction's apply writes, parsed the way StateData parses them. Unparseable data yields
        /// no debits (apply writes none either).
        /// </summary>
        public static List<(DebitKey Key, decimal Amount)> GetDebits(Transaction tx)
        {
            var debits = new List<(DebitKey, decimal)>();
            if (tx == null || string.IsNullOrEmpty(tx.FromAddress))
                return debits;

            // Native VFX: every non-coinbase, non-ZK transaction's apply debits Amount + Fee from the sender's Balance,
            // while VerifyTX compares each transaction alone with the committed balance. Honest admission and proposal
            // already sum a sender's pending transactions (DoubleSpendReplayCheck); block validation did not, so a
            // producer could include spends that together exceed the balance (third review).
            if (tx.FromAddress != "Coinbase_TrxFees" && tx.FromAddress != "Coinbase_BlkRwd"
                && !Privacy.PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType))
            {
                // An NFT sale completion's apply also debits the buyer for the inner payments (seller, royalty payee)
                // on top of the outer Amount + Fee (Amount is 0 there). One entry per transaction, so a lone sale is
                // judged only by the per-transaction rules (fourth review: sale + VFX send in one block went negative).
                var native = tx.Amount + tx.Fee + SaleCompletionPayments(tx);
                if (native > 0M)
                    debits.Add((new DebitKey(LedgerKind.Native, NativeUid, tx.FromAddress), native));
            }

            if (string.IsNullOrEmpty(tx.Data))
                return debits;

            try
            {
                switch (tx.TransactionType)
                {
                    case TransactionType.VBTC_V2_TRANSFER:
                        // Reserve (xRBX) sends debit at unlock, but their balance check already nets pending sends,
                        // so they are counted here too (the single-flight rule refuses a second one anyway).
                        foreach (var (uid, amt) in Bitcoin.Services.VBTCService.GetVbtcV2TransferOutflows(tx))
                            debits.Add((new DebitKey(LedgerKind.VbtcV2, uid, tx.FromAddress), amt));
                        return debits;

                    case TransactionType.VBTC_V2_WITHDRAWAL_REQUEST:
                    {
                        // The request debits (escrow) only from WithdrawalEscrowHeight on; earlier requests were burned
                        // at COMPLETE, so counting them here would refuse historical blocks the apply accepts. Same height
                        // source as the apply (tx.Height, set when the transaction is crafted into its block).
                        var height = tx.Height > 0 ? tx.Height : (Globals.LastBlock?.Height ?? 0) + 1;
                        if (!Bitcoin.Models.VBTCWithdrawalRequest.EscrowAppliesTo(height))
                            return debits;
                        foreach (var (uid, amt) in Bitcoin.Services.VBTCService.GetVbtcV2WithdrawalOutflows(tx))
                            debits.Add((new DebitKey(LedgerKind.VbtcV2, uid, tx.FromAddress), amt));
                        return debits;
                    }

                    case TransactionType.VBTC_V2_BRIDGE_LOCK:
                    {
                        var jobj = JObject.Parse(tx.Data);
                        var uid = jobj["ContractUID"]?.ToObject<string?>();
                        var amt = jobj["Amount"]?.ToObject<decimal?>();
                        if (!string.IsNullOrEmpty(uid) && amt is > 0)
                            debits.Add((new DebitKey(LedgerKind.VbtcV2, uid, tx.FromAddress), amt.Value));
                        return debits;
                    }

                    case TransactionType.VBTC_V2_SHIELD:
                        if (PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _)
                            && !string.IsNullOrWhiteSpace(payload.VbtcContractUid) && payload.VbtcTransparentAmount is > 0)
                            debits.Add((new DebitKey(LedgerKind.VbtcV2, payload.VbtcContractUid, tx.FromAddress), payload.VbtcTransparentAmount.Value));
                        return debits;
                }

                if (!IsFunctionDispatchType(tx.TransactionType))
                    return debits;

                // Same Function resolution as StateData: first element of a JArray, else a JObject.
                string? function = null;
                JToken? arrayElement = null;
                try
                {
                    var arr = JsonConvert.DeserializeObject<JArray>(tx.Data);
                    arrayElement = arr?[0];
                    function = (string?)arrayElement?["Function"];
                }
                catch { arrayElement = null; }

                JObject? obj = null;
                if (arrayElement == null)
                {
                    try { obj = JObject.Parse(tx.Data); function = obj["Function"]?.ToObject<string?>(); }
                    catch { return debits; }
                }

                switch (function)
                {
                    case "TransferVBTCV2()":
                    {
                        // Apply parses a JObject only and debits tx.FromAddress.
                        var o = obj ?? JObject.Parse(tx.Data);
                        var uid = o["ContractUID"]?.ToObject<string?>();
                        var amt = o["Amount"]?.ToObject<decimal?>();
                        if (!string.IsNullOrEmpty(uid) && amt is > 0)
                            debits.Add((new DebitKey(LedgerKind.VbtcV2, uid, tx.FromAddress), amt.Value));
                        break;
                    }
                    case "TransferCoin()":
                    {
                        // Apply accepts either shape and debits tx.FromAddress.
                        var src = arrayElement ?? obj;
                        var uid = (string?)src?["ContractUID"];
                        var amt = (decimal?)src?["Amount"];
                        if (!string.IsNullOrEmpty(uid) && amt is > 0)
                            debits.Add((new DebitKey(LedgerKind.VbtcV1, uid, tx.FromAddress), amt.Value));
                        break;
                    }
                    case "TransferCoinMulti()":
                    {
                        // Apply parses a JObject only and debits each input's holder.
                        var o = obj ?? JObject.Parse(tx.Data);
                        var inputs = o["Inputs"]?.ToObject<List<VBTCTransferInput>?>();
                        if (inputs != null)
                            foreach (var input in inputs)
                                if (!string.IsNullOrEmpty(input?.SCUID) && !string.IsNullOrEmpty(input.FromAddress) && input.Amount > 0)
                                    debits.Add((new DebitKey(LedgerKind.VbtcV1, input.SCUID, input.FromAddress), input.Amount));
                        break;
                    }
                    case "TokenTransfer()":
                    case "TokenBurn()":
                    {
                        // Apply parses a JObject only and debits the data's FromAddress (bound to the signer, NEW-04).
                        var o = obj ?? JObject.Parse(tx.Data);
                        var uid = o["ContractUID"]?.ToObject<string?>();
                        var from = o["FromAddress"]?.ToObject<string?>();
                        var amt = o["Amount"]?.ToObject<decimal?>();
                        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(from) && amt is > 0)
                            debits.Add((new DebitKey(LedgerKind.Token, uid, from), amt.Value));
                        break;
                    }
                }
            }
            catch { }
            return debits;
        }

        /// <summary>
        /// The committed balance the per-transaction checks compare a debit against, or null when the key is not a
        /// ledger balance (contract owner) and is left to the per-transaction rules.
        /// </summary>
        public static decimal? CommittedBalance(DebitKey key)
        {
            if (key.Kind == LedgerKind.Native)
            {
                // No account: VerifyTX allows that only for TKNZ_WD_ARB (arbiters); not judged here.
                var account = StateData.GetSpecificAccountStateTrei(key.Holder);
                if (account == null) return null;
                // Reserve accounts must keep 0.5 VFX (VerifyTX's reserve rule); the per-block total honours it too.
                return key.Holder.StartsWith("xRBX") ? account.Balance - 0.5M : account.Balance;
            }

            if (key.Kind == LedgerKind.Token)
            {
                var account = StateData.GetSpecificAccountStateTrei(key.Holder);
                return account?.TokenAccounts?.FirstOrDefault(x => x.SmartContractUID == key.ContractUid)?.Balance ?? 0M;
            }

            var sc = SmartContractStateTrei.GetSmartContractState(key.ContractUid);
            if (sc == null)
                return 0M;
            if (key.Holder == sc.OwnerAddress)
                return null;

            var ledger = (sc.SCStateTreiTokenizationTXes ?? new List<SmartContractStateTreiTokenizationTX>())
                .Where(x => x.FromAddress == key.Holder || x.ToAddress == key.Holder)
                .Sum(x => x.Amount);

            // Reserve sends defer their ledger debit to unlock; their pending rows are written at block apply on every
            // node, so netting them is deterministic (same as the VBTC_V2_TRANSFER checks).
            if (key.Kind == LedgerKind.VbtcV2 && key.Holder.StartsWith("xRBX"))
                ledger -= ReserveTransactions.GetPendingVBTCTransferTotal(key.Holder, key.ContractUid);

            return ledger;
        }

        /// <summary>
        /// Mempool admission and block proposal only (never block validation): a vBTC V2 OWNER's balance is its deposit
        /// address's confirmed BTC (ElectrumX) plus its owner ledger, the same figure the per-transaction admission check
        /// uses. Honest nodes therefore neither admit nor propose two owner debits that together exceed it; block
        /// validation keeps trusting the producer for owners (ElectrumX is not consensus state).
        /// </summary>
        public static decimal? PolicyBalance(DebitKey key)
        {
            var committed = CommittedBalance(key);
            if (committed.HasValue || key.Kind != LedgerKind.VbtcV2)
                return committed;
            try
            {
                var (ok, available, _) = Bitcoin.Services.VBTCService.TryGetAvailableTransparentVbtcBalance(key.ContractUid, key.Holder).GetAwaiter().GetResult();
                return ok ? available : 0M;
            }
            catch { return 0M; }
        }

        /// <summary>
        /// vBTC ledgers live on the contract record, which is looked up through LiteDB's default collation (case is
        /// ignored), so "ABC:1" and "abc:1" debit the same rows. Keys use the stored record's UID. Token balances are
        /// matched to the UID ordinally (validator and apply alike), so case variants there are separate balances.
        /// </summary>
        public static DebitKey Canonical(DebitKey key, Dictionary<string, string>? cache = null)
        {
            if (key.Kind == LedgerKind.Token || key.Kind == LedgerKind.Native)
                return key;
            if (cache == null || !cache.TryGetValue(key.ContractUid, out var uid))
            {
                uid = SmartContractStateTrei.GetSmartContractState(key.ContractUid)?.SmartContractUID ?? key.ContractUid;
                if (cache != null) cache[key.ContractUid] = uid;
            }
            return key with { ContractUid = uid };
        }

        public sealed class State
        {
            private readonly Func<DebitKey, decimal?> _balanceOf;
            internal Dictionary<DebitKey, decimal> Spent { get; } = new();
            internal Dictionary<DebitKey, int> Count { get; } = new();
            internal Dictionary<string, string> CanonicalUids { get; } = new(StringComparer.Ordinal);
            internal HashSet<string> Senders { get; } = new(StringComparer.Ordinal);
            internal HashSet<string> Swept { get; } = new(StringComparer.Ordinal);
            private readonly Dictionary<DebitKey, decimal?> _balances = new();

            /// <summary>Consensus state (block validation).</summary>
            public State() : this(CommittedBalance) { }
            public State(Func<DebitKey, decimal?> balanceOf) { _balanceOf = balanceOf; }
            /// <summary>Admission / proposal: also judges vBTC V2 owners (see <see cref="PolicyBalance"/>).</summary>
            public static State ForPolicy() => new State(PolicyBalance);

            internal decimal? BalanceOf(DebitKey key)
            {
                if (!_balances.TryGetValue(key, out var b))
                    _balances[key] = b = _balanceOf(key);
                return b;
            }
        }

        /// <summary>
        /// Adds a transaction's debits to the batch. Returns false (state untouched) when any holder's total on a contract,
        /// counting this transaction, is above its committed balance and that key now carries more than one debit.
        /// </summary>
        public static (bool Ok, string Reason) TryRegister(Transaction tx, State state)
        {
            if (tx == null || state == null)
                return (true, "");
            // Whitelisted historical transactions skip VerifyTX entirely; they are not judged here either.
            if (Globals.BadTxList.Exists(x => x == tx.Hash) || Globals.BadNFTTxList.Exists(x => x == tx.Hash))
                return (true, "");

            // A reserve Recover() sweeps every balance of the reserve to its recovery address, a debit of the whole
            // balance that per-transaction checks (committed state) cannot see: a reserve send in the same block wrote a
            // fresh Pending row after the sweep and paid out again at unlock. Recover() must be the only transaction
            // from its reserve in the block.
            var sender = tx.FromAddress ?? "";
            var isSweep = IsReserveRecover(tx);
            if (state.Swept.Contains(sender))
                return (false, $"{ReasonPrefix}: {sender} is swept by Recover() in this block; no other transaction from it may share the block.");
            if (isSweep && state.Senders.Contains(sender))
                return (false, $"{ReasonPrefix}: Recover() must be the only transaction from {sender} in its block.");

            var debits = GetDebits(tx);
            if (debits.Count == 0)
            {
                state.Senders.Add(sender);
                if (isSweep) state.Swept.Add(sender);
                return (true, "");
            }

            var byKey = debits.Select(d => (Key: Canonical(d.Key, state.CanonicalUids), d.Amount))
                .GroupBy(d => d.Key).Select(g => (Key: g.Key, Sum: g.Sum(d => d.Amount), N: g.Count())).ToList();

            foreach (var (key, sum, n) in byKey)
            {
                state.Spent.TryGetValue(key, out var spent);
                state.Count.TryGetValue(key, out var count);
                if (count + n < 2)
                    continue;
                var balance = state.BalanceOf(key);
                if (balance.HasValue && spent + sum > balance.Value)
                    return (false, $"{ReasonPrefix}: {key.Holder} debits {spent + sum} of {key.Kind} {key.ContractUid} within block; balance {balance.Value}.");
            }

            foreach (var (key, sum, n) in byKey)
            {
                state.Spent.TryGetValue(key, out var spent);
                state.Count.TryGetValue(key, out var count);
                state.Spent[key] = spent + sum;
                state.Count[key] = count + n;
            }
            state.Senders.Add(sender);
            if (isSweep) state.Swept.Add(sender);
            return (true, "");
        }

        /// <summary>The buyer debits StateData.CompleteSaleSmartContract writes beyond the outer transaction.</summary>
        public static decimal SaleCompletionPayments(Transaction tx)
        {
            if (tx?.TransactionType != TransactionType.NFT_SALE || string.IsNullOrEmpty(tx.Data))
                return 0M;
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var function = jobj["Function"]?.ToObject<string?>();
                if (function != "Sale_Complete()" && function != "M_Sale_Complete()")
                    return 0M;
                var inner = jobj["Transactions"]?.ToObject<List<Transaction>?>();
                if (inner == null || inner.Count == 0)
                    return 0M;
                var royalty = jobj["Royalty"]?.ToObject<bool?>();
                if (royalty == true)
                {
                    var toSeller = inner.FirstOrDefault(x => (x.Data ?? "").Contains("1/2"));
                    var toPayee = inner.FirstOrDefault(x => (x.Data ?? "").Contains("2/2"));
                    return (toSeller == null ? 0M : toSeller.Amount + toSeller.Fee) + (toPayee == null ? 0M : toPayee.Amount + toPayee.Fee);
                }
                var first = inner[0];
                return first == null ? 0M : first.Amount + first.Fee;
            }
            catch { return 0M; }
        }

        public static bool IsReserveRecover(Transaction tx)
        {
            if (tx?.TransactionType != TransactionType.RESERVE || string.IsNullOrEmpty(tx.Data))
                return false;
            try { return JObject.Parse(tx.Data)["Function"]?.ToObject<string?>() == "Recover()"; }
            catch { return false; }
        }

        /// <summary>
        /// Block proposal: keeps transactions in order, dropping one that would overspend together with that sender's
        /// later transactions (so the proposal has no nonce gap). A dropped transaction stays in the mempool for a later
        /// block, where it is re-checked against the new committed balance.
        /// </summary>
        public static List<Transaction> DropSameBlockOverspends(List<Transaction> approved, State? state = null)
        {
            state ??= State.ForPolicy();
            var blockedSenders = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<Transaction>();
            foreach (var tx in approved)
            {
                if (tx.FromAddress != null && blockedSenders.Contains(tx.FromAddress))
                    continue;
                var (ok, reason) = TryRegister(tx, state);
                if (!ok)
                {
                    if (tx.FromAddress != null) blockedSenders.Add(tx.FromAddress);
                    Utilities.LogUtility.Log($"[ProcessTxPool] Skipping {tx.Hash} for this block: {reason}", "TransactionData.ProcessTxPool()");
                    continue;
                }
                result.Add(tx);
            }
            return result;
        }

        /// <summary>
        /// Mempool admission: would this transaction, added to the same sender's pending transactions, overspend any
        /// holder's balance? (Siblings are taken from the same sender; the proposal and block checks cover the rest.)
        /// </summary>
        public static (bool Ok, string Reason) CheckAgainstPending(Transaction tx, IEnumerable<Transaction> pendingFromSameSender)
        {
            var debits = GetDebits(tx);
            if (debits.Count == 0)
                return (true, "");
            var state = State.ForPolicy();
            var keys = new HashSet<DebitKey>(debits.Select(d => Canonical(d.Key, state.CanonicalUids)));
            foreach (var p in pendingFromSameSender.OrderBy(p => p.Nonce))
            {
                if (p.Hash == tx.Hash)
                    continue;
                if (!GetDebits(p).Any(d => keys.Contains(Canonical(d.Key, state.CanonicalUids))))
                    continue;
                // A pending transaction that itself no longer fits is left for the proposal to drop.
                TryRegister(p, state);
            }
            return TryRegister(tx, state);
        }
    }
}
