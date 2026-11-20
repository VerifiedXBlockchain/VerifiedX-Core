using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.SalePaymentSharesHeight each payment of an NFT sale completion covers its own share of the listed
    /// price (seller: price less royalty; royalty payee: the royalty), and the completion carries no other entry.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SalePaymentSharesTests : IDisposable
    {
        private const string Nft = "6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e:1790600020";
        private const long Gate = 1002;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _seller = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _buyer = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _payee = NewKey();

        public SalePaymentSharesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"sale_shares_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.SalePaymentSharesHeight;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = Gate - 1 };
            Globals.SalePaymentSharesHeight = Gate;
            foreach (var k in new[] { _seller, _buyer, _payee })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.SalePaymentSharesHeight = _priorGate;
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

        private Transaction Payment(string to, decimal amount, string tag)
        {
            var p = new Transaction { FromAddress = _buyer.Address, ToAddress = to, Amount = amount, Fee = 0.000004M, Timestamp = TimeUtil.GetTime(), Nonce = 0,
                TransactionType = TransactionType.NFT_SALE, Data = JsonConvert.SerializeObject(new { Function = "Sale_Complete()", ContractUID = Nft, TXNum = tag }) };
            p.Build();
            p.Signature = SignatureService.CreateSignature(p.Hash, _buyer.Key, _buyer.Pub);
            return p;
        }

        private Transaction Complete(bool royalty, decimal rate, string payTo, List<Transaction> payments)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _buyer.Address, ToAddress = _seller.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = "Sale_Complete()", ContractUID = Nft, Royalty = royalty, RoyaltyAmount = rate, RoyaltyPayTo = payTo, Transactions = payments, KeySign = "k1" }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _buyer.Key, _buyer.Pub);
            return tx;
        }

        private void SeedRoyaltySale(decimal price, decimal rate, string payTo)
        {
            var body = VbtcTestContracts.BuildContractData(Nft, _seller.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures { FeatureName = FeatureName.Royalty,
                    FeatureFeatures = JObject.FromObject(new RoyaltyFeature { RoyaltyType = RoyaltyType.Percent, RoyaltyAmount = rate, RoyaltyPayToAddress = payTo }) },
            }, name: "Royalty NFT");
            Seed(body, price);
        }

        private void Seed(string body, decimal price) => SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
        {
            SmartContractUID = Nft, ContractData = body, MinterAddress = _seller.Address, OwnerAddress = _seller.Address,
            IsLocked = true, NextOwner = _buyer.Address, PurchaseAmount = price,
        });

        private static Task<(bool, string)> InBlock(Transaction tx, long height) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);

        [Fact]
        public async Task SellerBelowItsShare_WithAnUnpaidEntryCarried_RefusedFromTheHeight()
        {
            SeedRoyaltySale(10M, 0.1M, _payee.Address);
            var sale = Complete(true, 0.1M, _payee.Address, new List<Transaction>
            {
                Payment(_seller.Address, 0.5M, "1/2"),
                Payment(_payee.Address, 9M, "2/2"),
                Payment(_seller.Address, 80M, "extra"),
            });

            var before = await InBlock(sale, Gate - 1);
            Assert.True(before.Item1, before.Item2);

            Assert.False((await InBlock(sale, Gate)).Item1);
            Assert.False((await InBlock(sale, Gate + 10)).Item1);
            Assert.False((await TransactionValidatorService.VerifyTX(sale)).Item1);
        }

        [Fact]
        public async Task SellerBelowItsShare_WithoutAnyExtraEntry_RefusedFromTheHeight()
        {
            SeedRoyaltySale(10M, 0.1M, _payee.Address);
            var sale = Complete(true, 0.1M, _payee.Address, new List<Transaction>
            {
                Payment(_seller.Address, 8.5M, "1/2"),
                Payment(_payee.Address, 1M, "2/2"),
            });

            var (ok, message) = await InBlock(sale, Gate);
            Assert.False(ok);
            Assert.Contains("below its share", message);
        }

        [Fact]
        public async Task RoyaltyPayeeUnderpaid_RefusedFromTheHeight()
        {
            SeedRoyaltySale(10M, 0.1M, _payee.Address);
            var sale = Complete(true, 0.1M, _payee.Address, new List<Transaction>
            {
                Payment(_seller.Address, 10M, "1/2"),
                Payment(_payee.Address, 0.00000001M, "2/2"),
            });

            var before = await InBlock(sale, Gate - 1);
            Assert.True(before.Item1, before.Item2);

            var (ok, message) = await InBlock(sale, Gate);
            Assert.False(ok);
            Assert.Contains("below its share", message);
            Assert.False((await TransactionValidatorService.VerifyTX(sale)).Item1);
        }

        [Fact]
        public async Task CorrectShares_WithAnExtraEntry_RefusedFromTheHeight()
        {
            SeedRoyaltySale(10M, 0.1M, _payee.Address);
            var sale = Complete(true, 0.1M, _payee.Address, new List<Transaction>
            {
                Payment(_seller.Address, 9M, "1/2"),
                Payment(_payee.Address, 1M, "2/2"),
                Payment(_seller.Address, 0M, "extra"),
            });

            var (ok, message) = await InBlock(sale, Gate);
            Assert.False(ok);
            Assert.Contains("only the payments it makes", message);
        }

        [Fact]
        public async Task HonestRoyaltySale_AcceptedOnBothSidesOfTheHeight()
        {
            SeedRoyaltySale(10M, 0.1M, _payee.Address);
            var sale = Complete(true, 0.1M, _payee.Address, new List<Transaction>
            {
                Payment(_seller.Address, 9M, "1/2"),
                Payment(_payee.Address, 1M, "2/2"),
            });

            var before = await InBlock(sale, Gate - 1);
            Assert.True(before.Item1, before.Item2);
            var after = await InBlock(sale, Gate);
            Assert.True(after.Item1, after.Item2);
        }

        [Fact]
        public async Task HonestRoyaltySale_SplitInFloatingPoint_Accepted()
        {
            // 3 * 0.07 and 3 - 3 * 0.07 as a wallet working in doubles sends them.
            SeedRoyaltySale(3M, 0.07M, _payee.Address);
            var sale = Complete(true, 0.07M, _payee.Address, new List<Transaction>
            {
                Payment(_seller.Address, 2.7899999999999996M, "1/2"),
                Payment(_payee.Address, 0.21000000000000002M, "2/2"),
            });

            var (ok, message) = await InBlock(sale, Gate);
            Assert.True(ok, message);
        }

        [Theory]
        [InlineData("8.9999995", "1", true)]   // inside the tolerance
        [InlineData("9", "0.9999995", true)]
        [InlineData("8.999998", "1", false)]   // outside it
        [InlineData("9", "0.999998", false)]
        public async Task ShareTolerance_IsOneMillionth(string toSeller, string toPayee, bool accepted)
        {
            SeedRoyaltySale(10M, 0.1M, _payee.Address);
            var sale = Complete(true, 0.1M, _payee.Address, new List<Transaction>
            {
                Payment(_seller.Address, decimal.Parse(toSeller, System.Globalization.CultureInfo.InvariantCulture), "1/2"),
                Payment(_payee.Address, decimal.Parse(toPayee, System.Globalization.CultureInfo.InvariantCulture), "2/2"),
            });

            var (ok, message) = await InBlock(sale, Gate);
            Assert.True(ok == accepted, message);
        }

        [Fact]
        public async Task PlainNft_FullPriceWithAnExtraEntry_RefusedFromTheHeight()
        {
            Seed(VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain"), 50M);
            var sale = Complete(false, 0M, "", new List<Transaction> { Payment(_seller.Address, 50M, "1/1"), Payment(_seller.Address, 0M, "extra") });

            var before = await InBlock(sale, Gate - 1);
            Assert.True(before.Item1, before.Item2);
            var (ok, message) = await InBlock(sale, Gate);
            Assert.False(ok);
            Assert.Contains("only the payments it makes", message);
        }

        [Fact]
        public async Task PlainNft_PaidBelowItsPrice_RefusedFromTheHeight_AndFullPriceAccepted()
        {
            Seed(VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain"), 50M);

            var underpaid = Complete(false, 0M, "", new List<Transaction> { Payment(_seller.Address, 49.5M, "1/1") });
            var before = await InBlock(underpaid, Gate - 1);
            Assert.True(before.Item1, before.Item2);
            Assert.False((await InBlock(underpaid, Gate)).Item1);

            var full = Complete(false, 0M, "", new List<Transaction> { Payment(_seller.Address, 50M, "1/1") });
            var (ok, message) = await InBlock(full, Gate);
            Assert.True(ok, message);
        }
    }
}
