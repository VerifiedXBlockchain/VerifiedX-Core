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
    /// A bootstrap-reset proposal whose append is refused releases the proposer's own mark on that seq, as the ordinary
    /// proposer does; an appended one keeps it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BootstrapResetMarkReleaseTests : IDisposable
    {
        private const long HeadSeq = 3;
        private const long Effective = 500;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _members = new();
        private readonly CasterMembershipRecord _head;

        public BootstrapResetMarkReleaseTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bootstrap_mark_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        [Fact]
        public void ARefusedResetAppend_ReleasesTheProposersMark()
        {
            TipAt(Effective - 5);
            var reset = new CasterMembershipRecord
            {
                RecordSeq = HeadSeq + 1, EffectiveFromHeight = Effective, PrevRecordHash = _head.RecordHash,
                ChangeType = CasterMembershipStore.BootstrapResetChangeType, ChangedAddress = "", Casters = _head.Casters.Take(1).ToList(),
            };
            reset.RecordHash = CasterMembershipStore.ComputeRecordHash(reset); // unsigned: the append is refused
            Assert.True(CasterMembershipStore.TryMarkSigned(reset.RecordSeq, reset.RecordHash, CasterMembershipStore.ComputeCasterSetHash(reset), reset.EffectiveFromHeight));

            Assert.False(BootstrapResetService.AppendOrReleaseMark(reset));
            Assert.Equal(HeadSeq, CasterMembershipStore.GetCurrent()!.RecordSeq);
            Assert.True(CasterMembershipStore.TryMarkSigned(reset.RecordSeq, "other-hash", "other-set", Effective + 1));
        }

        [Fact]
        public void AnAppendedRecord_KeepsTheMark()
        {
            TipAt(Effective - 5);
            var record = Successor(Effective);
            Assert.True(CasterMembershipStore.TryMarkSigned(record.RecordSeq, record.RecordHash, CasterMembershipStore.ComputeCasterSetHash(record), record.EffectiveFromHeight));
            Assert.True(BootstrapResetService.AppendOrReleaseMark(record));
            Assert.Equal(HeadSeq + 1, CasterMembershipStore.GetCurrent()!.RecordSeq);
            Assert.False(CasterMembershipStore.TryMarkSigned(record.RecordSeq, "other-hash", "other-set", Effective + 1));
        }
    }
}
