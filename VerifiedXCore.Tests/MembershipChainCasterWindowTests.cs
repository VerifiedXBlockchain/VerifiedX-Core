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
    /// The Base casters' catch-up window holds every committee that governed a block of the day. From the membership
    /// chain's start a record governs from the block after the one that carried it, which can be well past its effective
    /// height, so the window is read at those heights too. Through BaseSignerSetPolicy.CasterWindow, over blocks committed by
    /// BlockValidatorService.ValidateBlock.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainCasterWindowTests : MembershipChainTestBase
    {
        private static List<string> Bases(IEnumerable<V> members) =>
            members.Select(m => BaseSignerSetPolicy.BaseAddressOf(m.Pub)).OrderBy(a => a).ToList();

        /// <summary>
        /// A demotion takes effect at one height and is carried ten blocks later; from the block after, it governs to the
        /// boundary. The window holds the committee before it and the one it put in force. Read at its effective height
        /// alone, the record in force was still the one before, and the window held that committee only.
        /// </summary>
        [Fact]
        public async Task ARecordCarriedPastItsEffectiveHeight_IsInTheWindow()
        {
            await Start();
            var anchor = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            Hold(anchor);
            var from = Tip;
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = anchor.RecordHash;
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(null, new List<CasterMembershipRecord> { anchor })), false));

            var demotion = Next(anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 2);
            while (Tip < demotion.EffectiveFromHeight + 10)
                Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(Casters.Take(3), null)), false));
            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { demotion })), false));
            Assert.Equal(anchor.RecordHash, CasterMembershipStore.GetForHeight(demotion.EffectiveFromHeight)!.RecordHash);

            var boundary = from + BaseSignerSetPolicy.CasterStableBlocks;
            var window = BaseSignerSetPolicy.CasterWindow(boundary);

            Assert.NotNull(window);
            Assert.Equal(new[] { Bases(Casters.Take(4)), Bases(Casters.Take(3)) }, window!.Select(c => c.OrderBy(a => a).ToList()));
        }
    }
}
