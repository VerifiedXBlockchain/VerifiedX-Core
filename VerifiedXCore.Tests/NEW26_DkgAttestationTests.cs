using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
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
    /// NEW-26 (owner-approved design change following the NEW-19 review): a vBTC V2 contract's DepositAddress,
    /// FrostGroupPublicKey and DKGProof were whatever the creator wrote into the contract body. The proof was unsigned
    /// JSON, so a creator could name a Bitcoin address it alone controls. From Globals.VbtcV2DkgAttestationHeight the
    /// address must be the Taproot address of the group key, and every participant of the key ceremony must sign the
    /// contract UID, key, address, owner, threshold and participant list (DKG_ATTESTED_V2). Participants must be eligible
    /// (active, funded) validators, a public ceremony must include at least 90% of them (owner decision, near-full
    /// participation), the threshold must be a majority of the participants, and the owner must be the transaction sender.
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
        private readonly (PrivateKey Key, string Pub, string Address) _otherCreator = NewKey();
        private readonly List<(PrivateKey Key, string Pub, string Address)> _public = Enumerable.Range(0, 10).Select(_ => NewKey()).ToList();
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
            foreach (var c in new[] { _minter, _otherCreator })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = c.Address, Balance = 100M, Nonce = 0 });

            // Active vBTC validators, registered on chain (10 public, 3 S3C), each holding the validator balance.
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

        private static string NewUid() => FrostDkgAttestation.NewContractUid();

        private static FrostDkgAttestation.Attestation Attest((PrivateKey Key, string Pub, string Address) v, string uid, string groupKey, string address, string owner, int threshold, IEnumerable<string> participants) =>
            new() { ValidatorAddress = v.Address, Signature = SignatureService.CreateSignature(FrostDkgAttestation.Message(uid, groupKey, address, owner, threshold, participants), v.Key, v.Pub) };

        /// <summary>
        /// A proof for a ceremony among <paramref name="participants"/>. By default every participant attests, the owner is
        /// the minter and the threshold is what the validators compute (51% of the participants).
        /// </summary>
        private string Proof(string uid, IEnumerable<(PrivateKey Key, string Pub, string Address)> participants, IEnumerable<(PrivateKey Key, string Pub, string Address)>? signers = null,
            string? owner = null, int? threshold = null, string? groupKey = null, string? address = null)
        {
            var list = participants.ToList();
            var addresses = list.Select(p => p.Address).ToList();
            var t = threshold ?? FrostDkgAttestation.ThresholdFor(addresses.Count, 51);
            var o = owner ?? _minter.Address;
            var gk = groupKey ?? _groupKey; var addr = address ?? _address;
            return FrostDkgAttestation.BuildProof(uid, gk, addr, o, t, addresses, (signers ?? list).Select(s => Attest(s, uid, gk, addr, o, t, addresses)));
        }

        private Transaction Create(string uid, string depositAddress, string groupKey, string proof, IEnumerable<string> snapshot, bool isS3C = false,
            (PrivateKey Key, string Pub, string Address)? creator = null)
        {
            var c = creator ?? _minter;
            var body = VbtcTestContracts.BuildContractData(uid, c.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.TokenizationV2,
                    FeatureFeatures = new TokenizationV2Feature
                    {
                        AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = depositAddress, Version = 2,
                        ValidatorAddressesSnapshot = snapshot.ToList(),
                        FrostGroupPublicKey = groupKey, RequiredThreshold = 51, DKGProof = proof, ProofBlockHeight = 1,
                        CeremonyId = uid, ImageBase = "default", IsS3C = isS3C,
                    },
                },
            }, name: "vBTC");
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = c.Address, ToAddress = c.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = uid, Data = body, MD5List = "NA" } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, c.Key, c.Pub);
            return tx;
        }

        /// <summary>A contract create for a ceremony among <paramref name="participants"/> (snapshot = participants).</summary>
        private Transaction CreateFor(string uid, IEnumerable<(PrivateKey Key, string Pub, string Address)> participants, string proof, bool isS3C = false,
            (PrivateKey Key, string Pub, string Address)? creator = null) =>
            Create(uid, _address, _groupKey, proof, participants.Select(p => p.Address), isS3C, creator);

        private static async Task<(bool Ok, string Message)> Verify(Transaction tx, long height = 100)
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);
            return (ok, message);
        }

        // ── PoCs (original finding) ────────────────────────────────────────────────────────

        [Fact]
        public async Task NEW26_PoC_DepositAddressTheCreatorControls_Refused()
        {
            var (_, creatorAddress) = NewGroupKey();
            var uid = NewUid();
            var legacyProof = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"ProofType\":\"DKG_COMPLETION_FROST_NATIVE\"}"));
            var (ok, _) = await Verify(Create(uid, creatorAddress, _groupKey, legacyProof, _public.Select(v => v.Address)));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_PoC_UnsignedProofForAMatchingAddress_Refused()
        {
            var uid = NewUid();
            var legacyProof = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"ProofType\":\"DKG_COMPLETION_FROST_NATIVE\"}"));
            var (ok, _) = await Verify(Create(uid, _address, _groupKey, legacyProof, _public.Select(v => v.Address)));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_PoC_AttestedAddressButDepositAddressSwapped_Refused()
        {
            var uid = NewUid();
            var (_, creatorAddress) = NewGroupKey();
            var (ok, message) = await Verify(Create(uid, creatorAddress, _groupKey, Proof(uid, _public), _public.Select(v => v.Address)));
            Assert.False(ok);
            Assert.Contains("Taproot address of its FROST group public key", message);
        }

        // ── Controls ───────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task NEW26_Control_EveryEligibleValidatorTookPartAndAttested_Accepted()
        {
            var uid = NewUid();
            var (ok, message) = await Verify(CreateFor(uid, _public, Proof(uid, _public)));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW26_Control_NinetyPercentParticipation_Accepted()
        {
            var uid = NewUid();
            var nine = _public.Take(9).ToList();                                  // 9 of 10 eligible
            var (ok, message) = await Verify(CreateFor(uid, nine, Proof(uid, nine)));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task NEW26_Control_BeforeActivation_LegacyContractAccepted()
        {
            var uid = NewUid();
            var (_, creatorAddress) = NewGroupKey();
            var (ok, message) = await Verify(Create(uid, creatorAddress, _groupKey, "fixture-proof", _public.Select(v => v.Address)), height: Activation - 1);
            Assert.True(ok, message);
        }

        // ── Review round 7 attacks (now refused) ───────────────────────────────────────────

        [Fact]
        public async Task NEW26_R7_SubsetOfValidatorsFormingTheGroup_Refused()
        {
            // A creator (with a colluding minority) ran the ceremony among a subset: a majority of the eligible set used to
            // be enough, so a group whose threshold the minority could meet was accepted.
            var uid = NewUid();
            var eight = _public.Take(8).ToList();                                 // 8 of 10 < 90%
            var (ok, message) = await Verify(CreateFor(uid, eight, Proof(uid, eight)));
            Assert.False(ok);
            Assert.Contains("at least 9 of the 10 eligible validators", message);
        }

        [Fact]
        public async Task NEW26_R7_CreatorsUnfundedRegistrationsAsParticipants_Refused()
        {
            // Reviewer PoC C: the creator's own registrations (no balance) held the key while funded validators attested.
            var sybils = Enumerable.Range(0, 3).Select(_ => NewKey()).ToList();
            long h = 40; foreach (var s in sybils) Register(s, isS3C: false, h++);
            var participants = _public.Take(9).Concat(sybils).ToList();
            var uid = NewUid();
            var (ok, message) = await Verify(CreateFor(uid, participants, Proof(uid, participants)));
            Assert.False(ok);
            Assert.Contains("is not an eligible validator", message);
        }

        [Fact]
        public async Task NEW26_R7_AttestationsReusedByAnotherCreator_Refused()
        {
            // Reviewer PoC B: a participant took the attestations and created the contract first under its own address.
            var uid = NewUid();
            var proof = Proof(uid, _public);                                       // made for _minter's ceremony
            var (ok, message) = await Verify(CreateFor(uid, _public, proof, creator: _otherCreator));
            Assert.False(ok);
            Assert.Contains("not made for this contract's creator", message);
        }

        [Fact]
        public async Task NEW26_ThresholdBelowAMajorityOfParticipants_Refused()
        {
            var uid = NewUid();
            var (ok, message) = await Verify(CreateFor(uid, _public, Proof(uid, _public, threshold: 2)));
            Assert.False(ok);
            Assert.Contains("is not a majority of its 10 participants", message);
        }

        [Fact]
        public async Task NEW26_AParticipantThatDidNotAttest_Refused()
        {
            var uid = NewUid();
            var (ok, message) = await Verify(CreateFor(uid, _public, Proof(uid, _public, signers: _public.Take(9))));
            Assert.False(ok);
            Assert.Contains("one attestation from each of its 10 participants", message);
        }

        [Fact]
        public async Task NEW26_RepeatedAttestation_Refused()
        {
            var uid = NewUid();
            var signers = _public.Take(9).Append(_public[0]).ToList();            // 10 entries, one participant missing
            var (ok, _) = await Verify(CreateFor(uid, _public, Proof(uid, _public, signers: signers)));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_AttestationsForAnotherContractOrKey_Refused()
        {
            var uid = NewUid();
            var other = NewUid();
            Assert.False((await Verify(CreateFor(uid, _public, Proof(other, _public)))).Ok);        // proof names another UID
            var (otherKey, otherAddress) = NewGroupKey();
            var relabelled = Proof(uid, _public, groupKey: otherKey, address: otherAddress);       // signatures over another key
            Assert.False((await Verify(CreateFor(uid, _public, relabelled))).Ok);
        }

        [Fact]
        public async Task NEW26_SnapshotDiffersFromTheParticipants_Refused()
        {
            var uid = NewUid();
            var tx = Create(uid, _address, _groupKey, Proof(uid, _public), _public.Take(9).Select(v => v.Address));
            var (ok, message) = await Verify(tx);
            Assert.False(ok);
            Assert.Contains("does not match the key ceremony's participants", message);
        }

        [Fact]
        public async Task NEW26_ValidatorRegisteredInTheSameBlock_NotEligible()
        {
            var late = NewKey();
            Register(late, isS3C: false, 100);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = late.Address, Balance = 5_000M });
            var participants = _public.Append(late).ToList();
            var uid = NewUid();
            var (ok, message) = await Verify(CreateFor(uid, participants, Proof(uid, participants)), height: 100);
            Assert.False(ok);
            Assert.Contains("is not an eligible validator", message);
        }

        [Fact]
        public async Task NEW26_S3cValidatorsCannotParticipateInAPublicContract()
        {
            var participants = _public.Take(9).Concat(_s3c.Take(1)).ToList();
            var uid = NewUid();
            var (ok, _) = await Verify(CreateFor(uid, participants, Proof(uid, participants)));
            Assert.False(ok);
        }

        [Fact]
        public async Task NEW26_S3c_WholePoolAttested_Accepted_AndPoolMustBeActiveS3cValidators()
        {
            var uid = NewUid();
            var (ok, message) = await Verify(CreateFor(uid, _s3c, Proof(uid, _s3c), isS3C: true));
            Assert.True(ok, message);

            var mixed = new[] { _s3c[0], _s3c[1], _public[0] }.ToList();          // a public validator in an S3C pool
            var uid2 = NewUid();
            Assert.False((await Verify(CreateFor(uid2, mixed, Proof(uid2, mixed), isS3C: true))).Ok);
        }

        [Fact]
        public async Task NEW26_ValidatorWhoseBalanceMovedAway_NotEligible()
        {
            var moved = _public[3].Address;
            var acct = StateData.GetAccountStateTrei().FindOne(a => a.Key == moved);
            acct.Balance = 4_999M;
            StateData.GetAccountStateTrei().UpdateSafe(acct);
            var uid = NewUid();
            var (ok, message) = await Verify(CreateFor(uid, _public, Proof(uid, _public)));
            Assert.False(ok);
            Assert.Contains("is not an eligible validator", message);

            // The remaining nine funded validators are 100% of the eligible set.
            var nine = _public.Where((_, i) => i != 3).ToList();
            var uid2 = NewUid();
            var (ok2, message2) = await Verify(CreateFor(uid2, nine, Proof(uid2, nine)));
            Assert.True(ok2, message2);
        }

        [Fact]
        public async Task NEW26_UnfundedRegistrationsDoNotRaiseTheParticipationRequirement()
        {
            var sybils = Enumerable.Range(0, 6).Select(_ => NewKey()).ToList();
            long h = 30; foreach (var s in sybils) Register(s, isS3C: false, h++);   // registered, no balance
            var uid = NewUid();
            var nine = _public.Take(9).ToList();
            var (ok, message) = await Verify(CreateFor(uid, nine, Proof(uid, nine)));
            Assert.True(ok, message);
        }

        // ── Registry, cache, decompile ─────────────────────────────────────────────────────

        [Fact]
        public void NEW26_ActiveSetAtHeight_IsDerivedFromCommittedBlocks_WithoutSyncGate()
        {
            Globals.IsChainSynced = false;
            Assert.Equal(13, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(99).Count);
            Assert.Equal(2, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(11).Count);
        }

        [Fact]
        public void NEW26_ActiveSetCache_FollowsARolledBackBlock()
        {
            Assert.Equal(7, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(16).Count); // cached for (16, hash)
            var blocks = BlockchainData.GetBlocks();
            blocks.DeleteMany(b => b.Height == 16);
            blocks.Insert(new Block { Height = 16, Hash = "h16-other", Transactions = new List<Transaction>() });
            Assert.Equal(6, VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(16).Count);
        }

        [Fact]
        public void NEW26_ProofWithManyParticipants_SurvivesTheContractWriterAndDecompiler()
        {
            var many = Enumerable.Range(0, 40).Select(_ => NewKey()).ToList();
            var uid = NewUid();
            var proof = Proof(uid, many);
            var tx = CreateFor(uid, many, proof);
            var body = SmartContractDeployBinding.ReadPayload(tx.Data).Data;
            var sc = SmartContractMain.GenerateSmartContractInMemory(body);
            var feature = (TokenizationV2Feature)sc.Features!.Single(f => f.FeatureName == FeatureName.TokenizationV2).FeatureFeatures;
            Assert.Equal(proof, feature.DKGProof);
            Assert.Equal(40, FrostDkgAttestation.ParseProof(feature.DKGProof)!.Attestations.Count);
        }

        // ── Funded validators (follow-up) ──────────────────────────────────────────────────

        [Fact]
        public void NEW26_FollowUp_BalanceCheckUsesTheExactAddress()
        {
            var funded = _public[0].Address;
            Assert.True(VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.HoldsValidatorBalance(funded));
            var i = Enumerable.Range(1, funded.Length - 1).First(k => char.IsLetter(funded[k]));
            var variant = funded[..i] + (char.IsUpper(funded[i]) ? char.ToLowerInvariant(funded[i]) : char.ToUpperInvariant(funded[i])) + funded[(i + 1)..];
            Assert.False(VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.HoldsValidatorBalance(variant));
            Assert.False(VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.HoldsValidatorBalance(NewKey().Address));
        }

        [Fact]
        public void NEW26_FollowUp_CeremonySelectionTakesFundedValidatorsOnly()
        {
            var sybils = Enumerable.Range(0, 2).Select(_ => NewKey()).ToList();
            long h = 30; foreach (var s in sybils) Register(s, isS3C: false, h++);
            var all = VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(99);
            var funded = VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.FundedOnly(all);
            Assert.Equal(15, all.Count);
            Assert.Equal(13, funded.Count);
            Assert.DoesNotContain(funded, v => sybils.Any(x => x.Address == v.ValidatorAddress));

            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var controller = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Controllers", "VBTCController.cs"));
            Assert.Equal(4, CountOf(controller, "FundedOnly(Services.VBTCValidatorRegistry.GetPublicValidators())"));
            Assert.Equal(0, CountOf(controller, "= Services.VBTCValidatorRegistry.GetPublicValidators()"));
            var s3c = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Services", "S3CService.cs"));
            Assert.Contains("VBTCValidatorRegistry.HoldsValidatorBalance(v.ValidatorAddress)", s3c);
        }

        // ── Wallet: never run or finish a ceremony whose contract would be refused (follow-up) ──

        [Fact]
        public void NEW26_FollowUp_TooFewReachableValidators_RefusedBeforeTheCeremony()
        {
            // 10 funded public validators: a ceremony needs 9 of them.
            Assert.Contains("at least 9 of the 10", FrostDkgAttestation.PreCeremonyShortfall(8, false, 0));
            Assert.Null(FrostDkgAttestation.PreCeremonyShortfall(9, false, 0));
            Assert.Contains("at least 3 of the 3", FrostDkgAttestation.PreCeremonyShortfall(2, true, 3));   // S3C: the whole pool
            Globals.LastBlock = new Block { Height = Activation - 10 };                                          // before activation
            Assert.Null(FrostDkgAttestation.PreCeremonyShortfall(0, false, 0));
        }

        [Fact]
        public void NEW26_FollowUp_FinishedCeremonyThatWouldBeRefused_Detected()
        {
            var uid = NewUid();
            var eight = _public.Take(8).ToList();
            Assert.Contains("at least 9 of the 10", FrostDkgAttestation.CeremonyResultError(uid, _groupKey, _address, Proof(uid, eight), eight.Select(v => v.Address).ToList(), false, _minter.Address));
            Assert.Null(FrostDkgAttestation.CeremonyResultError(uid, _groupKey, _address, Proof(uid, _public), _public.Select(v => v.Address).ToList(), false, _minter.Address));
            Assert.NotNull(FrostDkgAttestation.CeremonyResultError(uid, _groupKey, _address, Proof(uid, _public), _public.Select(v => v.Address).ToList(), false, _otherCreator.Address));
        }

        [Fact]
        public async Task NEW26_FollowUp_DepositAddressServedOnlyForAContractOnChain()
        {
            var uid = NewUid();
            VBTCContractV2.SaveContract(new VBTCContractV2 { SmartContractUID = uid, OwnerAddress = _minter.Address, DepositAddress = _address, FrostGroupPublicKey = _groupKey });
            var controller = new VerifiedXCore.Bitcoin.Controllers.VBTCController();

            var before = Newtonsoft.Json.Linq.JObject.Parse(await controller.GetMPCDepositAddress(uid));
            Assert.False((bool)before["Success"]!);                                   // local record only: not served
            Assert.Null(before["DepositAddress"]);

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = uid, ContractData = "x", MinterAddress = _minter.Address, OwnerAddress = _minter.Address });
            var after = Newtonsoft.Json.Linq.JObject.Parse(await controller.GetMPCDepositAddress(uid));
            Assert.True((bool)after["Success"]!);                                      // on chain: served
            Assert.Equal(_address, (string?)after["DepositAddress"]);
        }

        [Fact]
        public void NEW26_FollowUp_WalletChecksAreWiredIntoEveryCeremonyPath()
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var controller = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Controllers", "VBTCController.cs"));
            Assert.Equal(4, CountOf(controller, "FrostDkgAttestation.PreCeremonyShortfall("));   // every ceremony start
            Assert.Equal(3, CountOf(controller, "FrostDkgAttestation.CeremonyResultError("));    // every ceremony finish
            Assert.Equal(0, CountOf(controller, "DepositAddress = ceremony.Status == CeremonyStatus.Completed ? ceremony.DepositAddress : null"));
            Assert.Equal(2, CountOf(controller, "VBTCContractV2.DeleteContract(scUID); SmartContractMain.SmartContractData.DeleteSmartContract(scUID);"));
        }

        // ── Validator and coordinator sides ────────────────────────────────────────────────

        [Fact]
        public void NEW26_ValidatorAttestsOnlyItsOwnCeremony()
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(account);
            Globals.ValidatorAddress = account.Address;
            var uid = NewUid();
            var participants = _public.Take(2).Select(v => v.Address).Append(account.Address).ToList();
            var owner = _minter.Address;
            var t = FrostDkgAttestation.ThresholdFor(participants.Count, 51);

            Assert.Null(FrostDkgAttestation.SignLocal(uid, _groupKey, _address, owner, t, participants)); // no key package

            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore
            {
                SmartContractUID = uid, ValidatorAddress = account.Address, KeyPackage = "{}", PubkeyPackage = "{}", GroupPublicKey = _groupKey,
                ParticipantOrderJson = JsonConvert.SerializeObject(FrostDkgAttestation.Canonical(participants)), CreatedTimestamp = TimeUtil.GetTime(),
            }));
            var (_, otherAddress) = NewGroupKey();
            Assert.Null(FrostDkgAttestation.SignLocal(uid, _groupKey, otherAddress, owner, t, participants));                   // not the key's address
            Assert.Null(FrostDkgAttestation.SignLocal(NewUid(), _groupKey, _address, owner, t, participants));                  // another contract
            var (otherKey, otherKeyAddress) = NewGroupKey();
            Assert.Null(FrostDkgAttestation.SignLocal(uid, otherKey, otherKeyAddress, owner, t, participants));                  // another key
            Assert.Null(FrostDkgAttestation.SignLocal(uid, _groupKey, _address, owner, t, participants.Take(2).ToList()));       // not a participant
            Assert.Null(FrostDkgAttestation.SignLocal(uid, _groupKey, _address, owner, t, participants.Append(_public[5].Address).ToList())); // another participant list

            var a = FrostDkgAttestation.SignLocal(uid, _groupKey, _address, owner, t, participants);
            Assert.NotNull(a);
            Assert.Equal(account.Address, a!.ValidatorAddress);
            Assert.True(FrostDkgAttestation.Verify(a, uid, _groupKey, _address, owner, t, participants));
            Assert.False(FrostDkgAttestation.Verify(a, uid, _groupKey, _address, _otherCreator.Address, t, participants));     // owner bound
            Assert.False(FrostDkgAttestation.Verify(a, uid, _groupKey, _address, owner, t - 1, participants));                  // threshold bound
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
            Assert.Contains("dkgProof = FrostDkgAttestation.BuildProof(ceremonyId, groupPublicKey, taprootAddress, leaderAddress, signingThreshold, participants, attestations);", mpc);
            var startup = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "FROST", "FrostStartup.cs"));
            Assert.Contains("FrostDkgAttestation.SignLocal(session.SmartContractUID, session.GroupPublicKey, session.TaprootAddress,", startup);
            Assert.Contains("session.LeaderAddress, FrostDkgAttestation.ThresholdFor(session.ParticipantAddresses?.Count ?? 0, session.RequiredThreshold),", startup);
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
