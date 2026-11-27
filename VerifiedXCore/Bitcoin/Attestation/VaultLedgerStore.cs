using System.Security.Cryptography;
using System.Text;
using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Models;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// The one way to read or write a contract's vBTC ledger: who holds how much of a vault (and of a V1 tokenized-BTC
    /// contract). Every reader and writer goes through here, so where the ledger lives is decided in one place.
    ///
    /// Below <see cref="Globals.VaultLedgerHeight"/> the ledger is a list of rows inside the contract's state document
    /// (<see cref="SmartContractStateTrei.SCStateTreiTokenizationTXes"/>), and a holder's balance is the sum of the rows
    /// that name it. Every read built every row and every write rewrote the whole document, so the cost of one balance
    /// grew with the number of holders, and a document past what LiteDB holds could no longer be written at all.
    ///
    /// From the height (<see cref="MoveAll"/>) a contract's ledger is one <see cref="VaultLedgerEntry"/> per holder in a
    /// collection of its own, in the same database file as the contract state. A contract whose ledger has moved says so
    /// (<see cref="SmartContractStateTrei.LedgerMoved"/>), and keeps two counts beside it: the holders with a balance and
    /// the sum of the balances below zero. Rows a block adds are held on the contract
    /// (<see cref="SmartContractStateTrei.PendingLedgerRows"/>) and written, with the contract, when it is saved
    /// (<see cref="SaveMovedLedger"/>, called by <see cref="SmartContractStateTrei.UpdateSmartContract"/>): nothing reaches the
    /// ledger that is not saved with its contract, as before. Every read counts those rows too.
    /// </summary>
    public static class VaultLedgerStore
    {
        /// <summary>The entries' collection, in the contract state's database file.</summary>
        public const string CollectionName = "rsrv_vault_ledger";

        private static readonly IReadOnlyList<SmartContractStateTreiTokenizationTX> NoRows = Array.Empty<SmartContractStateTreiTokenizationTX>();

        public static ILiteCollection<VaultLedgerEntry> Entries() =>
            DbContext.DB_SmartContractStateTrei.GetCollection<VaultLedgerEntry>(CollectionName);

        /// <summary>
        /// The id of a holder's entry: the SHA-256 of the contract's stored UID and the address, each preceded by its
        /// length. Bounded, so no index key is too long to keep, and exact, so two addresses that differ only in letter case
        /// (base58 tells them apart) are two entries whatever the database's text comparison does.
        /// </summary>
        public static string EntryId(string contractUid, string address)
        {
            var uid = Encoding.UTF8.GetBytes(contractUid ?? "");
            var addr = Encoding.UTF8.GetBytes(address ?? "");
            var bytes = new byte[8 + uid.Length + addr.Length];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), uid.Length);
            uid.CopyTo(bytes, 4);
            BitConverter.TryWriteBytes(bytes.AsSpan(4 + uid.Length, 4), addr.Length);
            addr.CopyTo(bytes, 8 + uid.Length);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        /// <summary>A holder address: not empty, and not the "+" and "-" that stand for the other side of a row.</summary>
        public static bool IsHolder(string? address) => !string.IsNullOrEmpty(address) && address != "+" && address != "-";

        private static bool Moved(SmartContractStateTrei? contract) => contract?.LedgerMoved == true;

        /// <summary>Whether this state's ledgers have moved (the world record's marker): a contract's first rows then go to the collection.</summary>
        public static bool MovedInThisState()
        {
            var record = WorldTrei.GetWorldTrei().FindOne(x => true);
            return record != null && record.VaultLedgerMovedHeight > 0;
        }

        private static List<SmartContractStateTreiTokenizationTX> Pending(SmartContractStateTrei contract) =>
            contract.PendingLedgerRows ?? new List<SmartContractStateTreiTokenizationTX>();

        private static decimal Stored(SmartContractStateTrei contract, string address, out bool exists)
        {
            var entry = Entries().FindById(EntryId(contract.SmartContractUID, address));
            exists = entry != null;
            return entry?.Balance ?? 0M;
        }

        private static decimal PendingSum(SmartContractStateTrei contract, string? address)
        {
            decimal sum = 0M;
            foreach (var row in Pending(contract))
                if (row.FromAddress == address || row.ToAddress == address)
                    sum += row.Amount;
            return sum;
        }

        private static bool PendingNames(SmartContractStateTrei contract, string? address) =>
            Pending(contract).Any(r => r.FromAddress == address || r.ToAddress == address);

        // ── Reads ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Whether the contract has a ledger at all (it may hold no rows): null-list checks read this.</summary>
        public static bool HasLedger(SmartContractStateTrei? contract) =>
            Moved(contract) || contract?.SCStateTreiTokenizationTXes != null;

        /// <summary>Whether the contract's ledger holds anything: a row, or (moved) a holder with a balance.</summary>
        public static bool HasRows(SmartContractStateTrei? contract) =>
            Moved(contract)
                ? (contract!.LedgerHolders ?? 0) > 0 || Pending(contract).Count > 0
                : contract?.SCStateTreiTokenizationTXes is { Count: > 0 };

        /// <summary>
        /// The ledger's rows in order, none when it has no ledger. A moved ledger reads as a compacted one: one row a holder
        /// with a balance (a credit from "+" or a debit to "-"), then the rows not yet saved. Read-only: writes go through
        /// <see cref="Append(SmartContractStateTrei, IEnumerable{SmartContractStateTreiTokenizationTX})"/>.
        /// </summary>
        public static IReadOnlyList<SmartContractStateTreiTokenizationTX> Rows(SmartContractStateTrei? contract)
        {
            if (!Moved(contract))
                return contract?.SCStateTreiTokenizationTXes ?? NoRows;
            var uid = contract!.SmartContractUID;
            var rows = Entries().Find(x => x.ContractUid == uid)
                .Where(e => string.Equals(e.ContractUid, uid, StringComparison.Ordinal) && e.Balance != 0M)
                .OrderBy(e => e.Address, StringComparer.Ordinal)
                .Select(e => e.Balance > 0M
                    ? new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = e.Address, Amount = e.Balance }
                    : new SmartContractStateTreiTokenizationTX { FromAddress = e.Address, ToAddress = "-", Amount = e.Balance })
                .ToList();
            rows.AddRange(Pending(contract));
            return rows;
        }

        /// <summary>
        /// <paramref name="address"/>'s balance on the contract's ledger: the sum, in order, of the rows that name it as
        /// sender or recipient (ordinal), zero when none does. Moved: its entry, plus the rows not yet saved.
        /// </summary>
        public static decimal Balance(SmartContractStateTrei? contract, string? address)
        {
            if (Moved(contract))
                return (IsHolder(address) ? Stored(contract!, address!, out _) : 0M) + PendingSum(contract!, address);
            decimal sum = 0M;
            foreach (var row in Rows(contract))
                if (row.FromAddress == address || row.ToAddress == address)
                    sum += row.Amount;
            return sum;
        }

        /// <summary>
        /// <paramref name="address"/>'s balance as the overspend checks read it: what the rows naming it as recipient sum
        /// to, plus what the rows naming it as sender sum to. The same as <see cref="Balance"/> but for a row naming the
        /// address on both sides, which no writer makes; a moved ledger has no such row.
        /// </summary>
        public static decimal ReceivedPlusSent(SmartContractStateTrei? contract, string? address)
        {
            if (Moved(contract))
                return Balance(contract, address);
            decimal received = 0M, sent = 0M;
            foreach (var row in Rows(contract))
            {
                if (row.ToAddress == address) received += row.Amount;
                if (row.FromAddress == address) sent += row.Amount;
            }
            return received + sent;
        }

        /// <summary>
        /// The sum of the positions below zero held by addresses other than <paramref name="ownerAddress"/> and the
        /// placeholders ("+", "-", empty): a former owner's leftover owner-era debits. Each position is read as that
        /// address's own balance is. Zero or less.
        /// </summary>
        public static decimal NegativePositionsExcept(SmartContractStateTrei? contract, string ownerAddress)
        {
            if (Moved(contract))
            {
                var negative = contract!.LedgerNegative ?? 0M;
                foreach (var address in PendingHolders(contract))
                {
                    var stored = Stored(contract, address, out _);
                    negative += Math.Min(stored + PendingSum(contract, address), 0M) - Math.Min(stored, 0M);
                }
                if (IsHolder(ownerAddress))
                    negative -= Math.Min(Balance(contract, ownerAddress), 0M);
                return negative;
            }

            var rows = Rows(contract);
            if (rows.Count == 0)
                return 0M;

            var others = rows.SelectMany(r => new[] { r.FromAddress, r.ToAddress })
                .Where(a => !string.IsNullOrEmpty(a) && a != "+" && a != "-" && a != ownerAddress)
                .Distinct(StringComparer.Ordinal);

            // One pass over the rows: a row counts for each address it names, as each address's own balance reads it.
            // Summing the whole list again for every address took holders times rows, on every owner action on every
            // node (fourth review, round 6).
            var holders = new HashSet<string>(others, StringComparer.Ordinal);
            var positions = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (r.FromAddress != null && holders.Contains(r.FromAddress))
                    positions[r.FromAddress] = (positions.TryGetValue(r.FromAddress, out var a) ? a : 0M) + r.Amount;
                if (r.ToAddress != null && r.ToAddress != r.FromAddress && holders.Contains(r.ToAddress))
                    positions[r.ToAddress] = (positions.TryGetValue(r.ToAddress, out var b) ? b : 0M) + r.Amount;
            }
            decimal total = 0M;
            foreach (var position in positions.Values)
                if (position < 0M)
                    total += position;
            return total;
        }

        /// <summary>
        /// Whether the contract's ledger names <paramref name="address"/>. Below the height: any row names it, whatever its
        /// balance. Moved: it holds a balance, or an open withdrawal or bridge lock of the vault claims it
        /// (<see cref="BtcVaultLedger.OpenClaimants"/>, the addresses compaction kept at zero), or a row not yet saved names it.
        /// </summary>
        public static bool Names(SmartContractStateTrei? contract, string? address)
        {
            if (Moved(contract))
            {
                if (PendingNames(contract!, address)) return true;
                if (!IsHolder(address)) return false;
                var stored = Stored(contract!, address!, out var exists);
                return stored != 0M || (exists && BtcVaultLedger.OpenClaimants(contract!.SmartContractUID).Contains(address!));
            }
            foreach (var row in Rows(contract))
                if (row.FromAddress == address || row.ToAddress == address)
                    return true;
            return false;
        }

        /// <summary>Whether the contract's ledger names one of <paramref name="addresses"/> (<see cref="Names"/>).</summary>
        public static bool NamesAny(SmartContractStateTrei? contract, ICollection<string> addresses)
        {
            if (Moved(contract))
                return addresses.Any(a => Names(contract, a));
            foreach (var row in Rows(contract))
                if (addresses.Contains(row.ToAddress) || addresses.Contains(row.FromAddress))
                    return true;
            return false;
        }

        /// <summary>
        /// How many holders the contract's ledger names (the holder limit's count). Below the height: the addresses with
        /// rows, zero balances included. Moved: the holders with a balance, the holders at zero that an open withdrawal or
        /// lock claims, and any address a row not yet saved names.
        /// </summary>
        public static int HolderCount(SmartContractStateTrei? contract)
        {
            if (!Moved(contract))
            {
                var rows = Rows(contract);
                if (rows.Count == 0) return 0;
                var named = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (IsHolder(row?.FromAddress)) named.Add(row!.FromAddress);
                    if (IsHolder(row?.ToAddress)) named.Add(row!.ToAddress);
                }
                return named.Count;
            }

            var count = contract!.LedgerHolders ?? 0;
            var pending = PendingHolders(contract).ToHashSet(StringComparer.Ordinal);
            foreach (var claimant in BtcVaultLedger.OpenClaimants(contract.SmartContractUID))
                if (!pending.Contains(claimant) && Stored(contract, claimant, out var exists) == 0M && exists)
                    count++;
            foreach (var address in pending)
            {
                var stored = Stored(contract, address, out var exists);
                var before = stored != 0M || (exists && BtcVaultLedger.OpenClaimants(contract.SmartContractUID).Contains(address));
                if (!before) count++; // a row not yet saved names it
            }
            return count;
        }

        private static IEnumerable<string> PendingHolders(SmartContractStateTrei contract) =>
            Pending(contract).SelectMany(r => new[] { r.FromAddress, r.ToAddress }).Where(IsHolder).Distinct(StringComparer.Ordinal)!;

        // ── Writes ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds <paramref name="rows"/> to the end of the contract's ledger, in order. The contract is then written as before
        /// (<see cref="SmartContractStateTrei.UpdateSmartContract"/>), which keeps the rows: below the height in its document,
        /// from the height in the ledger's entries (<see cref="SaveMovedLedger"/>).
        /// </summary>
        public static void Append(SmartContractStateTrei contract, IEnumerable<SmartContractStateTreiTokenizationTX> rows)
        {
            var added = rows.ToList();
            if (Moved(contract) || (contract.SCStateTreiTokenizationTXes == null && MovedInThisState()))
            {
                contract.LedgerMoved = true;
                (contract.PendingLedgerRows ??= new List<SmartContractStateTreiTokenizationTX>()).AddRange(added);
                return;
            }
            if (contract.SCStateTreiTokenizationTXes?.Count > 0)
                contract.SCStateTreiTokenizationTXes.AddRange(added);
            else
                contract.SCStateTreiTokenizationTXes = added;
        }

        /// <summary>Adds one row to the end of the contract's ledger (<see cref="Append(SmartContractStateTrei, IEnumerable{SmartContractStateTreiTokenizationTX})"/>).</summary>
        public static void Append(SmartContractStateTrei contract, SmartContractStateTreiTokenizationTX row) =>
            Append(contract, new[] { row });

        /// <summary>
        /// Writes the rows a moved contract holds unsaved into its entries, and its counts onto the contract, inside the
        /// transaction its contract is saved in. Each holder's entry gains the sum of the rows that name it (a row counts for
        /// each holder it names, as a balance reads it). A write that fails throws.
        /// </summary>
        internal static void SaveMovedLedger(SmartContractStateTrei contract)
        {
            var pending = contract.PendingLedgerRows;
            if (!Moved(contract) || pending == null || pending.Count == 0) return;
            var deltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in pending)
            {
                if (row == null) continue;
                foreach (var address in new[] { row.FromAddress, row.ToAddress }.Where(IsHolder).Distinct(StringComparer.Ordinal))
                    deltas[address!] = (deltas.TryGetValue(address!, out var d) ? d : 0M) + row.Amount;
            }
            var holders = contract.LedgerHolders ?? 0;
            var negative = contract.LedgerNegative ?? 0M;
            var entries = Entries();
            foreach (var (address, delta) in deltas)
            {
                var id = EntryId(contract.SmartContractUID, address);
                var entry = entries.FindById(id) ?? new VaultLedgerEntry { Id = id, ContractUid = contract.SmartContractUID, Address = address };
                var before = entry.Balance;
                entry.Balance = before + delta;
                entry.LastModifiedHeight = StateWriteContext.StampHeight;
                holders += (entry.Balance != 0M ? 1 : 0) - (before != 0M ? 1 : 0);
                negative += Math.Min(entry.Balance, 0M) - Math.Min(before, 0M);
                if (!entries.Upsert(entry) && entries.FindById(id)?.Balance != entry.Balance)
                    throw new InvalidOperationException($"The ledger entry of {address} on {contract.SmartContractUID} was not written.");
            }
            contract.LedgerHolders = holders;
            contract.LedgerNegative = negative;
            contract.PendingLedgerRows = null;
        }

        /// <summary>
        /// Runs <paramref name="write"/> (a contract's save) and the save of its unsaved ledger rows as one write: both or
        /// neither. Synchronous, as LiteDB's transactions belong to a thread.
        /// </summary>
        internal static void InOneWrite(SmartContractStateTrei contract, Action write)
        {
            if (!Moved(contract) || contract.PendingLedgerRows is not { Count: > 0 })
            {
                write();
                return;
            }
            var db = DbContext.DB_SmartContractStateTrei;
            var began = db.BeginTrans();
            try
            {
                SaveMovedLedger(contract);
                write();
                if (began) db.Commit();
            }
            catch
            {
                if (began) db.Rollback();
                throw;
            }
        }

        // ── The move ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// At the start of the block at <see cref="Globals.VaultLedgerHeight"/>, or the first block after it applied to a state
        /// that has not moved (an older build crossed the height): every contract with a ledger in its document gets one
        /// entry per holder, holding that holder's balance read from the rows, and its two counts; its rows leave the
        /// document. Each contract moves in one write, so a move cut short is finished at the next block with nothing
        /// counted twice; the world record's marker is set once all have moved. Returns how many contracts moved.
        /// </summary>
        public static int MoveAll(long height)
        {
            if (height < Globals.VaultLedgerHeight || MovedInThisState()) return 0;
            var scs = SmartContractStateTrei.GetSCST();
            var db = DbContext.DB_SmartContractStateTrei;
            var moved = 0;
            foreach (var contract in scs.FindAll().Where(c => c.SCStateTreiTokenizationTXes != null).ToList())
            {
                var balances = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var row in contract.SCStateTreiTokenizationTXes!)
                {
                    if (row == null) continue;
                    foreach (var address in new[] { row.FromAddress, row.ToAddress }.Where(IsHolder).Distinct(StringComparer.Ordinal))
                        balances[address!] = (balances.TryGetValue(address!, out var b) ? b : 0M) + row.Amount;
                }
                var began = db.BeginTrans();
                try
                {
                    var entries = Entries();
                    foreach (var (address, balance) in balances)
                        entries.Upsert(new VaultLedgerEntry
                        {
                            Id = EntryId(contract.SmartContractUID, address), ContractUid = contract.SmartContractUID, Address = address,
                            Balance = balance, LastModifiedHeight = StateWriteContext.StampHeight,
                        });
                    contract.LedgerMoved = true;
                    contract.LedgerHolders = balances.Count(kv => kv.Value != 0M);
                    contract.LedgerNegative = balances.Values.Where(v => v < 0M).Aggregate(0M, (s, v) => s + v);
                    contract.SCStateTreiTokenizationTXes = null;
                    contract.LastModifiedHeight = StateWriteContext.StampHeight;
                    if (!scs.Update(contract))
                        throw new InvalidOperationException($"Contract {contract.SmartContractUID} was not written when its ledger moved.");
                    if (began) db.Commit();
                }
                catch
                {
                    if (began) db.Rollback();
                    throw;
                }
                moved++;
            }

            var world = WorldTrei.GetWorldTrei();
            var record = world.FindOne(x => true);
            if (record == null)
                world.Insert(new WorldTrei { VaultLedgerMovedHeight = height });
            else
            {
                record.VaultLedgerMovedHeight = height;
                if (!world.Update(record))
                    throw new InvalidOperationException("The ledger move's marker was not written.");
            }
            return moved;
        }

        /// <summary>A deleted contract's entries hold nothing, so a contract made again under its UID starts empty.</summary>
        internal static void ClearEntriesOf(string contractUid)
        {
            var entries = Entries();
            foreach (var entry in entries.Find(x => x.ContractUid == contractUid).Where(e => string.Equals(e.ContractUid, contractUid, StringComparison.Ordinal)).ToList())
            {
                if (entry.Balance == 0M) continue;
                entry.Balance = 0M;
                entry.LastModifiedHeight = StateWriteContext.StampHeight;
                entries.Update(entry);
            }
        }
    }
}
