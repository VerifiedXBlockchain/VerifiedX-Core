using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Shape rules for bridge transactions, enforced at validation (height-gated). Pure and testable.
    /// </summary>
    public static class BridgeTxRules
    {
        /// <summary>A transparent VFX address as written: no name, no shielded address, no stray whitespace.</summary>
        public static bool IsVfxAddress(string? address) =>
            !string.IsNullOrEmpty(address) && string.Equals(address, address.Trim(), StringComparison.Ordinal)
            && !address.Contains('.') && !address.StartsWith(VerifiedXCore.Privacy.ShieldedAddressConstants.Prefix, StringComparison.Ordinal)
            && AddressValidateUtility.ValidateRBXAddress(address);

        /// <summary>An address Base mints to: "0x" and exactly 40 hex digits, nothing around them, not the zero address.</summary>
        public static bool IsMintableEvmAddress(string? address) =>
            address != null && address.Length == 42 && address.StartsWith("0x", StringComparison.Ordinal)
            && address.Skip(2).All(Uri.IsHexDigit) && address.Skip(2).Any(c => c != '0');

        /// <summary>
        /// The VFX address a Base burn's destination names: itself when it is an address, the address a registered name
        /// points to now, or null.
        /// </summary>
        public static string? ResolveVfxDestination(string? destination)
        {
            if (IsVfxAddress(destination)) return destination;
            if (string.IsNullOrEmpty(destination) || !string.Equals(destination, destination.Trim(), StringComparison.Ordinal)) return null;
            try
            {
                var (found, address) = VerifiedXCore.Models.Adnr.GetAddress(destination);
                return found && IsVfxAddress(address) ? address : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The address a pool unlock credits at <paramref name="height"/>: its VfxCreditAddress from
        /// Globals.PoolUnlockCreditsAddressHeight, its VfxDestinationAddress before.
        /// </summary>
        public static string? PoolUnlockCredited(Newtonsoft.Json.Linq.JObject data, long height) =>
            (height >= Globals.PoolUnlockCreditsAddressHeight ? data["VfxCreditAddress"] : data["VfxDestinationAddress"])?.ToObject<string?>();

        /// <summary>
        /// From Globals.PoolUnlockCreditsAddressHeight: why a pool unlock's VfxCreditAddress is refused (missing, not a VFX
        /// address, or not the address its VfxDestinationAddress names), or null.
        /// </summary>
        public static string? PoolUnlockCreditRefusal(Newtonsoft.Json.Linq.JObject data, long height)
        {
            if (height < Globals.PoolUnlockCreditsAddressHeight) return null;
            var credit = data["VfxCreditAddress"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string?)data["VfxCreditAddress"] : null;
            if (!IsVfxAddress(credit)) return "A bridge pool unlock credits VfxCreditAddress, which must be a VFX address.";
            var named = ResolveVfxDestination(data["VfxDestinationAddress"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string?)data["VfxDestinationAddress"] : null);
            if (!string.Equals(credit, named, StringComparison.Ordinal))
                return "A bridge pool unlock's VfxCreditAddress is not the address its VfxDestinationAddress names.";
            return null;
        }

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

        /// <summary>
        /// A completion's Bitcoin transactions: one txid, or (from the path-audit rules) several separated by commas, one per
        /// vault that paid. The handler joins them so; a list was always refused, and an older exit paid by two or more
        /// vaults could never be completed (fifth review, round 21).
        /// </summary>
        public static bool IsBtcTxIdList(string? txids, long height)
        {
            if (IsBtcTxIdShape(txids)) return true;
            if (height < Globals.PathAuditRulesHeight || string.IsNullOrWhiteSpace(txids)) return false;
            var parts = txids.Split(',');
            return parts.Length <= 200 && parts.All(IsBtcTxIdShape);
        }

        /// <summary>The number of vaults an exit's recorded plan draws on (at least one).</summary>
        public static int VaultsInPlan(string? allocationsJson, string? fallbackContract)
        {
            try
            {
                var plan = string.IsNullOrWhiteSpace(allocationsJson) ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.PoolUnlockAllocation>>(allocationsJson);
                var vaults = plan?.Select(a => a?.SmartContractUID ?? "").Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).Count() ?? 0;
                return Math.Max(1, vaults);
            }
            catch { return 1; }
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
