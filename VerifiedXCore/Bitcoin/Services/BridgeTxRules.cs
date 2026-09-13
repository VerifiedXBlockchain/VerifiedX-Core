using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Shape rules for bridge transactions, enforced at validation (height-gated). Pure and testable.
    /// </summary>
    public static class BridgeTxRules
    {
        /// <summary>
        /// A pool unlock must be submitted by a committee caster (the handler), pay a canonical,
        /// well-formed VFX address (no stray whitespace: apply credits the raw string, so a padded
        /// address would credit an unusable ledger key), and carry an exact sat/decimal pair (no
        /// sub-satoshi dust that votes and evidence, both in sats, cannot see).
        /// </summary>
        public static (bool Ok, string Reason) CheckPoolUnlockShape(string? fromAddress, string? vfxDestination, decimal totalAmount, long totalAmountSats, HashSet<string> committee)
        {
            var (subOk, subReason) = CheckSubmitter(fromAddress, committee);
            if (!subOk) return (false, subReason);

            if (string.IsNullOrEmpty(vfxDestination)) return (false, "VfxDestinationAddress is required for bridge pool unlock.");
            if (!string.Equals(vfxDestination, vfxDestination.Trim(), StringComparison.Ordinal))
                return (false, "VfxDestinationAddress must not contain leading or trailing whitespace.");
            if (!AddressValidateUtility.ValidateAddress(vfxDestination))
                return (false, "VfxDestinationAddress is not a valid VFX address.");

            if (totalAmountSats <= 0) return (false, "TotalAmountSats must be positive.");
            if (totalAmount != totalAmountSats / 100_000_000M)
                return (false, "TotalAmount must equal TotalAmountSats / 1e8 exactly (no sub-satoshi precision).");

            return (true, "");
        }

        /// <summary>
        /// A FAIL may only "restore" allocations the exit actually reserved: each failed entry must
        /// match a recorded plan entry by lock id and amount. Otherwise anyone could blacklist any
        /// lock (BlacklistLock has no undo) with a fabricated FailedAllocations list.
        /// </summary>
        public static (bool Ok, string Reason) CheckFailAllocationsSubset(IEnumerable<Models.PoolUnlockAllocation>? failed, string? recordedPlanJson)
        {
            if (failed == null) return (false, "FailedAllocations missing.");
            List<Models.PoolUnlockAllocation>? plan;
            try { plan = string.IsNullOrWhiteSpace(recordedPlanJson) ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.PoolUnlockAllocation>>(recordedPlanJson); }
            catch { plan = null; }
            if (plan == null || plan.Count == 0) return (false, "Exit record has no recorded allocation plan; FAIL cannot be verified.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in failed)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.LockId)) return (false, "FailedAllocations entry missing LockId.");
                if (!seen.Add(f.LockId)) return (false, $"FailedAllocations lists lock {f.LockId} twice.");
                var match = plan.FirstOrDefault(pl => string.Equals(pl.LockId, f.LockId, StringComparison.Ordinal));
                if (match == null) return (false, $"FailedAllocations lock {f.LockId} is not part of this exit's plan.");
                if (match.UnlockAmount != f.UnlockAmount) return (false, $"FailedAllocations amount for lock {f.LockId} does not match the plan.");
            }
            return seen.Count > 0 ? (true, "") : (false, "FailedAllocations is empty.");
        }

        /// <summary>64 hex chars, optional 0x.</summary>
        public static bool IsBtcTxIdShape(string? txid)
        {
            if (string.IsNullOrWhiteSpace(txid)) return false;
            var h = txid.Trim();
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            return h.Length == 64 && h.All(Uri.IsHexDigit);
        }

        /// <summary>Bridge unlock / exit transactions are broadcast by the elected handler caster only.</summary>
        public static (bool Ok, string Reason) CheckSubmitter(string? fromAddress, HashSet<string> committee)
        {
            if (string.IsNullOrEmpty(fromAddress)) return (false, "Bridge transaction has no sender.");
            if (committee == null || committee.Count == 0) return (false, "Caster committee unavailable for this height.");
            if (!committee.Contains(fromAddress)) return (false, "Bridge transaction must be submitted by a committee caster.");
            return (true, "");
        }
    }
}
