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
