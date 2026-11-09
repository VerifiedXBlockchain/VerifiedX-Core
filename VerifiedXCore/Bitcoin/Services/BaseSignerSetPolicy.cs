using System.Text;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Who should be on the vBTCb contract, from VFX alone, so no one picks the bridge's signers. Once per epoch every
    /// validator takes the registered vBTC validators at the epoch's boundary block (public, holding the 5,000 VFX
    /// validator balance, each by the Base address of its own key) and the contract's current set, and computes the same
    /// change:
    /// <list type="bullet">
    /// <item>Removals: contract validators no longer registered, never below the contract's minimum of 3.</item>
    /// <item>Additions, once the removals are made: registered validators not on the contract, at most enough that the
    /// newcomers stay under a third of the set (from 3 the set grows 3, 4, 5, 7, 10, ... 50), up to the contract's cap.
    /// A group that registers at once therefore cannot reach the 2/3 that mints, and the set grows over days, not in one
    /// step.</item>
    /// <item>Validators already on the contract keep their seats; newcomers are ordered by keccak256(boundary block hash,
    /// address). The hash is not known when a validator registers, so generating addresses buys no place in the order.</item>
    /// </list>
    /// </summary>
    public static class BaseSignerSetPolicy
    {
        public const long EpochBlocks = 7_200;
        /// <summary>vBTCb.sol MIN_VALIDATORS and MAX_VALIDATORS.</summary>
        public const int MinValidators = 3, MaxValidators = 50;
        /// <summary>Blocks on top of an epoch boundary before it is acted on: as deep as a lock must be before a mint.</summary>
        public const int SettleBlocks = BaseBridgeAttestationService.MintAttestationDepth;

        /// <summary>The change for one epoch: removals first, then additions, each in the order they are signed and sent.</summary>
        public sealed record Plan(long EpochHeight, IReadOnlyList<string> Removes, IReadOnlyList<string> Adds);

        /// <summary>The epoch boundary at or below <paramref name="height"/>.</summary>
        public static long EpochAt(long height) => height < 0 ? -1 : height / EpochBlocks * EpochBlocks;

        /// <summary>The rule itself: addresses in, change out. Addresses are compared and returned lower-cased.</summary>
        public static Plan Compute(long epochHeight, string epochBlockHash, IEnumerable<string> candidates, IEnumerable<string> contractValidators)
        {
            static string Norm(string a) => a.Trim().ToLowerInvariant();
            var registered = candidates.Where(IsAddress).Select(Norm).ToHashSet();
            var contract = contractValidators.Where(IsAddress).Select(Norm).Distinct().ToList();
            var onContract = contract.ToHashSet();
            string Rank(string a) => RankOf(epochBlockHash, a);

            var removes = contract.Where(c => !registered.Contains(c)).OrderBy(Rank, StringComparer.Ordinal).ToList();
            if (contract.Count - removes.Count < MinValidators)
                removes = removes.Take(Math.Max(0, contract.Count - MinValidators)).ToList();
            var remaining = contract.Count - removes.Count;
            var room = Math.Max(0, Math.Min(MaxValidators - remaining, (remaining - 1) / 2));
            var adds = registered.Where(c => !onContract.Contains(c)).OrderBy(Rank, StringComparer.Ordinal).Take(room).ToList();
            return new Plan(epochHeight, removes, adds);
        }

        /// <summary>
        /// The plan at <paramref name="epochHeight"/> from this node's chain: the registry as of the boundary block (committed
        /// blocks only), the balance as committed state holds it now. Null when the boundary block is not on this chain.
        /// </summary>
        public static Plan? ForEpoch(long epochHeight, IEnumerable<string> contractValidators)
        {
            var block = BlockchainData.GetBlockByHeight(epochHeight);
            if (block == null || string.IsNullOrEmpty(block.Hash)) return null;
            var registered = VBTCValidatorRegistry.FundedOnly(VBTCValidatorRegistry.GetActiveValidatorsAt(epochHeight).Where(v => !v.IsS3C))
                .Select(v => BaseAddressOf(v.FrostPublicKey));
            return Compute(epochHeight, block.Hash, registered, contractValidators);
        }

        /// <summary>The Base address of a validator's own key (a registration's FrostPublicKey is its own, by consensus rule).</summary>
        public static string BaseAddressOf(string? publicKey)
        {
            try { return string.IsNullOrEmpty(publicKey) ? "" : ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(publicKey) ?? ""; }
            catch { return ""; }
        }

        internal static string RankOf(string epochBlockHash, string address)
        {
            var hex = address.Trim()[2..];
            var bytes = Encoding.UTF8.GetBytes(epochBlockHash ?? "").Concat(Convert.FromHexString(hex)).ToArray();
            return Convert.ToHexString(VbtcbTypedData.Keccak(bytes));
        }

        internal static bool IsAddress(string? a)
        {
            var t = (a ?? "").Trim();
            return t.Length == 42 && t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && t[2..].All(Uri.IsHexDigit);
        }
    }
}
