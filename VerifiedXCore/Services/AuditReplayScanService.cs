using Newtonsoft.Json.Linq;
using System.Text;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Security-audit (Sep 2026) replay precondition. The VX-01 and VX-02 consensus rules ship
    /// WITHOUT activation heights (coordinated deploy), so they also apply to every historical block a
    /// node replays on resync. This scan walks the local block database and reports any already-mined
    /// transaction the new rules would reject. Zero hits on a fully synced node means replay is safe.
    ///
    /// It deliberately calls the SAME predicates consensus uses (<see cref="VBTCService.GetVbtcAmountError"/>,
    /// <see cref="VBTCService.IsVbtcV2Contract"/>, <see cref="SmartContractDeployBinding.Validate"/>,
    /// <see cref="LedgerIntegrityRules"/>), so it cannot drift from the rules it certifies. State-dependent checks that
    /// existed before the audit fixes (balances, active-request gates) are out of scope; they are unchanged.
    ///
    /// Also covers the independent-review follow-ups: NEW-04 (token holder bound to signer), NEW-05 (legacy V1 vBTC
    /// amounts and co-signatures; the zero-row balance rule is stateful and approximated — see
    /// <see cref="ScanChain"/>), NEW-06 (one contract creation per ContractUID per block), the VX-01 follow-up
    /// (TransferVBTCV2() function path) and NEW-07 (same-block overspend). NEW-07 compares a block's debits with the
    /// holder's balance AT that height, which this scan does not reconstruct, so it reports every block in which one
    /// holder has two or more debits on one contract as a CANDIDATE to be re-checked against historical state.
    ///
    /// Limitation: the contract-type check reads the contract's CURRENT state record. An owner can replace a contract's
    /// code with Update(), so a contract updated after a historical transfer is judged by its current code.
    ///
    /// Run: start the node with the <c>auditreplayscan</c> argument. It scans, writes
    /// <c>audit-replay-scan-&lt;utc&gt;.txt</c> to the database folder, prints a summary and exits.
    /// </summary>
    public static class AuditReplayScanService
    {
        public sealed record Hit(long Height, string TxHash, TransactionType Type, string Rule, string Reason);

        /// <summary>
        /// Returns every new-rule violation in one mined transaction. Pure apart from reading the
        /// contract's state-trie record (ContractData never changes after mint).
        /// </summary>
        public static List<Hit> CheckTransaction(Transaction tx, long height)
        {
            var hits = new List<Hit>();
            void Add(string rule, string reason) => hits.Add(new Hit(height, tx.Hash ?? "", tx.TransactionType, rule, reason));

            try
            {
                switch (tx.TransactionType)
                {
                    case TransactionType.VBTC_V2_WITHDRAWAL_REQUEST:
                        CheckWithdrawalRequest(tx, Add);
                        break;
                    case TransactionType.VBTC_V2_TRANSFER:
                        CheckTransfer(tx, Add);
                        break;
                    case TransactionType.VBTC_V2_BRIDGE_LOCK:
                        CheckBridgeLock(tx, Add);
                        break;
                }

                if (IsMintFamily(tx.TransactionType))
                    CheckMintOrDeploy(tx, Add);

                CheckFunctionRules(tx, Add);

                var preimageError = LedgerIntegrityRules.CanonicalPreimage(tx); // NEW-18
                if (preimageError != null)
                    Add("NEW-18 ambiguous preimage", preimageError);

                var saleError = LedgerIntegrityRules.SaleAmounts(tx); // NEW-17
                if (saleError != null)
                    Add("NEW-17 sale amounts", saleError);

                if (LedgerIntegrityRules.VaultCodeUnchanged(tx) is string vaultCodeError) // NEW-26 (follow-up)
                    Add("NEW-26 vault code changed", vaultCodeError);

                var coinbaseError = LedgerIntegrityRules.CoinbaseShape(tx); // NEW-16
                if (coinbaseError != null)
                    Add("NEW-16 coinbase shape", coinbaseError);
                if (((LedgerIntegrityRules.IsCoinbase(tx) && height == Globals.SpecialBlockHeight) || height == 0) && LedgerIntegrityRules.ContentMatchesHash(tx) is string contentError) // NEW-25 (correction: genesis and the special block)
                    Add("NEW-25 coinbase/genesis content vs hash", contentError);

                var uidError = LedgerIntegrityRules.ContractUids(tx); // NEW-10
                if (uidError != null)
                    Add("NEW-10 contract UID", uidError);
            }
            catch (Exception ex)
            {
                Add("SCAN-ERROR", $"Could not evaluate: {ex.Message}");
            }

            return hits;
        }

        private static bool IsMintFamily(TransactionType t) =>
            t is TransactionType.NFT_TX or TransactionType.NFT_MINT or TransactionType.NFT_BURN
              or TransactionType.FTKN_MINT or TransactionType.FTKN_TX or TransactionType.FTKN_BURN
              or TransactionType.TKNZ_MINT or TransactionType.TKNZ_TX or TransactionType.TKNZ_BURN
              or TransactionType.SC_MINT or TransactionType.SC_TX or TransactionType.SC_BURN
              or TransactionType.TKNZ_WD_ARB or TransactionType.TKNZ_WD_OWNER
              or TransactionType.VBTC_V2_CONTRACT_CREATE;

        private static void CheckContract(string? scUid, Action<string, string> add, string rule)
        {
            if (string.IsNullOrEmpty(scUid)) return; // pre-existing "missing field" rule, unchanged
            var sc = SmartContractStateTrei.GetSmartContractState(scUid);
            if (sc == null) return;                   // pre-existing "not found" rule, unchanged
            if (!VBTCService.IsVbtcV2Contract(sc))
                add(rule, $"Target contract {scUid} is not a vBTC V2 contract.");
        }

        private static void CheckWithdrawalRequest(Transaction tx, Action<string, string> add)
        {
            var jobj = JObject.Parse(tx.Data);
            if (jobj["Function"]?.ToObject<string>() == VBTCService.MultiWithdrawalFunction)
            {
                foreach (var input in jobj["Inputs"]?.ToObject<List<VBTCV2MultiWithdrawalInput>>() ?? new())
                    CheckContract(input.SCUID, add, "VX-01 contract-type (multi withdrawal)");
                return;
            }

            var amountError = VBTCService.GetVbtcAmountError(jobj["Amount"]?.ToObject<decimal?>(), "vBTC V2 withdrawal request");
            if (amountError != null) add("VX-01 amount (withdrawal)", amountError);

            var feeRate = jobj["FeeRate"]?.ToObject<int?>();
            if (feeRate.HasValue && feeRate.Value <= 0) add("VX-01 fee rate (withdrawal)", $"FeeRate {feeRate.Value} <= 0");

            CheckContract(jobj["ContractUID"]?.ToObject<string>(), add, "VX-01 contract-type (withdrawal)");
        }

        private static void CheckTransfer(Transaction tx, Action<string, string> add)
        {
            var jobj = JObject.Parse(tx.Data);
            var fn = jobj["Function"]?.ToObject<string>();
            if (fn == VBTCService.MultiTransferFunction && jobj["ContractUID"] == null)
            {
                foreach (var input in jobj["Inputs"]?.ToObject<List<VBTCV2MultiTransferInput>>() ?? new())
                    CheckContract(input.SCUID, add, "VX-01 contract-type (multi transfer)");
                return;
            }

            var amountError = VBTCService.GetVbtcAmountError(jobj["Amount"]?.ToObject<decimal?>(), "vBTC V2 transfer");
            if (amountError != null) add("VX-01 amount (transfer)", amountError);
            CheckContract(jobj["ContractUID"]?.ToObject<string>(), add, "VX-01 contract-type (transfer)");
        }

        private static void CheckBridgeLock(Transaction tx, Action<string, string> add)
        {
            var jobj = JObject.Parse(tx.Data);
            var amountError = VBTCService.GetVbtcAmountError(jobj["Amount"]?.ToObject<decimal?>(), "bridge lock");
            if (amountError != null) add("VX-01 amount (bridge lock)", amountError);
            CheckContract(jobj["ContractUID"]?.ToObject<string>(), add, "VX-01 contract-type (bridge lock)");
        }

        private static void CheckMintOrDeploy(Transaction tx, Action<string, string> add)
        {
            var (function, scUid, body, _) = SmartContractDeployBinding.ReadPayload(tx.Data);
            var isDeploy = function == "TokenDeploy()";
            if (!isDeploy && function != "Mint()") return;

            var error = SmartContractDeployBinding.Validate(body, scUid, tx.FromAddress, isDeploy, out _);
            if (error != null)
                add(isDeploy ? "VX-02 binding (TokenDeploy)" : "VX-02 binding (Mint)", $"{error} tx ContractUID={scUid}");
        }

        /// <summary>Stateless follow-up rules on function-dispatched transactions (NEW-04, NEW-05, VX-01 follow-up).</summary>
        private static void CheckFunctionRules(Transaction tx, Action<string, string> add)
        {
            if (string.IsNullOrEmpty(tx.Data)) return;
            var (_, _, scUid, function, _) = TransactionUtility.GetSCTXFunctionAndUID(tx);
            if (string.IsNullOrEmpty(function)) return;
            JObject? obj = null;
            JToken? first = null;
            try { obj = JObject.Parse(tx.Data); } catch { }
            try { if (obj == null) first = JArray.Parse(tx.Data).FirstOrDefault(); } catch { }
            var d = (JToken?)obj ?? first;
            if (d == null) return;

            switch (function)
            {
                case "TokenTransfer()":
                    {
                        var e = LedgerIntegrityRules.TokenTransfer(tx.FromAddress, tx.ToAddress, d["FromAddress"]?.ToObject<string?>(), d["ToAddress"]?.ToObject<string?>(), d["Amount"]?.ToObject<decimal?>())
                            ?? LedgerIntegrityRules.TokenTransferResolvesToSender(d["FromAddress"]?.ToObject<string?>(), d["ToAddress"]?.ToObject<string?>());
                        if (e != null) add("NEW-04 token transfer binding", e);
                        break;
                    }
                case "TokenBurn()":
                    {
                        var e = LedgerIntegrityRules.TokenBurn(tx.FromAddress, d["FromAddress"]?.ToObject<string?>(), d["Amount"]?.ToObject<decimal?>());
                        if (e != null) add("NEW-04 token burn binding", e);
                        break;
                    }
                case "TokenVoteTopicCast()":
                    {
                        var e = LedgerIntegrityRules.TokenVoteCast(tx.FromAddress, d["FromAddress"]?.ToObject<string?>());
                        if (e != null) add("NEW-04 token vote binding", e);
                        break;
                    }
                case "TransferCoin()":
                    {
                        var e = LedgerIntegrityRules.V1TransferAmount(d["Amount"]?.ToObject<decimal?>());
                        if (e != null) add("NEW-05 V1 amount", e);
                        if (!string.IsNullOrEmpty(scUid) && SmartContractStateTrei.GetSmartContractState(scUid) == null)
                            add("NEW-05 V1 missing contract", $"Contract {scUid} not found.");
                        break;
                    }
                case "TransferCoinMulti()":
                    {
                        var sigInput = d["SignatureInput"]?.ToObject<string?>() ?? "";
                        foreach (var input in d["Inputs"]?.ToObject<List<VBTCTransferInput>>() ?? new())
                        {
                            var e = LedgerIntegrityRules.V1TransferMultiInput(input, sigInput, tx.ToAddress, tx.FromAddress);
                            if (e != null) add("NEW-05 V1 multi input", e);
                            if (!string.IsNullOrEmpty(input?.SCUID) && SmartContractStateTrei.GetSmartContractState(input.SCUID) == null)
                                add("NEW-05 V1 missing contract", $"Contract {input.SCUID} not found.");
                        }
                        break;
                    }
                case "TransferVBTCV2()":
                    {
                        var e = VBTCService.GetVbtcAmountError(d["Amount"]?.ToObject<decimal?>(), "vBTC V2 transfer");
                        if (e != null) add("VX-01 amount (TransferVBTCV2 function)", e);
                        if (string.IsNullOrEmpty(scUid) || SmartContractStateTrei.GetSmartContractState(scUid) == null)
                            add("VX-01 missing contract (TransferVBTCV2 function)", $"Contract {scUid} not found.");
                        else
                            CheckContract(scUid, add, "VX-01 contract-type (TransferVBTCV2 function)");
                        break;
                    }
            }
        }

        /// <summary>
        /// NEW-05 zero-row balance rule, approximated (it is stateful): a non-owner legacy V1 sender must have received on
        /// that contract earlier in the chain. "Owner" is the contract's current owner or minter. Hits under this rule are
        /// candidates for manual review, not proof.
        /// </summary>
        private sealed class V1ReceiptTracker
        {
            private readonly HashSet<string> _received = new(StringComparer.Ordinal);
            public void Received(string scUid, string address) => _received.Add(scUid + "|" + address);
            public bool HasReceived(string scUid, string address) => _received.Contains(scUid + "|" + address);
        }

        private static void TrackV1(Transaction tx, long height, V1ReceiptTracker tracker, List<Hit> hits)
        {
            if (tx.TransactionType != TransactionType.TKNZ_TX || string.IsNullOrEmpty(tx.Data)) return;
            var (_, _, scUid, function, _) = TransactionUtility.GetSCTXFunctionAndUID(tx);
            if (function == "TransferCoin()" && !string.IsNullOrEmpty(scUid))
            {
                CheckV1Sender(scUid, tx.FromAddress, tx, height, tracker, hits);
                tracker.Received(scUid, tx.ToAddress);
            }
            else if (function == "TransferCoinMulti()")
            {
                try
                {
                    foreach (var input in JObject.Parse(tx.Data)["Inputs"]?.ToObject<List<VBTCTransferInput>>() ?? new())
                    {
                        if (string.IsNullOrEmpty(input?.SCUID)) continue;
                        CheckV1Sender(input.SCUID, input.FromAddress, tx, height, tracker, hits);
                        tracker.Received(input.SCUID, tx.ToAddress);
                    }
                }
                catch { }
            }
        }

        private static void CheckV1Sender(string scUid, string sender, Transaction tx, long height, V1ReceiptTracker tracker, List<Hit> hits)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUid);
            if (sc == null) return; // reported by the missing-contract rule
            if (sender == sc.OwnerAddress || sender == sc.MinterAddress) return;
            if (!tracker.HasReceived(scUid, sender))
                hits.Add(new Hit(height, tx.Hash ?? "", tx.TransactionType, "NEW-05 V1 zero-row sender (approximate — review)",
                    $"{sender} sends on {scUid} with no earlier receipt on that contract in this chain."));
        }

        private static readonly HashSet<string> BaseAddresses = new(StringComparer.Ordinal)
            { "Adnr_Base", "DecShop_Base", "Topic_Base", "Vote_Base", "Reserve_Base", "Token_Base" };

        /// <summary>Native VFX movements visible in the transaction stream, as StateData applies them.</summary>
        private static void TrackNative(Transaction tx, long height, Dictionary<string, decimal> bal)
        {
            void Add(string? a, decimal v) { if (!string.IsNullOrEmpty(a)) { bal.TryGetValue(a, out var b); bal[a] = b + v; } }
            var coinbase = tx.FromAddress == "Coinbase_TrxFees" || tx.FromAddress == "Coinbase_BlkRwd";
            if (height > 0 && !coinbase && !VerifiedXCore.Privacy.PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType))
                Add(tx.FromAddress, -(tx.Amount + tx.Fee + SameBlockDebitGuard.SaleCompletionPayments(tx)));
            if (tx.ToAddress != null && BaseAddresses.Contains(tx.ToAddress))
                return;
            var reserveSender = tx.FromAddress?.StartsWith("xRBX") == true; // credited to LockedBalance (not spendable)
            if ((coinbase || tx.TransactionType == TransactionType.TX || tx.TransactionType == TransactionType.VFX_UNSHIELD) && !reserveSender)
                Add(tx.ToAddress, tx.Amount);
        }

        /// <summary>Scans every block in the local database, in height order.</summary>
        public static (long Blocks, long Transactions, List<Hit> Hits) ScanChain(Action<string>? progress = null)
        {
            var blocks = BlockchainData.GetBlocks();
            var tip = Globals.LastBlock?.Height ?? -1;
            var hits = new List<Hit>();
            long blockCount = 0, txCount = 0;
            const long progressEvery = 100_000;
            var v1 = new V1ReceiptTracker();
            var canonicalUids = new Dictionary<string, string>(StringComparer.Ordinal); // NEW-07
            var native = new Dictionary<string, decimal>(StringComparer.Ordinal);       // NEW-07 native: reconstructed balances

            // AUDIT-PREP (follow-up): one streaming pass in height order (index order). The per-batch range query
            // (Height >= a && Height <= b) re-read the collection for every batch, so a mainnet scan never finished.
            {
                foreach (var block in blocks.Query().OrderBy(b => b.Height).ToEnumerable())
                {
                    if (block.Height > tip) break;
                    if (block.Height > 0 && block.Height % progressEvery == 0)
                        progress?.Invoke($"Scanned heights 0..{block.Height - 1} of {tip} — {hits.Count} hit(s) so far");
                    blockCount++;
                    LedgerIntegrityRules.NormalizeTransactionHeights(block); // NEW-13: as validation and apply do
                    if (block.Height == 0 && block.Transactions != null && block.MerkleRoot != new Block { Transactions = block.Transactions }.MerkleRootOf()) // NEW-25
                        hits.Add(new Hit(0, "", TransactionType.TX, "NEW-25 genesis merkle root", "Genesis merkle root does not match its transactions."));
                    var createdInBlock = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // NEW-06
                    var debitsInBlock = new Dictionary<SameBlockDebitGuard.DebitKey, (int N, decimal Sum, Transaction Last)>(); // NEW-07
                    var sweepState = new SameBlockDebitGuard.State(_ => null) { BlockHeight = block.Height }; // NEW-07: balances not judged, only the Recover() rule
                    foreach (var tx in block.Transactions ?? new List<Transaction>())
                    {
                        txCount++;
                        hits.AddRange(CheckTransaction(tx, block.Height));
                        var dup = LedgerIntegrityRules.RegisterCreationInBlock(tx, createdInBlock);
                        if (dup != null)
                            hits.Add(new Hit(block.Height, tx.Hash ?? "", tx.TransactionType, "NEW-06 duplicate creation in block", dup));
                        try { TrackV1(tx, block.Height, v1, hits); } catch { }
                        if (tx.FromAddress != "Coinbase_TrxFees" && tx.FromAddress != "Coinbase_BlkRwd")
                        {
                            var (sweepOk, sweepReason) = SameBlockDebitGuard.TryRegister(tx, sweepState);
                            if (!sweepOk)
                                hits.Add(new Hit(block.Height, tx.Hash ?? "", tx.TransactionType, "NEW-07/24 block rule (Recover() alone, no repeated transaction)", sweepReason));
                        }
                        foreach (var (rawKey, amount) in SameBlockDebitGuard.GetDebits(tx))
                        {
                            var key = SameBlockDebitGuard.Canonical(rawKey, canonicalUids);
                            debitsInBlock.TryGetValue(key, out var d);
                            debitsInBlock[key] = (d.N + 1, d.Sum + amount, tx);
                        }
                    }
                    // NEW-07 native: judged against the balance reconstructed from every earlier block (credits that
                    // happen outside the transaction stream, e.g. reserve unlocks, are missing, so balances are
                    // understated: this over-reports, never under-reports, except for reserves swept by Recover()).
                    foreach (var (key, d) in debitsInBlock)
                        if (key.Kind == SameBlockDebitGuard.LedgerKind.Native && d.N >= 2 && block.Height > 0)
                        {
                            native.TryGetValue(key.Holder, out var bal);
                            if (d.Sum > bal)
                                hits.Add(new Hit(block.Height, d.Last.Hash ?? "", d.Last.TransactionType, "NEW-07 native candidate: spends exceed the reconstructed balance",
                                    $"{key.Holder}: {d.N} transactions spend {d.Sum} VFX; reconstructed balance before the block {bal}"));
                        }
                    foreach (var tx in block.Transactions ?? new List<Transaction>())
                        TrackNative(tx, block.Height, native);

                    foreach (var (key, d) in debitsInBlock)
                        if (key.Kind != SameBlockDebitGuard.LedgerKind.Native && d.N >= 2)
                            hits.Add(new Hit(block.Height, d.Last.Hash ?? "", d.Last.TransactionType, "NEW-07 candidate: several debits by one holder on one contract in block",
                                $"{key.Kind} {key.ContractUid} holder {key.Holder}: {d.N} debits totalling {d.Sum}; compare with the balance at height {block.Height - 1}"));
                }
                progress?.Invoke($"Scanned heights 0..{tip} of {tip} — {hits.Count} hit(s)");
            }

            return (blockCount, txCount, hits);
        }

        /// <summary>Entry point for the <c>auditreplayscan</c> startup argument.</summary>
        public static string RunAndWriteReport()
        {
            var startedUtc = DateTime.UtcNow;
            var (blocks, txs, hits) = ScanChain(msg => Console.WriteLine(msg));

            var sb = new StringBuilder();
            sb.AppendLine("VerifiedX security-audit replay scan (VX-01, VX-02, NEW-04, NEW-05, NEW-06, NEW-07 candidates)");
            sb.AppendLine($"Network: {(Globals.IsTestNet ? "testnet" : "mainnet")}");
            sb.AppendLine($"Build: {Globals.CLIVersion}");
            sb.AppendLine($"Started (UTC): {startedUtc:O}");
            sb.AppendLine($"Tip height: {Globals.LastBlock?.Height}");
            sb.AppendLine($"Blocks scanned: {blocks}");
            sb.AppendLine($"Transactions scanned: {txs}");
            sb.AppendLine($"Hits: {hits.Count}");
            foreach (var g in hits.GroupBy(h => h.Rule).OrderBy(g => g.Key))
                sb.AppendLine($"  {g.Key}: {g.Count()}");
            sb.AppendLine(hits.Count == 0
                ? "RESULT: PASS — no historical transaction violates the new rules; ungated replay is safe."
                : "RESULT: FAIL — historical transactions below violate the new rules; do NOT deploy ungated.");
            sb.AppendLine();
            foreach (var h in hits)
                sb.AppendLine($"height={h.Height} tx={h.TxHash} type={h.Type} rule=\"{h.Rule}\" reason=\"{h.Reason}\"");

            var path = Path.Combine(GetPathUtility.GetDatabasePath(), $"audit-replay-scan-{startedUtc:yyyyMMddTHHmmssZ}.txt");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
    }
}
