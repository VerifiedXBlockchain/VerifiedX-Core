using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A node that stands down keeps no absence timers. Testnet, Nov 7: two casters saw a member go down, stood down at a
    /// seed reset, and were elected half an hour later beside that member. Their timers had stopped with their observations
    /// and still read it as gone since then, so they signed its removal seconds after the election took effect.
    /// </summary>
    [Collection("DbContextSequential")]
    public class CasterSeatStandDownTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;
        private readonly CasterMembershipRecord _head;

        public CasterSeatStandDownTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"seat_standdown_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterSeatService.ResetForTests();
            _head = new CasterMembershipRecord
            {
                RecordSeq = 53, EffectiveFromHeight = 108_688, PrevRecordHash = "prev", ChangeType = VreeElection.EmergencyElectionChangeType,
                ChangedAddress = "",
                // Unroutable addresses: nobody answers, as a member that is still starting up answers nothing yet.
                Casters = new[] { "xGone", "xA", "xB" }.Select(a => new CasterInfo { Address = a, PeerIP = "192.0.2.1" }).ToList(),
            };
            _head.RecordHash = CasterMembershipStore.ComputeRecordHash(_head);
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Insert(_head.RecordSeq, _head);
            CasterMembershipStore.InvalidateCache();
        }

        public void Dispose()
        {
            Globals.IsBlockCaster = _priorIsBlockCaster;
            CasterSeatService.ResetForTests();
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task AMemberSeenGoneBeforeThisNodeStoodDown_IsNotRemovedOnItsReturnToTheCommittee()
        {
            var now = TimeUtil.GetTime();
            Globals.IsBlockCaster = true;
            CasterSeatService.Observe("xGone", present: false, now - 2_000, 107_000);   // seen gone while this node cast
            Assert.True(CasterSeatService.PastGrace("xGone", now));

            Globals.IsBlockCaster = false;                                                // stood down: a validator's monitor tick
            await CasterSeatService.ObserveCommitteeAsync();

            Globals.IsBlockCaster = true;                                                 // elected again beside that member
            Assert.Null(CasterSeatService.AbsentSeconds("xGone", now));
            Assert.NotNull(CasterSeatService.RemovalRefusal("Demotion", "xGone", _head, now));
        }

        [Fact]
        public async Task ACastingNode_KeepsTimingAMemberThatStaysGone()
        {
            var now = TimeUtil.GetTime();
            Globals.IsBlockCaster = true;
            CasterSeatService.Observe("xGone", present: false, now - 2_000, 107_000);

            await CasterSeatService.ObserveCommitteeAsync();                              // still unreachable: the timer keeps its start

            Assert.True(CasterSeatService.AbsentSeconds("xGone", TimeUtil.GetTime()) >= 2_000);
            Assert.Null(CasterSeatService.RemovalRefusal("Demotion", "xGone", _head, TimeUtil.GetTime()));
        }
    }
}
