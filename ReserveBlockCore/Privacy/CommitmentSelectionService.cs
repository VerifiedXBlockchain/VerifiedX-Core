using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Picks 1–2 <see cref="UnspentCommitment"/> inputs for a shielded spend + fixed fee (Phase 3 wallet helper).
    /// </summary>
    public static class CommitmentSelectionService
    {
        /// <summary>
        /// Selects the smallest sufficient single note, else the two smallest notes whose sum covers <paramref name="requiredTotal"/>.
        /// </summary>
        public static bool TrySelectInputs(
            IReadOnlyList<UnspentCommitment> candidates,
            decimal requiredTotal,
            out IReadOnlyList<UnspentCommitment> selected,
            out decimal changeAmount,
            out string? error)
        {
            selected = Array.Empty<UnspentCommitment>();
            changeAmount = 0;
            error = null;
            if (requiredTotal <= 0)
            {
                error = "requiredTotal must be positive.";
                return false;
            }

            var usable = candidates
                .Where(c => c != null && !c.IsSpent && c.Amount > 0 && !string.IsNullOrEmpty(c.Commitment))
                .OrderBy(c => c.Amount)
                .ToList();
            if (usable.Count == 0)
            {
                error = "No unspent commitments.";
                return false;
            }

            foreach (var c in usable)
            {
                if (c.Amount >= requiredTotal)
                {
                    selected = new[] { c };
                    changeAmount = c.Amount - requiredTotal;
                    return true;
                }
            }

            for (var i = 0; i < usable.Count; i++)
            {
                for (var j = i + 1; j < usable.Count; j++)
                {
                    var sum = usable[i].Amount + usable[j].Amount;
                    if (sum >= requiredTotal)
                    {
                        var pair = new[] { usable[i], usable[j] }.OrderBy(x => x.TreePosition).ToArray();
                        selected = pair;
                        changeAmount = sum - requiredTotal;
                        return true;
                    }
                }
            }

            error = "Insufficient shielded balance for amount + fee.";
            return false;
        }
    }
}
