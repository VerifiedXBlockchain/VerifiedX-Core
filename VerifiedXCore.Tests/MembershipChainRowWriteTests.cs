using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The committee for a height is read from the rows noted for the blocks that carried records. A stored block
    /// always has its row: the row is written first, and a block whose row cannot be written is not written. A row
    /// written for a block the store never got (a crash between the two) is never read. Through
    /// BlockValidatorService.ValidateBlock and BlockchainData.AddBlock.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainRowWriteTests : MembershipChainTestBase
    {
        private CasterMembershipRecord _anchor = null!;

        private async Task StartTheChain()
        {
            await Start();
            _anchor = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            Hold(_anchor);
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = _anchor.RecordHash;
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(null, new List<CasterMembershipRecord> { _anchor })), false));
        }

        public override void Dispose()
        {
            MembershipChain.FailRowWriteForTests = null;
            base.Dispose();
        }

        /// <summary>
        /// The row for a block carrying a demotion cannot be written. The block is not written either: the tip stays, and
        /// the committee is the one before. The same block is taken once rows can be written, with its demotion in force.
        /// Before, the block was stored without its row, and this node judged every later block by the old committee.
        /// </summary>
        [Fact]
        public async Task ABlockWhoseRowCannotBeWritten_IsNotWritten_AndIsTakenOnceItCanBe()
        {
            await StartTheChain();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
            var carrier = NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { demotion });
            var tip = Tip;

            MembershipChain.FailRowWriteForTests = () => true;
            Assert.False(await BlockValidatorService.ValidateBlock(Relayed(carrier), false));

            Assert.Equal(tip, Tip);
            Assert.Null(BlockchainData.GetBlockByHeight(carrier.Height));
            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);

            MembershipChain.FailRowWriteForTests = null;
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(carrier), false));
            Assert.Equal(demotion.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }

        /// <summary>
        /// A committee read runs between a block's row being written and the block itself (another thread: a caster's round,
        /// admission, the API). It does not see the row, whose block is not stored yet, and it leaves it there: once the
        /// block is stored its demotion is in force. Before, the read deleted the row and the block was stored without it.
        /// </summary>
        [Fact]
        public async Task ACommitteeReadBetweenTheRowAndTheBlock_LeavesTheRow()
        {
            await StartTheChain();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
            var carrier = NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { demotion });
            var readsInTheGap = 0;
            MembershipChain.AfterRowWrittenForTests = b =>
            {
                if (b.Height != carrier.Height) return;
                readsInTheGap++;
                MembershipChain.ResetForTests();
                Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(b.Height + 1)!.RecordHash); // the block is not stored yet
            };
            try
            {
                Assert.True(await BlockValidatorService.ValidateBlock(Relayed(carrier), false));
            }
            finally { MembershipChain.AfterRowWrittenForTests = null; }

            Assert.Equal(1, readsInTheGap);
            MembershipChain.ResetForTests();
            Assert.Equal(demotion.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }

        /// <summary>A row written for a block the store never got is not read, and the block written at that height decides.</summary>
        [Fact]
        public async Task ARowForABlockNeverWritten_IsNotRead()
        {
            await StartTheChain();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
            var lost = NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { demotion });
            MembershipChain.WriteRow(lost);                                 // the crash came before the block was written

            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(lost.Height + 1)!.RecordHash);
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(Casters.Take(3), null)), false));
            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }
    }
}
