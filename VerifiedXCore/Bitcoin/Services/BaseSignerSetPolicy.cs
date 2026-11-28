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
            // Ranked by what the boundary block gives as a seed: from the VRF height its mix, which its producer cannot choose.
            var seed = VerifiedXCore.Utilities.VrfSeed.SeedMaterialAt(epochHeight);
            return seed == null ? null : Compute(epochHeight, seed, registered, kept, contractValidators);
        }

        // ── The casters ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>vBTCb.sol MIN_CASTERS and MAX_CASTERS.</summary>
        public const int MinCasters = 3, MaxCasters = 10;

        /// <summary>The epoch's change to the contract's casters, sent as one updateCasters.</summary>
        public sealed record CasterPlan(long EpochHeight, IReadOnlyList<string> Add, IReadOnlyList<string> Remove)
        {
            public bool Empty => Add.Count == 0 && Remove.Count == 0;
        }

        /// <summary>
        /// The rule for boundaries before <see cref="Globals.BaseCasterCatchUpHeight"/> (from it, <see cref="ComputeCastersContinuous"/>).
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

        /// <summary>
        /// The casters' change at <paramref name="epochHeight"/> from this node's caster records: before
        /// <see cref="Globals.BaseCasterCatchUpHeight"/> by <see cref="ComputeCasters"/>, from it by
        /// <see cref="ComputeCastersContinuous"/>. The boundary decides which, so every signer judging one epoch uses one rule.
        /// </summary>
        public static CasterPlan? CastersForEpoch(long epochHeight, IEnumerable<string> contractCasters)
        {
            var seed = VerifiedXCore.Utilities.VrfSeed.SeedMaterialAt(epochHeight);
            if (seed == null) return null;
            if (epochHeight >= Globals.BaseCasterCatchUpHeight)
                return ComputeCastersContinuous(epochHeight, seed, CasterWindow(epochHeight), contractCasters);
            return ComputeCasters(epochHeight, seed, CommitteeAt(epochHeight), CommitteeAt(epochHeight - EpochBlocks),
                CommitteeAt(epochHeight - 2 * EpochBlocks), contractCasters);
        }

        /// <summary>
        /// Blocks a caster must stay in the committee without a break before it joins the contract, and stay out of it
        /// before it leaves (<see cref="ComputeCastersContinuous"/>): one epoch.
        /// </summary>
        public const int CasterStableBlocks = (int)EpochBlocks;

        /// <summary>The most caster records one window is read from; a day with more is churn, and changes nothing on Base.</summary>
        public const int MaxWindowRecords = 1_024;

        /// <summary>
        /// From <see cref="Globals.BaseCasterCatchUpHeight"/>: the contract's casters follow the committee within about a day,
        /// in one updateCasters an epoch.
        /// <list type="bullet">
        /// <item>A caster joins once it was in every committee of <paramref name="window"/> (the records governing the
        /// <see cref="CasterStableBlocks"/> up to the boundary), and leaves once it was in none of them. A caster that drops
        /// out for minutes neither joins nor leaves.</item>
        /// <item>All of it goes in one change, within three bounds: the removals alone never leave fewer than 3; the list
        /// stays at most 10; the casters kept outnumber those added, so one epoch never hands the contract's caster
        /// majority to casters that just joined. Additions take the room first, then removals; each in the order of
        /// keccak256(seed, address).</item>
        /// <item>Additions are always in the committee and removals never, so the share of the list that sits in the
        /// committee never falls. Any in-order part of the additions is still a valid change.</item>
        /// <item>A contract with fewer than 3 casters (upgraded from V3) gets additions only, at least enough to reach 3.</item>
        /// </list>
        /// Null <paramref name="window"/> (records missing for the window) changes nothing.
        /// </summary>
        public static CasterPlan ComputeCastersContinuous(long epochHeight, string seed, IEnumerable<IEnumerable<string>>? window, IEnumerable<string> contractCasters)
        {
            static string Norm(string a) => a.Trim().ToLowerInvariant();
            var none = new CasterPlan(epochHeight, Array.Empty<string>(), Array.Empty<string>());
            var samples = window?.Select(s => s.Where(IsAddress).Select(Norm).ToHashSet()).ToList();
            if (samples == null || samples.Count == 0) return none;
            var contract = contractCasters.Where(IsAddress).Select(Norm).Distinct().ToList();
            var onContract = contract.ToHashSet();
            string Rank(string a) => RankOf(seed, a);

            var inAny = samples.SelectMany(s => s).ToHashSet();
            var newcomers = samples[0].Where(c => !onContract.Contains(c) && samples.All(s => s.Contains(c)))
                .OrderBy(Rank, StringComparer.Ordinal).ToList();
            if (contract.Count < MinCasters)
            {
                var take = Math.Min(newcomers.Count, MaxCasters - contract.Count);
                return contract.Count + take >= MinCasters ? new CasterPlan(epochHeight, newcomers.Take(take).ToList(), Array.Empty<string>()) : none;
            }

            var departed = contract.Where(c => !inAny.Contains(c)).OrderBy(Rank, StringComparer.Ordinal).ToList();
            for (var a = Math.Min(newcomers.Count, MaxCasters); a >= 0; a--)
            {
                var most = Math.Min(departed.Count, contract.Count - Math.Max(MinCasters, a + 1));
                var least = Math.Max(0, contract.Count + a - MaxCasters);
                if (most >= least) return new CasterPlan(epochHeight, newcomers.Take(a).ToList(), departed.Take(most).ToList());
            }
            return none;
        }

        /// <summary>
        /// The Base addresses of every committee that governed a height in [<paramref name="epochHeight"/> -
        /// <see cref="CasterStableBlocks"/>, <paramref name="epochHeight"/>], in record order (effective heights advance with
        /// the seq, so each record from the one governing the window's first block to the one governing the boundary governs
        /// part of it). Null when a record of the window is missing from this node (the record era starts inside it, or
        /// the store was reset) or the window holds more than <see cref="MaxWindowRecords"/>.
        /// </summary>
        public static List<List<string>>? CasterWindow(long epochHeight)
        {
            var from = epochHeight - CasterStableBlocks;
            if (from < 0) return null;
            var first = VerifiedXCore.Services.CasterMembershipStore.GetForHeight(from);
            var last = VerifiedXCore.Services.CasterMembershipStore.GetForHeight(epochHeight);
            if (first == null || last == null || last.RecordSeq < first.RecordSeq || last.RecordSeq - first.RecordSeq >= MaxWindowRecords) return null;

            var committees = new List<List<string>>();
            for (var seq = first.RecordSeq; seq <= last.RecordSeq; seq++)
            {
                var record = seq == first.RecordSeq ? first : seq == last.RecordSeq ? last : VerifiedXCore.Services.CasterMembershipStore.GetBySeq(seq);
                if (record == null) return null;
                committees.Add(record.Casters.Select(c => BaseAddressOf(c.PublicKey)).Where(a => a.Length > 0).ToList());
            }
            return committees;
        }

        // ── A proposed removal ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Blocks back from the tip in which a validator on the contract must have a REGISTER or HEARTBEAT to count as live
        /// when its removal is proposed (<see cref="LiveForRemovalAt"/>). One number for both sides of a proposal: the
        /// operator's signature on it and the members' veto of it.
        /// </summary>
        public const int RemovalLiveBlocks = KeepSeenBlocks;

        /// <summary>The members that are live for a proposed removal, by Base address, lower-cased.</summary>
        public sealed record RemovalLiveness(IReadOnlySet<string> Validators, IReadOnlySet<string> Casters)
        {
            /// <summary>The first of these validators that is live, or null.</summary>
            public string? LiveValidator(IEnumerable<string> named) => named.FirstOrDefault(v => Validators.Contains((v ?? "").Trim().ToLowerInvariant()));
            /// <summary>The first of these casters that is live, or null.</summary>
            public string? LiveCaster(IEnumerable<string> named) => named.FirstOrDefault(c => Casters.Contains((c ?? "").Trim().ToLowerInvariant()));
        }

        /// <summary>
        /// One scan's result with the block it was made at. Kept as one reference that is read once and replaced whole
        /// (the veto loop and signing requests ask together, across a tip change): a block's height and hash are never
        /// read beside another block's list.
        /// </summary>
        private sealed record SeenAtBlock(long Height, string Hash, List<Models.VBTCValidator> Seen);

        private static volatile SeenAtBlock? _seenForRemoval;

        /// <summary>
        /// Who is live when a removal of members is proposed, as of <paramref name="tip"/>: the validators that hold the
        /// balance, are not S3C, were seen in the <see cref="RemovalLiveBlocks"/> up to the tip and have not exited since,
        /// and the casters in this node's current caster record. The one rule for a proposed removal: the operator's
        /// signature on a proposal (BaseAdminSigningService) and the members' veto of a pending one
        /// (BaseValidatorSyncService) both ask it at their tip, so a node does not cancel a proposal it would sign.
        /// </summary>
        public static RemovalLiveness LiveForRemovalAt(long tip)
        {
            var casters = (VerifiedXCore.Services.CasterMembershipStore.GetCurrent()?.Casters ?? new List<VerifiedXCore.Models.CasterInfo>())
                .Select(c => BaseAddressOf(c.PublicKey).ToLowerInvariant()).Where(a => a.Length > 0).ToHashSet();
            if (tip < 0) return new RemovalLiveness(new HashSet<string>(), casters);
            // One scan per tip block (a tip with no committed block to key on is scanned each time); the balance is read
            // each time, as committed state holds it now.
            var hash = BlockchainData.GetBlockByHeight(tip)?.Hash ?? "";
            List<Models.VBTCValidator> seen;
            if (hash.Length > 0 && _seenForRemoval is { } known && known.Height == tip && known.Hash == hash) seen = known.Seen;
            else
            {
                seen = VBTCValidatorRegistry.GetValidatorsSeenWithin(tip, RemovalLiveBlocks).Where(v => !v.IsS3C).ToList();
                if (hash.Length > 0) _seenForRemoval = new SeenAtBlock(tip, hash, seen);
            }
            var validators = VBTCValidatorRegistry.FundedOnly(seen).Select(v => BaseAddressOf(v.FrostPublicKey).ToLowerInvariant())
                .Where(a => a.Length > 0).ToHashSet();
            return new RemovalLiveness(validators, casters);
        }

        private static volatile SeenAtBlock? _seen;

        /// <summary>The public validators seen in the KeepSeenBlocks up to the boundary: one block scan per boundary block.</summary>
        private static List<Models.VBTCValidator> SeenAt(long epochHeight, string blockHash)
        {
            if (_seen is { } s && s.Height == epochHeight && s.Hash == blockHash) return s.Seen;
            var seen = VBTCValidatorRegistry.GetValidatorsSeenWithin(epochHeight, KeepSeenBlocks).Where(v => !v.IsS3C).ToList();
            _seen = new SeenAtBlock(epochHeight, blockHash, seen);
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
