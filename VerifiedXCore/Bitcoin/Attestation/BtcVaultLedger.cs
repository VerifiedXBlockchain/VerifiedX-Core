using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// A watched vault's vBTC ledger kept within what its one database document can hold (fourth review, round 5).
    ///
    /// A vault's ledger is a list of rows inside the contract's state document, two rows a transfer, never pruned. LiteDB
    /// refuses a document above <see cref="MaxDocumentBytes"/>, and its writer swallowed the refusal: once a busy or
    /// spammed vault reached it (about 80,000 transfers), every transfer and request on it passed validation and wrote
    /// nothing, while its withdrawals were still pinned and paid, so one balance could be withdrawn again and again.
    /// <list type="bullet">
    /// <item>Every row names one address and a sentinel ("+" or "-"), and every reader reads an address's balance as the
    /// sum of the rows that name it. So the rows can be replaced by one row an address with that sum, exactly
    /// (<see cref="Compact"/>), and are, on a watched vault, once that saves <see cref="CompactSavesRows"/> rows.</item>
    /// <item>A watched vault holds at most <see cref="MaxHolders"/> addresses with a balance: a transfer to a new holder
    /// beyond that is refused (<see cref="HolderLimitRefusal"/>). With compaction the document stays far below the
    /// limit; holders already there keep transferring and withdrawing.</item>
    /// <item>A watched vault's state is written so that a failure is thrown, not swallowed
    /// (SmartContractStateTrei.UpdateSmartContract), and a withdrawal's debit is written before its row.</item>
    /// </list>
    /// </summary>
    public static class BtcVaultLedger
    {
        /// <summary>LiteDB's largest document (LiteDB.Constants.MAX_DOCUMENT_SIZE of the bundled build), bytes.</summary>
        public const int MaxDocumentBytes = 16_683_050;

        /// <summary>Addresses with a balance a watched vault may hold (about 10 MB of rows at about 100 bytes a row).</summary>
        public const int MaxHolders = 100_000;

        /// <summary>Tests only: a smaller holder limit.</summary>
        internal static int? MaxHoldersOverride;
        private static int HolderLimit => MaxHoldersOverride ?? MaxHolders;

        /// <summary>A watched vault's ledger is compacted once that removes at least this many rows.</summary>
        public const int CompactSavesRows = 4_000;

        private static bool IsSentinel(string? address) => address == "+" || address == "-";

        /// <summary>
        /// Replaces the rows by one row an address holding the sum of the rows that name it (a credit row from "+" for a
        /// positive sum, a debit row to "-" for a negative one; addresses summing to zero are dropped), in address order.
        /// Every address's sum, and the sum of all rows, stay exactly as they were. Nothing changes (false) when a row
        /// names two addresses or none, or when it would remove fewer than <paramref name="minimumSaved"/> rows.
        /// </summary>
        public static bool Compact(SmartContractStateTrei? contract, int minimumSaved = CompactSavesRows)
        {
            var rows = contract?.SCStateTreiTokenizationTXes;
            if (rows == null || rows.Count < minimumSaved) return false;
            var net = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null) return false;
                bool fromReal = !string.IsNullOrEmpty(row.FromAddress) && !IsSentinel(row.FromAddress);
                bool toReal = !string.IsNullOrEmpty(row.ToAddress) && !IsSentinel(row.ToAddress);
                if (fromReal == toReal) return false;
                var address = fromReal ? row.FromAddress : row.ToAddress;
                net[address] = (net.TryGetValue(address, out var sum) ? sum : 0M) + row.Amount;
            }
            var compacted = net.Where(kv => kv.Value != 0M).Select(kv => kv.Value > 0M
                    ? new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = kv.Key, Amount = kv.Value }
                    : new SmartContractStateTreiTokenizationTX { FromAddress = kv.Key, ToAddress = "-", Amount = kv.Value })
                .ToList();
            if (rows.Count - compacted.Count < minimumSaved) return false;
            contract!.SCStateTreiTokenizationTXes = compacted;
            return true;
        }

        /// <summary>The addresses holding a non-zero balance on the vault's ledger.</summary>
        public static int HoldersWithABalance(SmartContractStateTrei? contract)
        {
            var rows = contract?.SCStateTreiTokenizationTXes;
            if (rows == null || rows.Count == 0) return 0;
            var net = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in rows)
                foreach (var address in new[] { row?.FromAddress, row?.ToAddress })
                    if (!string.IsNullOrEmpty(address) && !IsSentinel(address))
                        net[address] = (net.TryGetValue(address, out var sum) ? sum : 0M) + row!.Amount;
            return net.Count(kv => kv.Value != 0M);
        }

        private static decimal BalanceOf(SmartContractStateTrei contract, string address) =>
            contract.SCStateTreiTokenizationTXes?.Where(x => x.FromAddress == address || x.ToAddress == address).Sum(x => x.Amount) ?? 0M;

        /// <summary>
        /// Why a vBTC transfer is refused for the holder limit of a watched vault it moves vBTC on: the recipient holds
        /// nothing there yet and the vault already has <see cref="MaxHolders"/> holders with a balance. Null otherwise.
        /// </summary>
        public static string? HolderLimitRefusal(Transaction tx)
        {
            try
            {
                if (tx?.Data == null || string.IsNullOrEmpty(tx.ToAddress)) return null;
                var function = tx.TransactionType == TransactionType.VBTC_V2_TRANSFER ? "TransferVBTCV2()" : LedgerIntegrityRules.DispatchedCall(tx.Data).Function;
                if (tx.TransactionType != TransactionType.VBTC_V2_TRANSFER && function != "TransferVBTCV2()" && function != "TransferVBTCMultiV2()") return null;
                var token = JToken.Parse(tx.Data);
                var obj = token is JArray array ? array.FirstOrDefault() as JObject : token as JObject;
                if (obj == null) return null;
                var vaults = new List<string>();
                if (obj["Inputs"] is JArray inputs)
                    vaults.AddRange(inputs.OfType<JObject>().Select(i => i["SCUID"]?.Type == JTokenType.String ? (string?)i["SCUID"] : null).Where(u => !string.IsNullOrEmpty(u))!);
                else if (obj["ContractUID"]?.Type == JTokenType.String)
                    vaults.Add((string)obj["ContractUID"]!);
                foreach (var uid in vaults.Distinct(StringComparer.Ordinal))
                {
                    if (!BtcAttestBalance.IsAttestedVault(uid)) continue;
                    var contract = SmartContractStateTrei.GetSmartContractState(uid);
                    if (contract == null || BalanceOf(contract, tx.ToAddress) != 0M) continue;
                    if (HoldersWithABalance(contract) >= HolderLimit)
                        return $"Vault {uid} has {HolderLimit} holders with a balance, the most a vault holds; vBTC is moved on it to one of them, or held elsewhere.";
                }
                return null;
            }
            catch (Newtonsoft.Json.JsonException) { return null; } // unreadable data is refused by the transfer's own rules
        }
    }
}
