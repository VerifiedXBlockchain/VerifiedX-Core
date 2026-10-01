using System;
using System.Collections.Generic;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Mainnet fork at 7,414,815 (Oct 1 2026), the membership side. RCwBk was promoted but never received the record
    /// (seq -1) and cast under the legacy live-list rules; RVGy4 cast while in no record at all. A caster now casts only
    /// with a current record that lists it, and nobody outside the record enters (or stays in) the live caster list.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class ForkSafetyRecordTests
    {
        private static readonly HashSet<string> Committee = new(StringComparer.Ordinal) { "RCwBk", "RDABc", "RFoKr", "RH9XA", "RK28y" };

        private static CasterMembershipRecord Record(long seq, params string[] members) => new()
        {
            RecordSeq = seq,
            Casters = members.Select(a => new CasterInfo { Address = a, PeerIP = "10.0.0." + a.Length }).ToList(),
        };

        // ── B.1: cast only with a current record that lists us ────────────────────────────────────────

        [Fact]
        public void APromotedCasterWithoutTheRecord_Holds()
        {
            // RCwBk on Oct 1: no record, every other caster at seq 7.
            Assert.NotNull(BlockcasterNode.RecordHoldReason(null, new long[] { 7, 7, 7, 7 }, null, "RCwBk"));
            // Behind by one sequence.
            Assert.NotNull(BlockcasterNode.RecordHoldReason(6, new long[] { 7, 7, 6 }, Committee, "RCwBk"));
            // Current.
            Assert.Null(BlockcasterNode.RecordHoldReason(7, new long[] { 7, 7, 7, 7 }, Committee, "RCwBk"));
        }

        [Fact]
        public void OnePeersNewerHead_NeverHoldsACaster()
        {
            // Head claims are unverified: a single peer (or a minority) claiming a newer record is not enough.
            Assert.Null(BlockcasterNode.RecordHoldReason(7, new long[] { 99 }, Committee, "RH9XA"));
            Assert.Null(BlockcasterNode.RecordHoldReason(7, new long[] { 99, 7, 7 }, Committee, "RH9XA"));
            Assert.Null(BlockcasterNode.RecordHoldReason(7, new long[] { 99, 7 }, Committee, "RH9XA"));      // a tie is not a majority
            Assert.NotNull(BlockcasterNode.RecordHoldReason(7, new long[] { 8, 8, 7 }, Committee, "RH9XA"));
            // Nobody answered: nothing to compare against.
            Assert.Null(BlockcasterNode.RecordHoldReason(7, Array.Empty<long>(), Committee, "RH9XA"));
        }

        [Fact]
        public void ACasterOutsideTheCommittee_Holds()
        {
            // RVGy4 on Oct 1: casting with a current record that does not list it.
            Assert.NotNull(BlockcasterNode.RecordHoldReason(7, new long[] { 7, 7, 7 }, Committee, "RVGy4"));
            Assert.NotNull(BlockcasterNode.RecordHoldReason(7, new long[] { 7, 7, 7 }, Committee, ""));
            // Legacy era (no record anywhere): unchanged behaviour.
            Assert.Null(BlockcasterNode.RecordHoldReason(null, new long[] { -1, -1, -1 }, null, "RVGy4"));
        }

        // ── B.3: the live caster list follows the record ──────────────────────────────────────────────

        [Fact]
        public void OnlyRecordMembers_EnterTheLiveList()
        {
            Assert.True(CasterDiscoveryService.IsRecordMember("RVGy4", null, null));                      // legacy era
            Assert.True(CasterDiscoveryService.IsRecordMember("RH9XA", Committee, Record(7, Committee.ToArray())));
            Assert.False(CasterDiscoveryService.IsRecordMember("RVGy4", Committee, Record(7, Committee.ToArray())));
            // A rotation is reconciled when appended, before it governs: its incoming member is accepted early.
            var rotation = Record(8, "RCwBk", "RDABc", "RFoKr", "RH9XA", "RNew1");
            Assert.True(CasterDiscoveryService.IsRecordMember("RNew1", Committee, rotation));
            // ...and the outgoing member stays until the rotation takes effect.
            Assert.True(CasterDiscoveryService.IsRecordMember("RK28y", Committee, rotation));
        }

        [Fact]
        public void StraysInTheLiveList_ArePruned_SelfNever()
        {
            var originalSelf = Globals.ValidatorAddress;
            try
            {
                Globals.ValidatorAddress = "RSelf";
                var live = new List<Peers>
                {
                    new() { ValidatorAddress = "RH9XA" },
                    new() { ValidatorAddress = "RVGy4" },
                    new() { ValidatorAddress = "RSelf" },
                };
                var strays = BlockcasterNode.NonMembers(live, Committee, Record(7, Committee.ToArray()));
                Assert.Equal(new[] { "RVGy4" }, strays.ToArray());
                Assert.Empty(BlockcasterNode.NonMembers(live, null, null));                                 // legacy era
            }
            finally
            {
                Globals.ValidatorAddress = originalSelf;
            }
        }
    }
}
