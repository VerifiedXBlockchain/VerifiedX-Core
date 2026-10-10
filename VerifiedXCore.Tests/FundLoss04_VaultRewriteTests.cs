using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
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
    /// Fund-loss audit item 4. Consensus (Globals.VaultRewriteRulesHeight): Update(), Transfer(), Evolve() and
    /// Devolve() may not give a contract that is not a vBTC V2 vault a TokenizationV2 feature - the attestation runs on
    /// creations only and the vault-code freeze only covers contracts already recognised as vaults. Validator-local:
    /// a FROST key-share record filed under another on-chain contract is never adopted for a different contract.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss04_VaultRewriteTests : IDisposable
    {
        private const long Gate = 1000;
        private const string Refused = "A contract that is not a vBTC V2 vault cannot be given a TokenizationV2 feature";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _owner;

        public FundLoss04_VaultRewriteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl04_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.VaultRewriteRulesHeight;
            Globals.VaultRewriteRulesHeight = Gate;
            DbContext.Initialize();
            _owner = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.VaultRewriteRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private string PlainNft()
        {
            var uid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.PlainNftContractData,
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address, IsLocked = false, Nonce = 0,
            });
            return uid;
        }

        private string Vault(string depositAddress, string groupKey)
        {
            var uid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.VaultContractData(uid, "xVictimMinter", depositAddress, groupKey),
                MinterAddress = "xVictimMinter", OwnerAddress = "xVictimMinter", IsLocked = false, Nonce = 0,
            });
            return uid;
        }

        private Transaction Rewrite(string function, string scUid, string body, TransactionType type = TransactionType.NFT_TX, string? to = null)
        {
            var tx = new Transaction
            {
                FromAddress = _owner.Address, ToAddress = to ?? _owner.Address, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = type,
                Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = scUid, Data = body, MD5List = "NA", ToAddress = to ?? _owner.Address } }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        private const string VictimDeposit = "bc1pvictimvictimvictimvictimvictimvictimvictimvictimvictim00";
        private const string VictimGroupKey = "02" + "ab" + "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";

        private static void TipBelowGate() => Globals.LastBlock = new Block { Height = Gate - 2 };
        private static void TipAtGate() => Globals.LastBlock = new Block { Height = Gate - 1 };

        // ── Consensus rule ────────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Update()")]
        [InlineData("Evolve()")]
        [InlineData("Devolve()")]
        public async Task PlainNftRewrittenIntoAVault_PassesBelowGate_RefusedAtGate(string function)
        {
            var uid = PlainNft();
            var forgedVault = VbtcTestContracts.VaultContractData(uid, _owner.Address, VictimDeposit, VictimGroupKey);

            TipBelowGate();
            var (okBelow, msgBelow) = await TransactionValidatorService.VerifyTX(Rewrite(function, uid, forgedVault));
            Assert.True(okBelow, msgBelow); // the hole

            TipAtGate();
            var (okAt, msgAt) = await TransactionValidatorService.VerifyTX(Rewrite(function, uid, forgedVault));
            Assert.False(okAt);
            Assert.StartsWith(Refused, msgAt);
        }

        [Fact]
        public async Task TransferCarryingAVaultBody_ForAPlainNft_RefusedAtGate()
        {
            var uid = PlainNft();
            var forgedVault = VbtcTestContracts.VaultContractData(uid, _owner.Address, VictimDeposit, VictimGroupKey);
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Rewrite("Transfer()", uid, forgedVault, to: NewKey().Address));
            Assert.False(ok);
            Assert.StartsWith(Refused, msg);
        }

        [Fact]
        public async Task HonestRewrites_PassAtGate()
        {
            TipAtGate();
            var uid = PlainNft();
            // Update with a plain body (changed description) is still a plain NFT.
            var plainChanged = VbtcTestContracts.BuildContractData(uid, _owner.Address, null, description: "a new description");
            var (okUpdate, msgUpdate) = await TransactionValidatorService.VerifyTX(Rewrite("Update()", uid, plainChanged));
            Assert.True(okUpdate, msgUpdate);
            // A transfer resending the stored body unchanged.
            var (okTransfer, msgTransfer) = await TransactionValidatorService.VerifyTX(Rewrite("Transfer()", uid, VbtcTestContracts.PlainNftContractData, to: NewKey().Address));
            Assert.True(okTransfer, msgTransfer);
        }

        [Fact]
        public void Rule_IsInertBelowGate_AndLeavesVaultsToTheExistingFreeze()
        {
            var nft = PlainNft();
            var forgedVault = VbtcTestContracts.VaultContractData(nft, _owner.Address, VictimDeposit, VictimGroupKey);
            Assert.Null(LedgerIntegrityRules.VaultNotCreatedByRewrite(Rewrite("Update()", nft, forgedVault), Gate - 1));
            Assert.NotNull(LedgerIntegrityRules.VaultNotCreatedByRewrite(Rewrite("Update()", nft, forgedVault), Gate));

            var vault = Vault(VictimDeposit, VictimGroupKey);
            var tx = Rewrite("Update()", vault, VbtcTestContracts.PlainNftContractData);
            Assert.Null(LedgerIntegrityRules.VaultNotCreatedByRewrite(tx, Gate)); // a stored vault is VaultCodeUnchanged's business
            Assert.NotNull(LedgerIntegrityRules.VaultCodeUnchanged(tx));

            Assert.Null(LedgerIntegrityRules.VaultNotCreatedByRewrite(Rewrite("Update()", "never:1", forgedVault), Gate)); // missing: the function's own rule
            Assert.True(LedgerIntegrityRules.CarriesTokenizationV2(forgedVault));
            Assert.False(LedgerIntegrityRules.CarriesTokenizationV2(VbtcTestContracts.PlainNftContractData));
            Assert.False(LedgerIntegrityRules.CarriesTokenizationV2("not a body"));
        }

        // ── Validator-local: the FROST key-share fallback ─────────────────────────────────────────────────────────

        [Fact]
        public void KeyShareFiledUnderAnotherOnChainContract_IsNeverAdopted()
        {
            var victim = Vault(VictimDeposit, VictimGroupKey);
            var forged = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();

            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(victim, forged, out var reason));
            Assert.Contains(victim, reason);

            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(victim, victim, out _));                       // its own contract
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(Guid.NewGuid().ToString("N"), forged, out _)); // a pre-NEW-26 session id
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(Guid.NewGuid().ToString(), forged, out _));    // a pre-NEW-26 session id (dashed GUID)
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(null, forged, out _));
        }

        /// <summary>
        /// Re-audit (9 Oct 2026): a NEW-26 ceremony runs under the UID of the contract it will create, so a share filed under a
        /// contract-shaped UID whose contract was never created (never signed, nothing on chain) is still that contract's
        /// share: a forged vault naming it as its ceremony id does not get it.
        /// </summary>
        [Fact]
        public void KeyShareOfANeverCreatedContract_IsNeverAdoptedByAnotherContract()
        {
            var neverCreated = FrostDkgAttestation.NewContractUid();
            Assert.Null(SmartContractStateTrei.GetSmartContractState(neverCreated));
            var forged = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();

            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(neverCreated, forged, out var reason));
            Assert.Contains(neverCreated, reason);
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(neverCreated, neverCreated, out _));

            Assert.True(FrostDkgGuard.IsContractUid(neverCreated));

            // Re-audit: a vault created with a NEW-26 DKG proof never adopts a session-id record, however old.
            var prior = FrostDkgGuard.RequestHasDkgProof;
            try
            {
                FrostDkgGuard.RequestHasDkgProof = uid => uid == forged;
                Assert.False(FrostDkgGuard.MayAdoptKeyRecord(Guid.NewGuid().ToString(), forged, out var r2));
                Assert.Contains("DKG proof", r2);
                Assert.True(FrostDkgGuard.MayAdoptKeyRecord(forged, forged, out _)); // its own UID is always fine
                var legacyVault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
                Assert.True(FrostDkgGuard.MayAdoptKeyRecord(Guid.NewGuid().ToString(), legacyVault, out _)); // a pre-NEW-26 vault may still adopt its session-id share
            }
            finally { FrostDkgGuard.RequestHasDkgProof = prior; }
            Assert.False(FrostDkgGuard.ContractHasAttestedDkgProof(forged)); // not on chain: no proof
        }

        /// <summary>
        /// Third review: the predicate must test the proof TYPE. Every legacy vault carries a DKG_COMPLETION_FROST_NATIVE proof
        /// and keeps its share under the ceremony id until first use; treating presence as "attested" locked those vaults
        /// out of signing. The real predicate runs here against chain state, no mock.
        /// </summary>
        [Fact]
        public void OnlyAnAttestedProof_MarksAVaultAsFiledUnderItsOwnUid()
        {
            var legacyProof = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                SessionId = Guid.NewGuid().ToString(), GroupPublicKey = VictimGroupKey, PubkeyPackageHash = "ab", Timestamp = TimeUtil.GetTime(),
                FrostVersion = "x", ProofType = "DKG_COMPLETION_FROST_NATIVE",
            })));
            var legacyVault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = legacyVault, ContractData = VbtcTestContracts.VaultContractData(legacyVault, _owner.Address, VictimDeposit, VictimGroupKey, dkgProof: legacyProof),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address, IsLocked = false, Nonce = 0,
            });
            var attestedVault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            var attested = FrostDkgAttestation.BuildProof(attestedVault, VictimGroupKey, VictimDeposit, _owner.Address, 2, new[] { "xV1", "xV2", "xV3" }, Array.Empty<FrostDkgAttestation.Attestation>());
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = attestedVault, ContractData = VbtcTestContracts.VaultContractData(attestedVault, _owner.Address, VictimDeposit, VictimGroupKey, dkgProof: attested),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address, IsLocked = false, Nonce = 0,
            });
            var proofless = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = proofless, ContractData = VbtcTestContracts.VaultContractData(proofless, _owner.Address, VictimDeposit, VictimGroupKey, dkgProof: null),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address, IsLocked = false, Nonce = 0,
            });

            Assert.True(FrostDkgGuard.IsAttestedDkgProof(attested));
            Assert.False(FrostDkgGuard.IsAttestedDkgProof(legacyProof));
            Assert.False(FrostDkgGuard.IsAttestedDkgProof("proof"));
            Assert.False(FrostDkgGuard.IsAttestedDkgProof(null));
            Assert.True(FrostDkgGuard.ContractHasAttestedDkgProof(attestedVault));
            Assert.False(FrostDkgGuard.ContractHasAttestedDkgProof(legacyVault));
            Assert.False(FrostDkgGuard.ContractHasAttestedDkgProof(proofless));

            var sessionId = Guid.NewGuid().ToString();
            // The attested vault never adopts a session-id record.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(sessionId, attestedVault, out var why));
            Assert.Contains("attested DKG proof", why);
            // The legacy vault keeps its path to its never-used share (group-key match is still required by the caller).
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(sessionId, legacyVault, out _));
            // A proof-less body is a legacy-shaped vault too: adoption stays group-key gated, not proof gated.
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(sessionId, proofless, out _));
            // Nothing adopts another on-chain contract's share.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(legacyVault, proofless, out _));
            Assert.True(FrostDkgGuard.IsContractUid(attestedVault));
            Assert.False(FrostDkgGuard.IsContractUid(Guid.NewGuid().ToString("N")));
            Assert.False(FrostDkgGuard.IsContractUid(Guid.NewGuid().ToString()));
            Assert.False(FrostDkgGuard.IsContractUid("vault:1"));
            Assert.False(FrostDkgGuard.IsContractUid(Guid.NewGuid().ToString("N") + ":"));
            Assert.False(FrostDkgGuard.IsContractUid(Guid.NewGuid().ToString("N") + ":12a"));
            Assert.False(FrostDkgGuard.IsContractUid(null));
        }
    }
}
