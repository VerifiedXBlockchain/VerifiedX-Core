using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The Base signer side asks who is live, and who a caster replacement would seat that is not a caster, of the
    /// committee in force for the next block, not of the newest record this node holds: a record no block has carried may
    /// never govern a block. Through BaseValidatorSyncService.LiveTargetOf, the veto loop's and the signing route's rule.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainBaseSignerTests : MembershipChainTestBase
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

        private static string Base(V caster) => BaseSignerSetPolicy.BaseAddressOf(caster.Pub);

        private static string? Target(IEnumerable<string>? removes = null, IEnumerable<string>? adds = null) =>
            BaseValidatorSyncService.LiveTargetOf(
                new BaseBridgeService.PendingRemoval(Array.Empty<string>(), (removes ?? Array.Empty<string>()).ToList(), 1) { Nonce = 1, CasterAdds = (adds ?? Array.Empty<string>()).ToList() },
                Globals.LastBlock.Height, 0);

        /// <summary>
        /// This node holds a demotion of a live caster that no block has carried. A pending Base removal of that caster is
        /// still vetoed: the caster is in the committee in force. Once a block carries the demotion it is not.
        /// </summary>
        [Fact]
        public async Task APendingDemotionNoBlockCarried_DoesNotStopTheVetoOfTheCastersRemoval()
        {
            await StartTheChain();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 2);
            Assert.True(CasterMembershipStore.TryAppend(demotion, out var why), why);
            Assert.Equal(demotion.RecordHash, CasterMembershipStore.GetCurrent()!.RecordHash);

            Assert.Equal(Base(Casters[3]), Target(removes: new[] { Base(Casters[3]) }));

            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(Casters.Take(3), null)), false));
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { demotion })), false));
            Assert.Null(Target(removes: new[] { Base(Casters[3]) }));
        }

        /// <summary>A caster a pending, never-carried promotion would add is still a stranger to a replacement that seats it.</summary>
        [Fact]
        public async Task APendingPromotionNoBlockCarried_LeavesTheCandidateAStrangerToAReplacement()
        {
            await StartTheChain();
            var removal = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { removal })), false));
            var promotion = Next(removal, Casters.Take(3).Append(Casters[4]), "Promotion", Casters[4].Address, Tip + 2);
            Assert.True(CasterMembershipStore.TryAppend(promotion, out var why), why);

            Assert.Equal(Base(Casters[4]), Target(adds: new[] { Base(Casters[4]) }));
        }
    }
}
