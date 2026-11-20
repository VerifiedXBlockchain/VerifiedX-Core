using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A transaction's hash covers how its Amount and Fee are spelled, and a block's JSON writes a decimal that has no
    /// decimal places with one. At admission a transaction is judged, and kept, in the spelling a block carries.
    /// </summary>
    [Collection("DbContextSequential")]
    public class AmountSpellingAtAdmissionTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _receiver = NewKey();

        public AmountSpellingAtAdmissionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"spell_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            MempoolEvictionUtility.InvalidateAdmissionStats();
            Globals.LastBlock = new Block { Height = 100 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>A signed transfer whose hash is taken with the Amount spelled <paramref name="hashedAs"/> and which then carries <paramref name="carriedAs"/>.</summary>
        private Transaction Transfer(decimal hashedAs, decimal? carriedAs = null)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _sender.Address,
                ToAddress = _receiver.Address,
                Amount = hashedAs,
                Fee = 0,
                Nonce = 0,
                TransactionType = TransactionType.TX,
                Data = "[\"memo\"]",
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            tx.Amount = carriedAs ?? hashedAs;
            return tx;
        }

        /// <summary>The transaction as a node reads it out of a block.</summary>
        private static Transaction FromABlock(Transaction tx) =>
            JsonConvert.DeserializeObject<Block>(JsonConvert.SerializeObject(new Block { Transactions = new System.Collections.Generic.List<Transaction> { tx } }))!.Transactions[0];

        private const string HashRefusal = "hash is not equal to the original hash";

        [Fact]
        public async Task A_transfer_hashed_over_a_spelling_no_block_carries_is_not_admitted()
        {
            var tx = Transfer(hashedAs: 1M); // "1": a block writes it "1.0"
            Assert.Equal("1", tx.Amount.ToString());
            Assert.False(TransactionValidatorService.StandardHashMatches(FromABlock(tx), 101)); // as a block carries it

            var (admitted, why) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(admitted);
            Assert.Contains(HashRefusal, why);
        }

        [Fact]
        public async Task The_spellings_wallets_use_are_admitted_and_kept_as_a_block_carries_them()
        {
            // Hashed "5.0" and carried as 5 (a client's JSON number), and hashed and carried with decimal places.
            foreach (var tx in new[] { Transfer(hashedAs: 5.0M, carriedAs: 5M), Transfer(hashedAs: 5.0M), Transfer(hashedAs: 2.5M), Transfer(hashedAs: 3.10M) })
            {
                var (admitted, why) = await TransactionValidatorService.VerifyTX(tx);

                Assert.True(admitted, why);
                var inABlock = FromABlock(tx);
                Assert.Equal(inABlock.Amount.ToString(), tx.Amount.ToString());
                Assert.Equal(inABlock.Fee.ToString(), tx.Fee.ToString());
                Assert.True(TransactionValidatorService.StandardHashMatches(inABlock, 101));
            }
        }

        [Theory]
        [InlineData("0", "0.0")]
        [InlineData("0.0", "0.0")]
        [InlineData("1", "1.0")]
        [InlineData("1.0", "1.0")]
        [InlineData("1.50", "1.50")]
        [InlineData("123456789", "123456789.0")]
        [InlineData("0.00000001", "0.00000001")]
        [InlineData("-3", "-3.0")]
        public void An_amount_is_spelled_the_way_a_blocks_json_writes_it(string written, string inABlock)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var value = decimal.Parse(written, invariant);
            var tx = new Transaction { Amount = value, Fee = value, FromAddress = "a", ToAddress = "b" };
            var carried = FromABlock(tx);
            Assert.Equal(inABlock, carried.Amount.ToString(invariant));

            LedgerIntegrityRules.SpellAsABlockCarriesIt(tx);

            Assert.Equal(inABlock, tx.Amount.ToString(invariant));
            Assert.Equal(inABlock, tx.Fee.ToString(invariant));
            Assert.Equal(carried.GetHash(), tx.GetHash());
            // And it stays that spelling through the other JSON the node speaks (its SignalR hubs).
            var overSignalR = System.Text.Json.JsonSerializer.Deserialize<decimal>(System.Text.Json.JsonSerializer.Serialize(tx.Amount));
            Assert.Equal(inABlock, overSignalR.ToString(invariant));
        }

        [Fact]
        public void A_zero_amount_hashed_without_decimal_places_still_matches_in_the_spelling_a_block_carries()
        {
            // The spelling VerifyTX has always accepted for a zero Amount, at admission and in a block alike.
            var tx = new Transaction { Timestamp = 1_790_000_000, FromAddress = "xSender", ToAddress = "xRecipient", Amount = 0M, Fee = 0.00001M, Nonce = 3, TransactionType = TransactionType.TX };
            tx.Build();

            LedgerIntegrityRules.SpellAsABlockCarriesIt(tx);

            Assert.Equal("0.0", tx.Amount.ToString());
            Assert.True(TransactionValidatorService.StandardHashMatches(tx, 101));
            Assert.True(TransactionValidatorService.StandardHashMatches(FromABlock(tx), 101));
        }
    }
}
