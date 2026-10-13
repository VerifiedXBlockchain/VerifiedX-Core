using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
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
    /// A vault's contract reads the same on every node, at every time (second path audit, 14 October 2026).
    ///
    /// Whether a contract is a vault, where its Bitcoin is, and whether it is an S3C vault were all answered by running
    /// the contract's code. That code can read the node's clock (dateProc, which evolving NFTs use), and a contract with
    /// an evolving feature is handed the node's tip height. So stored code could answer differently on two nodes, or
    /// later than at its creation, when the key-ceremony rule looked at it.
    ///
    /// Transactions are judged as block validation judges them (VerifyTX at a block height) and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PathAudit2_VaultIdentityTests : IDisposable
    {
        private const long DkgRule = 50;
        private const long Rule = 200;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorDkgRule = Globals.VbtcV2DkgAttestationHeight;
        private readonly long _priorRule = Globals.PathAuditRulesHeight;
        private readonly long _priorRecord = Globals.BitcoinAttestationRecordHeight;
        private readonly long _priorBurnRule = Globals.VaultBurnRuleHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly List<(PrivateKey Key, string Pub, string Address)> _validators = Enumerable.Range(0, 10).Select(_ => NewKey()).ToList();
        private readonly string _groupKey;
        private readonly string _address;

        public PathAudit2_VaultIdentityTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pa2identity_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 109 };
            Globals.VbtcV2DkgAttestationHeight = DkgRule;
            Globals.PathAuditRulesHeight = Rule;
            Globals.BitcoinAttestationRecordHeight = long.MaxValue;
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
            Globals.PathAuditRulesHeight = _priorRule;
            Globals.BitcoinAttestationRecordHeight = _priorRecord;
            Globals.VaultBurnRuleHeight = _priorBurnRule;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private SmartContractFeatures VaultFeature(string uid, long proofBlockHeight = 1)
        {
            var participants = _validators.Select(v => v.Address).ToList();
            var threshold = FrostDkgAttestation.ThresholdFor(participants.Count, 51);
            var proof = FrostDkgAttestation.BuildProof(uid, _groupKey, _address, _owner.Address, threshold, participants, _validators.Select(v => new FrostDkgAttestation.Attestation
            {
                ValidatorAddress = v.Address,
                Signature = SignatureService.CreateSignature(FrostDkgAttestation.Message(uid, _groupKey, _address, _owner.Address, threshold, participants), v.Key, v.Pub),
            }));
            return new SmartContractFeatures
            {
                FeatureName = FeatureName.TokenizationV2,
                FeatureFeatures = new TokenizationV2Feature
                {
                    AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = _address, Version = 2, ValidatorAddressesSnapshot = participants,
                    FrostGroupPublicKey = _groupKey, RequiredThreshold = 51, DKGProof = proof, ProofBlockHeight = proofBlockHeight, CeremonyId = uid, ImageBase = "default",
                },
            };
        }

        private string Body(string uid, params SmartContractFeatures[] features) =>
            VbtcTestContracts.BuildContractData(uid, _owner.Address, features.ToList(), name: "vBTC");

        private static string SourceOf(string body) => Encoding.Unicode.GetString(SmartContractUtility.Decompress(Convert.FromBase64String(body)));
        private static string BodyOf(string source) => Encoding.Unicode.GetBytes(source).ToCompress().ToBase64();

        /// <summary>The same vault code with one more line: a read of the node's clock.</summary>
        private static string ReadingTheClock(string body)
        {
            const string anchor = "function GetIsStC() : string";
            var source = SourceOf(body);
            var at = source.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(at >= 0, "the vault source has no GetIsStC function");
            var brace = source.IndexOf('{', at);
            return BodyOf(source.Insert(brace + 1, "\r\n   var later = dateProc(\"638000000000000000\")"));
        }

        private Transaction Create(string uid, string body)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = uid, Data = body, MD5List = "NA" } }),
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

        private bool IsVaultAtTip(string uid, string body, long tip)
        {
            if (SmartContractStateTrei.GetSmartContractState(uid) == null)
                SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = uid, ContractData = body, MinterAddress = _owner.Address, OwnerAddress = _owner.Address, Nonce = 0 });
            Globals.LastBlock = new Block { Height = tip };
            return VBTCService.IsVbtcV2Contract(SmartContractStateTrei.GetSmartContractState(uid));
        }

        [Fact]
        public async Task AnHonestVault_IsCreated_AndIsAVault_BeforeAndAfterTheRule()
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var body = Body(uid, VaultFeature(uid));
            Assert.True(LedgerIntegrityRules.ContractCodeIsTimeless(body));
            var (ok, why) = await Verify(Create(uid, body), Rule - 1);
            Assert.True(ok, why);
            (ok, why) = await Verify(Create(uid, body), Rule);
            Assert.True(ok, why);
            Assert.True(IsVaultAtTip(uid, body, Rule - 10));
            Assert.True(IsVaultAtTip(uid, body, Rule + 10));
        }

        [Fact]
        public async Task CodeThatReadsTheClock_IsRefusedInAVault_AndSuchAContractIsNotAVault()
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var body = ReadingTheClock(Body(uid, VaultFeature(uid)));
            Assert.False(LedgerIntegrityRules.ContractCodeIsTimeless(body));

            // Before the rule it was created like any vault: its code could then answer differently later.
            var (ok, why) = await Verify(Create(uid, body), Rule - 1);
            Assert.True(ok, "before the rule this is the hole: " + why);
            (ok, why) = await Verify(Create(uid, body), Rule);
            Assert.False(ok);
            Assert.Contains("must read the same on every node at every time: it calls dateProc()", why);

            // And a contract already in state with such code (made before the rule, or as a plain NFT whose code only
            // later shows the feature) was a vault to every reader; from the rule it is not one.
            Assert.True(IsVaultAtTip(uid, body, Rule - 10));
            Assert.False(IsVaultAtTip(uid, body, Rule + 10));
        }

        /// <summary>
        /// The clock is not the only thing outside the code. `send` writes to a table that lives as long as the process
        /// and throws on a second call with the same key, so a body using it is read once per process and never again;
        /// `createSig` draws a random number. A vault's code may call what it declares itself and the built-ins known to
        /// be pure, nothing else; and a body is never run as one of the runner's console commands.
        /// </summary>
        [Fact]
        public async Task AVaultsCode_CallsOnlyWhatItDeclares_AndTheBuiltInsKnownToBePure()
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var honest = Body(uid, VaultFeature(uid));
            Assert.Null(LedgerIntegrityRules.NotTimelessBecause(honest));

            string With(string line)
            {
                const string anchor = "function GetIsStC() : string";
                var source = SourceOf(honest);
                var brace = source.IndexOf('{', source.IndexOf(anchor, StringComparison.Ordinal));
                return BodyOf(source.Insert(brace + 1, "\r\n   " + line));
            }
            foreach (var (line, named) in new[]
            {
                ("send(\"k|->v\")", "send"), ("var s = createSig(\"m\", \"k\")", "createSig"), ("var r = rand(5)", "rand"),
                ("var later = dateProc(\"638000000000000000\")", "dateProc"), ("var u = somethingNobodyDeclared()", "somethingNobodyDeclared"),
            })
            {
                var body = With(line);
                Assert.Equal($"it calls {named}()", LedgerIntegrityRules.NotTimelessBecause(body));
                var (ok, why) = await Verify(Create(uid, body), Rule);
                Assert.False(ok, $"a vault calling {named}() was created");
                // From the rule a stored contract with such code is not run to ask whether it is a vault: it is not one.
                var stored = Guid.NewGuid().ToString("N") + ":1755000001";
                Assert.False(IsVaultAtTip(stored, body, Rule + 10));
                Assert.Null(VBTCChainView.GetVault(stored));
            }

            // A console command is not a body, whatever the runner would do with it.
            var command = BodyOf("#load C:/somewhere/a-file");
            Assert.Equal("it is a console command, not code", LedgerIntegrityRules.NotTimelessBecause(command));
            Assert.Throws<InvalidOperationException>(() => SmartContractMain.GenerateSmartContractInMemory(command));

            // As a plain NFT's creation (not the vault type) code that is not timeless is not asked whether it makes a
            // vault: it never does, on any node, at any time.
            var nft = Create(uid, With("var later = dateProc(\"638000000000000000\")"));
            nft.TransactionType = TransactionType.NFT_MINT;
            Assert.Null(LedgerIntegrityRules.VbtcV2DkgAttestation(nft, Rule));
        }

        [Fact]
        public void AVaultCarriesItsOneFeatureAndNoOther()
        {
            // A second feature is what gives a contract's code something to depend on (an evolving feature is handed the
            // node's tip height). The node's own writer and decompiler do not produce or read a vault with two; the
            // rule does not rest on that.
            var uid = FrostDkgAttestation.NewContractUid();
            var body = Body(uid, VaultFeature(uid));
            var alone = SmartContractMain.GenerateSmartContractInMemory(body);
            Assert.True(LedgerIntegrityRules.VaultCodeIsPlain(alone, body));

            var withAnother = SmartContractMain.GenerateSmartContractInMemory(body);
            withAnother.Features!.Add(new SmartContractFeatures { FeatureName = FeatureName.Evolving });
            Assert.False(LedgerIntegrityRules.VaultCodeIsPlain(withAnother, body));

            var notAVault = SmartContractMain.GenerateSmartContractInMemory(VbtcTestContracts.PlainNftContractData);
            Assert.False(LedgerIntegrityRules.VaultCodeIsPlain(notAVault, VbtcTestContracts.PlainNftContractData));
            Assert.False(LedgerIntegrityRules.VaultCodeIsPlain(null, body));
        }

        [Fact]
        public async Task TheProofsBlockHeight_IsNotInTheFuture()
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var body = Body(uid, VaultFeature(uid, proofBlockHeight: 2_000_000_000));
            var (ok, why) = await Verify(Create(uid, body), Rule - 1);
            Assert.True(ok, "before the rule: " + why);
            (ok, why) = await Verify(Create(uid, body), Rule);
            Assert.False(ok);
            Assert.Contains("ProofBlockHeight", why);
            // The block that creates it, or any before: accepted.
            var atCreation = FrostDkgAttestation.NewContractUid();
            (ok, why) = await Verify(Create(atCreation, Body(atCreation, VaultFeature(atCreation, proofBlockHeight: Rule))), Rule);
            Assert.True(ok, why);
        }

        /// <summary>
        /// A vault the chain watches is created within twelve hours of its key ceremony, and never under a UID from
        /// before these rules. Its deposit address exists from the ceremony, and its Bitcoin is watched from about a day
        /// before its creation: a vault created days later could have Bitcoin at its address that no report can credit
        /// and no withdrawal can spend. And a vault burned in the last hours in which burning was possible could be
        /// created again from its old transaction.
        /// </summary>
        [Fact]
        public async Task AWatchedVault_IsCreatedSoonAfterItsCeremony_AndNotUnderAUidFromBeforeTheRules()
        {
            Globals.BitcoinAttestationRecordHeight = Rule;
            Globals.VaultBurnRuleHeight = Rule;   // as released: one fork height
            string UidMade(long secondsAgo) => Guid.NewGuid().ToString("N") + ":" + (TimeUtil.GetTime() - secondsAgo);

            var stale = UidMade(13 * 3600);
            var (ok, why) = await Verify(Create(stale, Body(stale, VaultFeature(stale))), Rule - 1);
            Assert.True(ok, "before the rule: " + why);
            (ok, why) = await Verify(Create(stale, Body(stale, VaultFeature(stale))), Rule);
            Assert.False(ok);
            Assert.Contains("not made within the last 12 hours", why);
            var recent = UidMade(11 * 3600);
            (ok, why) = await Verify(Create(recent, Body(recent, VaultFeature(recent))), Rule);
            Assert.True(ok, why);

            // The block before the rules was mined two hours ago: a UID made before then (or within the hour a UID may be
            // dated ahead) is from before the rules.
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = Rule - 1, Hash = "before-the-rules", Timestamp = TimeUtil.GetTime() - 2 * 3600, Transactions = new List<Transaction>() });
            var fromBefore = UidMade(3 * 3600);
            (ok, why) = await Verify(Create(fromBefore, Body(fromBefore, VaultFeature(fromBefore))), Rule);
            Assert.False(ok);
            Assert.Contains("made before the present rules started", why);
            var fromAfter = UidMade(30 * 60);
            (ok, why) = await Verify(Create(fromAfter, Body(fromAfter, VaultFeature(fromAfter))), Rule);
            Assert.True(ok, why);
        }

        [Fact]
        public void TwoVaultsWithOneDepositAddress_AreNotCreatedInOneBlock()
        {
            var (first, second, elsewhere) = (FrostDkgAttestation.NewContractUid(), Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime(), Guid.NewGuid().ToString("N") + ":" + (TimeUtil.GetTime() + 1));
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // As block validation registers a block's creations, one after the other.
            Assert.Null(LedgerIntegrityRules.RegisterCreationInBlock(Create(first, Body(first, VaultFeature(first))), created, Rule));
            Assert.Contains("same deposit address", LedgerIntegrityRules.RegisterCreationInBlock(Create(second, Body(second, VaultFeature(second))), created, Rule));
            // A contract that is not a vault, and the same pair before the rule: untouched.
            Assert.Null(LedgerIntegrityRules.RegisterCreationInBlock(Create(elsewhere, VbtcTestContracts.BuildContractData(elsewhere, _owner.Address, null)), created, Rule));
            var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.Null(LedgerIntegrityRules.RegisterCreationInBlock(Create(first, Body(first, VaultFeature(first))), before, Rule - 1));
            Assert.Null(LedgerIntegrityRules.RegisterCreationInBlock(Create(second, Body(second, VaultFeature(second))), before, Rule - 1));

            // A block's builder leaves the second one out, as its own validation would refuse the block: with the rule
            // in validation only, two such creations in the mempool had every producer build a block it then rejected,
            // for as long as both sat there.
            var (a, b, plain) = (Create(first, Body(first, VaultFeature(first))), Create(second, Body(second, VaultFeature(second))), Create(elsewhere, VbtcTestContracts.BuildContractData(elsewhere, _owner.Address, null)));
            b.FromAddress = "xAnotherSender";
            Globals.LastBlock = new Block { Height = Rule };
            Assert.Equal(new[] { a.Hash, plain.Hash }, TransactionData.DropDuplicateContractCreations(new List<Transaction> { a, b, plain }).Select(t => t.Hash));
            Globals.LastBlock = new Block { Height = Rule - 10 };
            Assert.Equal(3, TransactionData.DropDuplicateContractCreations(new List<Transaction> { a, b, plain }).Count);
            // And a block that carried the pair is a producer's fault, not a sign that this node's state is wrong.
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(LedgerIntegrityRules.SameDepositAddressInBlock));
        }

        [Fact]
        public void WhetherAVaultIsS3C_IsReadFromChainState_NotFromThisNodesOwnRecords()
        {
            var uid = Guid.NewGuid().ToString("N") + ":1755000009";
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.VaultContractData(uid, _owner.Address, _address, isS3C: true), MinterAddress = _owner.Address, OwnerAddress = _owner.Address, Nonce = 0,
            });
            // A record this node holds for the same UID says otherwise (a public node writes one for anyone who asks it to
            // build a creation transaction).
            VBTCContractV2.SaveContract(new VBTCContractV2 { SmartContractUID = uid, OwnerAddress = _owner.Address, DepositAddress = _address, IsS3C = false });
            Assert.True(VBTCService.ResolveContractIsS3C(uid));

            // Not on chain yet: the local record is all there is.
            var pending = Guid.NewGuid().ToString("N") + ":1755000010";
            VBTCContractV2.SaveContract(new VBTCContractV2 { SmartContractUID = pending, OwnerAddress = _owner.Address, DepositAddress = _address, IsS3C = true });
            Assert.True(VBTCService.ResolveContractIsS3C(pending));
        }
    }
}
