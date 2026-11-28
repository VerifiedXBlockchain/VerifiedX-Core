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
    /// under 20,000 in a 10 MB block) and a recovery address: far inside the document's room. A full vault changes hands
    /// (Transfer(), a sale's completion) only to an address its ledger names.
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
            return VaultLedgerStore.HolderCount(contract);
        }

        private static bool Names(SmartContractStateTrei contract, string address) => VaultLedgerStore.Names(contract, address);

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
        /// The smallest balance a holder keeps on a watched vault from Globals.VaultMinimumPositionHeight (10,000 sats):
        /// filling the holder limit then ties up 100,000 times this, and every position can pay for its own withdrawal.
        /// </summary>
        public const decimal MinPositionBtc = 0.0001M;

        /// <summary>
        /// Why a debit of <paramref name="amount"/> from <paramref name="holder"/> on a watched vault is refused, the
        /// holder having <paramref name="available"/> before it (its balance less what the block already debits): a holder
        /// moves at least <see cref="MinPositionBtc"/> or everything it has, and keeps nothing or at least that much. The
        /// owner, whose address is never refused by the holder limit, is held only to the first, and only for a transfer
        /// (the way new positions are made). So every position made from the height is nothing or at least that much, and
        /// so is every amount escrowed or locked, and with it every refund and every unlock to the locker. A position
        /// from before the height may still be moved whole. Null otherwise, and before the height.
        /// </summary>
        public static string? PositionRefusal(string scUID, string holder, decimal? available, decimal amount, bool isTransfer, long height)
        {
            if (height < Globals.VaultMinimumPositionHeight || amount <= 0M || !BtcAttestBalance.IsAttestedVault(scUID)) return null;
            var contract = SmartContractStateTrei.GetSmartContractState(scUID);
            if (contract == null) return null;
            if (string.Equals(holder, contract.OwnerAddress, StringComparison.Ordinal))
                return isTransfer && amount < MinPositionBtc
                    ? $"A transfer on vault {contract.SmartContractUID} moves at least {MinPositionBtc} vBTC; this one moves {amount}."
                    : null;
            if (available == null) return null;
            if (amount < MinPositionBtc && amount != available.Value)
                return $"A holder on vault {contract.SmartContractUID} moves at least {MinPositionBtc} vBTC or all it has ({available.Value}); this moves {amount}.";
            var left = available.Value - amount;
            if (left > 0M && left < MinPositionBtc)
                return $"A holder on vault {contract.SmartContractUID} keeps nothing or at least {MinPositionBtc} vBTC; moving {amount} of {available.Value} leaves {left}.";
            return null;
        }

        /// <summary>
        /// Why an unlock from the bridge pool may not credit <paramref name="share"/> of watched vault
        /// <paramref name="scUID"/> to <paramref name="destination"/>: it would leave the destination, not the vault's
        /// owner, with less than <see cref="MinPositionBtc"/> there. An unlock's amount is what a burner burned on Base, so
        /// it cannot be held to a transfer's rule: a one-satoshi burn to each of 100,000 new addresses filled the holder
        /// limit. Such a burn is not paid from a watched vault (as a Bitcoin exit too small to pay is not). Used by
        /// validation and by the casters' plan alike. Null otherwise, and before Globals.VaultMinimumPositionHeight.
        /// </summary>
        public static string? UnlockShareRefusal(string? scUID, string? destination, decimal share, long height)
        {
            if (share <= 0M || share >= UnlockShareMinimum(scUID, destination, height)) return null;
            return $"{SameBlockDebitGuard.MinimumPositionPrefix}: an unlock of {share} on vault {scUID} leaves {destination} with less than the {MinPositionBtc} vBTC a holder keeps.";
        }

        /// <summary>
        /// The smallest share of watched vault <paramref name="scUID"/> an unlock may give <paramref name="destination"/>:
        /// what it lacks of <see cref="MinPositionBtc"/> there; 0 for the vault's owner, another kind of vault, and before
        /// Globals.VaultMinimumPositionHeight.
        /// </summary>
        public static decimal UnlockShareMinimum(string? scUID, string? destination, long height)
        {
            if (height < Globals.VaultMinimumPositionHeight || string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(destination)
                || !BtcAttestBalance.IsAttestedVault(scUID))
                return 0M;
            var held = SameBlockDebitGuard.CommittedBalance(new SameBlockDebitGuard.DebitKey(SameBlockDebitGuard.LedgerKind.VbtcV2, scUID, destination));
            return held == null ? 0M : Math.Max(0M, MinPositionBtc - held.Value); // null: the owner
        }

        /// <summary>
        /// <see cref="UnlockShareRefusal"/> for each watched vault a pool unlock draws on (its allocations on one vault
        /// added up). Null for every other transaction.
        /// </summary>
        public static string? PoolUnlockPositionRefusal(Transaction tx, long height)
        {
            if (tx?.Data == null || tx.TransactionType != TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK || height < Globals.VaultMinimumPositionHeight) return null;
            try
            {
                var data = JObject.Parse(tx.Data);
                // Read as HolderLimitRefusal reads them, as validation and apply do.
                var destination = VerifiedXCore.Bitcoin.Services.BridgeTxRules.PoolUnlockCredited(data, height);
                var shares = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var allocation in data["Allocations"]?.ToObject<List<VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation>>() ?? new())
                {
                    if (string.IsNullOrWhiteSpace(allocation?.LockId) || allocation.UnlockAmount <= 0M) continue;
                    var uid = VBTCBridgeLockState.GetByLockId(allocation.LockId)?.SmartContractUID;
                    if (string.IsNullOrEmpty(uid)) continue;
                    shares[uid] = (shares.TryGetValue(uid, out var s) ? s : 0M) + allocation.UnlockAmount;
                }
                foreach (var (uid, share) in shares)
                    if (UnlockShareRefusal(uid, destination, share, height) is { } refusal)
                        return refusal;
                return null;
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException or FormatException or InvalidCastException)
            { return null; } // unreadable data is refused by the transaction's own rules
        }

        /// <summary>The longest sale key a watched vault's sale carries (a wallet's is 10 letters).</summary>
        public const int MaxSaleKeyLength = 64;

        /// <summary>
        /// The most purchase keys a watched vault keeps (each completed sale adds one, and from
        /// Globals.SaleListingKeyHeight each cancelled one; never removed).
        /// </summary>
        public const int MaxPurchaseKeys = 1_000;

        /// <summary>
        /// Why a sale of a watched vault is refused: its key is longer than <see cref="MaxSaleKeyLength"/>, or the vault
        /// already keeps <see cref="MaxPurchaseKeys"/> purchase keys. Each completed sale adds its key to the vault's
        /// state document for good, and a key could be about 29 KB: an owner selling the vault to itself about 575 times
        /// took the document past LiteDB's limit and froze every holder (fourth review, round 9). After the last sale it
        /// can still change hands by Transfer().
        /// </summary>
        public static string? SaleRefusal(Transaction tx)
        {
            try
            {
                if (tx?.Data == null || tx.TransactionType != TransactionType.NFT_SALE) return null;
                var data = JObject.Parse(tx.Data);
                var function = data["Function"]?.Type == JTokenType.String ? (string)data["Function"]! : null;
                if (function != "Sale_Start()" && function != "M_Sale_Start()" && function != "Sale_Complete()" && function != "M_Sale_Complete()" && function != "Sale_Cancel()") return null;
                var uid = data["ContractUID"]?.Type == JTokenType.String ? (string)data["ContractUID"]! : null;
                if (string.IsNullOrEmpty(uid) || !BtcAttestBalance.IsAttestedVault(uid)) return null;
                // Read as validation and apply read it (a number becomes its digits), so the bound is exact (round 10).
                if (data["KeySign"] is JToken keyToken && keyToken.Type != JTokenType.Null && keyToken.Type != JTokenType.String)
                    return $"A sale of vault {uid} carries its key as text.";
                var key = data["KeySign"]?.Type == JTokenType.String ? (string)data["KeySign"]! : null;
                if (key != null && key.Length > MaxSaleKeyLength)
                    return $"A sale of vault {uid} carries a key of at most {MaxSaleKeyLength} characters.";
                if ((function == "Sale_Start()" || function == "M_Sale_Start()")
                    && (SmartContractStateTrei.GetSmartContractState(uid)?.PurchaseKeys?.Count ?? 0) >= MaxPurchaseKeys)
                    return $"Vault {uid} has been sold {MaxPurchaseKeys} times, the most it keeps; it can change hands by Transfer().";
                return null;
            }
            catch (Newtonsoft.Json.JsonException) { return null; } // unreadable data is refused by the sale's own rules
        }

        /// <summary>
        /// Why a transaction is refused for the holder limit of a watched vault it credits: a vBTC transfer (single or
        /// multi), or an unlock from the bridge (to the lock's owner, or a pool unlock to its destination on each vault
        /// it draws on). Null otherwise.
        /// </summary>
        public static string? HolderLimitRefusal(Transaction tx, long? height = null)
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
                        // Fields read as validation and apply read them: a number becomes its digits (round 13).
                        var lockId = data["LockId"]?.ToObject<string?>();
                        var uid = (string.IsNullOrWhiteSpace(lockId) ? null : VBTCBridgeLockState.GetByLockId(lockId)?.SmartContractUID)
                            ?? data["ContractUID"]?.ToObject<string?>();
                        credits.Add((uid, tx.FromAddress));
                        break;
                    }
                    case TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK:
                    {
                        var data = JObject.Parse(tx.Data);
                        var destination = VerifiedXCore.Bitcoin.Services.BridgeTxRules.PoolUnlockCredited(data, height ?? (tx.Height > 0 ? tx.Height : (Globals.LastBlock?.Height ?? 0) + 1));
                        // The allocations as validation and apply read them (a numeric lock id becomes its digits; round 13).
                        foreach (var allocation in data["Allocations"]?.ToObject<List<VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation>>() ?? new())
                            if (!string.IsNullOrWhiteSpace(allocation?.LockId))
                                credits.Add((VBTCBridgeLockState.GetByLockId(allocation.LockId)?.SmartContractUID, destination));
                        break;
                    }
                    default:
                    {
                        var function = LedgerIntegrityRules.DispatchedCall(tx.Data).Function;
                        // A sale's completion makes its sender (the buyer) the owner: another change of hands the owner's
                        // exemption let grow a full vault one former owner at a time (fourth review, round 8).
                        var saleComplete = function == "Sale_Complete()" || function == "M_Sale_Complete()";
                        if (string.IsNullOrEmpty(saleComplete ? tx.FromAddress : tx.ToAddress)) return null;
                        var vbtcTransfer = tx.TransactionType == TransactionType.VBTC_V2_TRANSFER || function == "TransferVBTCV2()" || function == "TransferVBTCMultiV2()";
                        var vaultTransfer = function == "Transfer()";
                        if (!vbtcTransfer && !vaultTransfer && !saleComplete) return null;
                        var token = JToken.Parse(tx.Data);
                        var obj = token is JArray array ? array.FirstOrDefault() as JObject : token as JObject;
                        if (obj == null) return null;
                        var named = obj["ContractUID"]?.Type == JTokenType.String ? (string)obj["ContractUID"]! : null;
                        if (saleComplete)
                        {
                            credits.Add((named, tx.FromAddress));
                            break;
                        }
                        if (vaultTransfer)
                        {
                            // A vault handed to a new owner: the owner is never refused a credit, so a chain of owners, each
                            // left on the ledger as a holder, grew it without bound (fourth review, round 7). On a full vault
                            // it goes only to an address the ledger names.
                            credits.Add((named, tx.ToAddress));
                            if (obj["ToAddress"]?.Type == JTokenType.String) credits.Add((named, (string)obj["ToAddress"]!));
                            break;
                        }
                        // Every vault the data names: which one apply credits depends on the function and the height, and a
                        // field apply ignores must not hide the one it credits (an empty "Inputs" on a single transfer did,
                        // fourth review, round 7).
                        if (named != null) credits.Add((named, tx.ToAddress));
                        // The inputs as validation and apply read them: the typed reader matches "scuid" too, and takes the
                        // last of two spellings, so an exact "SCUID" read let any holder past the limit (fifth review, round 16).
                        // Inputs apply cannot read credit nothing: they never cancel the check of the vault already named
                        // (an unreadable "Inputs" on a single transfer threw here and skipped it; round 17).
                        if (obj["Inputs"] is JArray inputs)
                        {
                            List<VerifiedXCore.Bitcoin.Models.VBTCV2MultiTransferInput>? read = null;
                            try { read = inputs.ToObject<List<VerifiedXCore.Bitcoin.Models.VBTCV2MultiTransferInput>>(); }
                            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException or FormatException or InvalidCastException) { }
                            foreach (var input in read ?? new())
                                if (!string.IsNullOrEmpty(input?.SCUID))
                                    credits.Add((input.SCUID, tx.ToAddress));
                        }
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
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException or FormatException or InvalidCastException)
            { return null; } // unreadable data is refused by the transaction's own rules
        }
    }
}
