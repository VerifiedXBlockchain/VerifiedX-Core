using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The run of membership records a block carries, as block validation judges it: the chain starts at the first block
    /// at or after the height whose run begins with the pinned anchor; from then on a run extends the chain's record,
    /// signed by a majority of the committee before it, inside its carry window, and is in force from the next block.
    /// Through BlockValidatorService.ValidateBlock.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainRunTests : MembershipChainTestBase
    {
        private CasterMembershipRecord _anchor = null!;

        /// <summary>This node holds the four-caster record; the chain's height is the next block and that record is pinned.</summary>
        private async Task StartAtTheHeight()
        {
            await Start();
            _anchor = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            Hold(_anchor);
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = _anchor.RecordHash;
        }

        private async Task<bool> Take(Block block) => await BlockValidatorService.ValidateBlock(Relayed(block), false);

        private async Task Refused(Block block, string why)
        {
            var since = DateTime.UtcNow;
            Assert.False(await Take(block));
            Assert.Equal(new[] { RunStep }, StepsSince(since));
            Assert.True(BlockValidatorService.WasRefusedForItsContents(block.Height, block.Hash)); // under its producer's signature
            Assert.Contains(why, MembershipChain.RunRefusal(block) ?? "");
        }

        [Fact]
        public async Task TheChainStarts_AtTheFirstBlockWhoseRunBeginsWithTheAnchor_AndGovernsFromTheBlockAfter()
        {
            await StartAtTheHeight();
            var first = NextBlock(null, null);
            Assert.True(await Take(first));                              // no anchor yet: nothing starts
            Assert.False(MembershipChain.ActiveAt(Tip + 1));

            var start = NextBlock(null, new List<CasterMembershipRecord> { _anchor });
            Assert.True(await Take(start));

            Assert.False(MembershipChain.ActiveAt(start.Height));       // its own block was judged as before
            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(start.Height + 1)!.RecordHash);
            Assert.Equal(start.Hash, Data.BlockchainData.GetBlockByHeight(start.Height)!.Hash);
            Assert.Single(Data.BlockchainData.GetBlockByHeight(start.Height)!.MembershipRun!);
        }

        [Fact]
        public async Task BeforeTheChainStarts_ARunThatDoesNotBeginWithTheAnchor_IsRefused()
        {
            await StartAtTheHeight();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { demotion }), "does not begin with the anchor");

            var other = Record(3, 50, "prev", Casters.Take(3), "Promotion", Casters[2].Address);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { other }), "does not begin with the anchor");

            var tampered = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            tampered.Casters.RemoveAt(0);                                // its pinned hash, other fields
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { tampered }), "do not hash");
        }

        /// <summary>The anchor's block also carries the records signed after it, at any age: they were in force already.</summary>
        [Fact]
        public async Task TheAnchorsBlock_CarriesTheRecordsSignedAfterIt()
        {
            await StartAtTheHeight();
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, 60);
            var start = NextBlock(null, new List<CasterMembershipRecord> { _anchor, demotion });
            Assert.True(await Take(start));
            Assert.Equal(demotion.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }

        [Fact]
        public async Task AfterTheStart_ARunExtendsTheChainsRecord_AndIsInForceFromTheNextBlock()
        {
            await StartAtTheHeight();
            Assert.True(await Take(NextBlock(null, new List<CasterMembershipRecord> { _anchor })));
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
            var carrier = NextBlock(null, new List<CasterMembershipRecord> { demotion });

            Assert.True(await Take(carrier));

            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(carrier.Height)!.RecordHash);
            Assert.Equal(demotion.RecordHash, MembershipChain.RecordFor(carrier.Height + 1)!.RecordHash);
        }

        [Fact]
        public async Task ARunOutsideItsWindow_NotChained_UnderSigned_OrOfAnotherKind_IsRefused()
        {
            await StartAtTheHeight();
            Assert.True(await Take(NextBlock(null, new List<CasterMembershipRecord> { _anchor })));
            var next = Tip + 1;

            var early = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, next + 1);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { early }), "after this block");

            var late = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, next - MembershipChain.CarryWindowBlocks - 1);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { late }), "at most");
            var lastInWindow = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, next - MembershipChain.CarryWindowBlocks);
            Assert.Null(MembershipChain.RunRefusal(NextBlock(null, new List<CasterMembershipRecord> { lastInWindow })));

            var gap = Record(_anchor.RecordSeq + 2, next, _anchor.RecordHash, Casters.Take(3), "Demotion", Casters[3].Address);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { Signed(gap, Casters.Take(3)) }), "does not extend");
            var otherPrev = Record(_anchor.RecordSeq + 1, next, "another", Casters.Take(3), "Demotion", Casters[3].Address);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { Signed(otherPrev, Casters.Take(3)) }), "does not extend");

            var underSigned = Signed(Record(_anchor.RecordSeq + 1, next, _anchor.RecordHash, Casters.Take(3), "Demotion", Casters[3].Address), Casters.Take(2));
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { underSigned }), "signatures 2/3");
            var byOutsiders = Signed(Record(_anchor.RecordSeq + 1, next, _anchor.RecordHash, Casters.Take(3), "Demotion", Casters[3].Address), new[] { Casters[4], Producer, Minter });
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { byOutsiders }), "signatures 0/3");

            var election = Next(_anchor, Casters.Skip(1).Take(3), VreeElection.EmergencyElectionChangeType, "", next);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { election }), "not carried in a run");
            var reset = Next(_anchor, Casters.Take(3), CasterMembershipStore.BootstrapResetChangeType, "", next);
            await Refused(NextBlock(null, new List<CasterMembershipRecord> { reset }), "not carried in a run");

            var tooMany = Enumerable.Range(0, MembershipChain.MaxRunRecords + 1).Select(_ => early).ToList();
            await Refused(NextBlock(null, tooMany), "more than");

            Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }

        /// <summary>Two records in one block: the second is signed by the committee the first installs.</summary>
        [Fact]
        public async Task ARunOfTwo_IsJudgedRecordByRecord()
        {
            await StartAtTheHeight();
            Assert.True(await Take(NextBlock(null, new List<CasterMembershipRecord> { _anchor })));
            var demotion = Next(_anchor, Casters.Take(3), "Demotion", Casters[3].Address, Tip);
            var promotion = Next(demotion, Casters.Take(3).Append(Casters[4]), "Promotion", Casters[4].Address, Tip + 1);
            Assert.True(await Take(NextBlock(null, new List<CasterMembershipRecord> { demotion, promotion })));
            Assert.Equal(promotion.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
        }
    }
}
