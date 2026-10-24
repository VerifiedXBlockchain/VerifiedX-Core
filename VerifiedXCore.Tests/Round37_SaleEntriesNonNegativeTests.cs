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
    /// Round 37: every entry a sale completion carries has a non-negative amount and fee, paid or not. The buyer's
    /// balance is checked against the sum of all entries, so no entry may lower it. Refused at admission at every height
    /// and in blocks from Globals.SaleCompleteContractRulesHeight; a block below the height is judged as before (replay). An honest
    /// sale still passes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_SaleEntriesNonNegativeTests : IDisposable
    {
        private const long Gate = 1000;
        private const decimal Price = 2000M;
        private const string Reason = "Every entry of a sale completion must have a non-negative amount and fee.";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorSaleGate;
        private readonly (PrivateKey Key, string Pub, string Address) _buyer;
        private readonly string _seller;

        public Round37_SaleEntriesNonNegativeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37sale_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorSaleGate = Globals.SaleCompleteContractRulesHeight;
            Globals.SaleCompleteContractRulesHeight = Gate;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 10 }; // admission judges at tip + 1, below the gate

            _buyer = NewKey();
            _seller = NewKey().Address;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _buyer.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.SaleCompleteContractRulesHeight = _priorSaleGate;
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

        private string ListedContract()
        {
            var uid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.PlainNftContractData, MinterAddress = _seller,
                OwnerAddress = _seller, NextOwner = _buyer.Address, IsLocked = true, PurchaseAmount = Price, Nonce = 0,
            });
            return uid;
        }

        /// <summary>A royalty-less completion paying the seller the price, with any extra entries after the payment.</summary>
        private Transaction SaleComplete(string uid, params Transaction[] extra)
        {
            var payment = Signed(new Transaction
            {
                FromAddress = _buyer.Address, ToAddress = _seller, Amount = Price, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = "Sale_Complete()", ContractUID = uid, Royalty = false, TXNum = "1/1" }),
            });
            var entries = new List<Transaction> { payment };
            entries.AddRange(extra);
            return Signed(new Transaction
            {
                FromAddress = _buyer.Address, ToAddress = _seller, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE,
                Data = JsonConvert.SerializeObject(new { Function = "Sale_Complete()", ContractUID = uid, Royalty = false, Transactions = entries, KeySign = Guid.NewGuid().ToString("N") }),
            });
        }

        private Transaction Entry(decimal amount, decimal fee) => new Transaction
        {
            FromAddress = _buyer.Address, ToAddress = _seller, Amount = amount, Fee = fee, Nonce = 0,
            Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_SALE, Data = "x", Hash = "h", Signature = "s",
        };

        [Theory]
        [InlineData(-4000.0, 0.0)]
        [InlineData(0.00000001, -4000.0)]
        public async Task ANegativeExtraEntry_IsRefused_AtAdmissionAndInBlocksFromTheHeight(double amount, double fee)
        {
            var uid = ListedContract();
            var sale = SaleComplete(uid, Entry((decimal)amount, (decimal)fee));

            var (poolOk, poolMsg) = await TransactionValidatorService.VerifyTX(sale);
            Assert.False(poolOk);
            Assert.Equal(Reason, poolMsg);

            var (blockOk, blockMsg) = await TransactionValidatorService.VerifyTX(sale, false, true, false, null, false, Gate);
            Assert.False(blockOk);
            Assert.Equal(Reason, blockMsg);

            Assert.Equal(Reason, LedgerIntegrityRules.SaleEntriesNonNegative(sale, null));
            Assert.Null(LedgerIntegrityRules.SaleEntriesNonNegative(sale, Gate - 1)); // a block below the height replays as before
        }

        [Fact]
        public async Task AnHonestSale_StillFailsOnlyOnTheBuyersBalance_AndPassesWhenItCanPay()
        {
            var uid = ListedContract();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(SaleComplete(uid), false, true, false, null, false, Gate);
            Assert.False(ok);
            Assert.Equal("Amount exceeds balance.", msg); // 1000 VFX cannot pay 2000

            var buyer = StateData.GetSpecificAccountStateTrei(_buyer.Address)!;
            buyer.Balance = 5000M;
            StateData.GetAccountStateTrei().UpdateSafe(buyer);
            var (okPaid, msgPaid) = await TransactionValidatorService.VerifyTX(SaleComplete(uid), false, true, false, null, false, Gate);
            Assert.True(okPaid, msgPaid);
            var (okPool, msgPool) = await TransactionValidatorService.VerifyTX(SaleComplete(uid));
            Assert.True(okPool, msgPool);
        }
    }
}
