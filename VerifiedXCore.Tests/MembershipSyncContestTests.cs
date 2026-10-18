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
    /// Membership sync applies a finalized record as its peers applied it, unless the responders to one pull hold
    /// different records at a seq above our head: then nothing at or above that seq is adopted from the pull, and the
    /// fork is raised to the operator. The next pull asks again.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipSyncContestTests : IDisposable
    {
        private const long HeadSeq = 3;
        private const long Effective = 500;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _members = new();
        private readonly CasterMembershipRecord _head;

        public MembershipSyncContestTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"membership_contest_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        /// <summary>A demotion of member <paramref name="demoted"/> at seq 4, signed by the other two (a majority of three).</summary>
        private CasterMembershipRecord Successor(long effective, int demoted = 2)
        {
            var candidate = new CasterMembershipRecord
            {
                RecordSeq = HeadSeq + 1, EffectiveFromHeight = effective, PrevRecordHash = _head.RecordHash, ChangeType = "Demotion",
                ChangedAddress = _head.Casters[demoted].Address, Casters = _head.Casters.Where((_, i) => i != demoted).ToList(),
            };
            candidate.RecordHash = CasterMembershipStore.ComputeRecordHash(candidate);
            var payload = CasterMembershipStore.CanonicalPayload(candidate);
            foreach (var m in _members.Where(m => candidate.Casters.Any(c => c.Address == m.Address)))
                candidate.Signatures.Add(new RecordSignature { SignerAddress = m.Address, Signature = SignatureService.CreateSignature(payload, m.Key, m.Pub) });
            return candidate;
        }

        private static (string Ip, long HeadSeq, List<CasterMembershipRecord> Records) From(string ip, params CasterMembershipRecord[] records) =>
            (ip, records.Length == 0 ? HeadSeq : records.Max(r => r.RecordSeq), records.ToList());

        [Fact]
        public void TwoRespondersWithDifferentRecordsAtOneSeq_AdoptNeither()
        {
            TipAt(Effective + 50);
            var a = Successor(Effective, demoted: 2);
            var b = Successor(Effective, demoted: 1);
            Assert.NotEqual(a.RecordHash, b.RecordHash);
            var responses = new List<(string, long, List<CasterMembershipRecord>)> { From("10.0.0.2", a), From("10.0.0.3", b) };

            Assert.Equal(HeadSeq + 1, CasterDiscoveryService.ContestedSeq(HeadSeq, responses));
            Assert.Equal(0, CasterDiscoveryService.AdoptResponses(HeadSeq, responses));
            Assert.Equal(HeadSeq, CasterMembershipStore.GetCurrent()!.RecordSeq);
        }

        [Fact]
        public void OneRespondersRecord_IsAdopted()
        {
            TipAt(Effective + 50);
            var responses = new List<(string, long, List<CasterMembershipRecord>)> { From("10.0.0.2", Successor(Effective)), From("10.0.0.3") };

            Assert.Null(CasterDiscoveryService.ContestedSeq(HeadSeq, responses));
            Assert.Equal(1, CasterDiscoveryService.AdoptResponses(HeadSeq, responses));
            Assert.Equal(HeadSeq + 1, CasterMembershipStore.GetCurrent()!.RecordSeq);
        }

        [Fact]
        public void RespondersThatAgree_AreAdopted()
        {
            TipAt(Effective + 50);
            var record = Successor(Effective);
            var responses = new List<(string, long, List<CasterMembershipRecord>)> { From("10.0.0.2", record), From("10.0.0.3", record) };

            Assert.Null(CasterDiscoveryService.ContestedSeq(HeadSeq, responses));
            Assert.Equal(1, CasterDiscoveryService.AdoptResponses(HeadSeq, responses));
            Assert.Equal(record.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        [Fact]
        public void ADisagreementAtOrBelowOurHead_IsNotAContest()
        {
            var sibling = new CasterMembershipRecord { RecordSeq = HeadSeq, RecordHash = "sibling" };
            var responses = new List<(string, long, List<CasterMembershipRecord>)> { From("10.0.0.2", _head), From("10.0.0.3", sibling) };
            Assert.Null(CasterDiscoveryService.ContestedSeq(HeadSeq, responses));
        }
    }
}
