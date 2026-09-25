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
    /// NEW-17 (found by the fifth independent review; CRITICAL, pre-existing): NFT sale completion payments.
    ///  - An NFT with a non-royalty feature skipped every payment check: an inner payment of -1000 to an arbitrary
    ///    victim passed and was applied (victim negative, buyer credited).
    ///  - A plain NFT had no sign check on SoldFor and only a lower bound on the payment: a negative price and payment
    ///    minted VFX between two of the attacker's own keys.
    ///  - Royalty payments were upper-bounded only.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW17_NftSalePaymentTests : IDisposable
    {
        private const string Nft = "5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d5d:1790600010";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _seller = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _buyer = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _victim = NewKey();

        public NEW17_NftSalePaymentTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new17_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1001 };
            foreach (var k in new[] { _seller, _buyer, _victim })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
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

        private static Transaction Signed((PrivateKey Key, string Pub, string Address) signer, string to, TransactionType type, object data, long nonce = 0)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = nonce,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private Transaction Payment(string to, decimal amount)
        {
            var p = new Transaction { FromAddress = _buyer.Address, ToAddress = to, Amount = amount, Fee = 0M, Data = "p", Timestamp = TimeUtil.GetTime(), Nonce = 0 };
            p.Build();
            p.Signature = SignatureService.CreateSignature(p.Hash, _buyer.Key, _buyer.Pub);
            return p;
        }

        private void SeedLockedSale(string contractData, decimal price) => SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
        {
            SmartContractUID = Nft, ContractData = contractData, MinterAddress = _seller.Address, OwnerAddress = _seller.Address,
            IsLocked = true, NextOwner = _buyer.Address, PurchaseAmount = price,
        });

        private Transaction Complete(List<Transaction> payments) => Signed(_buyer, _seller.Address, TransactionType.NFT_SALE,
            new { Function = "M_Sale_Complete()", ContractUID = Nft, Royalty = false, RoyaltyAmount = 0M, RoyaltyPayTo = "", Transactions = payments, KeySign = "k1" });

        [Fact]
        public async Task NEW17_PoC_FeaturedNftWithoutRoyalty_NegativePaymentToAVictim_Refused()
        {
            var body = VbtcTestContracts.BuildContractData(Nft, _seller.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures { FeatureName = FeatureName.Tokenization,
                    FeatureFeatures = JObject.FromObject(new TokenizationFeature { AssetName = "x", AssetTicker = "x", DepositAddress = "tb1qfixturedeposit", PublicKeyProofs = "p", ImageBase = "default" }) },
            }, name: "Featured NFT");
            SeedLockedSale(body, 0M);
            var sale = Complete(new List<Transaction> { new Transaction { FromAddress = _buyer.Address, ToAddress = _victim.Address, Amount = -1000M, Fee = 0M, Data = "x", Hash = "h", Signature = "s" } });
            Assert.False((await TransactionValidatorService.VerifyTX(sale, false, true, false, null, false, 1002)).Item1);
            Assert.False((await TransactionValidatorService.VerifyTX(sale)).Item1);
        }

        [Fact]
        public async Task NEW17_PoC_NegativePriceAtSaleStart_Refused()
        {
            var body = VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain");
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Nft, ContractData = body, MinterAddress = _seller.Address, OwnerAddress = _seller.Address });
            var start = Signed(_seller, _buyer.Address, TransactionType.NFT_SALE,
                new { Function = "M_Sale_Start()", ContractUID = Nft, NextOwner = _buyer.Address, SoldFor = -100000M, KeySign = "k2", BidSignature = "manual" });
            Assert.False((await TransactionValidatorService.VerifyTX(start, false, true, false, null, false, 1002)).Item1);
        }

        [Fact]
        public async Task NEW17_PoC_PlainNft_NegativePayment_Refused()
        {
            SeedLockedSale(VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain"), -100000M); // state a pre-fix Sale_Start left
            var sale = Complete(new List<Transaction> { Payment(_seller.Address, -100000M) });
            Assert.False((await TransactionValidatorService.VerifyTX(sale, false, true, false, null, false, 1002)).Item1);
        }

        [Fact]
        public async Task NEW17_PoC_PaymentToSomeoneOtherThanTheSeller_Refused()
        {
            SeedLockedSale(VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain"), 50M);
            var sale = Complete(new List<Transaction> { Payment(_victim.Address, 50M) });
            Assert.False((await TransactionValidatorService.VerifyTX(sale, false, true, false, null, false, 1002)).Item1);
        }

        [Fact]
        public async Task NEW17_Control_PlainNftSaleAtItsPrice_Accepted()
        {
            SeedLockedSale(VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain"), 50M);
            var sale = Complete(new List<Transaction> { Payment(_seller.Address, 50M) });
            var (ok, message) = await TransactionValidatorService.VerifyTX(sale, false, true, false, null, false, 1002);
            Assert.True(ok, message);
        }

        // ── Sixth review ───────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("50", "0.00000790")]
        [InlineData("50.60", "0.00001022")]
        [InlineData("50.0", "0.00001234")]
        public async Task NEW17_FollowUp_HonestSaleWithTrailingZeros_Accepted(string amount, string fee)
        {
            // The recomputed inner hash differed after JSON parsing dropped trailing zeros: honest sales (and mainnet
            // history from block 899,466) were refused.
            var price = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
            SeedLockedSale(VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain"), price);
            var p = new Transaction { FromAddress = _buyer.Address, ToAddress = _seller.Address, Amount = price,
                Fee = decimal.Parse(fee, System.Globalization.CultureInfo.InvariantCulture), Timestamp = TimeUtil.GetTime(), Nonce = 0, TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = "M_Sale_Complete()", ContractUID = Nft }) };
            p.Build();
            p.Signature = SignatureService.CreateSignature(p.Hash, _buyer.Key, _buyer.Pub);
            var (ok, message) = await TransactionValidatorService.VerifyTX(Complete(new List<Transaction> { p }), false, true, false, null, false, 1002);
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW17_FollowUp_OnePaymentTaggedForSellerAndPayee_Refused()
        {
            // A royalty contract whose payee is the seller: one payment tagged "1/2" and "2/2" was selected (and paid)
            // twice by the apply but counted once by the balance check (buyer 2.01 -> -1.99).
            var body = VbtcTestContracts.BuildContractData(Nft, _seller.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures { FeatureName = FeatureName.Royalty,
                    FeatureFeatures = JObject.FromObject(new RoyaltyFeature { RoyaltyType = RoyaltyType.Percent, RoyaltyAmount = 0.5M, RoyaltyPayToAddress = _seller.Address }) },
            }, name: "Self royalty");
            SeedLockedSale(body, 2M);
            var p = new Transaction { FromAddress = _buyer.Address, ToAddress = _seller.Address, Amount = 2M, Fee = 0.000004M, Timestamp = TimeUtil.GetTime(), Nonce = 0, TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { TXNum = "1/2", Also = "2/2" }) };
            p.Build();
            p.Signature = SignatureService.CreateSignature(p.Hash, _buyer.Key, _buyer.Pub);
            var outer = Signed(_buyer, _seller.Address, TransactionType.NFT_SALE,
                new { Function = "M_Sale_Complete()", ContractUID = Nft, Royalty = true, RoyaltyAmount = 0.5M, RoyaltyPayTo = _seller.Address, Transactions = new List<Transaction> { p }, KeySign = "k6" });
            var (ok, message) = await TransactionValidatorService.VerifyTX(outer, false, true, false, null, false, 1002);
            Assert.False(ok);
        }
    }
}
