using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// A watched vault's vBTC ledger kept within what its one database document can hold (fourth review, rounds 5 and 6).
    ///
    /// A vault's ledger is a list of rows inside the contract's state document, two rows a transfer, never pruned. LiteDB
    /// refuses a document above <see cref="MaxDocumentBytes"/>, and its writer swallowed the refusal: once a busy or
    /// spammed vault reached it (about 80,000 transfers), every transfer and request on it passed validation and wrote
    /// nothing, while its withdrawals were still pinned and paid, so one balance could be withdrawn again and again.
    /// <list type="bullet">
    /// <item>Every row names one address and a sentinel ("+" or "-"), and every reader reads an address's balance as the
    /// sum of the rows that name it. So the rows can be replaced by one row an address with that sum, exactly
    /// (<see cref="Compact"/>), and are, on a watched vault, once that saves <see cref="CompactSavesRows"/> rows. The
    /// last <see cref="RawTailRows"/> rows are left as they are.</item>
    /// <item>The ledger names at most <see cref="MaxHolders"/> addresses (<see cref="LedgerAddresses"/>): every address
    /// a row names, a zero balance included until compaction drops it. A credit that would name one more is refused
    /// beyond that (<see cref="CreditRefusal"/>): a transfer, a reserve's delayed transfer when it falls due, and an
    /// unlock from the bridge. The vault's owner is never refused.</item>
    /// <item>A credit nobody can refuse, the refund of a withdrawal that is not paid, goes to an address the ledger still
    /// names: compaction keeps an address with nothing left while it has a withdrawal open or a bridge lock not yet
    /// unlocked on the vault (a row of zero, <see cref="OpenClaimants"/>). Before round 6 the count was of balances
    /// only, so a requester who escrowed everything freed a place that a new holder took, and the refund then put the
    /// requester back above the limit; enough at once took the document past LiteDB's limit and froze the vault.</item>
    /// <item>A watched vault's state is written so that a failure is thrown, not swallowed
    /// (SmartContractStateTrei.UpdateSmartContract), and a withdrawal's debit is written before its row.</item>
    /// </list>
    /// What is left is what one block can add past the limit (its credits are each judged against the state before it,
    /// under 20,000 in a 10 MB block) and a changed owner or a recovery address: far inside the document's room.
    /// </summary>
    public static class BtcVaultLedger
    {
        /// <summary>LiteDB's largest document (LiteDB.Constants.MAX_DOCUMENT_SIZE of the bundled build), bytes.</summary>
        public const int MaxDocumentBytes = 16_683_050;

        /// <summary>Addresses a watched vault's ledger may name (about 10 MB of rows at about 100 bytes a row).</summary>
        public const int MaxHolders = 100_000;

        /// <summary>Tests only: a smaller holder limit.</summary>
        internal static int? MaxHoldersOverride;
        private static int HolderLimit => MaxHoldersOverride ?? MaxHolders;

        /// <summary>A watched vault's ledger is compacted once that removes at least this many rows.</summary>
        public const int CompactSavesRows = 4_000;

        /// <summary>
        /// The newest rows compaction leaves as they are: the debit of a withdrawal request or a bridge lock is written
        /// before the request's or the lock's own record, so its address must still be named when that record is saved.
        /// </summary>
        public const int RawTailRows = 16;

        private static bool IsSentinel(string? address) => address == "+" || address == "-";

        private static bool IsReal(string? address) => !string.IsNullOrEmpty(address) && !IsSentinel(address);

        /// <summary>
        /// Replaces the rows, all but the newest <see cref="RawTailRows"/>, by one row an address holding the sum of the
        /// rows that name it (a credit row from "+" for a positive sum, a debit row to "-" for a negative one), in
        /// address order, followed by the newest rows unchanged. An address whose rows sum to zero is dropped, unless it
        /// is in <paramref name="keepAtZero"/>: it then keeps a debit row of zero. Every address's sum, and the sum of all
        /// rows, stay exactly as they were. Nothing changes (false) when a row names two addresses or none, or when it
        /// would remove fewer than <paramref name="minimumSaved"/> rows.
        /// </summary>
        public static bool Compact(SmartContractStateTrei? contract, int minimumSaved = CompactSavesRows, Func<ISet<string>>? keepAtZero = null)
        {
            var rows = contract?.SCStateTreiTokenizationTXes;
            if (rows == null || rows.Count < minimumSaved) return false;
            var prefixCount = Math.Max(0, rows.Count - RawTailRows);
            var net = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null) return false;
                bool fromReal = IsReal(row.FromAddress), toReal = IsReal(row.ToAddress);
                if (fromReal == toReal) return false;
                if (i >= prefixCount) continue; // the tail is only checked
                var address = fromReal ? row.FromAddress : row.ToAddress;
                net[address] = (net.TryGetValue(address, out var sum) ? sum : 0M) + row.Amount;
            }
            int zeros = net.Count(kv => kv.Value == 0M);
            ISet<string>? keep = null;
            if (zeros > 0 && keepAtZero != null)
            {
                // Read only when some address would be dropped at zero, and before deciding whether to compact.
                if (prefixCount - (net.Count - zeros) < minimumSaved) return false;
                keep = keepAtZero();
            }
            var compacted = new List<SmartContractStateTreiTokenizationTX>(net.Count + rows.Count - prefixCount);
            foreach (var kv in net)
            {
                if (kv.Value > 0M)
                    compacted.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = kv.Key, Amount = kv.Value });
                else if (kv.Value < 0M || (keep != null && keep.Contains(kv.Key)))
                    compacted.Add(new SmartContractStateTreiTokenizationTX { FromAddress = kv.Key, ToAddress = "-", Amount = kv.Value });
            }
            if (rows.Count - (compacted.Count + rows.Count - prefixCount) < minimumSaved) return false;
            compacted.AddRange(rows.Skip(prefixCount));
            contract!.SCStateTreiTokenizationTXes = compacted;
            return true;
        }

        /// <summary>Compacts a watched vault's ledger as its state is written, keeping the addresses that may yet be refunded.</summary>
        public static bool CompactWatched(SmartContractStateTrei contract) =>
            Compact(contract, CompactSavesRows, () => OpenClaimants(contract.SmartContractUID));

        /// <summary>
        /// The addresses that may yet be credited on the vault without a transaction that could be refused: requesters
        /// of its withdrawals not yet closed (a refund), and owners of its bridge locks not yet unlocked or blacklisted
        /// (the owner's own unlock). Sweeps and bridge exits escrow nothing and are refunded to nobody.
        /// </summary>
        public static ISet<string> OpenClaimants(string scUID)
        {
            var claimants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in BtcAttestStore.Db.GetCollection<VaultWithdrawalRow>(BtcAttestStore.Withdrawals).Find(x => x.SmartContractUID == scUID))
                if ((row.State == VaultWithdrawalState.Waiting || row.State == VaultWithdrawalState.Pinned) && !row.IsSweep && !row.IsBridgeExit
                    && !string.IsNullOrEmpty(row.Requester))
                    claimants.Add(row.Requester);
            foreach (var bridgeLock in VBTCBridgeLockState.GetCollection().Find(x => x.SmartContractUID == scUID))
                if (!bridgeLock.IsUnlocked && !bridgeLock.IsBlacklisted && !string.IsNullOrEmpty(bridgeLock.OwnerAddress))
                    claimants.Add(bridgeLock.OwnerAddress);
            return claimants;
        }

        /// <summary>The addresses the vault's ledger names (any balance, zero included).</summary>
        public static int LedgerAddresses(SmartContractStateTrei? contract)
        {
            var rows = contract?.SCStateTreiTokenizationTXes;
            if (rows == null || rows.Count == 0) return 0;
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (IsReal(row?.FromAddress)) named.Add(row!.FromAddress);
                if (IsReal(row?.ToAddress)) named.Add(row!.ToAddress);
            }
            return named.Count;
        }

        private static bool Names(SmartContractStateTrei contract, string address) =>
            contract.SCStateTreiTokenizationTXes?.Any(x => x.FromAddress == address || x.ToAddress == address) ?? false;

        /// <summary>
        /// Why a credit to <paramref name="recipient"/> on this vault is refused for its holder limit: the vault is
        /// watched, the recipient is not its owner and not yet named on its ledger, and the ledger already names
        /// <see cref="MaxHolders"/> addresses. Null otherwise.
        /// </summary>
        public static string? CreditRefusal(SmartContractStateTrei? contract, string? recipient)
        {
            if (contract == null || string.IsNullOrEmpty(recipient) || !BtcAttestBalance.IsAttestedVault(contract.SmartContractUID)) return null;
            if (string.Equals(recipient, contract.OwnerAddress, StringComparison.Ordinal) || Names(contract, recipient)) return null;
            return LedgerAddresses(contract) >= HolderLimit
                ? $"Vault {contract.SmartContractUID} has {HolderLimit} addresses on its ledger, the most a vault holds; vBTC is moved on it to one of them, or held elsewhere."
                : null;
        }

        /// <summary>
        /// Why a transaction is refused for the holder limit of a watched vault it credits: a vBTC transfer (single or
        /// multi), or an unlock from the bridge (to the lock's owner, or a pool unlock to its destination on each vault
        /// it draws on). Null otherwise.
        /// </summary>
        public static string? HolderLimitRefusal(Transaction tx)
        {
            try
            {
                if (tx?.Data == null) return null;
                var credits = new List<(string? Uid, string? Recipient)>();
                switch (tx.TransactionType)
                {
                    case TransactionType.VBTC_V2_BRIDGE_UNLOCK:
                    {
                        var data = JObject.Parse(tx.Data);
                        var lockId = data["LockId"]?.Type == JTokenType.String ? (string?)data["LockId"] : null;
                        var uid = (lockId == null ? null : VBTCBridgeLockState.GetByLockId(lockId)?.SmartContractUID)
                            ?? (data["ContractUID"]?.Type == JTokenType.String ? (string?)data["ContractUID"] : null);
                        credits.Add((uid, tx.FromAddress));
                        break;
                    }
                    case TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK:
                    {
                        var data = JObject.Parse(tx.Data);
                        var destination = data["VfxDestinationAddress"]?.Type == JTokenType.String ? (string?)data["VfxDestinationAddress"] : null;
                        if (data["Allocations"] is JArray allocations)
                            foreach (var allocation in allocations.OfType<JObject>())
                                if (allocation["LockId"]?.Type == JTokenType.String)
                                    credits.Add((VBTCBridgeLockState.GetByLockId((string)allocation["LockId"]!)?.SmartContractUID, destination));
                        break;
                    }
                    default:
                    {
                        if (string.IsNullOrEmpty(tx.ToAddress)) return null;
                        var function = tx.TransactionType == TransactionType.VBTC_V2_TRANSFER ? "TransferVBTCV2()" : LedgerIntegrityRules.DispatchedCall(tx.Data).Function;
                        if (tx.TransactionType != TransactionType.VBTC_V2_TRANSFER && function != "TransferVBTCV2()" && function != "TransferVBTCMultiV2()") return null;
                        var token = JToken.Parse(tx.Data);
                        var obj = token is JArray array ? array.FirstOrDefault() as JObject : token as JObject;
                        if (obj == null) return null;
                        if (obj["Inputs"] is JArray inputs)
                        {
                            foreach (var input in inputs.OfType<JObject>())
                                if (input["SCUID"]?.Type == JTokenType.String)
                                    credits.Add(((string)input["SCUID"]!, tx.ToAddress));
                        }
                        else if (obj["ContractUID"]?.Type == JTokenType.String)
                            credits.Add(((string)obj["ContractUID"]!, tx.ToAddress));
                        break;
                    }
                }
                foreach (var (uid, recipient) in credits.Distinct())
                {
                    if (string.IsNullOrEmpty(uid) || !BtcAttestBalance.IsAttestedVault(uid)) continue;
                    var refusal = CreditRefusal(SmartContractStateTrei.GetSmartContractState(uid), recipient);
                    if (refusal != null) return refusal;
                }
                return null;
            }
            catch (Newtonsoft.Json.JsonException) { return null; } // unreadable data is refused by the transaction's own rules
        }
    }
}
