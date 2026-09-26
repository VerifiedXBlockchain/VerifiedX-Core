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
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-28 (owner decision): legacy V1 vBTC (arbiter tokenization) is retired with this release and its balances are
    /// frozen. From Globals.VbtcV1RetirementHeight the chain refuses V1 withdrawals, the V1-only ledger functions, anything
    /// that names an existing V1 contract, and a new V1 contract; vBTC V2 and ordinary contracts are unaffected, and
    /// history below the height validates as before. The wallet refuses the V1 routes at once and arbiter mode is off.
    /// Found while tracing replay errors: V1 withdrawal records are never marked complete (TokenizedWithdrawals has no Id,
    /// so the update is refused) - left as is, since V1 is closed rather than repaired.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW28_VbtcV1RetirementTests : IDisposable
    {
        private const string V1 = "1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a:1736000000";
        private const string V2 = "2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b:1790700000";
        private const string Nft = "3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c:1790700001";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _gate = Globals.VbtcV1RetirementHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _other = NewKey();

        public NEW28_VbtcV1RetirementTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new28_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = _gate + 10 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _other.Address, Balance = 100M, Nonce = 0 });

            Save(V1, V1Body(V1));
            Save(V2, V2Body(V2));
            Save(Nft, VbtcTestContracts.BuildContractData(Nft, _owner.Address, null, name: "Plain"));
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private void Save(string uid, string body) => SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
        {
            SmartContractUID = uid, ContractData = body, MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
        });

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static SmartContractFeatures V1Feature() => new SmartContractFeatures
        {
            FeatureName = FeatureName.Tokenization,
            // As the writer takes it (the V1 wallet passed the feature as JSON).
            FeatureFeatures = Newtonsoft.Json.Linq.JObject.FromObject(new TokenizationFeature
            {
                AssetName = "Bitcoin", AssetTicker = "BTC", DepositAddress = "bc1qarbitermultisigdepositaddress000000000000000",
                PublicKeyProofs = "[]", ImageBase = "default",
            }),
        };

        private static SmartContractFeatures V2Feature(string uid) => new SmartContractFeatures
        {
            FeatureName = FeatureName.TokenizationV2,
            FeatureFeatures = new TokenizationV2Feature
            {
                AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = "bc1pvaultaddress000000000000000000000000000000000000000000", Version = 2,
                ValidatorAddressesSnapshot = new List<string> { "xValidator1" }, FrostGroupPublicKey = "02" + new string('a', 64),
                RequiredThreshold = 51, DKGProof = "proof", ProofBlockHeight = 1, CeremonyId = uid, ImageBase = "default",
            },
        };

        private string V1Body(string uid) => VbtcTestContracts.BuildContractData(uid, _owner.Address, new List<SmartContractFeatures> { V1Feature() }, name: "vBTC Token");
        private string V2Body(string uid) => VbtcTestContracts.BuildContractData(uid, _owner.Address, new List<SmartContractFeatures> { V2Feature(uid) }, name: "vBTC");

        private Transaction Tx(object data, TransactionType type, string? to = null)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = to ?? _other.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        // Every V1 action the wallet or an arbiter emitted, in the shapes they emit.
        public static IEnumerable<object[]> V1Actions()
        {
            yield return new object[] { "TransferCoin", TransactionType.TKNZ_TX };
            yield return new object[] { "TransferCoinMulti", TransactionType.TKNZ_TX };
            yield return new object[] { "WithdrawalRequest", TransactionType.TKNZ_WD_ARB };
            yield return new object[] { "WithdrawalComplete", TransactionType.TKNZ_WD_OWNER };
            yield return new object[] { "OwnershipTransfer", TransactionType.TKNZ_TX };
            yield return new object[] { "Sale", TransactionType.NFT_SALE };
            yield return new object[] { "Burn", TransactionType.TKNZ_BURN };
        }

        private Transaction V1Action(string kind, TransactionType type) => kind switch
        {
            "TransferCoin" => Tx(new[] { new { Function = "TransferCoin()", ContractUID = V1, Amount = 0.5M } }, type),
            "TransferCoinMulti" => Tx(new { Function = "TransferCoinMulti()", Inputs = new[] { new { SCUID = V1, FromAddress = _owner.Address, Amount = 0.5M, Signature = "sig" } }, Amount = 0.5M, SignatureInput = "x" }, type),
            "WithdrawalRequest" => Tx(new { Function = "TokenizedWithdrawalRequest()", ContractUID = V1, TokenizedWithdrawal = new TokenizedWithdrawals { RequestorAddress = _other.Address, OriginalUniqueId = "u1", SmartContractUID = V1, Amount = 0.1M } }, type),
            "WithdrawalComplete" => Tx(new { Function = "TokenizedWithdrawalComplete()", ContractUID = V1, UniqueId = "u1", TransactionHash = "btctx" }, type, "TW_Base"),
            "OwnershipTransfer" => Tx(new[] { new { Function = "Transfer()", ContractUID = V1, ToAddress = _other.Address, Data = V1Body(V1) } }, type),
            "Sale" => Tx(new { Function = "Sale_Start()", ContractUID = V1, NextOwner = _other.Address, SoldFor = 1.0M }, type),
            "Burn" => Tx(new[] { new { Function = "Burn()", ContractUID = V1, FromAddress = _owner.Address } }, type),
            _ => throw new ArgumentException(kind),
        };

        [Fact]
        public void TheDefinition_V1IsTheTokenizationFeatureWithoutTokenizationV2()
        {
            Assert.True(VBTCService.IsVbtcV1Contract(SmartContractStateTrei.GetSmartContractState(V1)));
            Assert.False(VBTCService.IsVbtcV1Contract(SmartContractStateTrei.GetSmartContractState(V2)));
            Assert.False(VBTCService.IsVbtcV1Contract(SmartContractStateTrei.GetSmartContractState(Nft)));
            Assert.False(VBTCService.IsVbtcV1Contract(null));
            Assert.False(VBTCService.DeclaresVbtcV1(new SmartContractMain { Features = new List<SmartContractFeatures> { V1Feature(), V2Feature(V2) } }));
            Assert.True(VBTCService.DeclaresVbtcV1(new SmartContractMain { Features = new List<SmartContractFeatures> { V1Feature() } }));
        }

        [Theory]
        [MemberData(nameof(V1Actions))]
        public async Task AtTheRetirementHeight_EveryV1ActionIsRefused_InABlockAndAtAdmission(string kind, TransactionType type)
        {
            var tx = V1Action(kind, type);
            Assert.Equal(LedgerIntegrityRules.VbtcV1RetiredMessage, LedgerIntegrityRules.VbtcV1Frozen(tx, _gate));

            var inBlock = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, _gate);
            Assert.False(inBlock.Item1);
            Assert.Equal(LedgerIntegrityRules.VbtcV1RetiredMessage, inBlock.Item2);

            var admission = await TransactionValidatorService.VerifyTX(tx);   // tip + 1 is past the height
            Assert.False(admission.Item1);
            Assert.Equal(LedgerIntegrityRules.VbtcV1RetiredMessage, admission.Item2);
        }

        [Theory]
        [MemberData(nameof(V1Actions))]
        public async Task BelowTheRetirementHeight_HistoryIsUntouched(string kind, TransactionType type)
        {
            var tx = V1Action(kind, type);
            Assert.Null(LedgerIntegrityRules.VbtcV1Frozen(tx, _gate - 1));
            var inBlock = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, _gate - 1);
            Assert.NotEqual(LedgerIntegrityRules.VbtcV1RetiredMessage, inBlock.Item2);   // other rules may still apply
        }

        [Fact]
        public async Task AtTheRetirementHeight_ANewV1ContractIsRefused()
        {
            const string fresh = "4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d:1790700002";
            var mint = Tx(new[] { new { Function = "Mint()", ContractUID = fresh, Data = V1Body(fresh) } }, TransactionType.TKNZ_MINT, _owner.Address);
            Assert.Equal(LedgerIntegrityRules.VbtcV1RetiredMessage, LedgerIntegrityRules.VbtcV1Creation(mint, _gate));
            Assert.Null(LedgerIntegrityRules.VbtcV1Creation(mint, _gate - 1));

            var inBlock = await TransactionValidatorService.VerifyTX(mint, false, true, false, null, false, _gate);
            Assert.False(inBlock.Item1);
            Assert.Equal(LedgerIntegrityRules.VbtcV1RetiredMessage, inBlock.Item2);
        }

        [Fact]
        public void VbtcV2AndOrdinaryContracts_AreUnaffected()
        {
            // V2 vault ownership transfers use TKNZ_TX and V2 mints may use TKNZ_MINT: the rule keys on the contract.
            var v2Transfer = Tx(new[] { new { Function = "Transfer()", ContractUID = V2, ToAddress = _other.Address, Data = V2Body(V2) } }, TransactionType.TKNZ_TX);
            var v2Send = Tx(new[] { new { Function = "TransferVBTCV2()", ContractUID = V2, Amount = 0.5M } }, TransactionType.VBTC_V2_TRANSFER);
            var nftTransfer = Tx(new[] { new { Function = "Transfer()", ContractUID = Nft, ToAddress = _other.Address, Data = "x" } }, TransactionType.NFT_TX);
            var nftSale = Tx(new { Function = "Sale_Start()", ContractUID = Nft, NextOwner = _other.Address, SoldFor = 1.0M }, TransactionType.NFT_SALE);
            var plainSend = Tx(new { Note = "hi" }, TransactionType.TX);
            foreach (var tx in new[] { v2Transfer, v2Send, nftTransfer, nftSale, plainSend })
                Assert.Null(LedgerIntegrityRules.VbtcV1Frozen(tx, _gate + 1000));

            const string freshV2 = "5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e:1790700003";
            const string freshNft = "6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f:1790700004";
            Assert.Null(LedgerIntegrityRules.VbtcV1Creation(Tx(new[] { new { Function = "Mint()", ContractUID = freshV2, Data = V2Body(freshV2) } }, TransactionType.TKNZ_MINT, _owner.Address), _gate));
            Assert.Null(LedgerIntegrityRules.VbtcV1Creation(Tx(new[] { new { Function = "Mint()", ContractUID = freshNft, Data = VbtcTestContracts.BuildContractData(freshNft, _owner.Address, null) } }, TransactionType.NFT_MINT, _owner.Address), _gate));
        }

        [Fact]
        public void TheRetirementUsesTheReleaseActivationHeight()
        {
            Assert.Equal(Globals.VbtcV2DkgAttestationHeight, Globals.VbtcV1RetirementHeight);
            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var program = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Program.cs"));
            Assert.Contains("Globals.VbtcV1RetirementHeight = Globals.IsTestNet ? 1_002_979 : Globals.VbtcV1RetirementHeight;", program);
        }

        // ── wallet side: refused at once, no height ──

        [Fact]
        public async Task Wallet_V1RoutesRefuse()
        {
            Assert.True(TokenizationService.V1Retired);
            Assert.Contains(TokenizationService.V1RetiredMessage, await TokenizationService.TransferCoin(new BTCTokenizeTransaction()));
            Assert.Contains(TokenizationService.V1RetiredMessage, await TokenizationService.TransferCoinMulti(new BTCTokenizeTransactionMulti()));
            Assert.Contains(TokenizationService.V1RetiredMessage, await TokenizationService.TransferOwnership(V1, _other.Address));
            Assert.Contains(TokenizationService.V1RetiredMessage, await TokenizationService.WithdrawalCoin(_owner.Address, "bc1qx", V1, 0.1M));
            Assert.Contains(TokenizationService.V1RetiredMessage, await TokenizationService.WithdrawalCoin(_owner.Address, "bc1qx", V1, 0.1M, 1, "u", "s", false));
            Assert.Equal((false, TokenizationService.V1RetiredMessage), await TokenizationService.CreateTokenizationSmartContract(new SmartContractMain()));
            Assert.Equal((false, TokenizationService.V1RetiredMessage), await TokenizationService.MintSmartContract(V1));
            Assert.Null(await TokenizationService.CreateTokenizationScMain(_owner.Address, "default", "bc1q", "[]"));
            var request = await TokenizationService.CreateTokenizedWithdrawal(new TokenizedWithdrawals(), _owner.Address, _other.Address, new Account(), V1);
            Assert.Null(request.Item1);
            var complete = await TokenizationService.CompleteTokenizedWithdrawal(_owner.Address, new Account(), V1, "btctx", "u1");
            Assert.Null(complete.Item1);
            // No V1 deposit address is fetched from arbiters any more.
            Assert.Equal(("FAIL", TokenizationService.V1RetiredMessage), await ArbiterService.GetTokenizationDetails(_owner.Address, V1));
        }

        [Fact]
        public void ArbiterModeDoesNotStart()
        {
            var priorValidator = Globals.ValidatorAddress;
            var priorArbiters = Globals.Arbiters;
            var priorIsArbiter = Globals.IsArbiter;
            try
            {
                // A validator listed as an arbiter, with its account in this wallet: the old check turned arbiter mode on.
                var account = AccountData.CreateNewAccount(skipSave: true);
                AccountData.GetAccounts().Insert(account);
                Globals.ValidatorAddress = account.Address;
                Globals.Arbiters = new List<VerifiedXCore.Models.Arbiter> { new VerifiedXCore.Models.Arbiter { Address = account.Address } };
                Globals.IsArbiter = false;
                StartupService.ArbiterCheck();
                Assert.False(Globals.IsArbiter);
            }
            finally
            {
                Globals.ValidatorAddress = priorValidator;
                Globals.Arbiters = priorArbiters;
                Globals.IsArbiter = priorIsArbiter;
            }
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
