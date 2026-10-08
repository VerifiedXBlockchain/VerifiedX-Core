using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
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
    /// Fund-loss audit item 2 (Globals.SaleCompleteContractRulesHeight): a Sale_Complete / M_Sale_Complete naming a
    /// contract with no state record is refused, the inner-payment positivity rule runs before the record lookup, and
    /// Evolve() on a missing contract is refused. Below the height the forged completion still passes (the hole, kept
    /// for replay); at the height an honest sale still passes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss02_SaleCompleteContractTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _buyer;
        private readonly string _seller;

        public FundLoss02_SaleCompleteContractTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl02_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.SaleCompleteContractRulesHeight;
            Globals.SaleCompleteContractRulesHeight = Gate;
            DbContext.Initialize();

            _buyer = NewKey();
            _seller = NewKey().Address;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _buyer.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.SaleCompleteContractRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private Transaction Signed(Transaction tx)
        {
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _buyer.Key, _buyer.Pub);
            return tx;
        }

        /// <summary>A completion shaped the way the wallet shapes a royalty-less one: one inner payment to the seller.</summary>
        private Transaction SaleComplete(string scUid, string payTo, decimal innerAmount, string function = "Sale_Complete()")
        {
            var inner = Signed(new Transaction
            {
                FromAddress = _buyer.Address, ToAddress = payTo, Amount = innerAmount, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = function, ContractUID = scUid, Royalty = false, TXNum = "1/1" }),
            });
            return Signed(new Transaction
            {
                FromAddress = _buyer.Address, ToAddress = payTo, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = function, ContractUID = scUid, Royalty = false, Transactions = new List<Transaction> { inner }, KeySign = Guid.NewGuid().ToString("N") }),
            });
        }

        private Transaction Evolve(string scUid)
        {
            return Signed(new Transaction
            {
                FromAddress = _buyer.Address, ToAddress = _buyer.Address, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_TX,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Evolve()", ContractUID = scUid, Data = VbtcTestContracts.PlainNftContractData, MD5List = "NA" } }),
            });
        }

        private string ListedContract(decimal price)
        {
            var uid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.PlainNftContractData, MinterAddress = _seller,
                OwnerAddress = _seller, NextOwner = _buyer.Address, IsLocked = true, PurchaseAmount = price, Nonce = 0,
            });
            return uid;
        }

        private static string UnmintedUid() => Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
        private static void TipBelowGate() => Globals.LastBlock = new Block { Height = Gate - 2 };
        private static void TipAtGate() => Globals.LastBlock = new Block { Height = Gate - 1 };

        [Theory]
        [InlineData("Sale_Complete()")]
        [InlineData("M_Sale_Complete()")]
        public async Task CompletionOnAContractNeverMinted_PassesBelowGate_RefusedAtGate(string function)
        {
            var victim = NewKey().Address;
            TipBelowGate();
            var (okBelow, msgBelow) = await TransactionValidatorService.VerifyTX(SaleComplete(UnmintedUid(), victim, 50_000M, function));
            Assert.True(okBelow, msgBelow); // the hole, as mined history would replay

            TipAtGate();
            var (okAt, msgAt) = await TransactionValidatorService.VerifyTX(SaleComplete(UnmintedUid(), victim, 50_000M, function));
            Assert.False(okAt);
            Assert.Equal("SC does not exist.", msgAt);
        }

        [Fact]
        public async Task NegativeInnerPayment_OnAContractNeverMinted_IsRefusedByThePositivityRule_AtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(SaleComplete(UnmintedUid(), NewKey().Address, -50_000M));
            Assert.False(ok);
            Assert.Contains("positive amount", msg);
        }

        [Fact]
        public async Task NegativeInnerPayment_OnAListedContract_IsRefused_AtGate()
        {
            TipAtGate();
            var uid = ListedContract(10M);
            var (ok, msg) = await TransactionValidatorService.VerifyTX(SaleComplete(uid, _seller, -10M));
            Assert.False(ok);
            Assert.Contains("positive amount", msg);
        }

        [Fact]
        public async Task HonestSale_PassesAtGate()
        {
            TipAtGate();
            var uid = ListedContract(10M);
            var (ok, msg) = await TransactionValidatorService.VerifyTX(SaleComplete(uid, _seller, 10M));
            Assert.True(ok, msg);
        }

        [Fact]
        public async Task BlockPath_JudgesAtTheBlocksHeight()
        {
            var victim = NewKey().Address;
            Globals.LastBlock = new Block { Height = Gate + 5000 };
            var (okHistorical, msg) = await TransactionValidatorService.VerifyTX(SaleComplete(UnmintedUid(), victim, 5M), blockDownloads: true, blockVerify: true, blockHeight: Gate - 1);
            Assert.True(okHistorical, msg);

            Globals.LastBlock = new Block { Height = 10 };
            var (okAtGate, msgAtGate) = await TransactionValidatorService.VerifyTX(SaleComplete(UnmintedUid(), victim, 5M), blockVerify: true, blockHeight: Gate);
            Assert.False(okAtGate);
            Assert.Equal("SC does not exist.", msgAtGate);
        }

        [Fact]
        public async Task EvolveOnAContractNeverMinted_IsRefusedAtGate_NotBelow()
        {
            TipBelowGate();
            var (_, msgBelow) = await TransactionValidatorService.VerifyTX(Evolve(UnmintedUid()));
            Assert.NotEqual("SC does not exist.", msgBelow);

            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Evolve(UnmintedUid()));
            Assert.False(ok);
            Assert.Equal("SC does not exist.", msg);
        }
    }
}
