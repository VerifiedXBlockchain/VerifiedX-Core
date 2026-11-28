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
    /// From the membership chain height the records a block carries are covered by its hash, signatures included; a block
    /// that carries none hashes as it always did. Below the height a carried run is outside the hash, and block validation
    /// drops it. Through BlockValidatorService.ValidateBlock.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipRunHashTests : MembershipChainTestBase
    {
        private CasterMembershipRecord Demotion()
        {
            var held = Record(3, 50, "prev", Casters.Take(4), "Promotion", Casters[3].Address);
            return Next(held, Casters.Take(3), "Demotion", Casters[3].Address, Tip + 1);
        }

        [Fact]
        public async Task ABlockWithoutARun_HashesAsBefore_AndARunChangesTheHashOnlyFromTheHeight()
        {
            await Start();
            var run = new List<CasterMembershipRecord> { Demotion() };
            var plain = NextBlock(null, null);
            var carrying = NextBlock(null, run);
            carrying.Timestamp = plain.Timestamp;
            carrying.Transactions = plain.Transactions;
            carrying.MerkleRoot = plain.MerkleRoot;

            // Below the height (unset): the run is not part of the block.
            Assert.Equal(plain.GetBlockHash(), carrying.GetBlockHash());

            Globals.MembershipChainHeight = Tip + 1;
            Assert.Equal(plain.GetBlockHash(), new Block
            {
                Height = plain.Height, Version = plain.Version, PrevHash = plain.PrevHash, MerkleRoot = plain.MerkleRoot, Timestamp = plain.Timestamp,
                NumOfTx = plain.NumOfTx, Validator = plain.Validator, TotalValidators = plain.TotalValidators, ValidatorAnswer = plain.ValidatorAnswer,
                ChainRefId = plain.ChainRefId, Transactions = plain.Transactions, MembershipRun = new List<CasterMembershipRecord>(),
            }.GetBlockHash()); // an empty run is no run
            var withRun = carrying.GetBlockHash();
            Assert.NotEqual(plain.GetBlockHash(), withRun);

            // The signatures are the block's too.
            run[0].Signatures[0].Signature = run[0].Signatures[1].Signature;
            Assert.NotEqual(withRun, carrying.GetBlockHash());
        }

        /// <summary>
        /// A block signed with its run, passed on without it (or with it re-signed), is another block: refused at the hash,
        /// and not as a fault of the block's own, since anyone passing it on can make such a copy.
        /// </summary>
        [Fact]
        public async Task ACopyWithItsRunStrippedOrResigned_IsRefusedAtTheHash_AndNotHeldAgainstTheBlock()
        {
            await Start();
            Globals.MembershipChainHeight = Tip + 1;
            var block = NextBlock(null, new List<CasterMembershipRecord> { Demotion() });

            var stripped = Relayed(block);
            stripped.MembershipRun = null;
            var resigned = Relayed(block);
            resigned.MembershipRun![0].Signatures.RemoveAt(0);
            foreach (var copy in new[] { stripped, resigned })
            {
                var since = DateTime.UtcNow;
                Assert.False(await BlockValidatorService.ValidateBlock(copy, false));
                Assert.Equal(new[] { HashStep }, StepsSince(since));
                Assert.False(BlockValidatorService.WasRefusedForItsContents(copy.Height, copy.Hash));
            }
        }

        /// <summary>Below the height a run on a block is not the block's: the block is taken, and stored without it.</summary>
        [Fact]
        public async Task BelowTheHeight_ARunIsDropped_AndTheBlockTaken()
        {
            await Start();
            var block = NextBlock(null, new List<CasterMembershipRecord> { Demotion() });

            Assert.True(await BlockValidatorService.ValidateBlock(Relayed(block), false));

            Assert.Equal(block.Height, Tip);
            Assert.Null(BlockchainData.GetBlockByHeight(block.Height)!.MembershipRun);
        }
    }
}
