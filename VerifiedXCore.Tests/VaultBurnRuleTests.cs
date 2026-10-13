using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// A vBTC V2 vault is never burned, and a vault's UID is never created twice (path audit, 12 October 2026).
    ///
    /// Burn() deletes a contract's state record. For a vault that record holds the whole ledger: what every holder has,
    /// and every debit its owner ever made. What is kept beside it under the vault's UID stays: completed withdrawals
    /// (added back to the owner's balance), bridge locks, attested coins. And the creation transaction's proof named no
    /// time, so it could be sent again. An owner could therefore burn its vault and create it again: the holders'
    /// vBTC gone, its own debits gone, its balance back to the whole deposit plus what it had already withdrawn.
    ///
    /// Transactions are judged as block validation judges them (VerifyTX at a block height) and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VaultBurnRuleTests : IDisposable
    {
        private const long DkgRule = 50;
        private const long BurnRule = 200;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorDkgRule = Globals.VbtcV2DkgAttestationHeight;
        private readonly long _priorBurnRule = Globals.VaultBurnRuleHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly List<(PrivateKey Key, string Pub, string Address)> _validators = Enumerable.Range(0, 10).Select(_ => NewKey()).ToList();
        private readonly string _groupKey;
        private readonly string _address;

        public VaultBurnRuleTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vaultburn_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 99 };
            Globals.VbtcV2DkgAttestationHeight = DkgRule;
            Globals.VaultBurnRuleHeight = BurnRule;
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

        /// <summary>A vault creation with a valid key-ceremony proof: every validator took part and signed for this UID, key, address and owner.</summary>
        private Transaction Create(string uid)
        {
            var participants = _validators.Select(v => v.Address).ToList();
            var threshold = FrostDkgAttestation.ThresholdFor(participants.Count, 51);
            var proof = FrostDkgAttestation.BuildProof(uid, _groupKey, _address, _owner.Address, threshold, participants, _validators.Select(v => new FrostDkgAttestation.Attestation
            {
                ValidatorAddress = v.Address,
                Signature = SignatureService.CreateSignature(FrostDkgAttestation.Message(uid, _groupKey, _address, _owner.Address, threshold, participants), v.Key, v.Pub),
            }));
            var body = VbtcTestContracts.BuildContractData(uid, _owner.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.TokenizationV2,
                    FeatureFeatures = new TokenizationV2Feature
                    {
                        AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = _address, Version = 2, ValidatorAddressesSnapshot = participants,
                        FrostGroupPublicKey = _groupKey, RequiredThreshold = 51, DKGProof = proof, ProofBlockHeight = 1, CeremonyId = uid, ImageBase = "default",
                    },
                },
            }, name: "vBTC");
            return Signed(TransactionType.VBTC_V2_CONTRACT_CREATE, new[] { new { Function = "Mint()", ContractUID = uid, Data = body, MD5List = "NA" } });
        }

        private Transaction Burn(string uid, TransactionType type = TransactionType.NFT_BURN) =>
            Signed(type, new[] { new { Function = "Burn()", ContractUID = uid } });

        private Transaction Signed(TransactionType type, object data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
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
            var create = Create(uid);
            var (ok, why) = await Verify(create, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, create);
            Assert.True(VBTCService.IsVbtcV2Contract(SmartContractStateTrei.GetSmartContractState(uid)!));
            return uid;
        }

        // ── Burning ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AVaultIsNotBurned_FromTheRuleHeight_UnderAnyTransactionType()
        {
            var uid = await CreateVault();

            // Before the rule, as it always was: the owner could burn it, and with it everything every holder had.
            var (before, _) = await Verify(Burn(uid), BurnRule - 1);
            Assert.True(before);

            foreach (var type in new[] { TransactionType.NFT_BURN, TransactionType.NFT_TX, TransactionType.TKNZ_BURN })
            {
                var (ok, why) = await Verify(Burn(uid, type), BurnRule);
                Assert.False(ok, $"a vault burn was accepted as {type}");
                Assert.Contains("vBTC V2 vault cannot be burned", why);
            }
            // The rule is about vaults: any other contract of the owner is burned as before.
            var plainUid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = plainUid, ContractData = VbtcTestContracts.PlainNftContractData, MinterAddress = _owner.Address, OwnerAddress = _owner.Address, Nonce = 0,
            });
            var (plain, plainWhy) = await Verify(Burn(plainUid), BurnRule);
            Assert.True(plain, plainWhy);
        }

        // ── Creating it again ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AVaultBurnedBeforeTheRule_IsNotCreatedAgain_OnceItHasWithdrawalsOrBridgeLocks()
        {
            var uid = await CreateVault();
            // It was used: a withdrawal was mined against it and a holder locked vBTC of it for the bridge. Both are
            // kept under the vault's UID, outside the record a burn deletes.
            var lockTx = Signed(TransactionType.VBTC_V2_BRIDGE_LOCK, new { Function = "VBTCBridgeLock()", ContractUID = uid });
            Assert.True(VBTCBridgeLockState.TryInsertFromLockTx(lockTx, uid, Guid.NewGuid().ToString("N"), 0.5M, 50_000_000, "0x" + new string('a', 40)));

            // The owner burns it while that was still possible.
            await Apply(Tip + 1, Burn(uid));
            Assert.Null(SmartContractStateTrei.GetSmartContractState(uid));

            // Before the rule the same creation was accepted again: an empty ledger over the same Bitcoin and the same locks.
            var (before, _) = await Verify(Create(uid), BurnRule - 1);
            Assert.True(before);
            var (ok, why) = await Verify(Create(uid), BurnRule);
            Assert.False(ok);
            Assert.Contains("a vault's UID is never created twice", why);
        }

        [Fact]
        public async Task AVaultsCreation_CannotBeSentAgainLater_ItsUidSaysWhenItWasMade()
        {
            // A UID is "id:time". A vault's creation follows its key ceremony within minutes.
            var old = Guid.NewGuid().ToString("N") + ":" + (TimeUtil.GetTime() - 8 * 24 * 3600);
            var (ok, why) = await Verify(Create(old), BurnRule);
            Assert.False(ok);
            Assert.Contains("was not made within the last 7 days", why);

            var (future, futureWhy) = await Verify(Create(Guid.NewGuid().ToString("N") + ":" + (TimeUtil.GetTime() + 2 * 3600)), BurnRule);
            Assert.False(future);
            Assert.Contains("was not made within the last 7 days", futureWhy);

            // (A UID that does not end in a time at all is refused by the UID format rule, as before.)
            // A vault made now is created as before, also under the rule.
            var (fresh, freshWhy) = await Verify(Create(FrostDkgAttestation.NewContractUid()), BurnRule);
            Assert.True(fresh, freshWhy);
            // And before the rule nothing about the UID was asked.
            var (was, _) = await Verify(Create(old), BurnRule - 1);
            Assert.True(was);
        }

        [Fact]
        public async Task ADepositAddressBelongsToOneVault()
        {
            // Another vault is watched at this address already.
            var script = NBitcoin.BitcoinAddress.Create(_address, NBitcoin.Network.Main).ScriptPubKey.ToHex().ToLowerInvariant();
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveScript(new VerifiedXCore.Bitcoin.Attestation.VaultDepositScriptRow
            {
                ScriptHex = script, SmartContractUID = "0123456789abcdef0123456789abcdef:1700000000", DepositAddress = _address, CreatedAtVfxHeight = 90, WatchFromBitcoinHeight = 1,
            });
            var (ok, why) = await Verify(Create(FrostDkgAttestation.NewContractUid()), Tip + 1);
            Assert.False(ok);
            Assert.Contains("already belongs to vault", why);
        }
    }
}
