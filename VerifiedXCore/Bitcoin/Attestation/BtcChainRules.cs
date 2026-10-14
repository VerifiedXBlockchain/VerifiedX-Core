using System.Numerics;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Bitcoin's header rules for the two networks this chain follows: mainnet, and testnet4 (BIP 94). A header is
    /// accepted into the attested chain only when it extends the stored tip and passes every rule here. The rules read
    /// nothing but earlier headers, so every node reaches the same verdict with no network.
    ///
    /// Deliberately not checked: the "not more than two hours in the future" rule. It compares against a local clock,
    /// which consensus cannot do, and it is not needed here: a header counts only once it is buried under further
    /// proof-of-work (the confirmation depth, which consensus enforces).
    /// docs/PLAN-vBTC-Bitcoin-Attestation.md, section 3.1.
    /// </summary>
    public sealed class BtcChainRules
    {
        /// <summary>Blocks between difficulty changes.</summary>
        public const int Interval = 2016;
        /// <summary>Target seconds between blocks.</summary>
        public const int Spacing = 600;
        /// <summary>Target seconds for one interval: two weeks.</summary>
        public const int Timespan = Interval * Spacing;
        /// <summary>Headers the median-time-past rule reads.</summary>
        public const int MedianSpan = 11;
        /// <summary>BIP 94: how far before its parent the first block of an interval may be timed.</summary>
        public const int MaxTimewarp = 600;

        /// <summary>The easiest target mainnet and testnet4 allow, compact form (00000000ffff...).</summary>
        public const uint PowLimitBits = 0x1d00ffff;

        public string Name { get; }
        /// <summary>This network's easiest target, compact form.</summary>
        public uint LimitBits { get; }
        private readonly BigInteger _limit;
        /// <summary>Testnet rule: a block more than twenty minutes after its parent may use the easiest target.</summary>
        public bool AllowMinDifficulty { get; }
        /// <summary>
        /// BIP 94 (testnet4): a retarget starts from the FIRST block of the interval's target, so a run of
        /// minimum-difficulty blocks at the end of an interval cannot reset the difficulty; and the first block of an
        /// interval may not be timed more than ten minutes before its parent.
        /// </summary>
        public bool EnforceBip94 { get; }
        /// <summary>Test networks only: the target never changes (every header carries <see cref="LimitBits"/>).</summary>
        public bool NoRetarget { get; }

        private BtcChainRules(string name, uint limitBits, BigInteger limit, bool allowMinDifficulty, bool enforceBip94, bool noRetarget)
        {
            Name = name;
            LimitBits = limitBits;
            _limit = limit;
            AllowMinDifficulty = allowMinDifficulty;
            EnforceBip94 = enforceBip94;
            NoRetarget = noRetarget;
        }

        private static readonly BigInteger MainLimit = (BigInteger.One << 224) - BigInteger.One;

        public static readonly BtcChainRules Mainnet = new("mainnet", PowLimitBits, MainLimit, allowMinDifficulty: false, enforceBip94: false, noRetarget: false);
        public static readonly BtcChainRules Testnet4 = new("testnet4", PowLimitBits, MainLimit, allowMinDifficulty: true, enforceBip94: true, noRetarget: false);

        /// <summary>
        /// Tests only: regtest's easiest target with the testnet rule that a block more than twenty minutes after its
        /// parent uses it, so that tests can produce chains mixing blocks of full difficulty (any harder target, fixed
        /// by the checkpoint's) with easy ones. Heights in tests stay inside one interval.
        /// </summary>
        internal static readonly BtcChainRules RegtestWithEasyBlocks = new("regtest-easy", 0x207fffff, (BigInteger.One << 255) - BigInteger.One, allowMinDifficulty: true, enforceBip94: true, noRetarget: false);

        /// <summary>
        /// Bitcoin's regression-test rules (easiest possible target, never retargeted), so that tests can produce valid
        /// chains of headers. Reachable only through <see cref="OverrideForTests"/>, which production never sets.
        /// </summary>
        internal static readonly BtcChainRules Regtest = new("regtest", 0x207fffff, (BigInteger.One << 255) - BigInteger.One, allowMinDifficulty: false, enforceBip94: false, noRetarget: true);

        /// <summary>Tests only. Null in every production process.</summary>
        internal static BtcChainRules? OverrideForTests;

        /// <summary>The rules for the Bitcoin network this node's chain follows (testnet4 on VFX testnet, mainnet otherwise).</summary>
        public static BtcChainRules Current => OverrideForTests ?? (Globals.IsTestNet ? Testnet4 : Mainnet);

        /// <summary>
        /// Whether a header counts toward the burial of the blocks below it. On a network with the testnet rule a
        /// block timed more than twenty minutes after its parent carries the easiest target there is: a header anyone
        /// makes in a moment on a laptop. Such headers are Bitcoin and are followed, but they bury nothing: counted,
        /// a made-up block could be buried for free, and the two thirds of the signers who saw it first on their best
        /// chain would have signed a deposit that never happened. A header counts when it carries more work than the
        /// easiest target. On mainnet there is no such rule and every header counts.
        /// </summary>
        public bool CountsTowardBurial(BtcHeader header) => !AllowMinDifficulty || header.Bits != LimitBits;

        /// <summary>
        /// The lowest height at which the block at <paramref name="block"/> is buried: <paramref name="depth"/> headers
        /// that count sit above it. Null when that is not reached by <paramref name="through"/>, or a header on the
        /// way is not held. On mainnet this is block + depth.
        /// </summary>
        public long? BuriedAt(long block, long through, int depth, Func<long, BtcHeader?> at)
        {
            if (depth <= 0) return block <= through ? block : null;
            var counted = 0;
            for (var h = block + 1; h <= through; h++)
            {
                var header = at(h);
                if (header == null) return null;
                if (CountsTowardBurial(header) && ++counted == depth) return h;
            }
            return null;
        }

        /// <summary>
        /// Checks <paramref name="header"/> as the block at <paramref name="height"/>. <paramref name="at"/> returns the
        /// header already accepted at a height, or null. Null reason on success.
        /// </summary>
        public string? Validate(BtcHeader header, long height, Func<long, BtcHeader?> at)
        {
            if (header == null) return "no header";
            if (height <= 0) return "a header's height must be above zero";

            var prev = at(height - 1);
            if (prev == null) return $"the header before height {height} is not known";
            if (!string.Equals(header.PrevHash, prev.Hash, StringComparison.Ordinal))
                return $"header at {height} does not extend {prev.Hash}";

            if (!ExpectedBits(height, header.Time, at, out var expected, out var why))
                return why;
            if (header.Bits != expected)
                return $"header at {height} claims target {header.Bits:x8}; the rules require {expected:x8}";

            if (!header.MeetsOwnTarget())
                return $"header at {height} does not meet its own proof-of-work target";

            if (!MedianTimePast(height, at, out var median, out why))
                return why;
            if (header.Time <= median)
                return $"header at {height} is timed {header.Time}, not after the median {median} of the {MedianSpan} before it";

            if (EnforceBip94 && height % Interval == 0 && (long)header.Time < (long)prev.Time - MaxTimewarp)
                return $"header at {height} opens an interval more than {MaxTimewarp} seconds before its parent (BIP 94)";

            return null;
        }

        /// <summary>
        /// The compact target the block at <paramref name="height"/> must carry (Bitcoin's GetNextWorkRequired), given
        /// the time it claims. False with a reason when an earlier header the rule needs is not available.
        /// </summary>
        public bool ExpectedBits(long height, uint headerTime, Func<long, BtcHeader?> at, out uint bits, out string? reason)
        {
            bits = 0;
            reason = null;
            var prev = at(height - 1);
            if (prev == null) { reason = $"the header before height {height} is not known"; return false; }

            if (NoRetarget) { bits = LimitBits; return true; }

            if (height % Interval != 0)
            {
                if (!AllowMinDifficulty) { bits = prev.Bits; return true; }

                // Testnet: more than twenty minutes after the parent, the easiest target is the required one.
                if ((long)headerTime > (long)prev.Time + Spacing * 2) { bits = LimitBits; return true; }

                // Otherwise: the target of the last block that was not one of those easy blocks, looking back no further
                // than the first block of this interval.
                var h = height - 1;
                var cursor = prev;
                while (h % Interval != 0 && cursor.Bits == LimitBits)
                {
                    var earlier = at(h - 1);
                    if (earlier == null) { reason = $"the header at height {h - 1} is not known (needed for the testnet target rule)"; return false; }
                    cursor = earlier;
                    h--;
                }
                bits = cursor.Bits;
                return true;
            }

            // A new interval: retarget from how long the last one took.
            var first = at(height - Interval);
            if (first == null) { reason = $"the header at height {height - Interval} is not known (needed to retarget at {height})"; return false; }

            long actual = (long)prev.Time - (long)first.Time;
            if (actual < Timespan / 4) actual = Timespan / 4;
            if (actual > Timespan * 4) actual = Timespan * 4;

            var startFrom = EnforceBip94 ? first.Bits : prev.Bits;
            if (!BtcHeader.TryDecodeCompact(startFrom, out var target))
            { reason = $"the target {startFrom:x8} the retarget at {height} starts from cannot be decoded"; return false; }

            target = target * actual / Timespan;
            if (target > _limit) target = _limit;
            bits = BtcHeader.EncodeCompact(target);
            return true;
        }

        /// <summary>The median of the times of the <see cref="MedianSpan"/> headers before <paramref name="height"/>.</summary>
        public static bool MedianTimePast(long height, Func<long, BtcHeader?> at, out uint median, out string? reason)
        {
            median = 0;
            reason = null;
            var times = new List<uint>(MedianSpan);
            for (var h = height - 1; h >= height - MedianSpan && h >= 0; h--)
            {
                var header = at(h);
                if (header == null) { reason = $"the header at height {h} is not known (needed for the median time of {height})"; return false; }
                times.Add(header.Time);
            }
            if (times.Count == 0) { reason = $"no headers before height {height}"; return false; }
            times.Sort();
            median = times[times.Count / 2];
            return true;
        }
    }
}
