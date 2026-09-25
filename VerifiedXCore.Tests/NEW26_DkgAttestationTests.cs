using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
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
    /// NEW-26 (owner-approved design change following the NEW-19 review): a vBTC V2 contract's DepositAddress,
    /// FrostGroupPublicKey and DKGProof were whatever the creator wrote into the contract body. The proof was unsigned
    /// JSON, so a creator could name a Bitcoin address it alone controls: deposits to it mint vBTC other people hold,
    /// and the creator can move the BTC without any validator. From Globals.VbtcV2DkgAttestationHeight the address must
    /// be the Taproot address of the group key and a majority of the eligible validators must sign the DKG result.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW26_DkgAttestationTests : IDisposable
    {
        private const long Activation = 50;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorActivation;
        private readonly string? _priorValidatorAddress;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly (PrivateKey Key, string Pub, string Address) _minter = NewKey();
        private readonly List<(PrivateKey Key, string Pub, string Address)> _public = Enumerable.Range(0, 4).Select(_ => NewKey()).ToList();
        private readonly List<(PrivateKey Key, string Pub, string Address)> _s3c = Enumerable.Range(0, 3).Select(_ => NewKey()).ToList();
        private readonly string _groupKey;
        private readonly string _address;

        public NEW26_DkgAttestationTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new26_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorActivation = Globals.VbtcV2DkgAttestationHeight;
            _priorValidatorAddress = Globals.ValidatorAddress;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 99 }; // admission height 100
            Globals.VbtcV2DkgAttestationHeight = Activation;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _minter.Address, Balance = 100M, Nonce = 0 });

            // Active vBTC validators, registered on chain (4 public, 3 S3C), each holding the validator balance.
            long h = 10;
            foreach (var v in _public) Register(v, isS3C: false, h++);
            foreach (var v in _s3c) Register(v, isS3C: true, h++);
            foreach (var v in _public.Concat(_s3c))
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = 5_000M, Nonce = 0 });

            (_groupKey, _address) = NewGroupKey();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.VbtcV2DkgAttestationHeight = _priorActivation;
            Globals.ValidatorAddress = _priorValidatorAddress;
            Globals.IsChainSynced = _priorSynced;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static (string GroupKey, string Address) NewGroupKey()
        {
            var xOnly = new NBitcoin.Key().PubKey.ToBytes()[1..];
            var hex = Convert.ToHexString(xOnly).ToLowerInvariant();
            return (hex, FrostDkgAttestation.DeriveTaprootAddress(hex, NBitcoin.Network.Main)!);
        }

        private static void Register((PrivateKey Key, string Pub, string Address) v, bool isS3C, long height) =>
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = height, Hash = $"h{height}-{Guid.NewGuid():N}", // unique: the registry caches by (height, hash)
                Transactions = new List<Transaction>
                {
                    new Transaction
                    {
                        TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = "r" + height,
                        Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.0.1", FrostPublicKey = v.Pub, IsS3C = isS3C }),
                    },
                },
            });

        private static FrostDkgAttestation.Attestation Attest((PrivateKey Key, string Pub, string Address) v, string uid, string groupKey, string address) =>
            new() { ValidatorAddress = v.Address, Signature = SignatureService.CreateSignature(FrostDkgAttestation.Message(uid, groupKey, address), v.Key, v.Pub) };

        private static string NewUid() => FrostDkgAttestation.NewContractUid();

        private Transaction Create(string uid, string depositAddress, string groupKey, string proof, bool isS3C = false, List<string>? snapshot = null)
        {
            var body = VbtcTestContracts.BuildContractData(uid, _minter.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.TokenizationV2,
                    FeatureFeatures = new TokenizationV2Feature
                    {
                        AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = depositAddress, Version = 2,
                        ValidatorAddressesSnapshot = snapshot ?? (isS3C ? _s3c : _public).Select(v => v.Address).ToList(),
                        FrostGroupPublicKey = groupKey, RequiredThreshold = 51, DKGProof = proof, ProofBlockHeight = 1,
                        CeremonyId = uid, ImageBase = "default", IsS3C = isS3C,
                    },
                },
            }, name: "vBTC");
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _minter.Address, ToAddress = _minter.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = uid, Data = body, MD5List = "NA" } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _minter.Key, _minter.Pub);
            return tx;
        }

        private string Proof(string uid, string groupKey, string address, IEnumerable<(PrivateKey Key, string Pub, string Address)> signers) =>
            FrostDkgAttestation.BuildProof(uid, groupKey, address, signers.Select(s => Attest(s, uid, groupKey, address)));

        private static async Task<(bool Ok, string Message)> Verify(Transaction tx, long height = 100)
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);
            return (ok, message);
        }

        // ── PoCs ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task NEW26_PoC_DepositAddressTheCreatorControls_Refused()
        {
            // The creator's own key: the validators never held it. The group key and (unsigned) proof are decoration.
            var (_, creatorAddress) = NewGroupKey();
            var uid = NewUid();
            var legacyProof = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"ProofType\":\"DKG_COMPLETION_FROST_NATIVE\"}"));
            var (ok, _) = await Verify(Create(uid, creatorAddress, _groupKey, legacyProof));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_PoC_UnsignedProofForAMatchingAddress_Refused()
        {
            var uid = NewUid();
            var legacyProof = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"ProofType\":\"DKG_COMPLETION_FROST_NATIVE\"}"));
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, legacyProof));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_PoC_AttestedAddressButDepositAddressSwapped_Refused()
        {
            var uid = NewUid();
            var (_, creatorAddress) = NewGroupKey();
            var (ok, message) = await Verify(Create(uid, creatorAddress, _groupKey, Proof(uid, _groupKey, _address, _public.Take(3))));
            Assert.False(ok);
            Assert.Contains("Taproot address of its FROST group public key", message);
        }

        // ── Controls ───────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task NEW26_Control_MajorityOfActivePublicValidatorsAttested_Accepted()
        {
            var uid = NewUid();
            var (ok, message) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, _public.Take(3)))); // 3 of 4
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW26_Control_BeforeActivation_LegacyContractAccepted()
        {
            var uid = NewUid();
            var (_, creatorAddress) = NewGroupKey();
            var (ok, message) = await Verify(Create(uid, creatorAddress, _groupKey, "fixture-proof"), height: Activation - 1);
            Assert.True(ok, message);
        }

        // ── Attestation rules ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task NEW26_MinorityOfValidators_Refused()
        {
            var uid = NewUid();
            var (ok, message) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, _public.Take(2)))); // 2 of 4 < 51%
            Assert.False(ok);
            Assert.Contains("2 eligible validator(s); 3 required", message);
        }

        [Fact]
        public async Task NEW26_RepeatedAttestationCountsOnce_Refused()
        {
            var uid = NewUid();
            var signers = new[] { _public[0], _public[1], _public[1], _public[1] };
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, signers)));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_UnregisteredSigners_Refused()
        {
            var uid = NewUid();
            var strangers = Enumerable.Range(0, 4).Select(_ => NewKey()).ToList();
            var snapshot = strangers.Select(s => s.Address).ToList();
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, strangers), snapshot: snapshot));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_AttestationsForAnotherContract_Refused()
        {
            // One DKG's attestations cannot back a second contract (two contracts sharing one deposit address).
            var uid = NewUid();
            var other = NewUid();
            var proofForOther = Proof(other, _groupKey, _address, _public.Take(3));
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, proofForOther));
            Assert.False(ok);

            // Same attestations relabelled with this UID: the signatures are over the other UID.
            var relabelled = FrostDkgAttestation.BuildProof(uid, _groupKey, _address, _public.Take(3).Select(s => Attest(s, other, _groupKey, _address)));
            Assert.False((await Verify(Create(uid, _address, _groupKey, relabelled))).Ok);
        }

        [Fact]
        public async Task NEW26_ValidatorRegisteredInTheSameBlock_NotCounted()
        {
            // The eligible set is the one committed by the previous block.
            var late = NewKey();
            Register(late, isS3C: false, 100);
            var uid = NewUid();
            var snapshot = _public.Select(v => v.Address).Append(late.Address).ToList();
            var signers = new[] { _public[0], _public[1], late };
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, signers), snapshot: snapshot), height: 100);
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_S3cSignersDoNotCountForAPublicContract()
        {
            var uid = NewUid();
            var snapshot = _public.Concat(_s3c).Select(v => v.Address).ToList();
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, _s3c), snapshot: snapshot));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_S3c_MajorityOfItsPool_Accepted_AndPoolMustBeActiveS3cValidators()
        {
            var uid = NewUid();
            var (ok, message) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, _s3c.Take(2)), isS3C: true));
            Assert.True(ok, message);

            // A pool naming a public validator (or anyone not an active S3C validator) is refused.
            var uid2 = NewUid();
            var mixed = new List<string> { _s3c[0].Address, _s3c[1].Address, _public[0].Address };
            var (ok2, _) = await Verify(Create(uid2, _address, _groupKey, Proof(uid2, _groupKey, _address, _s3c.Take(2)), isS3C: true, snapshot: mixed));
            Assert.False(ok2);
        }

        [Fact]
        public void NEW26_ActiveSetAtHeight_IsDerivedFromCommittedBlocks_WithoutSyncGate()
        {
            Globals.IsChainSynced = false;
            Assert.Equal(7, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(99).Count);
            Assert.Equal(2, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(11).Count);
        }

        [Fact]
        public void NEW26_ActiveSetCache_FollowsARolledBackBlock()
        {
            Assert.Equal(7, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(16).Count); // cached for (16, "h16")
            var blocks = BlockchainData.GetBlocks();
            blocks.DeleteMany(b => b.Height == 16);
            blocks.Insert(new Block { Height = 16, Hash = "h16-other", Transactions = new List<Transaction>() });
            Assert.Equal(6, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(16).Count);
        }

        [Fact]
        public void NEW26_ProofWithManyAttestations_SurvivesTheContractWriterAndDecompiler()
        {
            var many = Enumerable.Range(0, 40).Select(_ => NewKey()).ToList();
            var uid = NewUid();
            var proof = Proof(uid, _groupKey, _address, many);
            var tx = Create(uid, _address, _groupKey, proof, snapshot: many.Select(m => m.Address).ToList());
            var body = SmartContractDeployBinding.ReadPayload(tx.Data).Data;
            var sc = SmartContractMain.GenerateSmartContractInMemory(body);
            var feature = (TokenizationV2Feature)sc.Features!.Single(f => f.FeatureName == FeatureName.TokenizationV2).FeatureFeatures;
            Assert.Equal(proof, feature.DKGProof);
            Assert.Equal(40, FrostDkgAttestation.ParseProof(feature.DKGProof)!.Attestations.Count);
        }

        // ── Follow-up: only validators holding the validator balance count (registry Sybils) ──

        /// <summary>Registers validators that do not hold the balance now (registration checked it once; the same funds moved on).</summary>
        private List<(PrivateKey Key, string Pub, string Address)> RegisterUnfundedSybils(int n)
        {
            var sybils = Enumerable.Range(0, n).Select(_ => NewKey()).ToList();
            long h = 30;
            foreach (var s in sybils) Register(s, isS3C: false, h++);
            return sybils;
        }

        [Fact]
        public async Task NEW26_FollowUp_PoC_UnfundedSybilRegistrationsSupplyTheMajority_Refused()
        {
            // 4 funded honest validators + 6 registrations kept alive with one balance moved between them: the Sybils
            // were 6 of 10 active validators, a 51% majority, and could attest a key only they hold.
            var sybils = RegisterUnfundedSybils(6);
            var uid = NewUid();
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, sybils), snapshot: sybils.Select(x => x.Address).ToList()));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_FollowUp_UnfundedRegistrationsDoNotRaiseTheMajority()
        {
            // Control: the funded validators' majority is the basis; unfunded registrations do not dilute it.
            RegisterUnfundedSybils(6);
            var uid = NewUid();
            var (ok, message) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, _public.Take(3))));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW26_FollowUp_ValidatorWhoseBalanceMovedAway_NotCounted()
        {
            foreach (var v in new[] { _public[1], _public[2] })
            {
                var acct = StateData.GetAccountStateTrei().FindOne(a => a.Key == v.Address);
                acct.Balance = 4_999M;
                StateData.GetAccountStateTrei().UpdateSafe(acct);
            }
            var uid = NewUid();
            // Funded: public[0], public[3] -> 2 required; public[1], public[2] no longer count.
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, Proof(uid, _groupKey, _address, _public.Take(3))));
            Assert.False(ok);
        }

        [Fact]
        public void NEW26_FollowUp_BalanceCheckUsesTheExactAddress()
        {
            var funded = _public[0].Address;
            Assert.True(VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.HoldsValidatorBalance(funded));
            // LiteDB lookups ignore case: a case variant must not read the funded account's balance.
            var i = Enumerable.Range(1, funded.Length - 1).First(k => char.IsLetter(funded[k]));
            var variant = funded[..i] + (char.IsUpper(funded[i]) ? char.ToLowerInvariant(funded[i]) : char.ToUpperInvariant(funded[i])) + funded[(i + 1)..];
            Assert.False(VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.HoldsValidatorBalance(variant));
            Assert.False(VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.HoldsValidatorBalance(NewKey().Address));
        }

        [Fact]
        public void NEW26_FollowUp_CeremonySelectionTakesFundedValidatorsOnly()
        {
            var sybils = RegisterUnfundedSybils(2);
            var all = VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(99);
            var funded = VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.FundedOnly(all);
            Assert.Equal(9, all.Count);
            Assert.Equal(7, funded.Count);
            Assert.DoesNotContain(funded, v => sybils.Any(x => x.Address == v.ValidatorAddress));

            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var controller = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Controllers", "VBTCController.cs"));
            Assert.Equal(4, CountOf(controller, "FundedOnly(Services.VBTCValidatorRegistry.GetPublicValidators())"));
            Assert.Equal(0, CountOf(controller, "= Services.VBTCValidatorRegistry.GetPublicValidators()"));
            var s3c = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Services", "S3CService.cs"));
            Assert.Contains("VBTCValidatorRegistry.HoldsValidatorBalance(v.ValidatorAddress)", s3c);
        }

        // ── Validator and coordinator sides ────────────────────────────────────────────────

        [Fact]
        public void NEW26_ValidatorAttestsOnlyAKeyItStored()
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(account);
            Globals.ValidatorAddress = account.Address;
            var uid = NewUid();

            Assert.Null(FrostDkgAttestation.SignLocal(uid, _groupKey, _address)); // no key package

            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore
            {
                SmartContractUID = uid, ValidatorAddress = account.Address, KeyPackage = "{}", PubkeyPackage = "{}", GroupPublicKey = _groupKey, CreatedTimestamp = TimeUtil.GetTime(),
            }));
            var (_, otherAddress) = NewGroupKey();
            Assert.Null(FrostDkgAttestation.SignLocal(uid, _groupKey, otherAddress));   // not the key's address
            Assert.Null(FrostDkgAttestation.SignLocal(NewUid(), _groupKey, _address));  // another contract
            var (otherKey, otherKeyAddress) = NewGroupKey();
            Assert.Null(FrostDkgAttestation.SignLocal(uid, otherKey, otherKeyAddress)); // another key

            var a = FrostDkgAttestation.SignLocal(uid, _groupKey, _address);
            Assert.NotNull(a);
            Assert.Equal(account.Address, a!.ValidatorAddress);
            Assert.True(FrostDkgAttestation.Verify(a, uid, _groupKey, _address));
        }

        [Fact]
        public void NEW26_CeremonyIdIsTheContractUid_AndTheCoordinatorBuildsTheAttestedProof()
        {
            Assert.Null(LedgerIntegrityRules.ContractUids(new Transaction
            {
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = NewUid() } }),
            }));

            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var controller = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Controllers", "VBTCController.cs"));
            Assert.DoesNotContain("var ceremonyId = Guid.NewGuid().ToString();", controller);
            Assert.DoesNotContain("var scUID = Guid.NewGuid()", controller);
            Assert.Equal(3, CountOf(controller, "var scUID = payload.CeremonyId;"));

            var mpc = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Services", "FrostMPCService.cs"));
            Assert.Contains("dkgProof = FrostDkgAttestation.BuildProof(ceremonyId, groupPublicKey, taprootAddress, attestations);", mpc);
            var startup = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "FROST", "FrostStartup.cs"));
            Assert.Contains("FrostDkgAttestation.SignLocal(session.SmartContractUID, session.GroupPublicKey, session.TaprootAddress)", startup);
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
