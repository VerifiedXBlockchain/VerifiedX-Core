using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
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
    /// The vBTC V2 owner deposit check through VerifyTX, the same-block debit guard and block building: the owner's
    /// deposit is enforced by validators at admission and block proposal, "could not verify" is never reported as
    /// "insufficient", block validation never asks Electrum, and block building keeps an unverifiable transaction in
    /// the mempool instead of deleting it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VbtcOwnerDepositValidationTests : IDisposable
    {
        private const string Vault = "d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0:1786715889";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly string _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _recipient = NewKey();

        public VbtcOwnerDepositValidationTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ownerdeposit_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1_018_200, Timestamp = TimeUtil.GetTime() };
            foreach (var k in new[] { _owner, _recipient })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });

            // A vault the owner holds no ledger rows on: every debit must come from the Bitcoin deposit.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault, ContractData = VbtcTestContracts.VaultContractData(Vault, _owner.Address, "tb1pvault"),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address, SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>(),
            });
            Globals.ValidatorAddress = "";
            Globals.IsBlockCaster = false;
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            Globals.ValidatorAddress = _priorValidatorAddress;
            Globals.IsBlockCaster = _priorIsBlockCaster;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private Transaction OwnerTransfer(decimal amount)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _recipient.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_TRANSFER,
                Data = JsonConvert.SerializeObject(new { Function = "TransferVBTCV2()", ContractUID = Vault, FromAddress = _owner.Address, ToAddress = _recipient.Address, Amount = amount }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        private static async Task<(bool Ok, string Reason)> VerifyAs(ElectrumCheckMode mode, Transaction tx)
        {
            using (ElectrumCheckScope.Enter(mode))
                return await TransactionValidatorService.VerifyTX(tx);
        }

        [Fact]
        public async Task LocalSubmit_DepositCovers_Accepted()
        {
            using var electrum = new FakeElectrum(("a", true, 1.0M));
            var result = await TransactionValidatorService.VerifyTX(OwnerTransfer(0.5M));
            Assert.True(result.Item1, result.Item2);
        }

        [Fact]
        public async Task LocalSubmit_NoServerAnswers_SaysRetry_NotInsufficient()
        {
            using var electrum = new FakeElectrum(("a", false, 0M), ("b", false, 0M));
            var (ok, reason) = await TransactionValidatorService.VerifyTX(OwnerTransfer(0.5M));
            Assert.False(ok);
            Assert.True(VbtcOwnerDeposit.IsUnverifiable(reason), reason);
            Assert.DoesNotContain("Insufficient", reason);
        }

        [Fact]
        public async Task LocalSubmit_ShortfallConfirmedByTwoServers_Insufficient()
        {
            using var electrum = new FakeElectrum(("a", true, 0.1M), ("b", true, 0.1M));
            var (ok, reason) = await TransactionValidatorService.VerifyTX(OwnerTransfer(0.5M));
            Assert.False(ok);
            Assert.Contains("Insufficient", reason);
        }

        [Fact]
        public async Task PeerAdmission_PlainNode_DoesNotAskElectrum_AndDoesNotDropTheTransaction()
        {
            using var electrum = new FakeElectrum(("a", false, 0M));
            var (ok, reason) = await VerifyAs(ElectrumCheckMode.PeerAdmission, OwnerTransfer(0.5M));
            Assert.True(ok, reason);
            Assert.Empty(electrum.Asked);
        }

        [Fact]
        public async Task PeerAdmission_Validator_NoAnswer_Admits_ButAConfirmedShortfallIsRefused()
        {
            Globals.ValidatorAddress = "xValidator";
            using (var electrum = new FakeElectrum(("a", false, 0M)))
            {
                var (ok, reason) = await VerifyAs(ElectrumCheckMode.PeerAdmission, OwnerTransfer(0.5M));
                Assert.True(ok, reason);
                Assert.NotEmpty(electrum.Asked);
            }
            using (new FakeElectrum(("a", true, 0.1M)))
            {
                var (ok, reason) = await VerifyAs(ElectrumCheckMode.PeerAdmission, OwnerTransfer(0.5M));
                Assert.False(ok);
                Assert.Contains("Insufficient", reason);
            }
        }

        [Fact]
        public async Task BlockProposal_NoAnswer_IsUnverifiable()
        {
            Globals.ValidatorAddress = "xValidator";
            using var electrum = new FakeElectrum(("a", false, 0M));
            var (ok, reason) = await VerifyAs(ElectrumCheckMode.BlockProposal, OwnerTransfer(0.5M));
            Assert.False(ok);
            Assert.True(VbtcOwnerDeposit.IsUnverifiable(reason), reason);
        }

        [Fact]
        public async Task BlockValidation_NeverAsksElectrum()
        {
            using var electrum = new FakeElectrum(("a", true, 0M));
            var result = await TransactionValidatorService.VerifyTX(OwnerTransfer(0.5M), false, true, false, null, false, 1_018_201);
            Assert.True(result.Item1, result.Item2); // trusts the producer, as before
            Assert.Empty(electrum.Asked);
        }

        [Fact]
        public void DebitGuard_PlainNodeRelaying_DoesNotJudgeOwners_ProposalUsesTheLedgerLowerBound()
        {
            var ownerKey = new SameBlockDebitGuard.DebitKey(SameBlockDebitGuard.LedgerKind.VbtcV2, Vault, _owner.Address);
            using var electrum = new FakeElectrum(("a", false, 0M));

            using (ElectrumCheckScope.Enter(ElectrumCheckMode.PeerAdmission))
                Assert.Null(SameBlockDebitGuard.PolicyBalance(ownerKey));
            Assert.Empty(electrum.Asked);

            Globals.ValidatorAddress = "xValidator";
            using (ElectrumCheckScope.Enter(ElectrumCheckMode.PeerAdmission))
                Assert.Null(SameBlockDebitGuard.PolicyBalance(ownerKey)); // no answer: block proposal judges it
            using (ElectrumCheckScope.Enter(ElectrumCheckMode.BlockProposal))
                Assert.Equal(0M, SameBlockDebitGuard.PolicyBalance(ownerKey)); // ledger alone (none here)
        }

        [Fact]
        public async Task BlockBuilding_KeepsAnUnverifiableTransactionInTheMempool()
        {
            Globals.ValidatorAddress = "xValidator";
            var tx = OwnerTransfer(0.5M);
            tx.TransactionRating = TransactionRating.A; // set on receipt; block building skips unrated transactions
            TransactionData.GetPool().InsertSafe(tx);

            using (new FakeElectrum(("a", false, 0M)))
            {
                var proposed = await TransactionData.ProcessTxPool();
                Assert.DoesNotContain(proposed, t => t.Hash == tx.Hash);
                Assert.NotNull(TransactionData.GetPool().FindOne(t => t.Hash == tx.Hash)); // kept for a later block
            }

            using (new FakeElectrum(("a", true, 1.0M)))
            {
                var proposed = await TransactionData.ProcessTxPool();
                Assert.Contains(proposed, t => t.Hash == tx.Hash);
            }
        }

        [Fact]
        public async Task BlockBuilding_StillDeletesAConfirmedShortfall()
        {
            Globals.ValidatorAddress = "xValidator";
            var tx = OwnerTransfer(0.5M);
            tx.TransactionRating = TransactionRating.A; // set on receipt; block building skips unrated transactions
            TransactionData.GetPool().InsertSafe(tx);

            using (new FakeElectrum(("a", true, 0.1M)))
            {
                var proposed = await TransactionData.ProcessTxPool();
                Assert.DoesNotContain(proposed, t => t.Hash == tx.Hash);
                Assert.Null(TransactionData.GetPool().FindOne(t => t.Hash == tx.Hash));
            }
        }
    }
}
