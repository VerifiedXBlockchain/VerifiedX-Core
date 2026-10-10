using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using System.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
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

        private const string Me = "xValidatorMe";

        private string LegacyVault(string groupKey, string? uid = null, string? proof = "legacy")
        {
            uid ??= Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            var dkgProof = proof == "legacy"
                ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    SessionId = Guid.NewGuid().ToString(), GroupPublicKey = groupKey, PubkeyPackageHash = "ab", Timestamp = TimeUtil.GetTime(),
                    FrostVersion = "x", ProofType = "DKG_COMPLETION_FROST_NATIVE",
                })))
                : proof;
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.VaultContractData(uid, _owner.Address, VictimDeposit, groupKey, dkgProof: dkgProof),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address, IsLocked = false, Nonce = 0,
            });
            return uid;
        }

        private static string Key(char c) => "02" + new string(c, 64);

        private static FrostValidatorKeyStore Share(string filedUnder, string groupKey, string validator = Me)
        {
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore { SmartContractUID = filedUnder, ValidatorAddress = validator, KeyPackage = "share-of-" + groupKey, PubkeyPackage = "p", GroupPublicKey = groupKey }));
            return FrostValidatorKeyStore.GetKeyPackage(filedUnder, validator)!;
        }

        [Fact]
        public void KeyShareFiledUnderAnotherOnChainContract_IsNeverAdopted()
        {
            var victim = Vault(VictimDeposit, VictimGroupKey);
            var forged = LegacyVault(VictimGroupKey, proof: null);

            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(victim, forged, VictimGroupKey, out var reason));
            Assert.Contains(victim, reason);
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(victim, victim, VictimGroupKey, out _));   // its own contract
        }

        /// <summary>
        /// Fourth review. A share still under its pre-NEW-26 session id went to whichever body asked for it, so a body that
        /// copied a legacy vault's group key was handed the share when it asked first (the earlier test asserted exactly
        /// that for a proof-less body). It now belongs to the one vault on chain that carries the key; with two carriers
        /// it is used for neither.
        /// </summary>
        [Fact]
        public void ASessionIdShare_BelongsToTheOneVaultThatCarriesItsKey()
        {
            var key = Key('a');
            var legacyVault = LegacyVault(key);
            var session = Guid.NewGuid().ToString();
            var stranger = LegacyVault(Key('b'), proof: null);

            // The vault's own share, never used: found by group key, adopted.
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(session, legacyVault, key, out _));
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(Guid.NewGuid().ToString("N"), legacyVault, key, out _));
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(null, legacyVault, key, out _));          // a record with no label is a session record
            // A contract that does not carry the key gets nothing, whatever the caller matched on.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, stranger, key, out var w0));
            Assert.Contains(legacyVault, w0);
            // A request for a contract that is not on chain gets nothing.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, Guid.NewGuid().ToString("N") + ":1", key, out _));
            // A share whose key no contract carries (its ceremony's contract was never created).
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, legacyVault, Key('c'), out var w1));
            Assert.Contains("no contract on chain", w1);
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, legacyVault, "", out _));

            // The attack: a body with no proof (or a legacy-type one) copies the vault's key and asks first.
            var forged = LegacyVault(key, proof: null);
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, forged, key, out var w2));
            Assert.Contains("2 contracts on chain carry group key", w2);
            Assert.Contains(forged, w2);
            Assert.Contains(legacyVault, w2);
            // ...and the real vault's share is not handed out either until an operator says which is which.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, legacyVault, key, out _));

            // The copy may spell the key differently: x-only, upper case, 0x. It is the same key.
            var key2 = Key('d');
            var vault2 = LegacyVault(key2);
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(session, vault2, key2, out _));
            foreach (var spelling in new[] { key2.Substring(2), key2.ToUpperInvariant(), "0x" + key2.Substring(2), " " + key2 + " ", "03" + key2.Substring(2) })
            {
                var copy = LegacyVault(spelling, proof: null);
                Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, copy, key2, out _));
                Assert.False(FrostDkgGuard.MayAdoptKeyRecord(session, vault2, key2, out _));
                SmartContractStateTrei.DeleteSmartContract(SmartContractStateTrei.GetSmartContractState(copy)!);
                Assert.True(FrostDkgGuard.MayAdoptKeyRecord(session, vault2, key2, out _));
            }
            Assert.Equal(new string('d', 64), FrostKeyShareBinding.NormalizeKey(" 0x" + new string('D', 64) + " "));
            Assert.Equal("", FrostKeyShareBinding.NormalizeKey(null));
        }

        /// <summary>
        /// The binding run (once the node is synced): every session-id share with exactly one vault for its key is filed
        /// under that vault, so a body that copies the key afterwards finds a share filed under a contract - which is
        /// never relabelled. Real key-store records and chain state throughout.
        /// </summary>
        [Fact]
        public void BindSessionShares_FilesEachShareUnderItsOneVault_AndLeavesTheAmbiguousAlone()
        {
            var vaultA = LegacyVault(Key('a'));                                         // one vault, share never used
            var shareA = Share(Guid.NewGuid().ToString(), Key('a'));
            var vaultB = LegacyVault(Key('b'));                                         // its key was copied before this node migrated
            var copyOfB = LegacyVault(Key('b').Substring(2).ToUpperInvariant(), proof: null);
            var shareB = Share(Guid.NewGuid().ToString(), Key('b'));
            var shareC = Share(Guid.NewGuid().ToString("N"), Key('c'));                 // a ceremony whose contract was never created
            var vaultD = LegacyVault(Key('d'));                                         // already filed under its vault
            var shareD = Share(vaultD, Key('d'));
            var pending = FrostDkgAttestation.NewContractUid();                          // a NEW-26 ceremony's share, contract not created yet
            var shareE = Share(pending, Key('e'));
            var vaultF = LegacyVault(Key('f'));                                         // another validator address in the same store
            var shareF = Share(Guid.NewGuid().ToString(), Key('f'), "xValidatorOld");

            var result = FrostKeyShareBinding.BindSessionShares();
            Assert.Equal(6, result.Records);
            Assert.Equal(2, result.Bound);
            Assert.Equal(2, result.FiledUnderContract);
            Assert.Equal(1, result.NoVault);
            Assert.Equal(1, result.Ambiguous);
            Assert.Equal(0, result.Failed);
            Assert.Contains(result.Notes, n => n.Contains(vaultB) && n.Contains(copyOfB) && n.Contains("NOT bound"));

            string FiledUnder(FrostValidatorKeyStore r) => FrostValidatorKeyStore.GetAllKeyPackages().Single(x => x.Id == r.Id).SmartContractUID;
            Assert.Equal(vaultA, FiledUnder(shareA));
            Assert.Equal(shareB.SmartContractUID, FiledUnder(shareB));                   // untouched
            Assert.Equal(shareC.SmartContractUID, FiledUnder(shareC));
            Assert.Equal(vaultD, FiledUnder(shareD));
            Assert.Equal(pending, FiledUnder(shareE));
            Assert.Equal(vaultF, FiledUnder(shareF));
            Assert.Equal("share-of-" + Key('a'), FrostValidatorKeyStore.GetKeyPackage(vaultA, Me)!.KeyPackage);

            // Idempotent.
            var again = FrostKeyShareBinding.BindSessionShares();
            Assert.Equal(0, again.Bound);
            Assert.Equal(4, again.FiledUnderContract);
            Assert.Equal(1, again.Ambiguous);

            // A copy of A's key made AFTER the binding gets nothing: the share is filed under vault A.
            var lateCopy = LegacyVault(Key('a'), proof: null);
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(FiledUnder(shareA), lateCopy, Key('a'), out var why));
            Assert.Contains(vaultA, why);
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(FiledUnder(shareA), vaultA, Key('a'), out _));
            Assert.Equal(0, FrostKeyShareBinding.BindSessionShares().Bound);
            Assert.Equal(vaultA, FiledUnder(shareA));

            // The ambiguous share: neither carrier gets it at signing...
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(FiledUnder(shareB), vaultB, Key('b'), out _));
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(FiledUnder(shareB), copyOfB, Key('b'), out _));
            // ...until an operator decides. Only onto a contract that carries the key; never a share already under a contract.
            Assert.False(FrostKeyShareBinding.BindByOperator(shareB.Id, vaultA).Ok);
            Assert.False(FrostKeyShareBinding.BindByOperator(shareB.Id, "not-a-contract").Ok);
            Assert.False(FrostKeyShareBinding.BindByOperator(999_999, vaultB).Ok);
            Assert.False(FrostKeyShareBinding.BindByOperator(shareB.Id, null).Ok);
            Assert.False(FrostKeyShareBinding.BindByOperator(shareA.Id, lateCopy).Ok);  // filed under vault A: never moved, same key or not
            Assert.False(FrostKeyShareBinding.BindByOperator(shareE.Id, vaultA).Ok);
            Assert.True(FrostKeyShareBinding.BindByOperator(shareB.Id, vaultB).Ok);
            Assert.Equal(vaultB, FiledUnder(shareB));
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(FiledUnder(shareB), copyOfB, Key('b'), out _));
            Assert.True(FrostKeyShareBinding.BindByOperator(shareB.Id, vaultB).Ok);     // already there
        }

        /// <summary>
        /// Fifth review: after an upgrade each validator checks its key store. The answer is one line. "Nothing under a
        /// session id" is not the test: a share whose ceremony never produced a contract stays there and is normal. The
        /// test is nothing ambiguous and nothing still waiting to be bound.
        /// </summary>
        [Fact]
        public void TheKeystoreReport_SaysWhetherAnOperatorHasAnythingToDo()
        {
            var priorSynced = Globals.IsChainSynced;
            FrostKeyShareBinding.ResetForTests();
            try
            {
                Globals.IsChainSynced = false;
                var empty = FrostKeyShareBinding.Report(Me);
                Assert.True(empty.Ok);
                Assert.Equal(0, empty.Records);

                var vaultA = LegacyVault(Key('a'));
                var shareA = Share(Guid.NewGuid().ToString(), Key('a'));
                var shareC = Share(Guid.NewGuid().ToString("N"), Key('c'));               // its ceremony's contract was never created
                var vaultD = LegacyVault(Key('d'));
                Share(vaultD, Key('d'));
                Share(Guid.NewGuid().ToString(), Key('f'), "xValidatorOld");              // another validator's row: not in this one's report

                // Before the node is synced: a share is waiting, and the report says wait, not act.
                var before = FrostKeyShareBinding.Report(Me);
                Assert.False(before.Ok);
                Assert.Equal(3, before.Records);
                Assert.Equal(1, before.PendingBind);
                Assert.Equal(1, before.NoVault);
                Assert.StartsWith("WAIT", before.Verdict);
                Assert.Equal(FrostKeyShareBinding.StateSessionPendingBind, before.Keys.Single(k => k.Id == shareA.Id).State);
                Assert.Equal(new[] { vaultA }, before.Keys.Single(k => k.Id == shareA.Id).ContractsCarryingKey);
                Assert.Equal(4, FrostKeyShareBinding.Report().Records);                   // no filter: every row

                // After the binding: OK, with the never-created ceremony's share still under its session id.
                Globals.IsChainSynced = true;
                Assert.Equal(1, FrostKeyShareBinding.BindSessionShares().Bound);          // (the unfiltered run also sees the other validator's row: no vault for it)
                var after = FrostKeyShareBinding.Report(Me);
                Assert.True(after.Ok);
                Assert.True(after.BindingHasRun);
                Assert.StartsWith("OK", after.Verdict);
                Assert.Contains("which is normal", after.Verdict);
                Assert.Equal(1, after.UnderSessionId);
                Assert.Equal(1, after.NoVault);
                Assert.Equal(0, after.PendingBind);
                Assert.Equal(FrostKeyShareBinding.StateSessionNoVault, after.Keys.Single(k => k.Id == shareC.Id).State);
                Assert.Equal(FrostKeyShareBinding.StateUnderContract, after.Keys.Single(k => k.Id == shareA.Id).State);

                // A key copied AFTER the binding: the share is safe under its vault; the report notes the copy and stays OK.
                var lateCopy = LegacyVault(Key('a'), proof: null);
                var copied = FrostKeyShareBinding.Report(Me);
                Assert.True(copied.Ok);
                Assert.Equal(1, copied.KeyCopied);
                Assert.Equal(FrostKeyShareBinding.StateUnderContractKeyCopied, copied.Keys.Single(k => k.Id == shareA.Id).State);
                Assert.Contains(lateCopy, copied.Keys.Single(k => k.Id == shareA.Id).Status);

                // A key copied BEFORE a share was bound: ambiguous, and the operator is told what to do.
                var vaultB = LegacyVault(Key('b'));
                var copyOfB = LegacyVault(Key('b'), proof: null);
                var shareB = Share(Guid.NewGuid().ToString(), Key('b'));
                Assert.Equal(0, FrostKeyShareBinding.BindSessionShares().Bound);
                var ambiguous = FrostKeyShareBinding.Report(Me);
                Assert.False(ambiguous.Ok);
                Assert.Equal(1, ambiguous.Ambiguous);
                Assert.StartsWith("ACTION NEEDED", ambiguous.Verdict);
                Assert.Contains("/frost/keystore/bind/", ambiguous.Verdict);
                var rowB = ambiguous.Keys.Single(k => k.Id == shareB.Id);
                Assert.Equal(FrostKeyShareBinding.StateSessionAmbiguous, rowB.State);
                Assert.Contains(vaultB, rowB.ContractsCarryingKey);
                Assert.Contains(copyOfB, rowB.ContractsCarryingKey);

                Assert.True(FrostKeyShareBinding.BindByOperator(shareB.Id, vaultB).Ok);
                var resolved = FrostKeyShareBinding.Report(Me);
                Assert.True(resolved.Ok);
                Assert.Equal(2, resolved.KeyCopied);

                // A share that could be bound but was not, on a synced node whose binding has run: act, do not wait.
                var vaultE = LegacyVault(Key('e'));
                Share(Guid.NewGuid().ToString(), Key('e'));
                var late = FrostKeyShareBinding.Report(Me);
                Assert.False(late.Ok);
                Assert.StartsWith("ACTION NEEDED", late.Verdict);
                Assert.Contains("/frost/keystore/migrate", late.Verdict);
                Assert.Equal(1, FrostKeyShareBinding.BindSessionShares().Bound);
                Assert.True(FrostKeyShareBinding.Report(Me).Ok);
                Assert.NotNull(FrostValidatorKeyStore.GetKeyPackage(vaultE, Me));

                // Chain state unreadable: the report does not say OK.
                var priorKeys = FrostKeyShareBinding.OnChainVaultKeys;
                try
                {
                    FrostKeyShareBinding.OnChainVaultKeys = () => throw new InvalidOperationException("state unavailable");
                    var unknown = FrostKeyShareBinding.Report(Me);
                    Assert.False(unknown.Ok);
                    Assert.StartsWith("UNKNOWN", unknown.Verdict);
                }
                finally { FrostKeyShareBinding.OnChainVaultKeys = priorKeys; }
            }
            finally
            {
                Globals.IsChainSynced = priorSynced;
                FrostKeyShareBinding.ResetForTests();
            }
        }

        /// <summary>The binding waits for the node to be at the network's height, and runs once per process.</summary>
        [Fact]
        public async Task TheBindingRun_WaitsForSync_AndRunsOnce()
        {
            var priorSynced = Globals.IsChainSynced;
            FrostKeyShareBinding.ResetForTests();
            try
            {
                var vault = LegacyVault(Key('a'));
                var share = Share(Guid.NewGuid().ToString(), Key('a'));
                Globals.IsChainSynced = false;
                var run = FrostKeyShareBinding.RunOnceWhenSyncedAsync(default, pollMilliseconds: 20);
                await Task.Delay(150);
                Assert.False(run.IsCompleted);
                Assert.Equal(share.SmartContractUID, FrostValidatorKeyStore.GetAllKeyPackages().Single().SmartContractUID); // not while catching up

                Globals.IsChainSynced = true;
                var result = await run;
                Assert.NotNull(result);
                Assert.Equal(1, result!.Bound);
                Assert.Equal(vault, FrostValidatorKeyStore.GetAllKeyPackages().Single().SmartContractUID);
                Assert.Null(await FrostKeyShareBinding.RunOnceWhenSyncedAsync(default, pollMilliseconds: 20));      // once
            }
            finally
            {
                Globals.IsChainSynced = priorSynced;
                FrostKeyShareBinding.ResetForTests();
            }
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
            var forged = LegacyVault(VictimGroupKey, proof: null);                      // the only contract carrying the key

            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(neverCreated, forged, VictimGroupKey, out var reason));
            Assert.Contains(neverCreated, reason);
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(neverCreated, neverCreated, VictimGroupKey, out _));
            Assert.True(FrostDkgGuard.IsContractUid(neverCreated));
            Assert.False(FrostDkgGuard.ContractHasAttestedDkgProof(Guid.NewGuid().ToString("N") + ":1")); // not on chain: no proof
        }

        /// <summary>
        /// Third review: the predicate must test the proof TYPE. Every legacy vault carries a DKG_COMPLETION_FROST_NATIVE proof
        /// and keeps its share under the ceremony id until first use; treating presence as "attested" locked those vaults
        /// out of signing. The real predicate runs here against chain state, no mock.
        /// </summary>
        [Fact]
        public void OnlyAnAttestedProof_MarksAVaultAsFiledUnderItsOwnUid()
        {
            var legacyKey = Key('1');
            var legacyVault = LegacyVault(legacyKey);
            var attestedKey = Key('2');
            var attestedVault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            var attested = FrostDkgAttestation.BuildProof(attestedVault, attestedKey, VictimDeposit, _owner.Address, 2, new[] { "xV1", "xV2", "xV3" }, Array.Empty<FrostDkgAttestation.Attestation>());
            LegacyVault(attestedKey, attestedVault, attested);
            var prooflessKey = Key('3');
            var proofless = LegacyVault(prooflessKey, proof: null);

            Assert.True(FrostDkgGuard.IsAttestedDkgProof(attested));
            Assert.False(FrostDkgGuard.IsAttestedDkgProof("proof"));
            Assert.False(FrostDkgGuard.IsAttestedDkgProof(null));
            Assert.True(FrostDkgGuard.ContractHasAttestedDkgProof(attestedVault));
            Assert.False(FrostDkgGuard.ContractHasAttestedDkgProof(legacyVault));
            Assert.False(FrostDkgGuard.ContractHasAttestedDkgProof(proofless));

            var sessionId = Guid.NewGuid().ToString();
            // The attested vault never adopts a session-id record, even as the only carrier of the key.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(sessionId, attestedVault, attestedKey, out var why));
            Assert.Contains("attested DKG proof", why);
            // The legacy vault keeps its path to its never-used share: it is the one vault carrying the key.
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(sessionId, legacyVault, legacyKey, out _));
            // A proof-less body that is the one carrier of ITS key is a legacy-shaped vault with its own share.
            Assert.True(FrostDkgGuard.MayAdoptKeyRecord(sessionId, proofless, prooflessKey, out _));
            // The same body gets nothing for a key another vault carries.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(sessionId, proofless, legacyKey, out _));
            // Nothing adopts another on-chain contract's share.
            Assert.False(FrostDkgGuard.MayAdoptKeyRecord(legacyVault, proofless, legacyKey, out _));
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
