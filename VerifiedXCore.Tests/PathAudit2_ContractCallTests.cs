using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using FeeCalcService = VerifiedXCore.Services.FeeCalcService;
using SignatureService = VerifiedXCore.Services.SignatureService;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The rules read the contract call the dispatcher runs (second path audit, 14 October 2026).
    ///
    /// The vault rules (not burned, code unchanged, not made by a rewrite, no retired V1 function) all ask one reader
    /// what a transaction's contract call is. That reader took Function, ContractUID, Data and MD5List in one step and
    /// gave up on the whole call when one of them was not text. The dispatchers that run the call read only Function
    /// and ContractUID. A call carrying <c>"MD5List":{}</c> was therefore "no function" to every rule and still ran.
    ///
    /// Transactions are judged as block validation judges them (VerifyTX at a block height) and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PathAudit2_ContractCallTests : IDisposable
    {
        private const long DkgRule = 50;
        private const long OldRules = 100;   // burn, rewrite and V1 rules in force from here
        private const long Rule = 200;       // the rule under test
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorDkgRule = Globals.VbtcV2DkgAttestationHeight;
        private readonly long _priorBurnRule = Globals.VaultBurnRuleHeight;
        private readonly long _priorRewriteRule = Globals.VaultRewriteRulesHeight;
        private readonly long _priorV1Rule = Globals.VbtcV1RetirementHeight;
        private readonly long _priorRule = Globals.PathAuditRulesHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _other = NewKey();
        private readonly List<(PrivateKey Key, string Pub, string Address)> _validators = Enumerable.Range(0, 10).Select(_ => NewKey()).ToList();
        private readonly string _groupKey;
        private readonly string _address;

        public PathAudit2_ContractCallTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pa2call_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 109 };
            Globals.VbtcV2DkgAttestationHeight = DkgRule;
            Globals.VaultBurnRuleHeight = OldRules;
            Globals.VaultRewriteRulesHeight = OldRules;
            Globals.VbtcV1RetirementHeight = OldRules;
            Globals.PathAuditRulesHeight = Rule;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
            long h = 10;
            foreach (var v in _validators)
            {
                BlockchainData.GetBlocks().InsertSafe(new Block
                {
                    Height = h, Hash = $"h{h}-{Guid.NewGuid():N}",
                    Transactions = new List<Transaction>
                    {
                        new Transaction
                        {
                            TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = "r" + h,
                            Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.0.1", FrostPublicKey = v.Pub, IsS3C = false }),
                        },
                    },
                });
                h++;
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = 5_000M, Nonce = 0 });
            }
            var xOnly = new NBitcoin.Key().PubKey.ToBytes()[1..];
            _groupKey = Convert.ToHexString(xOnly).ToLowerInvariant();
            _address = FrostDkgAttestation.DeriveTaprootAddress(_groupKey, NBitcoin.Network.Main)!;
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.VbtcV2DkgAttestationHeight = _priorDkgRule;
            Globals.VaultBurnRuleHeight = _priorBurnRule;
            Globals.VaultRewriteRulesHeight = _priorRewriteRule;
            Globals.VbtcV1RetirementHeight = _priorV1Rule;
            Globals.PathAuditRulesHeight = _priorRule;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static long Tip => Globals.LastBlock.Height;

        private string VaultBody(string uid, string depositAddress)
        {
            var participants = _validators.Select(v => v.Address).ToList();
            var threshold = FrostDkgAttestation.ThresholdFor(participants.Count, 51);
            var proof = FrostDkgAttestation.BuildProof(uid, _groupKey, _address, _owner.Address, threshold, participants, _validators.Select(v => new FrostDkgAttestation.Attestation
            {
                ValidatorAddress = v.Address,
                Signature = SignatureService.CreateSignature(FrostDkgAttestation.Message(uid, _groupKey, _address, _owner.Address, threshold, participants), v.Key, v.Pub),
            }));
            return VbtcTestContracts.BuildContractData(uid, _owner.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.TokenizationV2,
                    FeatureFeatures = new TokenizationV2Feature
                    {
                        AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = depositAddress, Version = 2, ValidatorAddressesSnapshot = participants,
                        FrostGroupPublicKey = _groupKey, RequiredThreshold = 51, DKGProof = proof, ProofBlockHeight = 1, CeremonyId = uid, ImageBase = "default",
                    },
                },
            }, name: "vBTC");
        }

        private Transaction Signed(TransactionType type, object data, string? to = null)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = to ?? _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = data as string ?? JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        private static async Task<(bool Ok, string Why)> Verify(Transaction tx, long height)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);
            return (ok, why);
        }

        private static async Task Apply(long height, params Transaction[] txs)
        {
            var block = new Block { Height = height, Hash = $"b{height}", StateRoot = "root" + height, Timestamp = TimeUtil.GetTime(), Transactions = txs.ToList() };
            Assert.True(await StateData.UpdateTreis(block));
            Globals.LastBlock = block;
        }

        private async Task<string> CreateVault()
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var create = Signed(TransactionType.VBTC_V2_CONTRACT_CREATE, new[] { new { Function = "Mint()", ContractUID = uid, Data = VaultBody(uid, _address), MD5List = "NA" } });
            var (ok, why) = await Verify(create, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, create);
            Assert.True(VBTCService.IsVbtcV2Contract(SmartContractStateTrei.GetSmartContractState(uid)!));
            return uid;
        }

        private const string Unreadable = "contract call cannot be read";

        [Fact]
        public async Task ABurnWhoseMd5ListIsNotText_WasInvisibleToTheBurnRule_AndIsNowRefused()
        {
            var uid = await CreateVault();
            var plain = Signed(TransactionType.NFT_BURN, new[] { new { Function = "Burn()", ContractUID = uid } });
            var hidden = Signed(TransactionType.NFT_BURN, $"[{{\"Function\":\"Burn()\",\"ContractUID\":\"{uid}\",\"MD5List\":{{}}}}]");

            // The burn rule is in force: an ordinary burn of the vault is refused.
            var (ok, why) = await Verify(plain, Rule - 1);
            Assert.False(ok);
            Assert.Contains("cannot be burned", why);
            // The same burn with an MD5List that is not text was read as "no function" by the rule, and accepted.
            (ok, why) = await Verify(hidden, Rule - 1);
            Assert.True(ok, "before the rule this is the hole: " + why);
            // The dispatcher runs it all the same.
            Assert.Equal("Burn()", LedgerIntegrityRules.DispatchedCall(hidden.Data).Function);
            Assert.Null(SmartContractDeployBinding.ReadPayload(hidden.Data).Function);

            // From the rule: refused, whatever the shape of the field and whatever the transaction type.
            foreach (var bad in new[] { "{}", "[]", "[\"a\"]", "{\"a\":1}" })
                foreach (var type in new[] { TransactionType.NFT_BURN, TransactionType.NFT_TX, TransactionType.TKNZ_BURN, TransactionType.SC_TX })
                {
                    var tx = Signed(type, $"[{{\"Function\":\"Burn()\",\"ContractUID\":\"{uid}\",\"MD5List\":{bad}}}]");
                    (ok, why) = await Verify(tx, Rule);
                    Assert.False(ok, $"a burn with MD5List {bad} was accepted as {type}");
                    Assert.Contains(Unreadable, why);
                }
            // A Data field that is not text hides the call the same way.
            (ok, why) = await Verify(Signed(TransactionType.NFT_BURN, $"[{{\"Function\":\"Burn()\",\"ContractUID\":\"{uid}\",\"Data\":{{}}}}]"), Rule);
            Assert.False(ok);
            Assert.Contains(Unreadable, why);
            // A UID that is not text: the dispatcher keeps the function and runs it with no UID; the call is refused.
            var noUid = Signed(TransactionType.NFT_BURN, "[{\"Function\":\"Burn()\",\"ContractUID\":{}}]");
            Assert.Equal("Burn()", LedgerIntegrityRules.DispatchedCall(noUid.Data).Function);
            (ok, why) = await Verify(noUid, Rule);
            Assert.False(ok);
            Assert.Contains(Unreadable, why);
            // Locators that are not a plain value passed validation and made the apply throw on every node.
            var stored = SmartContractStateTrei.GetSmartContractState(uid)!.ContractData;
            var badLocators = Signed(TransactionType.NFT_TX, "[" + JsonConvert.SerializeObject(new { Function = "Transfer()", ContractUID = uid, ToAddress = _other.Address, Data = stored }).TrimEnd('}') + ",\"Locators\":{}}]", _other.Address);
            (ok, why) = await Verify(badLocators, Rule);
            Assert.False(ok);
            Assert.Contains("Locators and Amount must each be a plain value", why);
            // And the ordinary burn is still refused by its own rule.
            (ok, why) = await Verify(plain, Rule);
            Assert.False(ok);
            Assert.Contains("cannot be burned", why);
            Assert.NotNull(SmartContractStateTrei.GetSmartContractState(uid));
        }

        [Fact]
        public async Task ARewriteOfAVaultsCode_HiddenTheSameWay_IsRefused()
        {
            var uid = await CreateVault();
            // The owner's own Bitcoin address in place of the validators' key: what every reader of the vault would then use.
            var ownKey = Convert.ToHexString(new NBitcoin.Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var swapped = VaultBody(uid, FrostDkgAttestation.DeriveTaprootAddress(ownKey, NBitcoin.Network.Main)!);

            foreach (var function in new[] { "Update()", "Transfer()", "Evolve()" })
            {
                var plain = Signed(TransactionType.NFT_TX, new[] { new { Function = function, ContractUID = uid, Data = swapped, ToAddress = _other.Address } }, _other.Address);
                var (ok, why) = await Verify(plain, Rule - 1);
                Assert.False(ok, $"{function} with new code was accepted");
                Assert.Contains("cannot be changed after creation", why);

                var hidden = Signed(TransactionType.NFT_TX, "[" + JsonConvert.SerializeObject(new { Function = function, ContractUID = uid, Data = swapped, ToAddress = _other.Address }).TrimEnd('}') + ",\"MD5List\":{}}]", _other.Address);
                Assert.Null(LedgerIntegrityRules.VaultCodeUnchanged(hidden));                       // the hole: the rule saw no call
                (ok, why) = await Verify(hidden, Rule);
                Assert.False(ok, $"{function} with new code and an unreadable MD5List was accepted");
                Assert.Contains(Unreadable, why);
            }
        }

        [Fact]
        public async Task TheRetiredV1LedgerFunction_HiddenTheSameWay_IsRefused()
        {
            var uid = await CreateVault();
            var call = $"[{{\"Function\":\"TransferCoin()\",\"ContractUID\":\"{uid}\",\"Amount\":1000,\"MD5List\":{{}}}}]";
            var hidden = Signed(TransactionType.TKNZ_TX, call, _other.Address);
            Assert.Null(LedgerIntegrityRules.VbtcV1Frozen(hidden, Rule));                           // the hole: the V1 freeze saw no function
            var (ok, why) = await Verify(hidden, Rule);
            Assert.False(ok);
            Assert.Contains(Unreadable, why);
            // Readable, it is refused by name.
            (ok, why) = await Verify(Signed(TransactionType.TKNZ_TX, new[] { new { Function = "TransferCoin()", ContractUID = uid, Amount = 1000 } }, _other.Address), Rule);
            Assert.False(ok);
            Assert.Contains("V1 vBTC is retired", why);
        }

        [Fact]
        public async Task OrdinaryContractCalls_AreReadAsBefore()
        {
            var uid = await CreateVault();
            // Text fields, an absent MD5List, a null MD5List, the single-object shape: all read the same by both.
            foreach (var data in new[]
            {
                $"[{{\"Function\":\"Transfer()\",\"ContractUID\":\"{uid}\",\"MD5List\":\"NA\"}}]",
                $"[{{\"Function\":\"Transfer()\",\"ContractUID\":\"{uid}\"}}]",
                $"[{{\"Function\":\"Transfer()\",\"ContractUID\":\"{uid}\",\"MD5List\":null}}]",
                $"{{\"Function\":\"Sale_Complete()\",\"ContractUID\":\"{uid}\",\"Royalty\":true,\"RoyaltyAmount\":1.5}}",
            })
                Assert.Null(LedgerIntegrityRules.ContractCallIsReadable(new Transaction { TransactionType = TransactionType.NFT_TX, Data = data }, Rule));
            // Round 24: a vBTC transaction's Data names each key once, in one spelling.
            Transaction Vbtc(string data) => new() { TransactionType = TransactionType.VBTC_V2_TRANSFER, Data = data };
            var clean = $"{{\"Function\":\"TransferVBTCV2()\",\"ContractUID\":\"{uid}\",\"ToAddress\":\"x\",\"Amount\":5}}";
            Assert.Null(LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean), Rule));
            Assert.Contains("twice", LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean.Replace("\"Amount\":5", "\"Amount\":5,\"Amount\":0.00000001")), Rule));
            Assert.Contains("two spellings", LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean.Replace("\"Amount\":5", "\"Amount\":0.00000001,\"amount\":5")), Rule));
            Assert.Contains("two spellings", LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc($"{{\"Inputs\":[{{\"ContractUID\":\"{uid}\",\"Amount\":1,\"AMOUNT\":2}}]}}"), Rule));
            Assert.Null(LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean.Replace("\"Amount\":5", "\"Amount\":5,\"Amount\":1")), Rule - 1));   // before the rules
            Assert.Null(LedgerIntegrityRules.VbtcDataKeysAreDistinct(new Transaction { TransactionType = TransactionType.NFT_TX, Data = "{\"a\":1,\"A\":2}" }, Rule));
            // Round 25: names outside plain ASCII are refused on every platform alike (their case pairs follow ICU).
            Assert.Contains("plain ASCII", LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean.Replace("\"Amount\":5", "\"Amount\":5,\"Ꟁ\":1,\"ꟁ\":1")), Rule));
            Assert.Contains("plain ASCII", LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean.Replace("\"Amount\":5", "\"Amount\":5,\"é\":1")), Rule));
            Assert.Contains("plain ASCII", LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean.Replace("\"ToAddress\":\"x\"", "\"ToAddress\":\"xİ\"")), Rule));   // a value
            Assert.Null(LedgerIntegrityRules.VbtcDataKeysAreDistinct(Vbtc(clean.Replace("\"ToAddress\":\"x\"", "\"ToAddress\":\"x\",\"Memo\":null,\"N\":[1,true]")), Rule));
            // Round 27: a contract call's body may hold line breaks and any text (a web wallet's raw code; mainnet 6,544,281).
            var rawBody = new Transaction { TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE, Data = $"[{{\"Function\":\"Mint()\",\"ContractUID\":\"{uid}\",\"Data\":\"let AssetName = \\\"Café\\\"\nlet X = 1\",\"MD5List\":\"NA\"}}]" };
            Assert.Null(LedgerIntegrityRules.VbtcDataKeysAreDistinct(rawBody, Rule));
            Assert.Contains("plain ASCII", LedgerIntegrityRules.VbtcDataKeysAreDistinct(new Transaction { TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE, Data = rawBody.Data.Replace("\"MD5List\":\"NA\"", "\"MD5List\":\"N\nA\"") }, Rule));
            // Round 30: the older token-transfer shape (to Token_Base, recipient in the data only) is refused from the rules.
            var holder = _other.Address;
            Assert.NotNull(LedgerIntegrityRules.TokenTransfer(holder, "Token_Base", holder, "Rrecipient", 1M, Rule));
            Assert.Null(LedgerIntegrityRules.TokenTransfer(holder, "Token_Base", holder, "Rrecipient", 1M, Rule - 1));
            Assert.Null(LedgerIntegrityRules.TokenTransfer(holder, "Rrecipient", holder, "Rrecipient", 1M, Rule));
            // Round 27: VFX goes to an address somebody can sign for.
            var signer = _other.Address;
            Assert.Null(LedgerIntegrityRules.VfxRecipientCanHoldIt(new Transaction { TransactionType = TransactionType.TX, ToAddress = signer }, Rule));
            Assert.Null(LedgerIntegrityRules.VfxRecipientCanHoldIt(new Transaction { TransactionType = TransactionType.TX, ToAddress = "Token_Base" }, Rule));
            Assert.NotNull(LedgerIntegrityRules.VfxRecipientCanHoldIt(new Transaction { TransactionType = TransactionType.TX, ToAddress = "alice.vfx" }, Rule));
            Assert.NotNull(LedgerIntegrityRules.VfxRecipientCanHoldIt(new Transaction { TransactionType = TransactionType.VFX_UNSHIELD, ToAddress = "zfx_" + signer }, Rule));
            Assert.NotNull(LedgerIntegrityRules.VfxRecipientCanHoldIt(new Transaction { TransactionType = TransactionType.VFX_UNSHIELD, ToAddress = "Token_Base" }, Rule));
            Assert.Null(LedgerIntegrityRules.VfxRecipientCanHoldIt(new Transaction { TransactionType = TransactionType.TX, ToAddress = "alice.vfx" }, Rule - 1));
            // Round 26: names, addresses and ids consensus looks up by text are plain ASCII.
            Transaction Typed(TransactionType type, string data, string to = "Base") => new() { TransactionType = type, Data = data, ToAddress = to, FromAddress = "xA" };
            Assert.Null(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.ADNR, "{\"Function\":\"AdnrCreate()\",\"Name\":\"plain\"}"), Rule));
            Assert.NotNull(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.ADNR, "{\"Function\":\"AdnrCreate()\",\"Name\":\"\uA7C0\"}"), Rule));
            Assert.Null(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.ADNR, "{\"Function\":\"AdnrCreate()\",\"Name\":\"\uA7C0\"}"), Rule - 1));
            Assert.NotNull(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.ADNR, "{\"Function\":\"BTCAdnrTransfer()\",\"BTCFromAddress\":\"bc1\u0171\"}"), Rule));
            Assert.NotNull(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.VOTE, "{\"Function\":\"TopicVote()\",\"Vote\":{\"TopicUID\":\"\uA7C0\"}}"), Rule));
            Assert.NotNull(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.DSTR, "{\"Function\":\"DecShopUpdate()\",\"DecShop\":{\"UniqueId\":\"\uA7C1\"}}"), Rule));
            Assert.NotNull(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.TX, null!, "\uA7C0name.vfx"), Rule));
            // Round 28: a field that cannot be read does not leave the others unchecked.
            Assert.NotNull(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.FTKN_TX, "{\"Function\":\"TokenTransfer()\",\"TokenVoteTopic\":\"x\",\"ToAddress\":\"x\uA7C0\"}", "Token_Base"), Rule));
            Assert.Null(LedgerIntegrityRules.IdentifiersArePlainAscii(Typed(TransactionType.DSTR, "{\"Function\":\"DecShopUpdate()\",\"DecShop\":{\"UniqueId\":\"abc\",\"Name\":\"Caf\u00e9\"}}"), Rule)); // free text stays free
            // A transaction that is not a contract call is not this rule's business.
            Assert.Null(LedgerIntegrityRules.ContractCallIsReadable(new Transaction { TransactionType = TransactionType.TX, Data = "[{\"Function\":\"Burn()\",\"MD5List\":{}}]" }, Rule));
            // The honest transfer of a vault, resending its code unchanged, passes as before.
            var stored = SmartContractStateTrei.GetSmartContractState(uid)!.ContractData;
            var (ok, why) = await Verify(Signed(TransactionType.NFT_TX, new[] { new { Function = "Transfer()", ContractUID = uid, ToAddress = _other.Address, Data = stored, MD5List = "NA" } }, _other.Address), Rule);
            Assert.True(ok, why);
        }
    }
}
