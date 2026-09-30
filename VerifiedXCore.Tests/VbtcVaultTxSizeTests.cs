using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Mainnet report: with 160+ vBTC validators a vault creation is refused as "too large" - since NEW-26 the body carries
    /// every ceremony participant (snapshot, proof participant list, one signed attestation each), about 460 bytes of
    /// transaction per validator, over the 30 KB cap from ~60 validators. From VbtcVaultTxSizeHeight a vault creation, and a
    /// Transfer() resending a vault's stored code unchanged, may be up to 256 KB; every other transaction keeps 30 KB.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VbtcVaultTxSizeTests : IDisposable
    {
        private const string Vault = "5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c:1790800000";
        private const string NewVault = "4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d:1790800001";
        private const string Nft = "3e3e3e3e3e3e3e3e3e3e3e3e3e3e3e3e:1790800002";
        private const long Activation = 5_000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight = Globals.VbtcVaultTxSizeHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _buyer = NewKey();
        private readonly string _vaultBody;
        private readonly string _nftBody;

        public VbtcVaultTxSizeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vaultsize_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.VbtcVaultTxSizeHeight = Activation;
            Globals.LastBlock = new Block { Height = Activation - 10 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _buyer.Address, Balance = 100M, Nonce = 0 });

            _vaultBody = VaultBody(Vault, validators: 160);
            _nftBody = VbtcTestContracts.BuildContractData(Nft, _owner.Address, null, name: "Plain");
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault, ContractData = _vaultBody, MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
            });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Nft, ContractData = _nftBody, MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.VbtcVaultTxSizeHeight = _priorHeight;
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

        /// <summary>A vault body the size NEW-26 makes it: N validator addresses and a proof of N signed attestations.</summary>
        private string VaultBody(string uid, int validators)
        {
            var rng = new Random(uid.GetHashCode());
            string Random(int bytes) { var b = new byte[bytes]; rng.NextBytes(b); return Convert.ToBase64String(b); }
            var participants = Enumerable.Range(0, validators).Select(_ => "R" + Random(30).Replace("+", "a").Replace("/", "b").Substring(0, 33)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            var proof = JsonConvert.SerializeObject(new
            {
                ProofType = "DKG_ATTESTED_V2", ContractUID = uid, Participants = participants,
                Attestations = participants.Select(p => new { ValidatorAddress = p, Signature = Random(71) + "." + Random(66) }),
            });
            return VbtcTestContracts.BuildContractData(uid, _owner.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.TokenizationV2,
                    FeatureFeatures = new TokenizationV2Feature
                    {
                        AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = "bc1pvaultaddress", Version = 2,
                        ValidatorAddressesSnapshot = participants, FrostGroupPublicKey = "02" + new string('a', 64),
                        RequiredThreshold = 51, DKGProof = proof.ToBase64(), ProofBlockHeight = 1, CeremonyId = uid, ImageBase = "default",
                    },
                },
            }, name: "vBTC");
        }

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

        private static int Size(Transaction tx) => JsonConvert.SerializeObject(tx).Length;

        [Fact]
        public void The160ValidatorVault_IsOverThe30KbCap()
        {
            var create = Tx("Mint()", NewVault, VaultBody(NewVault, 160), TransactionType.VBTC_V2_CONTRACT_CREATE);
            Assert.InRange(Size(create), 60 * 1024, 100 * 1024);
            Assert.False(TransactionValidatorService.VerifyTXSize(create, LedgerIntegrityRules.DefaultMaxTxSizeBytes));
        }

        [Fact]
        public void The512ValidatorMaximum_FitsTheVaultCap()
        {
            var create = Tx("Mint()", NewVault, VaultBody(NewVault, 512), TransactionType.VBTC_V2_CONTRACT_CREATE);
            Assert.True(TransactionValidatorService.VerifyTXSize(create, Globals.MaxVbtcVaultTxSizeBytes));
        }

        [Fact]
        public void Creation_GetsTheVaultCap_OnlyFromTheHeight()
        {
            var create = Tx("Mint()", NewVault, VaultBody(NewVault, 160), TransactionType.VBTC_V2_CONTRACT_CREATE);
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(create, Activation - 1));
            Assert.Equal(Globals.MaxVbtcVaultTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(create, Activation));
        }

        [Theory]
        [InlineData(TransactionType.TKNZ_TX)]
        [InlineData(TransactionType.NFT_TX)]
        public void VaultTransferResendingTheStoredCode_GetsTheVaultCap(TransactionType type)
        {
            var transfer = Tx("Transfer()", Vault, _vaultBody, type, _buyer.Address);
            Assert.True(Size(transfer) > LedgerIntegrityRules.DefaultMaxTxSizeBytes);
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(transfer, Activation - 1));
            Assert.Equal(Globals.MaxVbtcVaultTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(transfer, Activation));
        }

        [Fact]
        public void VaultTransferWithADifferentBody_Keeps30Kb()
        {
            var transfer = Tx("Transfer()", Vault, VaultBody(Vault, 200), TransactionType.TKNZ_TX, _buyer.Address);
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(transfer, Activation));
        }

        [Fact]
        public void EverythingElse_Keeps30Kb()
        {
            // A plain NFT transfer with its own (stored) code, an unknown contract, another vault function, and a plain TX.
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(Tx("Transfer()", Nft, _nftBody, TransactionType.NFT_TX, _buyer.Address), Activation));
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(Tx("Transfer()", NewVault, _vaultBody, TransactionType.TKNZ_TX, _buyer.Address), Activation));
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(Tx("Update()", Vault, _vaultBody, TransactionType.TKNZ_TX), Activation));
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(Tx("Mint()", NewVault, _vaultBody, TransactionType.NFT_MINT), Activation));
            var plain = new Transaction { TransactionType = TransactionType.TX, Data = "x" };
            Assert.Equal(LedgerIntegrityRules.DefaultMaxTxSizeBytes, LedgerIntegrityRules.MaxTxSizeBytes(plain, Activation));
        }

        [Fact]
        public async Task VerifyTX_RefusesTheLargeCreationBeforeTheHeight_AndNotForSizeFromIt()
        {
            var create = Tx("Mint()", NewVault, VaultBody(NewVault, 160), TransactionType.VBTC_V2_CONTRACT_CREATE);

            // Admission (tip + 1 below the height) and a block below it: refused for size, as today.
            var admission = await TransactionValidatorService.VerifyTX(create);
            Assert.False(admission.Item1);
            Assert.Equal("This transactions is too large. Max size allowed is 30 kb.", admission.Item2);
            var below = await TransactionValidatorService.VerifyTX(create, false, true, false, null, false, Activation - 1);
            Assert.Equal("This transactions is too large. Max size allowed is 30 kb.", below.Item2);

            // From the height the size check passes (the creation rules after it still judge the body).
            var at = await TransactionValidatorService.VerifyTX(create, false, true, false, null, false, Activation);
            Assert.DoesNotContain("too large", at.Item2 ?? "");
            Globals.LastBlock = new Block { Height = Activation - 1 };
            Assert.DoesNotContain("too large", (await TransactionValidatorService.VerifyTX(create)).Item2 ?? "");
        }

        [Fact]
        public async Task VerifyTX_LargeVaultTransfer_PassesTheSizeCheckFromTheHeight()
        {
            var transfer = Tx("Transfer()", Vault, _vaultBody, TransactionType.TKNZ_TX, _buyer.Address);
            Assert.Equal("This transactions is too large. Max size allowed is 30 kb.", (await TransactionValidatorService.VerifyTX(transfer)).Item2);
            Assert.DoesNotContain("too large", (await TransactionValidatorService.VerifyTX(transfer, false, true, false, null, false, Activation)).Item2 ?? "");
        }

        [Fact]
        public async Task VerifyTX_OversizedPlainNftTransfer_StillRefusedAfterTheHeight()
        {
            var transfer = Tx("Transfer()", Nft, _nftBody + new string(' ', 40 * 1024), TransactionType.NFT_TX, _buyer.Address);
            var result = await TransactionValidatorService.VerifyTX(transfer, false, true, false, null, false, Activation + 1);
            Assert.False(result.Item1);
            Assert.Equal("This transactions is too large. Max size allowed is 30 kb.", result.Item2);
        }
    }
}
