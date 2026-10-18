using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// The block producer's VRF seed from Globals.VrfSeedHeight. Before it the VRF of height h hashed the parent's block
    /// hash, and the parent's producer chose fields of that hash, so it could try variants until it won the next round.
    /// From the height the seed of h is SHA256("vfx-vrf-seed|" + hash of the epoch's anchor block + "|" + h): the anchor is
    /// VrfAnchorLagBlocks below the epoch's first height, final before the epoch starts, and within an epoch the seed is a
    /// function of the anchor and the height alone, so a block withheld or varied changes no later seed. A key made after
    /// the anchor could be ground against the known schedule, so a validator produces in an epoch only if the active set at
    /// its anchor lists it as registered before the anchor.
    /// </summary>
    public static class VrfSeed
    {
        /// <summary>Block hash at a height, from block storage (never Globals.BlockHashes). Test seam.</summary>
        internal static Func<long, string?> BlockHashAt = height => BlockchainData.GetBlockByHeight(height)?.Hash;

        /// <summary>The active validator set as of a height, from committed blocks. Test seam.</summary>
        internal static Func<long, List<VBTCValidator>> ActiveAt = VBTCValidatorRegistry.GetActiveValidatorsAt;

        /// <summary>The validators active at <paramref name="anchor"/> and registered before it.</summary>
        public static IEnumerable<string> EligibleAt(long anchor) =>
            ActiveAt(anchor)
                .Where(v => v.RegistrationBlockHeight < anchor && !string.IsNullOrEmpty(v.ValidatorAddress))
                .Select(v => v.ValidatorAddress);

        public const string IneligibleReason = "vrf-epoch";

        public static bool Active(long height) => height >= Globals.VrfSeedHeight;

        public static long Epoch(long height) => (height - Globals.VrfSeedHeight) / Globals.VrfEpochBlocks;

        public static long EpochStart(long height) => Globals.VrfSeedHeight + Epoch(height) * Globals.VrfEpochBlocks;

        public static long AnchorHeight(long height) => EpochStart(height) - Globals.VrfAnchorLagBlocks;

        /// <summary>The seed of <paramref name="height"/> (at or above the height), or null when the anchor block is not in storage.</summary>
        public static string? SeedFor(long height)
        {
            var anchorHash = BlockHashAt(AnchorHeight(height));
            if (string.IsNullOrEmpty(anchorHash))
                return null;
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes($"vfx-vrf-seed|{anchorHash}|{height}"))).ToLowerInvariant();
        }

        /// <summary>
        /// The VRF's third input for <paramref name="height"/>: the parent's hash below the height, the epoch seed from it
        /// (null when the anchor is not in storage, so no proof is made or accepted).
        /// </summary>
        public static string? ThirdInput(long height, string prevBlockHash) => Active(height) ? SeedFor(height) : prevBlockHash;

        private sealed record EligibleSet(long Anchor, string AnchorHash, HashSet<string> Addresses);
        private static volatile EligibleSet? _eligibleCache;

        internal static void ClearCacheForTests() => _eligibleCache = null;

        /// <summary>Null when <paramref name="address"/> may produce at <paramref name="height"/>, else <see cref="IneligibleReason"/>. Below the height everyone may.</summary>
        public static string? Ineligibility(string? address, long height)
        {
            if (!Active(height))
                return null;
            if (string.IsNullOrEmpty(address))
                return IneligibleReason;
            var anchor = AnchorHeight(height);
            var anchorHash = BlockHashAt(anchor);
            if (string.IsNullOrEmpty(anchorHash))
                return IneligibleReason;
            var cached = _eligibleCache;
            if (cached == null || cached.Anchor != anchor || cached.AnchorHash != anchorHash)
            {
                cached = new EligibleSet(anchor, anchorHash, new HashSet<string>(EligibleAt(anchor), StringComparer.Ordinal));
                _eligibleCache = cached;
            }
            return cached.Addresses.Contains(address) ? null : IneligibleReason;
        }
    }
}
