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
    /// NEW-08 (found by the second independent review; pre-existing; possible chain split): the single-shape vBTC V2
    /// withdrawal request's per-contract mempool guard also ran during block verification. A node whose own mempool held
    /// a competing request for the same contract rejected a block its peers accepted. The multi-contract shape already
    /// limited the same guard to mempool admission.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW08_WithdrawalGuardAtBlockVerifyTests : IDisposable
    {
        private const string V2 = "7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e:1790500003";
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _other = NewKey();

        public NEW08_WithdrawalGuardAtBlockVerifyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new08_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1001 };
            Globals.WithdrawalEscrowHeight = 1;

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = V2, ContractData = VbtcTestContracts.VbtcV2ContractData,
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _holder.Address, Amount = 1.0M },
                    new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _other.Address, Amount = 1.0M },
                },
            });
            foreach (var k in new[] { _owner, _holder, _other })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.WithdrawalEscrowHeight = _priorEscrowHeight;
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

        private Transaction V2Withdrawal(decimal amount, long nonce = 0) =>
            Signed(_holder, _holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "WithdrawalRequest()", ContractUID = V2, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString() }, nonce);

        [Fact]
        public async Task NEW08_BlockVerification_DoesNotReadTheLocalMempool()
        {
            // Another requester's withdrawal for the same contract sits in THIS node's mempool. The block carrying the
            // holder's request must verify the same here as on a node without it.
            var otherHolderTx = Signed(_other, _other.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "WithdrawalRequest()", ContractUID = V2, BTCAddress = BtcDest, Amount = 0.1M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString() });
            TransactionData.GetPool().InsertSafe(otherHolderTx);

            var mine = V2Withdrawal(0.5M);
            var atAdmission = await TransactionValidatorService.VerifyTX(mine);
            Assert.False(atAdmission.Item1); // mempool admission still refuses a second pending request for the contract

            var (ok, message) = await TransactionValidatorService.VerifyTX(mine, blockDownloads: false, blockVerify: true);
            Assert.True(ok, message);
        }

        // ── Follow-up (third review): NFT sale checks read the local mempool at block verification ──

        private const string Nft = "7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e:1790500020";
        private const string OtherNft = "7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e:1790500021";

        private void SeedNft(string uid) => SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
        {
            SmartContractUID = uid, ContractData = VbtcTestContracts.PlainNftContractData,
            MinterAddress = _holder.Address, OwnerAddress = _holder.Address,
        });

        private Transaction SaleStart(string uid) => Signed(_holder, _other.Address, TransactionType.NFT_SALE,
            new { Function = "M_Sale_Start()", ContractUID = uid, NextOwner = _other.Address, KeySign = "key-" + uid, SoldFor = 5M, BidSignature = "manual" }, 1);

        private Transaction PendingNftTransfer(string uid) => Signed(_holder, _owner.Address, TransactionType.NFT_TX,
            new[] { new { Function = "Transfer()", ContractUID = uid, ToAddress = _owner.Address, Data = "" } }, 0);

        /// <summary>
        /// D18: an owner's Transfer() of an NFT and its own sale start of the same NFT, in one block, each passed against
        /// the owner before the block; applied together the new owner held an NFT locked into the old owner's sale. From
        /// PathAuditRulesHeight a block moves or sells a contract, not both (either order); below it history replays as it was.
        /// </summary>
        [Fact]
        public async Task D18_ATransferAndASaleOfOneNft_InOneBlock_TheSecondIsRefused()
        {
            SeedNft(Nft);
            var transfer = PendingNftTransfer(Nft);
            var sale = SaleStart(Nft);
            Assert.True((await TransactionValidatorService.VerifyTX(transfer, blockDownloads: false, blockVerify: true)).Item1);
            Assert.True((await TransactionValidatorService.VerifyTX(sale, blockDownloads: false, blockVerify: true)).Item1);

            foreach (var (first, second) in new[] { (transfer, sale), (sale, transfer) })
            {
                var steps = new LedgerIntegrityRules.NftStepState();
                Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(first, steps, Globals.PathAuditRulesHeight));
                var refused = LedgerIntegrityRules.RegisterNftStepInBlock(second, steps, Globals.PathAuditRulesHeight);
                Assert.NotNull(refused);
                Assert.StartsWith(LedgerIntegrityRules.NftMoveAndSaleInBlock, refused);
                Assert.False(BlockValidatorService.IsStateCorruptionSignal(refused));   // a producer's fault: no restore or rebuild
            }
            // The producer keeps them apart the same way (ProcessTxPool's final pass): one goes in this block, the other waits.
            var tip = Globals.LastBlock;
            Globals.LastBlock = new Block { Height = Globals.PathAuditRulesHeight - 1 };
            try
            {
                Assert.Equal(new[] { sale.Hash }, TransactionData.DropDuplicateContractCreations(new List<Transaction> { sale, transfer }).Select(x => x.Hash));
                Assert.Equal(new[] { transfer.Hash }, TransactionData.DropDuplicateContractCreations(new List<Transaction> { transfer, sale }).Select(x => x.Hash));
            }
            finally { Globals.LastBlock = tip; }

            // A plain payment whose Data only carries a Transfer() of the NFT moves nothing: it does not crowd the sale out.
            var lookalike = new Transaction
            {
                FromAddress = _other.Address, ToAddress = _owner.Address, Amount = 1M, Fee = 0.00001M, Nonce = 0, Timestamp = TimeUtil.GetTime(),
                TransactionType = TransactionType.TX, Data = JsonConvert.SerializeObject(new[] { new { Function = "Transfer()", ContractUID = Nft, ToAddress = _owner.Address } }),
            };
            var withLookalike = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(lookalike, withLookalike, Globals.PathAuditRulesHeight));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(sale, withLookalike, Globals.PathAuditRulesHeight));

            var below = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(transfer, below, Globals.PathAuditRulesHeight - 1));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(sale, below, Globals.PathAuditRulesHeight - 1));

            // What the guard keeps out: applied together, the new owner holds an NFT locked into the old owner's sale.
            await StateData.UpdateTreis(new Block { Height = 5, StateRoot = "root", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction> { transfer, sale } });
            var record = SmartContractStateTrei.GetSmartContractState(Nft)!;
            Assert.Equal(_owner.Address, record.OwnerAddress);
            Assert.True(record.IsLocked);
            Assert.Equal(_other.Address, record.NextOwner);
        }

        [Fact]
        public async Task NEW08_FollowUp_NftSale_BlockVerificationIgnoresTheLocalMempool()
        {
            SeedNft(Nft);
            TransactionData.GetPool().InsertSafe(PendingNftTransfer(Nft));
            var sale = SaleStart(Nft);
            Assert.False((await TransactionValidatorService.VerifyTX(sale)).Item1);        // admission: same contract pending
            var (ok, message) = await TransactionValidatorService.VerifyTX(sale, blockDownloads: false, blockVerify: true);
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW08_FollowUp_NftSale_PendingTransactionOfAnotherContractDoesNotBlock()
        {
            // The sibling loop parsed the transaction under validation, so any pending NFT transaction from the seller
            // matched and refused the sale.
            SeedNft(Nft); SeedNft(OtherNft);
            TransactionData.GetPool().InsertSafe(PendingNftTransfer(OtherNft));
            var (ok, message) = await TransactionValidatorService.VerifyTX(SaleStart(Nft));
            Assert.True(ok, message);
        }
    }
}
