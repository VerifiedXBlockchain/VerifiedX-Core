using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From the membership chain's start the casters a node works with, and whether it casts itself, follow the committee
    /// the chain has in force, not a record it holds that no block has carried yet. A caster a pending demotion removes
    /// keeps casting until the block that carries the demotion is written; a promoted one joins then. Through
    /// CasterMembershipService.ReconcileBlockCastersToRecord as every record writer calls it, and BlockValidatorService.ValidateBlock.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainCasterTests : MembershipChainTestBase
    {
        private CasterMembershipRecord _anchor = null!;

        /// <summary>The chain has started from the four-caster record, and this node is its fourth caster.</summary>
        private async Task AsTheFourthCaster()
        {
            await Start();
            _anchor = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            Hold(_anchor);
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = _anchor.RecordHash;
            await Commit(NextBlock(null, new List<CasterMembershipRecord> { _anchor }));
            Globals.ValidatorAddress = Casters[3].Address;
            Globals.IsBlockCaster = true;
            Globals.BlockCasters = new ConcurrentBag<Peers>(Casters.Take(4).Select((c, i) => new Peers
            {
                PeerIP = $"10.9.1.{i + 1}", IsValidator = true, ValidatorAddress = c.Address, ValidatorPublicKey = c.Pub,
            }));
        }

        // A caster's own commit path holds the next block for its round's agreement; here a block arrives certified.
        private static async Task Commit(Block block) => Assert.True(await BlockValidatorService.ValidateBlock(Relayed(block), false, skipCasterCheck: true));

        private static HashSet<string> Pool() => Globals.BlockCasters.Select(p => p.ValidatorAddress!).ToHashSet();

        [Fact]
        public async Task ADemotedCaster_KeepsCasting_UntilTheBlockThatCarriesItsDemotionIsWritten()
        {
            await AsTheFourthCaster();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 2);
            Assert.True(CasterMembershipStore.TryAppend(demotion, out var why), why);

            CasterMembershipService.ReconcileBlockCastersToRecord(demotion);   // as the proposer and an announcement do

            Assert.True(Globals.IsBlockCaster);
            Assert.Equal(4, Pool().Count);
            await Commit(NextBlock(Casters.Take(3), null));                   // not carried yet
            Assert.True(Globals.IsBlockCaster);

            await Commit(NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { demotion }));

            Assert.False(Globals.IsBlockCaster);
            Assert.Equal(Casters.Take(3).Select(c => c.Address).ToHashSet(), Pool());
        }

        /// <summary>The probed heal re-adds members of the committee in force, not of a promotion no block has carried.</summary>
        [Fact]
        public async Task TheHeal_DoesNotSeatAPromotionNoBlockCarried()
        {
            await AsTheFourthCaster();
            var removal = Next(_anchor, new[] { Casters[0], Casters[1], Casters[3] }, "Demotion", Casters[2].Address, Tip + 1);
            await Commit(NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { removal }));
            var promotion = Next(removal, new[] { Casters[0], Casters[1], Casters[3], Casters[4] }, "Promotion", Casters[4].Address, Tip + 2);
            Assert.True(CasterMembershipStore.TryAppend(promotion, out var why), why);
            CasterMembershipService.ResetHealState();
            try
            {
                for (var i = 0; i <= CasterMembershipService.RequiredHealProbeStreak; i++)
                    await CasterMembershipService.TryHealBlockCastersFromRecordAsync(probe: _ => Task.FromResult(true));
                Assert.DoesNotContain(Casters[4].Address, Pool());
            }
            finally { CasterMembershipService.ResetHealState(); }
        }

        [Fact]
        public async Task APromotedCaster_JoinsThePool_WhenTheBlockThatCarriesItIsWritten()
        {
            await AsTheFourthCaster();
            var removal = Next(_anchor, new[] { Casters[0], Casters[1], Casters[3] }, "Demotion", Casters[2].Address, Tip + 1);
            await Commit(NextBlock(Casters.Take(3), new List<CasterMembershipRecord> { removal }));
            Assert.DoesNotContain(Casters[2].Address, Pool());

            var promotion = Next(removal, new[] { Casters[0], Casters[1], Casters[3], Casters[4] }, "Promotion", Casters[4].Address, Tip + 2);
            Assert.True(CasterMembershipStore.TryAppend(promotion, out var why), why);
            CasterMembershipService.ReconcileBlockCastersToRecord(promotion);
            Assert.DoesNotContain(Casters[4].Address, Pool());

            await Commit(NextBlock(new[] { Casters[0], Casters[1] }, null));
            await Commit(NextBlock(new[] { Casters[0], Casters[1] }, new List<CasterMembershipRecord> { promotion }));

            Assert.Contains(Casters[4].Address, Pool());
            Assert.True(Globals.IsBlockCaster);
        }
    }
}
