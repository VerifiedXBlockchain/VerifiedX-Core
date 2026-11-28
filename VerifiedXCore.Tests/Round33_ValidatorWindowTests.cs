using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The validator set at a height is read from the window's blocks; a block is read from the store once and reused
    /// while the chain above it still names it, so a new height reads its new block only (fifth review, round 33).
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round33_ValidatorWindowTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public Round33_ValidatorWindowTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r33vw_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (pub, AccountData.GetHumanAddress(pub));
        }

        private static Transaction Validator(TransactionType type, (string Pub, string Address) v, long height) => new Transaction
        {
            TransactionType = type, FromAddress = v.Address, ToAddress = v.Address, Hash = $"{type}-{height}-{Guid.NewGuid():N}",
            Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.0.1", FrostPublicKey = v.Pub, IsS3C = false }),
        };

        /// <summary>Blocks <paramref name="from"/>..<paramref name="to"/> chained on the block below, with a registration in some.</summary>
        private static void Chain(long from, long to, string tag, Func<long, Transaction?> txAt)
        {
            var below = BlockchainData.GetBlockByHeight(from - 1)?.Hash;
            for (var h = from; h <= to; h++)
            {
                var hash = $"{tag}{h}-{Guid.NewGuid():N}";
                var tx = txAt(h);
                BlockchainData.GetBlocks().InsertSafe(new Block { Height = h, Hash = hash, PrevHash = below, Transactions = tx == null ? new List<Transaction>() : new List<Transaction> { tx } });
                below = hash;
            }
        }

        [Fact]
        public void ANewHeight_ReadsItsNewBlockOnly_AndARewrittenTailIsReadAgain()
        {
            var validators = Enumerable.Range(0, 5).Select(_ => NewKey()).ToList();
            Chain(0, 40, "a", h => h % 8 == 0 && h / 8 < validators.Count ? Validator(TransactionType.VBTC_V2_VALIDATOR_REGISTER, validators[(int)(h / 8)], h) : null);
            Assert.Equal(5, VBTCValidatorRegistry.GetActiveValidatorsAt(40).Count);

            Chain(41, 41, "a", _ => null);
            var before = VBTCValidatorRegistry.BlocksReadOnThisThread;
            Assert.Equal(5, VBTCValidatorRegistry.GetActiveValidatorsAt(41).Count);
            Assert.Equal(1, VBTCValidatorRegistry.BlocksReadOnThisThread - before);         // block 41 only

            // A rollback replaces 33..41: validator 4 (registered at 32) now exits at 35.
            BlockchainData.GetBlocks().DeleteMany(b => b.Height >= 33);
            Chain(33, 41, "b", h => h == 35 ? Validator(TransactionType.VBTC_V2_VALIDATOR_EXIT, validators[4], h) : null);
            before = VBTCValidatorRegistry.BlocksReadOnThisThread;
            var after = VBTCValidatorRegistry.GetActiveValidatorsAt(41);
            Assert.Equal(4, after.Count);
            Assert.DoesNotContain(after, v => v.ValidatorAddress == validators[4].Address);
            Assert.InRange(VBTCValidatorRegistry.BlocksReadOnThisThread - before, 9, 10);    // the new 33..41 (and 32, to see it follows)
        }

        /// <summary>
        /// Two windows are asked at every new tip, the last 1,000 blocks and the two days a Base seat is judged over, and
        /// the 1,000 before an epoch boundary below the tip in between. On a chain whose blocks name the one before them,
        /// each of them reads only the block it has not seen: the short scan does not drop what the long one reuses, and a
        /// scan below the tip does not drop the tip's.
        /// </summary>
        [Fact]
        public void ScansOfTwoWindows_AndOneBelowTheTip_EachReadOnlyTheNewBlock()
        {
            const int twoDays = 14_400;
            const long boundary = 7_200, start = twoDays + 30;
            var (old, recent) = (NewKey(), NewKey());
            var blocks = new List<Block>();
            string? below = null;
            for (long h = 0; h <= start; h++)
            {
                var hash = $"w{h}-{Guid.NewGuid():N}";
                var tx = h == 100 ? Validator(TransactionType.VBTC_V2_VALIDATOR_REGISTER, old, h)
                    : h == start - 50 ? Validator(TransactionType.VBTC_V2_VALIDATOR_REGISTER, recent, h) : null;
                blocks.Add(new Block { Height = h, Hash = hash, PrevHash = below, Transactions = tx == null ? new List<Transaction>() : new List<Transaction> { tx } });
                below = hash;
            }
            BlockchainData.GetBlocks().InsertBulkSafe(blocks);

            long Reads(Action scan)
            {
                var before = VBTCValidatorRegistry.BlocksReadOnThisThread;
                scan();
                return VBTCValidatorRegistry.BlocksReadOnThisThread - before;
            }
            void Long(long tip) => Assert.Equal(2, VBTCValidatorRegistry.GetValidatorsSeenWithin(tip, twoDays).Count);
            void Short(long tip) => Assert.Equal(recent.Address, Assert.Single(VBTCValidatorRegistry.GetActiveValidatorsAt(tip)).ValidatorAddress);

            Assert.InRange(Reads(() => Long(start)), twoDays, twoDays + 1);           // the first long scan reads its whole window
            Short(start);
            for (var tip = start + 1; tip <= start + 3; tip++)
            {
                Chain(tip, tip, "w", _ => null);
                Assert.Equal(1, Reads(() => Short(tip)));                              // the new block
                Assert.Equal(1, Reads(() => Long(tip)));                               // again the new block only, after the short scan
                Assert.InRange(Reads(() => VBTCValidatorRegistry.GetActiveValidatorsAt(boundary)), 0, 1);   // a scan below the tip: its own top block at most
                Assert.Equal(1, Reads(() => Long(tip)));                               // and the tip's blocks are still kept
            }
        }
    }
}
