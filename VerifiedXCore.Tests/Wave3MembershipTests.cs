using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Wave 3 (caster membership record): canonical hashing, genesis determinism, and the
    /// successor-validation matrix. Signature-positive paths need real validator keys and are
    /// covered by the testnet drills; everything structural/cryptographic-negative is here.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class Wave3MembershipTests
    {
        private static CasterMembershipRecord MakeRecord(long seq, long effective, string prevHash, string changeType, string changedAddr, params (string Addr, string Ip, string Pk)[] casters)
        {
            var rec = new CasterMembershipRecord
            {
                RecordSeq = seq,
                EffectiveFromHeight = effective,
                PrevRecordHash = prevHash,
                ChangeType = changeType,
                ChangedAddress = changedAddr,
                Casters = casters.Select(c => new CasterInfo { Address = c.Addr, PeerIP = c.Ip, PublicKey = c.Pk }).ToList(),
                Signatures = new List<RecordSignature>()
            };
            rec.RecordHash = CasterMembershipStore.ComputeRecordHash(rec);
            return rec;
        }

        // ── Canonical payload / hash ─────────────────────────────────────

        [Fact]
        public void RecordHash_IsDeterministic_AndOrderIndependent()
        {
            var a = MakeRecord(1, 100, "abc", "Promotion", "xB", ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"));
            var b = MakeRecord(1, 100, "abc", "Promotion", "xB", ("xB", "2.2.2.2", "PKB"), ("xA", "1.1.1.1", "PKA"));
            Assert.Equal(a.RecordHash, b.RecordHash); // internal ordinal sort makes input order irrelevant
            Assert.Equal(64, a.RecordHash.Length);    // sha256 hex
            Assert.Equal(a.RecordHash, a.RecordHash.ToLowerInvariant());
        }

        [Fact]
        public void RecordHash_ChangesWithAnyField()
        {
            var baseline = MakeRecord(1, 100, "abc", "Promotion", "xB", ("xA", "1.1.1.1", "PKA"));
            Assert.NotEqual(baseline.RecordHash, MakeRecord(2, 100, "abc", "Promotion", "xB", ("xA", "1.1.1.1", "PKA")).RecordHash);
            Assert.NotEqual(baseline.RecordHash, MakeRecord(1, 101, "abc", "Promotion", "xB", ("xA", "1.1.1.1", "PKA")).RecordHash);
            Assert.NotEqual(baseline.RecordHash, MakeRecord(1, 100, "xyz", "Promotion", "xB", ("xA", "1.1.1.1", "PKA")).RecordHash);
            Assert.NotEqual(baseline.RecordHash, MakeRecord(1, 100, "abc", "Demotion", "xB", ("xA", "1.1.1.1", "PKA")).RecordHash);
            Assert.NotEqual(baseline.RecordHash, MakeRecord(1, 100, "abc", "Promotion", "xB", ("xA", "1.1.1.1", "PKA"), ("xC", "3.3.3.3", "PKC")).RecordHash);
        }

        // ── Genesis ──────────────────────────────────────────────────────

        [Fact]
        public void GenesisRecord_IsDeterministicForBoundary_AndSeedAllowlisted()
        {
            var g1 = CasterMembershipStore.BuildGenesisRecord(12345);
            var g2 = CasterMembershipStore.BuildGenesisRecord(12345);
            Assert.Equal(g1.RecordHash, g2.RecordHash);           // same boundary → identical record (concurrent seed attempts merge)
            Assert.NotEqual(g1.RecordHash, CasterMembershipStore.BuildGenesisRecord(12346).RecordHash);
            Assert.Equal(0, g1.RecordSeq);
            Assert.Equal(12345, g1.EffectiveFromHeight);
            Assert.Equal(CasterMembershipStore.GenesisPrevHash, g1.PrevRecordHash);
            Assert.Equal(3, g1.Casters.Count);
            Assert.All(g1.Casters, c => Assert.Contains(c.Address, Globals.BootstrapCasterAddresses));
            // Sorted by address ordinal
            var sorted = g1.Casters.Select(c => c.Address).OrderBy(a => a, StringComparer.Ordinal).ToList();
            Assert.Equal(sorted, g1.Casters.Select(c => c.Address).ToList());
        }

        [Fact]
        public void Genesis_RequiresSeedSignatures()
        {
            // Wave 6: genesis is no longer deterministic-only — it needs >=2 valid signatures
            // from the hardcoded seed allowlist. Structurally-correct but UNSIGNED genesis fails.
            var unsigned = CasterMembershipStore.BuildGenesisRecord(500);
            Assert.False(CasterMembershipStore.ValidateSuccessor(null, unsigned, out var reason));
            Assert.Contains("signature", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Genesis_JunkOrNonSeedSignatures_Rejected()
        {
            var g = CasterMembershipStore.BuildGenesisRecord(500);
            // Junk sigs claiming seed identities + a (format-valid) sig from a non-seed address.
            var seeds = Globals.BootstrapCasterAddresses.Take(2).ToList();
            g.Signatures = new List<RecordSignature>
            {
                new() { SignerAddress = seeds[0], Signature = "MA==.MA==" },
                new() { SignerAddress = seeds[1], Signature = "garbage" },
                new() { SignerAddress = "xNotASeed", Signature = "MA==.MA==" },
            };
            Assert.False(CasterMembershipStore.ValidateSuccessor(null, g, out var reason));
            Assert.Contains("signature", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Genesis_WrongCasterSet_Rejected()
        {
            var g = CasterMembershipStore.BuildGenesisRecord(500);
            g.Casters.Add(new CasterInfo { Address = "xIntruder", PeerIP = "9.9.9.9", PublicKey = "PK" });
            g.RecordHash = CasterMembershipStore.ComputeRecordHash(g);
            Assert.False(CasterMembershipStore.ValidateSuccessor(null, g, out var reason));
            Assert.Contains("seed list", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Genesis_TamperedHashOrPrev_Rejected()
        {
            var g1 = CasterMembershipStore.BuildGenesisRecord(500);
            g1.RecordHash = new string('0', 64);
            Assert.False(CasterMembershipStore.ValidateSuccessor(null, g1, out var r1));
            Assert.Contains("recordHash", r1, StringComparison.OrdinalIgnoreCase);

            var g2 = CasterMembershipStore.BuildGenesisRecord(500);
            g2.PrevRecordHash = "notgenesis";
            g2.RecordHash = CasterMembershipStore.ComputeRecordHash(g2);
            Assert.False(CasterMembershipStore.ValidateSuccessor(null, g2, out var r2));
            Assert.Contains("prevHash", r2, StringComparison.OrdinalIgnoreCase);
        }

        // ── Successor validation matrix ──────────────────────────────────

        private static CasterMembershipRecord Prev()
            => MakeRecord(5, 1000, "prevprevhash", "Promotion", "xC",
                ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"), ("xC", "3.3.3.3", "PKC"));

        [Fact]
        public void ValidateSuccessor_WrongSeq_Rejected()
        {
            var prev = Prev();
            var cand = MakeRecord(7, 1100, prev.RecordHash, "Demotion", "xC", ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"));
            Assert.False(CasterMembershipStore.ValidateSuccessor(prev, cand, out var reason));
            Assert.Contains("seq", reason);
        }

        [Fact]
        public void ValidateSuccessor_WrongPrevHash_Rejected()
        {
            var prev = Prev();
            var cand = MakeRecord(6, 1100, "notprevhash", "Demotion", "xC", ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"));
            Assert.False(CasterMembershipStore.ValidateSuccessor(prev, cand, out var reason));
            Assert.Contains("prevRecordHash", reason);
        }

        [Fact]
        public void ValidateSuccessor_NonAdvancingHeight_Rejected()
        {
            var prev = Prev();
            var cand = MakeRecord(6, 1000, prev.RecordHash, "Demotion", "xC", ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"));
            Assert.False(CasterMembershipStore.ValidateSuccessor(prev, cand, out var reason));
            Assert.Contains("height", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateSuccessor_EmptyCasterSet_Rejected()
        {
            var prev = Prev();
            var cand = MakeRecord(6, 1100, prev.RecordHash, "Demotion", "xC");
            Assert.False(CasterMembershipStore.ValidateSuccessor(prev, cand, out var reason));
            Assert.Contains("empty", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateSuccessor_TamperedRecordHash_Rejected()
        {
            var prev = Prev();
            var cand = MakeRecord(6, 1100, prev.RecordHash, "Demotion", "xC", ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"));
            cand.RecordHash = new string('0', 64);
            Assert.False(CasterMembershipStore.ValidateSuccessor(prev, cand, out var reason));
            Assert.Contains("recordHash", reason);
        }

        [Fact]
        public void ValidateSuccessor_FakeOrForeignSignatures_Rejected()
        {
            var prev = Prev();
            var cand = MakeRecord(6, 1100, prev.RecordHash, "Demotion", "xC", ("xA", "1.1.1.1", "PKA"), ("xB", "2.2.2.2", "PKB"));
            // Junk signatures from previous-set members + a valid-format one from a NON-member.
            cand.Signatures = new List<RecordSignature>
            {
                new() { SignerAddress = "xA", Signature = "MA==.MA==" },
                new() { SignerAddress = "xB", Signature = "garbage" },
                new() { SignerAddress = "xNOTAMEMBER", Signature = "MA==.MA==" },
            };
            Assert.False(CasterMembershipStore.ValidateSuccessor(prev, cand, out var reason));
            Assert.Contains("signatures", reason, StringComparison.OrdinalIgnoreCase);
        }

        // ── Record-era gating (legacy fallback) ──────────────────────────

        [Fact]
        public void RecordEra_InactiveWithoutGenesis_CommitteeIsNull()
        {
            // Wave 6: the era is store-driven — with no genesis record installed (unit tests never
            // mint one, which needs real seed keys), the subsystem is dark and quorum sites fall
            // back to the legacy BlockCasters view.
            Assert.Null(CasterMembershipStore.GetCurrent());
            Assert.False(CasterMembershipStore.RecordEraActive);
            Assert.Null(CasterMembershipStore.GetCommitteeForHeight(123456));
            Assert.Null(CasterMembershipStore.GetForHeight(123456));
        }

        [Fact]
        public void HandleSignRequest_EraInactive_RefusesToSign()
        {
            var req = new MembershipSignRequest { Candidate = Prev(), ProposerAddress = "xA" };
            Assert.Null(CasterMembershipService.HandleSignRequest(req));
        }

        [Fact]
        public async Task ProposeRotation_EraInactive_IsNoOpSuccess()
        {
            // Legacy era: rotation must not block promotions/demotions — returns true untouched.
            var ok = await CasterMembershipService.ProposeRotationAsync("Promotion", "xNEW", new CasterInfo { Address = "xNEW" });
            Assert.True(ok);
        }
    }
}
