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
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-26 (follow-up, review round 7 finding F1): Update(), Transfer() and the Evolve functions write the body they
    /// carry over a contract's stored code, so the owner of a vBTC V2 vault could replace its DepositAddress and DKG data
    /// after creation (reviewer PoC: Update() accepted at admission and in a block; the resolved deposit address changed).
    /// A vault's code must now never change: the carried body must equal the stored code.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW26_VaultCodeFrozenTests : IDisposable
    {
        private const string Vault = "7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a:1790700000";
        private const string Nft = "6b6b6b6b6b6b6b6b6b6b6b6b6b6b6b6b:1790700001";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _buyer = NewKey();
        private readonly string _vaultBody;

        public NEW26_VaultCodeFrozenTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new26v_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1000 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _buyer.Address, Balance = 100M, Nonce = 0 });

            _vaultBody = VaultBody(Vault, "bc1pattestedvaultaddress00000000000000000000000000000000000");
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault, ContractData = _vaultBody, MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
            });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Nft, ContractData = VbtcTestContracts.BuildContractData(Nft, _owner.Address, null, name: "Plain"),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
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

        private string VaultBody(string uid, string depositAddress) => VbtcTestContracts.BuildContractData(uid, _owner.Address, new List<SmartContractFeatures>
        {
            new SmartContractFeatures
            {
                FeatureName = FeatureName.TokenizationV2,
                FeatureFeatures = new TokenizationV2Feature
                {
                    AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = depositAddress, Version = 2,
                    ValidatorAddressesSnapshot = new List<string> { "xValidator1" }, FrostGroupPublicKey = "02" + new string('a', 64),
                    RequiredThreshold = 51, DKGProof = "proof", ProofBlockHeight = 1, CeremonyId = uid, ImageBase = "default",
                },
            },
        }, name: "vBTC");

        private Transaction Tx(string function, string uid, string? body, TransactionType type, string? to = null)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = to ?? _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type,
                Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = uid, Data = body } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        [Fact]
        public async Task NEW26_PoC_OwnerUpdatesTheVaultsDepositAddress_Refused()
        {
            var swapped = VaultBody(Vault, "bc1pattackercontrolledaddress000000000000000000000000000000");
            var tx = Tx("Update()", Vault, swapped, TransactionType.NFT_MINT);
            var (admitted, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admitted);
            Assert.Equal("A vBTC V2 vault's contract code cannot be changed after creation.", message);
            Assert.False((await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, 1001)).Item1);
        }

        [Theory]
        [InlineData("Transfer()")]
        [InlineData("Evolve()")]
        [InlineData("Devolve()")]
        [InlineData("ChangeEvolveStateSpecific()")]
        public void NEW26_OtherFunctionsThatRewriteTheCode_RefusedOnAVault(string function)
        {
            var swapped = VaultBody(Vault, "bc1pattackercontrolledaddress000000000000000000000000000000");
            Assert.NotNull(LedgerIntegrityRules.VaultCodeUnchanged(Tx(function, Vault, swapped, TransactionType.NFT_TX, _buyer.Address)));
        }

        [Fact]
        public void NEW26_TransferWithAnEmptyBody_Refused()
        {
            // The apply writes the carried body: an empty one would wipe the vault's code.
            Assert.NotNull(LedgerIntegrityRules.VaultCodeUnchanged(Tx("Transfer()", Vault, null, TransactionType.NFT_TX, _buyer.Address)));
            Assert.NotNull(LedgerIntegrityRules.VaultCodeUnchanged(Tx("Transfer()", Vault, "", TransactionType.NFT_TX, _buyer.Address)));
        }

        [Fact]
        public void NEW26_Control_TransferResendingTheSameBody_Allowed()
        {
            // How every historical vault transfer looks (8 on mainnet, 1 on testnet).
            Assert.Null(LedgerIntegrityRules.VaultCodeUnchanged(Tx("Transfer()", Vault, _vaultBody, TransactionType.NFT_TX, _buyer.Address)));
        }

        [Fact]
        public void NEW26_Control_APlainNftStillUpdates()
        {
            var newBody = VbtcTestContracts.BuildContractData(Nft, _owner.Address, null, name: "Plain v2");
            Assert.Null(LedgerIntegrityRules.VaultCodeUnchanged(Tx("Update()", Nft, newBody, TransactionType.NFT_MINT)));
        }

        [Fact]
        public void NEW26_ObjectShapedPayloadIsCheckedToo()
        {
            var swapped = VaultBody(Vault, "bc1pattackercontrolledaddress000000000000000000000000000000");
            var tx = Tx("Update()", Vault, swapped, TransactionType.NFT_MINT);
            tx.Data = JsonConvert.SerializeObject(new { Function = "Update()", ContractUID = Vault, Data = swapped });
            Assert.NotNull(LedgerIntegrityRules.VaultCodeUnchanged(tx));
        }
    }
}
