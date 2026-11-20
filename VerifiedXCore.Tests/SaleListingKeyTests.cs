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
    /// From Globals.SaleListingKeyHeight a sale is tied to its listing: Sale_Start records its purchase key, a completion
    /// must carry that key, and Sale_Cancel retires it. Transactions are validated by VerifyTX at their block's height and
    /// applied by UpdateTreis, as a block is.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SaleListingKeyTests : IDisposable
    {
        private const string Nft = "7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f:1790600030";
        private const long Gate = 2000;
        private const decimal Price = 5M;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _seller = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _buyer = NewKey();

        public SaleListingKeyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"sale_key_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.SaleListingKeyHeight;
            DbContext.Initialize();
            Globals.SaleListingKeyHeight = Gate;
            foreach (var k in new[] { _seller, _buyer })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Nft, ContractData = VbtcTestContracts.BuildContractData(Nft, _seller.Address, null, name: "Plain"),
                MinterAddress = _seller.Address, OwnerAddress = _seller.Address,
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.SaleListingKeyHeight = _priorGate;
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

        private static Transaction Signed((PrivateKey Key, string Pub, string Address) signer, string to, object data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0,
                Nonce = AccountStateTrei.GetNextNonce(signer.Address), TransactionType = TransactionType.NFT_SALE, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private Transaction Start(string key) => Signed(_seller, _buyer.Address,
            new { Function = "M_Sale_Start()", ContractUID = Nft, NextOwner = _buyer.Address, SoldFor = Price, KeySign = key, BidSignature = "manual" });

        private Transaction Cancel(string key) => Signed(_seller, _seller.Address,
            new { Function = "Sale_Cancel()", ContractUID = Nft, KeySign = key });

        private Transaction Complete(string key)
        {
            var payment = new Transaction { FromAddress = _buyer.Address, ToAddress = _seller.Address, Amount = Price, Fee = 0.000004M, Timestamp = TimeUtil.GetTime(), Nonce = 0,
                TransactionType = TransactionType.NFT_SALE, Data = JsonConvert.SerializeObject(new { Function = "M_Sale_Complete()", ContractUID = Nft }) };
            payment.Build();
            payment.Signature = SignatureService.CreateSignature(payment.Hash, _buyer.Key, _buyer.Pub);
            return Signed(_buyer, _seller.Address,
                new { Function = "M_Sale_Complete()", ContractUID = Nft, Royalty = false, RoyaltyAmount = 0M, RoyaltyPayTo = "", Transactions = new List<Transaction> { payment }, KeySign = key });
        }

        private static Task<(bool, string)> Verify(Transaction tx, long height) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);

        /// <summary>Validates the transaction at the height and applies it in a block of that height.</summary>
        private static async Task Mine(Transaction tx, long height)
        {
            var (ok, message) = await Verify(tx, height);
            Assert.True(ok, message);
            await StateData.UpdateTreis(new Block { Height = height, StateRoot = "root", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction> { tx } });
            Globals.LastBlock = new Block { Height = height };
        }

        private static SmartContractStateTrei State() => SmartContractStateTrei.GetSmartContractState(Nft)!;

        [Fact]
        public async Task CancelledListing_ItsKeyAndItsCompletion_DoNotSettleALaterListing()
        {
            Globals.LastBlock = new Block { Height = Gate - 1 };

            await Mine(Start("key-one"), Gate);
            Assert.Equal("key-one", State().SaleKey);
            var completionForFirstListing = Complete("key-one");

            await Mine(Cancel("key-one"), Gate + 1);
            Assert.Null(State().SaleKey);
            Assert.Contains("key-one", State().PurchaseKeys!);

            // The cancelled listing's key cannot start another sale, in a block or at admission.
            var relist = Start("key-one");
            Assert.False((await Verify(relist, Gate + 2)).Item1);
            Assert.False((await TransactionValidatorService.VerifyTX(relist)).Item1);

            // A new listing under a new key is not settled by the completion written for the cancelled one.
            await Mine(Start("key-two"), Gate + 2);
            Assert.False((await Verify(completionForFirstListing, Gate + 3)).Item1);
            Assert.False((await TransactionValidatorService.VerifyTX(completionForFirstListing)).Item1);
            Assert.Equal(_seller.Address, State().OwnerAddress);

            // The completion written for the listing in progress settles it.
            await Mine(Complete("key-two"), Gate + 3);
            Assert.Equal(_buyer.Address, State().OwnerAddress);
            Assert.Null(State().SaleKey);
            Assert.False(State().IsLocked);
        }

        [Fact]
        public async Task CancelWithADifferentKey_StillRetiresTheListingsKey()
        {
            Globals.LastBlock = new Block { Height = Gate - 1 };

            await Mine(Start("key-one"), Gate);
            await Mine(Cancel("some-other-key"), Gate + 1);

            Assert.Contains("key-one", State().PurchaseKeys!);
            Assert.False((await Verify(Start("key-one"), Gate + 2)).Item1);
        }

        [Fact]
        public async Task CompletionCarryingAnotherKey_RefusedFromTheHeight()
        {
            Globals.LastBlock = new Block { Height = Gate - 1 };
            await Mine(Start("key-one"), Gate);

            var (ok, message) = await Verify(Complete("another-key"), Gate + 1);
            Assert.False(ok);
            Assert.Contains("not the key of the sale in progress", message);

            var honest = await Verify(Complete("key-one"), Gate + 1);
            Assert.True(honest.Item1, honest.Item2);
        }

        [Fact]
        public async Task BelowTheHeight_CancelAndRelistBehaveAsBefore()
        {
            Globals.LastBlock = new Block { Height = Gate - 100 };

            await Mine(Start("key-one"), Gate - 99);
            Assert.Null(State().SaleKey);
            var completionForFirstListing = Complete("key-one");

            await Mine(Cancel("key-one"), Gate - 98);
            Assert.True(State().PurchaseKeys == null || !State().PurchaseKeys!.Contains("key-one"));

            await Mine(Start("key-one"), Gate - 97);
            var (ok, message) = await Verify(completionForFirstListing, Gate - 96);
            Assert.True(ok, message);
        }

        [Fact]
        public async Task SaleStartedBelowTheHeight_CompletesAndCancelsAsBeforeAboveIt()
        {
            Globals.LastBlock = new Block { Height = Gate - 2 };
            await Mine(Start("key-one"), Gate - 1);
            Assert.Null(State().SaleKey);

            // No key was recorded for this listing: any unused key completes it, as before.
            var (ok, message) = await Verify(Complete("another-key"), Gate + 1);
            Assert.True(ok, message);

            await Mine(Cancel("key-one"), Gate + 1);
            Assert.False(State().IsLocked);
            Assert.True(State().PurchaseKeys == null || State().PurchaseKeys!.Count == 0);
        }
    }
}
