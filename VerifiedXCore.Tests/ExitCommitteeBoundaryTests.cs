using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// #292: an exit's handler was elected from the committee of the tip, while its signers check the leader against the
    /// next block's committee and validation checks its votes at the next block. A signed rotation taking effect at the
    /// next block made the elected handler one the signers refused. The exit's casters now read the next block's
    /// committee, as the signers do.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ExitCommitteeBoundaryTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;

        public ExitCommitteeBoundaryTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"exit_committee_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
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

        private static void Record(long seq, long effective, params string[] casters)
        {
            var record = new CasterMembershipRecord
            {
                RecordSeq = seq, EffectiveFromHeight = effective, PrevRecordHash = seq == 0 ? "GENESIS" : "prev", ChangeType = "Promotion",
                Casters = casters.OrderBy(c => c, StringComparer.Ordinal).Select(c => new CasterInfo { Address = c, PeerIP = "10.0.0.1" }).ToList(),
            };
            record.RecordHash = CasterMembershipStore.ComputeRecordHash(record);
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Insert(seq, record);
            CasterMembershipStore.InvalidateCache();
        }

        [Fact]
        public void AnExitsCasters_ReadTheNextBlocksCommittee_AsItsSignersDo()
        {
            Record(0, 100, "xCasterA", "xCasterB", "xCasterC");
            Record(1, 201, "xCasterA", "xCasterB", "xCasterD");                    // a rotation taking effect at the next block
            Globals.LastBlock = new Block { Height = 200 };

            var committee = BurnExitConsensusService.ExitCommittee();
            Assert.Contains("xCasterD", committee);
            Assert.DoesNotContain("xCasterC", committee);
            Assert.True(FrostSigningAuthorization.IsCommitteeCaster("xCasterD"));   // the signers' check agrees
            Assert.False(FrostSigningAuthorization.IsCommitteeCaster("xCasterC"));

            // The election cannot pick the caster the next block's committee no longer has.
            var elected = BridgeCasterConsensus.SelectHandler(new[] { ("xCasterC", "h"), ("xCasterD", "h") }, committee);
            Assert.Equal("xCasterD", elected);
        }
    }
}
