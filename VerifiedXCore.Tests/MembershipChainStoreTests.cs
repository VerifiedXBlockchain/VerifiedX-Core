using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From the membership chain's start this node's own records follow the chain: what a block carried is written into
    /// them, a record held in another's place gives way, and a record the chain did not carry within its window goes, with
    /// everything signed on top of it and the mark this node keeps of having signed it. Driven by
    /// BlockValidatorService.ValidateBlock committing blocks.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainStoreTests : MembershipChainTestBase
    {
        private CasterMembershipRecord _anchor = null!;

        private async Task StartTheChain()
        {
            await Start();
            _anchor = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            Hold(_anchor);
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = _anchor.RecordHash;
            await Commit(NextBlock(null, new List<CasterMembershipRecord> { _anchor }));
        }

        private static async Task Commit(Block block) => Assert.True(await BlockValidatorService.ValidateBlock(Relayed(block), false));

        /// <summary>A block the chain's four-caster committee certified, carrying <paramref name="run"/>.</summary>
        private Block Certified(List<CasterMembershipRecord>? run = null) => NextBlock(Casters.Take(3), run);

        private static string Set(CasterMembershipRecord r) => CasterMembershipStore.ComputeCasterSetHash(r);

        /// <summary>
        /// This node proposed a demotion and signed it: the record is its head, and its mark holds the sequence for that
        /// caster set. No producer carries it. Until its window has passed nothing changes: the head stays, and this node
        /// will not sign another set at the sequence. Once no block can carry it, the head is the chain's record again and
        /// the mark is released, so another set can be signed there, appended and carried. Left as it was, every later
        /// proposal would have been built on a record no block can carry, and the committee could never change again.
        /// </summary>
        [Fact]
        public async Task ARecordTheChainNeverCarried_IsDroppedOnceItsWindowPasses_AndItsSequenceIsFreeAgain()
        {
            await StartTheChain();
            var effective = Tip + 2;
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, effective);
            Assert.True(CasterMembershipStore.TryMarkSigned(demotion.RecordSeq, demotion.RecordHash, Set(demotion), effective));
            Assert.True(CasterMembershipStore.TryAppend(demotion, out var why), why);
            Assert.Equal(demotion.RecordSeq, HeldSeq());

            var other = Next(_anchor, Casters.Where((_, i) => i != 2).Take(3), "Demotion", Casters[2].Address, effective + MembershipChain.CarryWindowBlocks + 1);
            while (Tip < effective + MembershipChain.CarryWindowBlocks - 1)
                await Commit(Certified());

            // The last block that may still carry it is next: nothing has changed.
            Assert.Equal(demotion.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
            Assert.False(CasterMembershipStore.TryMarkSigned(other.RecordSeq, other.RecordHash, Set(other), other.EffectiveFromHeight));

            await Commit(Certified());                                     // and it does not

            Assert.Equal(_anchor.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
            Assert.True(CasterMembershipStore.TryMarkSigned(other.RecordSeq, other.RecordHash, Set(other), other.EffectiveFromHeight));
            Assert.True(CasterMembershipStore.TryAppend(other, out why), why);
            while (Tip + 1 < other.EffectiveFromHeight)
                await Commit(Certified());
            await Commit(Certified(new List<CasterMembershipRecord> { other }));
            Assert.Equal(other.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
            Assert.Equal(other.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        /// <summary>A node that never received a record the chain carried holds it once the block is written.</summary>
        [Fact]
        public async Task ARecordTheChainCarried_IsWrittenIntoThisNodesRecords()
        {
            await StartTheChain();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
            await Commit(Certified(new List<CasterMembershipRecord> { demotion }));
            Assert.Equal(demotion.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
        }

        /// <summary>A record this node holds at the sequence the chain filled with another gives way, with what it held above it.</summary>
        [Fact]
        public async Task ARecordHeldInPlaceOfTheChainsOne_GivesWay()
        {
            await StartTheChain();
            var mine = Next(_anchor, Casters.Where((_, i) => i != 2).Take(3), "Demotion", Casters[2].Address, Tip + 1);
            Hold(mine);
            Hold(Next(mine, Casters.Take(2).Append(Casters[3]).Append(Casters[4]), "Promotion", Casters[4].Address, Tip + 2));
            var chains = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);

            await Commit(Certified(new List<CasterMembershipRecord> { chains }));

            Assert.Equal(chains.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);
            Assert.Null(CasterMembershipStore.GetBySeq(chains.RecordSeq + 1));
        }

        /// <summary>
        /// A record announced after its effective height is still taken while a block may carry it; before the chain
        /// started a late record was refused, since it would have governed blocks already made.
        /// </summary>
        [Fact]
        public async Task ALateRecord_IsTakenWhileABlockMayStillCarryIt()
        {
            await StartTheChain();
            for (var i = 0; i < 5; i++) await Commit(Certified());
            var late = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip - 3);
            Assert.True(CasterMembershipStore.TryAppend(late, out var why), why);

            Hold(_anchor);
            DbDelete(late.RecordSeq);
            var tooLate = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1 - MembershipChain.CarryWindowBlocks - 1);
            Assert.False(CasterMembershipStore.TryAppend(tooLate, out why));
            Assert.Contains("has passed", why);
        }

        private static void DbDelete(long seq)
        {
            Data.DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Delete(seq);
            CasterMembershipStore.InvalidateCache();
        }
    }
}
