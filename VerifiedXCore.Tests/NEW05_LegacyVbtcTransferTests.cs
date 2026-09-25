using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
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
    /// NEW-05 (found by the independent review; not in the audit; same class as VX-01): legacy vBTC (V1, Tokenization)
    /// transfers.
    ///  - TransferCoin() had no positive-amount rule and checked a non-owner's balance only when it already had ledger
    ///    rows, so a sender with no rows could send any amount and a negative amount credited the recipient — V1 vBTC
    ///    minted from nothing.
    ///  - TransferCoinMulti() computed each input holder's signature check and never used the result, so naming any
    ///    holder as an input spent their balance.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW05_LegacyVbtcTransferTests : IDisposable
    {
        private const string V1Contract = "7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a:1790300000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _attacker = NewKey();

        public NEW05_LegacyVbtcTransferTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new05_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };

            var body = VbtcTestContracts.BuildContractData(V1Contract, _owner.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.Tokenization,
                    FeatureFeatures = Newtonsoft.Json.Linq.JObject.FromObject(new TokenizationFeature { AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = "tb1qfixturedeposit", PublicKeyProofs = "p", ImageBase = "default" }),
                },
            }, name: "V1 Fixture");

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = V1Contract,
                ContractData = body,
                MinterAddress = _owner.Address,
                OwnerAddress = _owner.Address,
                // The holder received 5 vBTC from the owner earlier.
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = _holder.Address, ToAddress = _holder.Address, Amount = 5M },
                },
            });
            foreach (var k in new[] { _owner, _holder, _attacker })
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

        private static Transaction Signed((PrivateKey Key, string Pub, string Address) signer, string to, string data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.TKNZ_TX, Data = data,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        /// <summary>TransferCoin() exactly as TokenizationService.CreateVFXTokenizedTransaction builds it.</summary>
        private static Transaction TransferCoin((PrivateKey, string, string Address) signer, string to, decimal amount) =>
            Signed(signer, to, JsonConvert.SerializeObject(new[] { new { Function = "TransferCoin()", ContractUID = V1Contract, Amount = amount } }));

        /// <summary>TransferCoinMulti() exactly as TokenizationService.CreateVFXTokenizedTransactionMulti builds it.</summary>
        private static Transaction TransferMulti((PrivateKey, string, string Address) signer, string to, VBTCTransferInput input, string signatureInput) =>
            Signed(signer, to, JsonConvert.SerializeObject(new { Function = "TransferCoinMulti()", Inputs = new[] { input }, Amount = input.Amount, SignatureInput = signatureInput }));

        [Fact]
        public async Task NEW05_PoC_SenderWithNoLedgerRows_CannotMint()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(TransferCoin(_attacker, _holder.Address, 1000M));
            Assert.False(ok);
            Assert.Contains("Insufficient Balance", message);
        }

        [Fact]
        public async Task NEW05_PoC_NegativeAmount_Refused()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(TransferCoin(_holder, _attacker.Address, -1000M));
            Assert.False(ok);
            Assert.Contains("greater than zero", message);
        }

        [Fact]
        public async Task NEW05_Control_HolderSendsWithinBalance_Accepted()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(TransferCoin(_holder, _attacker.Address, 1M));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW05_PoC_MultiInputNamingAVictimWithAForgedSignature_Refused()
        {
            var sigInput = "forgedinput" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var input = new VBTCTransferInput { SCUID = V1Contract, FromAddress = _holder.Address, Amount = 5M, Signature = "forged" };
            var (ok, message) = await TransactionValidatorService.VerifyTX(TransferMulti(_attacker, _attacker.Address, input, sigInput));
            Assert.False(ok);
            Assert.Contains("signature", message);
        }

        [Fact]
        public async Task NEW05_Control_MultiInputSignedByItsHolder_Accepted()
        {
            var sigInput = "realinput" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var input = new VBTCTransferInput
            {
                SCUID = V1Contract, FromAddress = _holder.Address, Amount = 2M,
                Signature = SignatureService.CreateSignature(sigInput + _attacker.Address + _holder.Address, _holder.Key, _holder.Pub),
            };
            var (ok, message) = await TransactionValidatorService.VerifyTX(TransferMulti(_holder, _attacker.Address, input, sigInput));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW05_TransferOnAMissingContract_Refused()
        {
            var tx = Signed(_holder, _attacker.Address, JsonConvert.SerializeObject(new[] { new { Function = "TransferCoin()", ContractUID = "no-such-contract:1", Amount = 1M } }));
            var (ok, _) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
        }
    }
}
