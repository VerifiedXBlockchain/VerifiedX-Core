using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A caster never signs two caster sets at one seq while either could still be appended: its mark is released only
    /// once the marked candidate's effective height is behind its tip, and a live append refuses a record whose effective
    /// height has passed. A record fetched by membership sync is applied as its peers applied it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipMarkChainTimeTests : IDisposable
    {
        private const long HeadSeq = 3;
        private const long Effective = 500;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _members = new();
        private readonly CasterMembershipRecord _head;

        public MembershipMarkChainTimeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"mark_chain_time_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            for (var i = 0; i < 3; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                _members.Add((key, pub, AccountData.GetHumanAddress(pub)));
            }
            _head = new CasterMembershipRecord
            {
                RecordSeq = HeadSeq, EffectiveFromHeight = 400, PrevRecordHash = "prev", ChangeType = "Promotion",
                Casters = _members.Select(m => new CasterInfo { Address = m.Address, PeerIP = "10.0.0.1", PublicKey = m.Pub }).OrderBy(c => c.Address, StringComparer.Ordinal).ToList(),
            };
            _head.RecordHash = CasterMembershipStore.ComputeRecordHash(_head);
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Insert(HeadSeq, _head);
            CasterMembershipStore.InvalidateCache();
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void TipAt(long height) => Globals.LastBlock = new Block { Height = height, Timestamp = TimeUtil.GetTime() };

        /// <summary>A demotion of the last member at seq 4, signed by the other two (a majority of three).</summary>
        private CasterMembershipRecord Successor(long effective)
        {
            var candidate = new CasterMembershipRecord
            {
                RecordSeq = HeadSeq + 1, EffectiveFromHeight = effective, PrevRecordHash = _head.RecordHash, ChangeType = "Demotion",
                ChangedAddress = _head.Casters[2].Address, Casters = _head.Casters.Take(2).ToList(),
            };
            candidate.RecordHash = CasterMembershipStore.ComputeRecordHash(candidate);
            var payload = CasterMembershipStore.CanonicalPayload(candidate);
            foreach (var m in _members.Where(m => candidate.Casters.Any(c => c.Address == m.Address)))
                candidate.Signatures.Add(new RecordSignature { SignerAddress = m.Address, Signature = SignatureService.CreateSignature(payload, m.Key, m.Pub) });
            return candidate;
        }

        [Fact]
        public void ALiveAppend_TakesARecordWhoseEffectiveHeightIsAhead()
        {
            TipAt(Effective - 1);
            Assert.True(CasterMembershipStore.TryAppend(Successor(Effective), out var reason), reason);
            Assert.Equal(HeadSeq + 1, CasterMembershipStore.GetCurrent()!.RecordSeq);
        }

        [Fact]
        public void ALiveAppend_RefusesARecordWhoseEffectiveHeightHasPassed()
        {
            TipAt(Effective);
            Assert.False(CasterMembershipStore.TryAppend(Successor(Effective), out var reason));
            Assert.Contains("has passed", reason);
            Assert.Equal(HeadSeq, CasterMembershipStore.GetCurrent()!.RecordSeq);
        }

        [Fact]
        public void MembershipSync_StillAppliesAFinalizedRecord_SoALaggingCasterCatchesUp()
        {
            TipAt(Effective + 50);
            Assert.Equal(1, CasterMembershipStore.TryAppendChain(new List<CasterMembershipRecord> { Successor(Effective) }));
            Assert.Equal(HeadSeq + 1, CasterMembershipStore.GetCurrent()!.RecordSeq);
        }

        [Fact]
        public void ASignersMark_RefusesAnotherSetUntilTheMarkedCandidateIsBehindTheTip()
        {
            const long seq = HeadSeq + 1;
            TipAt(Effective - 5);
            Assert.True(CasterMembershipStore.TryMarkSigned(seq, "hash-a", "set-a", Effective));

            TipAt(Effective + CasterMembershipStore.SignedMarkReleaseMarginBlocks - 1);
            Assert.False(CasterMembershipStore.TryMarkSigned(seq, "hash-b", "set-b", Effective + 20));
            Assert.True(CasterMembershipStore.TryMarkSigned(seq, "hash-a2", "set-a", Effective + 10)); // the same set, another proposer's height

            // The mark now covers the later of the two candidates of set-a.
            TipAt(Effective + CasterMembershipStore.SignedMarkReleaseMarginBlocks);
            Assert.False(CasterMembershipStore.TryMarkSigned(seq, "hash-b", "set-b", Effective + 20));

            TipAt(Effective + 10 + CasterMembershipStore.SignedMarkReleaseMarginBlocks);
            Assert.True(CasterMembershipStore.TryMarkSigned(seq, "hash-b", "set-b", Effective + 20));
        }

        [Fact]
        public void ASignersMark_IsNotReleasedByWallClockAge()
        {
            const long seq = HeadSeq + 1;
            TipAt(Effective - 5);
            Assert.True(CasterMembershipStore.TryMarkSigned(seq, "hash-a", "set-a", Effective));
            var marks = DbContext.DB_Config!.GetCollection<CasterMembershipStore.SignedSeqMarker>("rsrv_membership_signed");
            var mark = marks.FindById(seq);
            mark.SignedAt = TimeUtil.GetTime() - 86_400; // signed a day ago
            marks.Update(seq, mark);

            Assert.False(CasterMembershipStore.TryMarkSigned(seq, "hash-b", "set-b", Effective + 20));
        }
    }
}
