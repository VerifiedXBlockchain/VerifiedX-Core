using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The producer puts on its block the records it holds that the chain has not carried yet and that the block may
    /// carry, each judged as a verifier will judge it: what it crafts, its own validation takes. Through
    /// BlockchainData.CraftBlock_V5 as the validator loop calls it, then BlockValidatorService.ValidateBlock.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainProducerTests : MembershipChainTestBase
    {
        private CasterMembershipRecord _anchor = null!;

        private async Task AtTheHeight()
        {
            await Start();
            AccountData.GetAccounts().InsertSafe(new Account { Address = Producer.Address, PublicKey = Producer.Pub, PrivateKey = "0" + Producer.Key.secret.ToString("x") });
            Globals.ValidatorAddress = Producer.Address;
            _anchor = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            Hold(_anchor);
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = _anchor.RecordHash;
        }

        private async Task<Block> Craft()
        {
            var height = Tip + 1;
            var answer = ProofUtility.ComputeVrf(Producer.Pub, height, VrfSeed.ThirdInput(height, Globals.LastBlock.Hash) ?? Globals.LastBlock.Hash).ProofHash;
            var block = await BlockchainData.CraftBlock_V5(Producer.Address, 1, answer, height, false, true);
            Assert.NotNull(block);
            return block!;
        }

        private async Task Commit(Block block, IEnumerable<V>? attestors)
        {
            if (attestors != null) Certify(block, attestors);
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(block), false));
        }

        [Fact]
        public async Task TheProducerStartsTheChain_WithTheAnchorAndTheRecordsAfterIt()
        {
            await AtTheHeight();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, 60);
            Hold(demotion);

            var block = await Craft();

            Assert.Equal(new[] { _anchor.RecordHash, demotion.RecordHash }, block.MembershipRun!.Select(r => r.RecordHash));
            await Commit(block, null);
            Assert.Equal(demotion.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }

        [Fact]
        public async Task AProducerWithoutTheAnchor_StartsNothing_AndItsBlockIsTaken()
        {
            await AtTheHeight();
            Globals.MembershipAnchorRecordHash = Record(3, 50, "prev", Casters.Take(3), "Promotion", Casters[2].Address).RecordHash;
            var block = await Craft();
            Assert.Null(block.MembershipRun);
            await Commit(block, null);
            Assert.False(MembershipChain.ActiveAt(Tip + 1));
        }

        [Fact]
        public async Task APendingRecord_GoesOnTheFirstBlockItMay_AndNotBefore()
        {
            await AtTheHeight();
            await Commit(await Craft(), null);                              // starts the chain
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 2);
            Hold(demotion);

            var early = await Craft();                                      // one block before its effective height
            Assert.Null(early.MembershipRun);
            await Commit(early, Casters.Take(3));

            var carrier = await Craft();
            Assert.Equal(demotion.RecordHash, Assert.Single(carrier.MembershipRun!).RecordHash);
            await Commit(carrier, Casters.Take(3));
            Assert.Equal(demotion.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);

            var after = await Craft();                                      // carried: not again
            Assert.Null(after.MembershipRun);
        }

        [Fact]
        public async Task ARecordPastItsCarryWindow_IsNotPutOnTheBlock()
        {
            await AtTheHeight();
            await Commit(await Craft(), null);
            var stale = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1 - MembershipChain.CarryWindowBlocks - 1);
            Hold(stale);
            var block = await Craft();
            Assert.Null(block.MembershipRun);
            await Commit(block, Casters.Take(3));
            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }
    }
}
