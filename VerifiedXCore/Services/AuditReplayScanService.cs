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
    /// <see cref="VBTCService.IsVbtcV2Contract"/>, <see cref="SmartContractDeployBinding.Validate"/>), so it
    /// cannot drift from the rules it certifies. State-dependent checks that existed before the audit fixes
    /// (balances, active-request gates) are out of scope; they are unchanged.
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

        /// <summary>Scans every block in the local database, in height order.</summary>
        public static (long Blocks, long Transactions, List<Hit> Hits) ScanChain(Action<string>? progress = null)
        {
            var blocks = BlockchainData.GetBlocks();
            var tip = Globals.LastBlock?.Height ?? -1;
            var hits = new List<Hit>();
            long blockCount = 0, txCount = 0;
            const long batch = 1000;

            for (long start = 0; start <= tip; start += batch)
            {
                var end = Math.Min(tip, start + batch - 1);
                var page = blocks.Query().Where(b => b.Height >= start && b.Height <= end).ToList().OrderBy(b => b.Height);
                foreach (var block in page)
                {
                    blockCount++;
                    foreach (var tx in block.Transactions ?? new List<Transaction>())
                    {
                        txCount++;
                        hits.AddRange(CheckTransaction(tx, block.Height));
                    }
                }
                progress?.Invoke($"Scanned heights {start}..{end} of {tip} — {hits.Count} hit(s) so far");
            }

            return (blockCount, txCount, hits);
        }

        /// <summary>Entry point for the <c>auditreplayscan</c> startup argument.</summary>
        public static string RunAndWriteReport()
        {
            var startedUtc = DateTime.UtcNow;
            var (blocks, txs, hits) = ScanChain(msg => Console.WriteLine(msg));

            var sb = new StringBuilder();
            sb.AppendLine("VerifiedX security-audit replay scan (VX-01, VX-02)");
            sb.AppendLine($"Network: {(Globals.IsTestNet ? "testnet" : "mainnet")}");
            sb.AppendLine($"Build: {Globals.CLIVersion}");
            sb.AppendLine($"Started (UTC): {startedUtc:O}");
            sb.AppendLine($"Tip height: {Globals.LastBlock?.Height}");
            sb.AppendLine($"Blocks scanned: {blocks}");
            sb.AppendLine($"Transactions scanned: {txs}");
            sb.AppendLine($"Hits: {hits.Count}");
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
