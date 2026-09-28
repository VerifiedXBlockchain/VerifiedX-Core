using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
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
    /// Tester report MTI#9: a vBTC V2 owner's balance counted a FORMER owner's withdrawal. Testnet vault e1ac15e7...: xWg3
    /// minted it and withdrew 0.0055 as owner (block 781,154), then transferred ownership to xMjrfrzk (781,627), who withdrew
    /// 0.0005 twice and sent xWg3 0.0001. The all-requesters add-back (from V2WithdrawalOwnerAddBackFixHeight) credited
    /// xWg3's owner-era 0.0055 to xMjrfrzk: ledger 0.0054, balance 0.0057 against a 0.0003 deposit, and a 0.001 transfer was
    /// admitted and mined. A former owner's leftover owner-era debits now count as a zero holding. Mainnet has one such vault
    /// (d11a9ef3..., new owner overstated by 0.0003, nothing spent from it).
    /// </summary>
    [Collection("DbContextSequential")]
    public class VbtcExOwnerBalanceTests : IDisposable
    {
        private const string Vault = "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1:1786715889";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorAddBackHeight = Globals.V2WithdrawalOwnerAddBackFixHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _formerOwner = NewKey();   // xWg3 in the report
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();         // xMjrfrzk
        private readonly (PrivateKey Key, string Pub, string Address) _recipient = NewKey();     // xDHj

        public VbtcExOwnerBalanceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"exowner_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.V2WithdrawalOwnerAddBackFixHeight = 1;   // testnet
            Globals.LastBlock = new Block { Height = 1_018_200, Timestamp = TimeUtil.GetTime() };
            foreach (var k in new[] { _formerOwner, _owner, _recipient })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.V2WithdrawalOwnerAddBackFixHeight = _priorAddBackHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static SmartContractStateTreiTokenizationTX Debit(string a, decimal amt) => new() { FromAddress = a, ToAddress = "-", Amount = -amt };
        private static SmartContractStateTreiTokenizationTX Credit(string a, decimal amt) => new() { FromAddress = "+", ToAddress = a, Amount = amt };

        private SmartContractStateTrei SaveVault(string owner, params SmartContractStateTreiTokenizationTX[] rows)
        {
            var state = new SmartContractStateTrei
            {
                SmartContractUID = Vault, ContractData = VbtcTestContracts.VaultContractData(Vault, _formerOwner.Address, "tb1pvault"),
                MinterAddress = _formerOwner.Address, OwnerAddress = owner, SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>(rows),
            };
            SmartContractStateTrei.SaveSmartContract(state);
            return SmartContractStateTrei.GetSmartContractState(Vault)!;
        }

        private static void CompletedWithdrawal(string requester, decimal amount, long height) =>
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = requester, OriginalUniqueId = Guid.NewGuid().ToString(), SmartContractUID = Vault, Amount = amount,
                BTCDestination = "tb1qdest", FeeRate = 5, TransactionHash = "req" + height, Status = VBTCWithdrawalStatus.Completed,
                IsCompleted = true, RequestBlockHeight = height, Timestamp = TimeUtil.GetTime(),
            }));

        /// <summary>The report's vault as it stood when the 0.001 transfer was admitted.</summary>
        private SmartContractStateTrei TheReportedVault()
        {
            CompletedWithdrawal(_formerOwner.Address, 0.0055M, 781_154);   // as owner
            CompletedWithdrawal(_owner.Address, 0.0005M, 781_689);
            CompletedWithdrawal(_owner.Address, 0.0005M, 928_001);
            return SaveVault(_owner.Address,
                Debit(_formerOwner.Address, 0.0055M), Debit(_owner.Address, 0.0005M), Debit(_owner.Address, 0.0005M),
                Credit(_formerOwner.Address, 0.0001M), Debit(_owner.Address, 0.0001M));
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

        [Fact]
        public void TheReportedVault_NoLongerCreditsTheFormerOwnersWithdrawal()
        {
            var state = TheReportedVault();
            // Before: -0.0011 own rows + 0.0065 add-back = 0.0054 (the report's LedgerBalance); with a 0.0003 deposit, 0.0057.
            // Now the former owner's -0.0054 position counts as zero: the owner holds exactly the deposit.
            Assert.Equal(0.0000M, VBTCService.GetOwnerLedgerBalance(state, _owner.Address, 1_018_200));
        }

        [Fact]
        public async Task TheReportedTransfer_IsRefusedAtAdmission_AndBlocksStayValid()
        {
            TheReportedVault();
            var tx = OwnerTransfer(0.001M);
            // Admission with an empty deposit (the old ledger figure alone covered 0.001).
            using var electrum = new FakeElectrum(("server", true, 0M));
            var admission = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admission.Item1);
            Assert.Contains("Insufficient", admission.Item2);
            // Block validation trusts the producer for owner debits (unchanged): history stays valid.
            Assert.True((await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, 1_018_201)).Item1);
        }

        [Fact]
        public void MainnetShape_NewOwnerHoldsExactlyTheDeposit()
        {
            // d11a9ef3...: RNiQ (owner) sent RPKx 0.0001 and withdrew 0.0002, then ownership moved to RPKx.
            CompletedWithdrawal(_formerOwner.Address, 0.0002M, 6_556_166);
            var state = SaveVault(_owner.Address,
                Credit(_owner.Address, 0.0001M), Debit(_formerOwner.Address, 0.0001M), Debit(_formerOwner.Address, 0.0002M));
            Assert.Equal(0.0000M, VBTCService.GetOwnerLedgerBalance(state, _owner.Address, 7_300_000));   // was +0.0003
        }

        [Fact]
        public void OrdinaryVaults_AreUnchanged()
        {
            // The owner sent a holder 0.0056; the holder withdrew 0.0055: the owner is not charged twice (the add-back).
            CompletedWithdrawal(_recipient.Address, 0.0055M, 900_000);
            var state = SaveVault(_owner.Address,
                Credit(_recipient.Address, 0.0056M), Debit(_owner.Address, 0.0056M), Debit(_recipient.Address, 0.0055M));
            Assert.Equal(-0.0001M, VBTCService.GetOwnerLedgerBalance(state, _owner.Address, 1_018_200));   // deposit - 0.0001 held
            Assert.Equal(0M, VBTCService.NegativeNonOwnerPositions(state, _owner.Address));
        }

        [Fact]
        public void BeforeTheAddBackActivation_NothingChanges()
        {
            var state = TheReportedVault();
            Globals.V2WithdrawalOwnerAddBackFixHeight = 2_000_000;
            // Owner-only add-back and no correction, exactly as before: -0.0011 + 0.0010.
            Assert.Equal(-0.0001M, VBTCService.GetOwnerLedgerBalance(state, _owner.Address, 1_018_200));
        }
    }
}
