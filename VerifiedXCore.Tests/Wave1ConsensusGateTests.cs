using System.Collections.Concurrent;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Wave 1 (fork-incident remediation): A2 consensus-version handshake gate,
    /// Phase E cooperative-bootstrap notice verification and seed-injection gating.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class Wave1ConsensusGateTests
    {
        // ── A2: ConsensusVersionGate ─────────────────────────────────────

        [Fact]
        public void ConsensusVersionGate_MatchingVersion_Passes()
        {
            Assert.True(ConsensusVersionGate.Check(Globals.ConsensusVersion.ToString()));
            Assert.True(ConsensusVersionGate.Check($"  {Globals.ConsensusVersion}  ")); // trimmed
        }

        [Fact]
        public void ConsensusVersionGate_MissingOrEmptyHeader_Rejected()
        {
            Assert.False(ConsensusVersionGate.Check(null));
            Assert.False(ConsensusVersionGate.Check(""));
            Assert.False(ConsensusVersionGate.Check("   "));
        }

        [Fact]
        public void ConsensusVersionGate_MismatchOrGarbage_Rejected()
        {
            Assert.False(ConsensusVersionGate.Check((Globals.ConsensusVersion + 1).ToString()));
            Assert.False(ConsensusVersionGate.Check((Globals.ConsensusVersion - 1).ToString()));
            Assert.False(ConsensusVersionGate.Check("abc"));
            Assert.False(ConsensusVersionGate.Check("1.0"));
        }

        // ── Phase E: BootstrapAgreementNotice verification ───────────────

        private static BootstrapAgreementNotice MakeNotice(string seedAddress, long height = 100, string hash = "abc123", long? ts = null, string sig = "MA==.MA==")
            => new BootstrapAgreementNotice
            {
                SeedAddress = seedAddress,
                Height = height,
                TipHash = hash,
                Timestamp = ts ?? TimeUtil.GetTime(),
                Signature = sig
            };

        [Fact]
        public void VerifyNotice_UnknownSeedAddress_Rejected()
        {
            var notice = MakeNotice("xNotARealSeedAddress123456789012345");
            Assert.False(BootstrapCoordinationService.VerifyNotice(notice, 100, "abc123"));
        }

        [Fact]
        public void VerifyNotice_HeightOrHashMismatch_Rejected()
        {
            var seed = Globals.BootstrapCasterAddresses.First();
            Assert.False(BootstrapCoordinationService.VerifyNotice(MakeNotice(seed, height: 100, hash: "abc123"), 101, "abc123"));
            Assert.False(BootstrapCoordinationService.VerifyNotice(MakeNotice(seed, height: 100, hash: "abc123"), 100, "different"));
        }

        [Fact]
        public void VerifyNotice_StaleTimestamp_Rejected()
        {
            var seed = Globals.BootstrapCasterAddresses.First();
            var stale = MakeNotice(seed, ts: TimeUtil.GetTime() - 3600);
            Assert.False(BootstrapCoordinationService.VerifyNotice(stale, 100, "abc123"));
        }

        [Fact]
        public void VerifyNotice_InvalidSignature_Rejected()
        {
            // Everything else valid — a known seed address, matching height/hash, fresh
            // timestamp — but the signature is junk. Must fail on signature verification.
            var seed = Globals.BootstrapCasterAddresses.First();
            var notice = MakeNotice(seed);
            Assert.False(BootstrapCoordinationService.VerifyNotice(notice, notice.Height, notice.TipHash));
        }

        [Fact]
        public void VerifyNotice_SelfNotice_Rejected()
        {
            var originalAddr = Globals.ValidatorAddress;
            try
            {
                var seed = Globals.BootstrapCasterAddresses.First();
                Globals.ValidatorAddress = seed;
                var notice = MakeNotice(seed);
                Assert.False(BootstrapCoordinationService.VerifyNotice(notice, notice.Height, notice.TipHash));
            }
            finally
            {
                Globals.ValidatorAddress = originalAddr;
            }
        }

        // ── Phase E: seed-retirement injection gating ────────────────────

        [Fact]
        public void ShouldInjectHardcodedBootstrapPeers_SeedWithoutAgreement_Refused()
        {
            var originalAddr = Globals.ValidatorAddress;
            var originalCasters = Globals.BlockCasters;
            try
            {
                // Even with an empty caster bag (which normally forces injection),
                // a SEED node without an active bootstrap agreement must not self-inject.
                Globals.ValidatorAddress = Globals.BootstrapCasterAddresses.First();
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Assert.False(SeedNodeService.ShouldInjectHardcodedBootstrapPeers());
            }
            finally
            {
                Globals.ValidatorAddress = originalAddr;
                Globals.BlockCasters = originalCasters;
            }
        }

        [Fact]
        public void ShouldInjectHardcodedBootstrapPeers_NonSeedEmptyBag_Allowed()
        {
            var originalAddr = Globals.ValidatorAddress;
            var originalCasters = Globals.BlockCasters;
            try
            {
                // A non-seed node with an empty caster bag keeps the permissive cold-start
                // behavior — it needs the hardcoded IPs to find the network at all.
                Globals.ValidatorAddress = "xNotASeedValidatorAddress1234567890";
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Assert.True(SeedNodeService.ShouldInjectHardcodedBootstrapPeers());
            }
            finally
            {
                Globals.ValidatorAddress = originalAddr;
                Globals.BlockCasters = originalCasters;
            }
        }

        // ── Phase E: single-source seed list ─────────────────────────────

        [Fact]
        public void GetBootstrapSeedPeers_ReturnsThreeCompleteEntries_PerNetwork()
        {
            var originalTestNet = Globals.IsTestNet;
            var originalCustom = Globals.IsCustomTestNet;
            try
            {
                foreach (var (testnet, custom) in new[] { (false, false), (true, false), (true, true) })
                {
                    Globals.IsTestNet = testnet;
                    Globals.IsCustomTestNet = custom;
                    var seeds = SeedNodeService.GetBootstrapSeedPeers();
                    Assert.Equal(3, seeds.Count);
                    Assert.All(seeds, s =>
                    {
                        Assert.False(string.IsNullOrEmpty(s.PeerIP));
                        Assert.False(string.IsNullOrEmpty(s.ValidatorAddress));
                        Assert.False(string.IsNullOrEmpty(s.ValidatorPublicKey));
                        // Every hardcoded seed must be in the bootstrap allowlist —
                        // guards the genesis-list discrepancy flagged in the plan.
                        Assert.Contains(s.ValidatorAddress, Globals.BootstrapCasterAddresses);
                    });
                }
            }
            finally
            {
                Globals.IsTestNet = originalTestNet;
                Globals.IsCustomTestNet = originalCustom;
            }
        }
    }
}
