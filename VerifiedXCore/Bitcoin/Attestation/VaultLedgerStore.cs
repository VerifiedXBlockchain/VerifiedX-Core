using VerifiedXCore.Models;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// The one way to read or write a contract's vBTC ledger: who holds how much of a vault (and of a V1 tokenized-BTC
    /// contract). Today the ledger is a list of rows inside the contract's state document
    /// (<see cref="SmartContractStateTrei.SCStateTreiTokenizationTXes"/>), and a holder's balance is the sum of the rows
    /// that name it. Every reader and writer goes through here, so the ledger can move to storage of its own without
    /// any caller changing what it means: a holder's balance, whether the contract has a ledger, every row.
    /// </summary>
    public static class VaultLedgerStore
    {
        private static readonly IReadOnlyList<SmartContractStateTreiTokenizationTX> NoRows = Array.Empty<SmartContractStateTreiTokenizationTX>();

        /// <summary>Whether the contract has a ledger at all (it may hold no rows): null-list checks read this.</summary>
        public static bool HasLedger(SmartContractStateTrei? contract) => contract?.SCStateTreiTokenizationTXes != null;

        /// <summary>Whether the contract's ledger holds at least one row.</summary>
        public static bool HasRows(SmartContractStateTrei? contract) => contract?.SCStateTreiTokenizationTXes is { Count: > 0 };

        /// <summary>The ledger's rows in order, none when it has no ledger. Read-only: writes go through <see cref="Append"/>.</summary>
        public static IReadOnlyList<SmartContractStateTreiTokenizationTX> Rows(SmartContractStateTrei? contract) =>
            contract?.SCStateTreiTokenizationTXes ?? NoRows;

        /// <summary>
        /// <paramref name="address"/>'s balance on the contract's ledger: the sum, in order, of the rows that name it as
        /// sender or recipient (ordinal), zero when none does.
        /// </summary>
        public static decimal Balance(SmartContractStateTrei? contract, string? address)
        {
            decimal sum = 0M;
            foreach (var row in Rows(contract))
                if (row.FromAddress == address || row.ToAddress == address)
                    sum += row.Amount;
            return sum;
        }

        /// <summary>
        /// <paramref name="address"/>'s balance as the overspend checks read it: what the rows naming it as recipient sum
        /// to, plus what the rows naming it as sender sum to. The same as <see cref="Balance"/> but for a row naming the
        /// address on both sides, which no writer makes.
        /// </summary>
        public static decimal ReceivedPlusSent(SmartContractStateTrei? contract, string? address)
        {
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
        /// address's own balance is, from the rows it appears in. Zero or less.
        /// </summary>
        public static decimal NegativePositionsExcept(SmartContractStateTrei? contract, string ownerAddress)
        {
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
            decimal negative = 0M;
            foreach (var position in positions.Values)
                if (position < 0M)
                    negative += position;
            return negative;
        }

        /// <summary>Whether any row of the contract's ledger names <paramref name="address"/>, whatever its balance.</summary>
        public static bool Names(SmartContractStateTrei? contract, string? address)
        {
            foreach (var row in Rows(contract))
                if (row.FromAddress == address || row.ToAddress == address)
                    return true;
            return false;
        }

        /// <summary>Whether any row of the contract's ledger names one of <paramref name="addresses"/>.</summary>
        public static bool NamesAny(SmartContractStateTrei? contract, ICollection<string> addresses)
        {
            foreach (var row in Rows(contract))
                if (addresses.Contains(row.ToAddress) || addresses.Contains(row.FromAddress))
                    return true;
            return false;
        }

        /// <summary>
        /// Adds <paramref name="rows"/> to the end of the contract's ledger, in order. The contract is then written as
        /// before (<see cref="SmartContractStateTrei.UpdateSmartContract"/>), which keeps the rows.
        /// </summary>
        public static void Append(SmartContractStateTrei contract, IEnumerable<SmartContractStateTreiTokenizationTX> rows)
        {
            var added = rows.ToList();
            if (contract.SCStateTreiTokenizationTXes?.Count > 0)
                contract.SCStateTreiTokenizationTXes.AddRange(added);
            else
                contract.SCStateTreiTokenizationTXes = added;
        }

        /// <summary>Adds one row to the end of the contract's ledger (<see cref="Append(SmartContractStateTrei, IEnumerable{SmartContractStateTreiTokenizationTX})"/>).</summary>
        public static void Append(SmartContractStateTrei contract, SmartContractStateTreiTokenizationTX row) =>
            Append(contract, new[] { row });
    }
}
