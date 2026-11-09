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
    /// <item>Removals: contract validators that exited, lost the balance or were not seen for two days
    /// (<see cref="KeepSeenBlocks"/>), fewer than a third of the set in one epoch and never below the contract's minimum
    /// of 3.</item>
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

        /// <summary>
        /// Blocks a validator on the contract may go without a REGISTER or HEARTBEAT before it loses its seat: two days. A
        /// validator knocked offline for a few hours keeps it, and its operator can send the heartbeat through any node.
        /// </summary>
        public const int KeepSeenBlocks = 2 * (int)EpochBlocks;

        /// <summary>The rule with every candidate also the one kept (see the overload below).</summary>
        public static Plan Compute(long epochHeight, string epochBlockHash, IEnumerable<string> candidates, IEnumerable<string> contractValidators) =>
            Compute(epochHeight, epochBlockHash, candidates, candidates, contractValidators);

        /// <summary>
        /// The rule itself: addresses in, change out. Addresses are compared and returned lower-cased. A contract validator
        /// stays while it is in <paramref name="kept"/> (or <paramref name="candidates"/>); fewer than a third of the set
        /// leave in one epoch, so knocking validators offline removes them slowly, never at once.
        /// </summary>
        public static Plan Compute(long epochHeight, string epochBlockHash, IEnumerable<string> candidates, IEnumerable<string> kept, IEnumerable<string> contractValidators)
        {
            static string Norm(string a) => a.Trim().ToLowerInvariant();
            var registered = candidates.Where(IsAddress).Select(Norm).ToHashSet();
            var staying = kept.Where(IsAddress).Select(Norm).Concat(registered).ToHashSet();
            var contract = contractValidators.Where(IsAddress).Select(Norm).Distinct().ToList();
            var onContract = contract.ToHashSet();
            string Rank(string a) => RankOf(epochBlockHash, a);

            var removes = contract.Where(c => !staying.Contains(c)).OrderBy(Rank, StringComparer.Ordinal).ToList();
            removes = removes.Take(Math.Max(0, Math.Min(contract.Count - MinValidators, (contract.Count - 1) / 3))).ToList();
            var remaining = contract.Count - removes.Count;
            var room = Math.Max(0, Math.Min(MaxValidators - remaining, (remaining - 1) / 2));
            var adds = registered.Where(c => !onContract.Contains(c)).OrderBy(Rank, StringComparer.Ordinal).Take(room).ToList();
            return new Plan(epochHeight, removes, adds);
        }

        /// <summary>
        /// The plan at <paramref name="epochHeight"/> from this node's chain: the registry as of the boundary block (committed
        /// blocks only), the balance as committed state holds it now. Newcomers must be active at the boundary; a validator
        /// already on the contract keeps its seat while it was seen in the <see cref="KeepSeenBlocks"/> before it, holds the
        /// balance and has not exited. Null when the boundary block is not on this chain.
        /// </summary>
        public static Plan? ForEpoch(long epochHeight, IEnumerable<string> contractValidators)
        {
            var block = BlockchainData.GetBlockByHeight(epochHeight);
            if (block == null || string.IsNullOrEmpty(block.Hash)) return null;
            var registered = VBTCValidatorRegistry.FundedOnly(VBTCValidatorRegistry.GetActiveValidatorsAt(epochHeight).Where(v => !v.IsS3C))
                .Select(v => BaseAddressOf(v.FrostPublicKey));
            var kept = VBTCValidatorRegistry.FundedOnly(SeenAt(epochHeight, block.Hash)).Select(v => BaseAddressOf(v.FrostPublicKey));
            return Compute(epochHeight, block.Hash, registered, kept, contractValidators);
        }

        // ── The casters ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>vBTCb.sol MIN_CASTERS and MAX_CASTERS.</summary>
        public const int MinCasters = 3, MaxCasters = 10;

        /// <summary>The epoch's change to the contract's casters: at most one out and one in, sent as one updateCasters.</summary>
        public sealed record CasterPlan(long EpochHeight, IReadOnlyList<string> Add, IReadOnlyList<string> Remove)
        {
            public bool Empty => Add.Count == 0 && Remove.Count == 0;
        }

        /// <summary>
        /// The contract's casters follow the VFX caster committee, slowly: a caster joins once it was in the committee at
        /// this boundary and the two before (two days), and leaves once it was out at all three, one of each per epoch.
        /// Taking a committee seat for hours (by knocking a caster offline, say) moves nothing on Base, and the team has
        /// the two days to see it. A contract with no casters yet (upgraded from V3) gets every member that was in at all
        /// three boundaries, once there are at least three. Addresses are compared and returned lower-cased; null
        /// committees (no record) change nothing.
        /// </summary>
        public static CasterPlan ComputeCasters(long epochHeight, string epochBlockHash, IEnumerable<string>? now, IEnumerable<string>? dayBefore,
            IEnumerable<string>? twoDaysBefore, IEnumerable<string> contractCasters)
        {
            static string Norm(string a) => a.Trim().ToLowerInvariant();
            var none = new CasterPlan(epochHeight, Array.Empty<string>(), Array.Empty<string>());
            if (now == null || dayBefore == null || twoDaysBefore == null) return none;
            var samples = new[] { now, dayBefore, twoDaysBefore }.Select(s => s.Where(IsAddress).Select(Norm).ToHashSet()).ToArray();
            var contract = contractCasters.Where(IsAddress).Select(Norm).Distinct().ToList();
            string Rank(string a) => RankOf(epochBlockHash, a);

            var stayedIn = samples[0].Where(c => samples[1].Contains(c) && samples[2].Contains(c)).OrderBy(Rank, StringComparer.Ordinal).ToList();
            if (contract.Count == 0)
                return stayedIn.Count >= MinCasters ? new CasterPlan(epochHeight, stayedIn.Take(MaxCasters).ToList(), Array.Empty<string>()) : none;

            var add = stayedIn.Where(c => !contract.Contains(c)).Take(1).ToList();
            var remove = contract.Where(c => samples.All(s => !s.Contains(c))).OrderBy(Rank, StringComparer.Ordinal).Take(1).ToList();
            if (contract.Count - remove.Count + add.Count > MaxCasters) add.Clear();
            if (contract.Count - remove.Count + add.Count < MinCasters) remove.Clear();
            return new CasterPlan(epochHeight, add, remove);
        }

        /// <summary>The Base addresses of the caster committee in force at <paramref name="height"/>, or null without a record.</summary>
        public static List<string>? CommitteeAt(long height)
        {
            if (height < 0) return null;
            var record = VerifiedXCore.Services.CasterMembershipStore.GetForHeight(height);
            return record?.Casters.Select(c => BaseAddressOf(c.PublicKey)).Where(a => a.Length > 0).ToList();
        }

        /// <summary>The casters' change at <paramref name="epochHeight"/> from this node's caster records (see <see cref="ComputeCasters"/>).</summary>
        public static CasterPlan? CastersForEpoch(long epochHeight, IEnumerable<string> contractCasters)
        {
            var block = BlockchainData.GetBlockByHeight(epochHeight);
            if (block == null || string.IsNullOrEmpty(block.Hash)) return null;
            return ComputeCasters(epochHeight, block.Hash, CommitteeAt(epochHeight), CommitteeAt(epochHeight - EpochBlocks),
                CommitteeAt(epochHeight - 2 * EpochBlocks), contractCasters);
        }

        private static (long Height, string Hash, List<Models.VBTCValidator> Seen)? _seen;

        /// <summary>The public validators seen in the KeepSeenBlocks up to the boundary: one block scan per boundary block.</summary>
        private static List<Models.VBTCValidator> SeenAt(long epochHeight, string blockHash)
        {
            if (_seen is { } s && s.Height == epochHeight && s.Hash == blockHash) return s.Seen;
            var seen = VBTCValidatorRegistry.GetValidatorsSeenWithin(epochHeight, KeepSeenBlocks).Where(v => !v.IsS3C).ToList();
            _seen = (epochHeight, blockHash, seen);
            return seen;
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
