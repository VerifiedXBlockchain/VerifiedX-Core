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
    }
}
