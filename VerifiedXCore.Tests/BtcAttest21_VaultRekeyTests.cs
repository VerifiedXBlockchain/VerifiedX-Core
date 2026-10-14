using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxBlock = VerifiedXCore.Models.Block;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A vault outlives the validators that made its key (third pass, 14 October 2026; plan 8.8).
    ///
    /// A vault's Bitcoin sits under a threshold key whose shares the validators of its creation hold. When fewer of them
    /// run than sign together, nothing of the vault can ever be signed again: a withdrawal fixed on its coins can be
    /// neither paid nor given up, and the Bitcoin is lost. Every epoch the chain counts each key's holders that are still
    /// active with their stake bonded; when half of those it can spare are gone the key is due, the validators of the day
    /// make a new one by the rule a creation follows, and the old key's coins are moved to it by transactions fixed by
    /// rule and signed by the old key's holders.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest21_VaultRekeyTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly Network? _priorNetwork = Globals.BTCNetwork;

        public BtcAttest21_VaultRekeyTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            BtcVaultKeyService.ForgetAttempts();
        }

        public override void Dispose()
        {
            Globals.BTCNetwork = _priorNetwork;
            BtcVaultKeyService.ForgetAttempts();
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;

        // ── Bitcoin side ─────────────────────────────────────────────────────────────

        private async Task<BtcTx> Deposit(NBitcoin.Script vault, long sats)
        {
            var tx = Pay(vault, sats);
            await Report(_btc.Mine(tx));
            return tx;
        }

        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            var report = Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, BtcAttestRules.EpochOf(Tip + 1)));
            var tx = Signed(report);
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
        }

        private async Task<BtcTx> SignAndConfirm(VaultPin pin)
        {
            var tx = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(pin.Txid, tx.GetHash().ToString());
            await Report(_btc.Mine(tx));
            return tx;
        }

        // ── VFX side ─────────────────────────────────────────────────────────────────

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private VfxTx Request(string vault, decimal amount, int feeRate = 10) =>
            SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString("N") });

        private async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private static VaultWithdrawalRow Row(VfxTx request, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(request.Hash, vault))!;

        private static VfxTx Lifecycle(V v, TransactionType type, long height) => new()
        {
            TransactionType = type, FromAddress = v.Address, ToAddress = v.Address, Hash = $"{type}-{height}-{v.Address}",
            Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.0.1", FrostPublicKey = v.Pub, IsS3C = false }),
        };

        /// <summary>
        /// Moves the chain to the next epoch's first block the way a node gets there: the validators in
        /// <paramref name="running"/> announce themselves on the way (the registry counts what the blocks hold), those in
        /// <paramref name="leaving"/> send their exit.
        /// </summary>
        private async Task ToNextEpoch(IEnumerable<V> running, IEnumerable<V>? leaving = null)
        {
            var epochStart = H1 + ((Tip - H1) / BtcAttestRules.EpochBlocks + 1) * BtcAttestRules.EpochBlocks;
            var h = epochStart - 20;
            foreach (var v in running)
            {
                BlockchainData.GetBlocks().InsertSafe(new VfxBlock { Height = h, Hash = $"h{h}-{Guid.NewGuid():N}", Transactions = new List<VfxTx> { Lifecycle(v, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, h) } });
                h++;
            }
            foreach (var v in leaving ?? Enumerable.Empty<V>())
            {
                BlockchainData.GetBlocks().InsertSafe(new VfxBlock { Height = h, Hash = $"h{h}-{Guid.NewGuid():N}", Transactions = new List<VfxTx> { Lifecycle(v, TransactionType.VBTC_V2_VALIDATOR_EXIT, h) } });
                h++;
            }
            await Apply(epochStart);
        }

        /// <summary>A key ceremony as validators run it: a new key UID, a real key, and each participant's attestation.</summary>
        private static (string KeyUid, string Address, NBitcoin.Script Script, string Proof) Ceremony(string rekeyOf, IEnumerable<V> participants, int threshold, string leader, string? uidOfVault = null)
        {
            // The UID the chain fixes for the vault's next key (its due key's NextKeyHex, and a time).
            var current = BtcAttestStore.ScriptRowForVault(uidOfVault ?? rekeyOf);
            var keyUid = current != null ? BtcVaultKeys.NewKeyUid(current) : FrostDkgAttestation.NewContractUid();
            var groupKey = Convert.ToHexString(new Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var address = FrostDkgAttestation.DeriveTaprootAddress(groupKey, FrostDkgAttestation.ConsensusNetwork)!;
            var list = participants.ToList();
            var canonical = FrostDkgAttestation.Canonical(list.Select(p => p.Address));
            var keyPackageHash = FrostDkgAttestation.KeyPackageHash("pkp|" + keyUid); // the package every participant finished with
            var message = FrostDkgAttestation.Message(keyUid, groupKey, address, leader, threshold, canonical, rekeyOf, keyPackageHash);
            var proof = FrostDkgAttestation.BuildProof(keyUid, groupKey, address, leader, threshold, canonical,
                list.Select(p => new FrostDkgAttestation.Attestation { ValidatorAddress = p.Address, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, p.Key, p.Pub) }), keyPackageHash);
            return (keyUid, address, ScriptOf(address), proof);
        }

        private static VfxTx RekeyTx(string vault, string proof) =>
            BtcAttestTxValidator.Create(TransactionType.VBTC_V2_VAULT_REKEY, new BtcRekey { ContractUID = vault, DKGProof = proof }.ToCanonicalJson());

        private static async Task Refused(VfxTx tx, string because)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.False(ok, $"accepted; expected: {because}");
            Assert.Contains(because, why);
        }

        private static FrostSigningStartRequest Announce(string keyUid, NBitcoin.Script coinScript, string reference, BtcTx tx, string leader)
        {
            var prevouts = tx.Inputs.Select(i =>
            {
                var coin = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N));
                return new PinnedWithdrawalCoin { TxId = i.PrevOut.Hash.ToString(), Vout = i.PrevOut.N, ValueSats = (ulong)(coin?.ValueSats ?? 77_777), ScriptPubKeyHex = coinScript.ToHex() };
            }).ToList();
            var spent = prevouts.Select(p => new TxOut(Money.Satoshis((long)p.ValueSats), NBitcoin.Script.FromHex(p.ScriptPubKeyHex))).ToArray();
            var precomputed = tx.PrecomputeTransactionData(spent);
            var sighashes = Enumerable.Range(0, tx.Inputs.Count)
                .Select(i => Convert.ToHexString(tx.GetSignatureHashTaproot(precomputed, new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant())
                .ToList();
            return new FrostSigningStartRequest
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = keyUid, WithdrawalRequestHash = reference, LeaderAddress = leader,
                MessageHash = sighashes[0], InputIndex = 0, InputCount = tx.Inputs.Count, AllInputSighashes = sighashes,
                UnsignedTxHex = tx.ToHex(), Prevouts = prevouts, Timestamp = TimeUtil.GetTime(),
            };
        }

        private static async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

        // ── Health ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AKey_IsDueWhenHalfOfTheHoldersItCanSpareAreGone_AndCountedAgainEveryEpoch()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(0, key.Generation);
            Assert.Equal(vault.Uid, key.KeyUID);
            Assert.Equal(Signers.Select(s => s.Address).OrderBy(a => a, StringComparer.Ordinal), key.Holders);
            Assert.Equal(3, BtcVaultKeys.MinSigners(key));              // 51% of four, rounded up
            Assert.Equal(-1, key.HealthyHolders);                       // not looked at before the first epoch
            Assert.Equal(1, BtcVaultKeys.SpareBeforeRekey(4, 3));
            Assert.Equal(17, BtcVaultKeys.SpareBeforeRekey(100, 67));

            // All four still run: counted, not due.
            await ToNextEpoch(Signers);
            key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(4, key.HealthyHolders);
            Assert.Equal(0, key.RekeyDueSinceVfxHeight);
            var lookedAt = key.LastModifiedHeight;

            // One sends its exit: three still sign, but the key has nothing left to spare. Due from that epoch on.
            await ToNextEpoch(Signers.Take(3), leaving: Signers.Skip(3));
            key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(3, key.HealthyHolders);
            Assert.Equal(Tip, key.RekeyDueSinceVfxHeight);
            Assert.True(key.LastModifiedHeight > lookedAt);
            Assert.Null(BtcVaultKeys.CannotSignRefusal(vault.Uid));     // still signs: withdrawals go on
            var dueAt = key.RekeyDueSinceVfxHeight;

            // Nothing changed at the next epoch: the row is not written again, and the key stays due from the same block.
            var stamped = key.LastModifiedHeight;
            await ToNextEpoch(Signers.Take(3));
            key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(stamped, key.LastModifiedHeight);
            Assert.Equal(dueAt, key.RekeyDueSinceVfxHeight);
        }

        [Fact]
        public async Task AKeyThatCanNoLongerSign_TakesNoNewWithdrawalAndNoBridgeLock()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 300_000);
            await Mine(Request(vault.Uid, 0.0005M));                  // before: accepted and fixed

            await ToNextEpoch(Signers.Take(2), leaving: Signers.Skip(2));
            var key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(2, key.HealthyHolders);
            Assert.Contains("needs to sign", BtcVaultKeys.CannotSignRefusal(vault.Uid));
            await Refused(Request(vault.Uid, 0.0005M), "nothing can be paid from it until it is moved to a new key");
            var lockTx = SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault.Uid, LockId = Guid.NewGuid().ToString("N"), Amount = 0.001M, AmountSats = 100_000L, EvmDestination = "0x000000000000000000000000000000000000dEaD" });
            await Refused(lockTx, "nothing can be paid from it until it is moved to a new key");
        }

        // ── Re-key ───────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ADueKey_IsReplacedByTheValidatorsOfTheDay_UnderTheRuleACreationFollows()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 200_000);
            var stillHere = Signers.Take(3).ToList();
            var ceremony = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address);

            // Not due yet: refused.
            await Refused(RekeyTx(vault.Uid, ceremony.Proof), "is not due for replacement");

            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            Assert.True(BtcAttestStore.ScriptRowForVault(vault.Uid)!.RekeyDueSinceVfxHeight > 0);
            ceremony = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address); // made now that it is due: its UID is the one fixed for it

            // What a creation's ceremony must be, a re-key's must be: nearly every eligible validator, each attesting.
            await Refused(RekeyTx(vault.Uid, Ceremony(vault.Uid, stillHere.Take(2), 3, Signers[0].Address).Proof), "must list 3 to 512 distinct participants");
            await Refused(RekeyTx(vault.Uid, Ceremony(vault.Uid, Signers, 3, Signers[0].Address).Proof), "is not an eligible validator"); // the one that left
            var forged = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address);
            var proof = FrostDkgAttestation.ParseProof(forged.Proof)!;
            proof.Attestations[0].Signature = proof.Attestations[1].Signature;
            await Refused(RekeyTx(vault.Uid, FrostDkgAttestation.BuildProof(proof.ContractUID, proof.GroupPublicKey, proof.TaprootAddress, proof.Owner, proof.Threshold, proof.Participants, proof.Attestations, proof.PublicKeyPackageHash)), "has no valid attestation");
            // The new key's UID is its own: not the vault's, not a contract's.
            var asVault = FrostDkgAttestation.ParseProof(Ceremony(vault.Uid, stillHere, 2, Signers[0].Address).Proof)!;
            await Refused(RekeyTx(vault.Uid, BuildWithUid(asVault, vault.Uid, stillHere)), "the new key's UID is the vault's own");
            // And the vault is named exactly; an S3C vault's key is not the chain's to move.
            var s3c = await CreateS3CVault(Tip + 1);
            await Refused(RekeyTx(vault.Uid.ToUpperInvariant(), ceremony.Proof), "does not match the stored contract");
            await Refused(RekeyTx(s3c, ceremony.Proof), "is not due for replacement");

            var rekey = RekeyTx(vault.Uid, ceremony.Proof);
            var (ok, why) = await Verify(rekey, Tip + 1);
            Assert.True(ok, why);
            // One re-key of a vault a block.
            var state = new BtcAttestBlockRules.State();
            Assert.True(BtcAttestBlockRules.TryRegister(rekey, state).Ok);
            Assert.Equal(BtcAttestBlockRules.OneRekeyReason, BtcAttestBlockRules.TryRegister(RekeyTx(vault.Uid, Ceremony(vault.Uid, stillHere, 2, Signers[1].Address).Proof), state).Reason);
            await Apply(Tip + 1, rekey);

            var keys = BtcAttestStore.ScriptRowsForVault(vault.Uid);
            Assert.Equal(2, keys.Count);
            Assert.Equal(Tip, keys[0].RetiredAtVfxHeight);
            var current = keys[1];
            Assert.Equal(current.ScriptHex, BtcAttestStore.ScriptRowForVault(vault.Uid)!.ScriptHex);
            Assert.Equal(1, current.Generation);
            Assert.Equal(ceremony.KeyUid, current.KeyUID);
            Assert.Equal(ceremony.Address, current.DepositAddress);
            Assert.Equal(stillHere.Select(s => s.Address).OrderBy(a => a, StringComparer.Ordinal), current.Holders);
            Assert.Equal(2, current.MinSigners);           // 67% of three, never all three (RekeyThreshold)
            Assert.Equal(vault.Uid, BtcVaultKeys.VaultOf(ceremony.KeyUid));
            Assert.Equal(current.Holders, BtcWithdrawalSigner.SignersOf(ceremony.KeyUid));
            Assert.Equal(Signers.Count, BtcWithdrawalSigner.SignersOf(vault.Uid).Count); // the vault's UID still names its first key

            // Once: the vault is not due any more, and the key UID is taken.
            await Refused(RekeyTx(vault.Uid, Ceremony(vault.Uid, stillHere, 2, Signers[0].Address).Proof), "is not due for replacement");
            Assert.Equal(current.GroupKeyHex, FrostDkgGuard.ResolveOnChainGroupPublicKey(ceremony.KeyUid));

            // The API shows the vault at its new address, with both keys.
            var view = JObject.FromObject(BtcVaultView.Vault(vault.Uid)!);
            Assert.Equal(ceremony.Address, (string)view["DepositAddress"]!);
            Assert.Equal(2, ((JArray)view["Keys"]!).Count);
            Assert.True((bool)view["Keys"]![1]!["Current"]!);
        }

        private static string BuildWithUid(FrostDkgAttestation.Proof template, string uid, IEnumerable<V> participants)
        {
            var list = participants.ToList();
            var canonical = FrostDkgAttestation.Canonical(list.Select(p => p.Address));
            var message = FrostDkgAttestation.Message(uid, template.GroupPublicKey, template.TaprootAddress, template.Owner, template.Threshold, canonical);
            return FrostDkgAttestation.BuildProof(uid, template.GroupPublicKey, template.TaprootAddress, template.Owner, template.Threshold, canonical,
                list.Select(p => new FrostDkgAttestation.Attestation { ValidatorAddress = p.Address, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, p.Key, p.Pub) }));
        }

        private async Task<string> CreateS3CVault(long height)
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var body = VbtcTestContracts.VaultContractData(uid, Minter.Address, NewTaprootAddress(), isS3C: true, snapshot: Signers.Select(s => s.Address));
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = Minter.Address, ToAddress = Minter.Address, Amount = 0M, Fee = 0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = uid, Data = body, MD5List = "NA" } }),
            };
            tx.Build();
            await Apply(height, tx);
            return uid;
        }

        // ── Sweep ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheOldKeysCoins_AreMovedByRule_TheFeeFallsOnTheOwner_AndWithdrawalsGoOnUnderTheNewKey()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var coin = await Deposit(vault.Script, 200_000);
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var ownerBefore = await Spendable(vault.Uid, Minter);
            var ceremony = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address);
            await Mine(RekeyTx(vault.Uid, ceremony.Proof));

            // The sweep is fixed in the re-key's own block: the old key's coin, one output to the new key.
            var sweep = Assert.Single(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            var pin = Assert.Single(sweep.Pins);
            Assert.Equal(new[] { Outpoint(coin, 0) }, pin.Inputs);
            var fee = BtcCanonicalWithdrawal.VSize(1) * BtcWithdrawals.SweepFeeRate;
            Assert.Equal(fee, pin.FeeSats);
            Assert.Equal(200_000 - fee, pin.DestinationSats);
            Assert.Equal(ceremony.Script.ToHex(), sweep.DestinationScriptHex);
            Assert.Equal(VaultCoinStatus.Pinned, BtcAttestStore.Coin(Outpoint(coin, 0))!.Status);
            // Nobody may cancel, raise or release it; the API names it.
            Assert.Equal("Sweep", (string)JObject.FromObject(BtcVaultView.Vault(vault.Uid)!)["OpenWithdrawals"]![0]!["Kind"]!);

            // Signed by the OLD key's holders, under the vault's own UID (the first key's), for that transaction only.
            var sweepTx = BtcTx.Parse(pin.TxHex, Network.Main);
            BtcWithdrawalSigner.SettleBlocksOverride = 0;
            var (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, sweep.Ref, sweepTx, Signers[3].Address));
            Assert.True(ok, why); // the one that left still holds its share of the old key, and may lead
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(ceremony.KeyUid, vault.Script, sweep.Ref, sweepTx, Signers[0].Address));
            Assert.False(ok); // the new key signs nothing of the old key's coins
            Assert.Contains("does not spend the contract deposit address", why);
            Assert.Equal(BtcAttestStore.ScriptRowsForVault(vault.Uid)[0].ScriptHex, BtcVaultKeys.KeyOfCoins(pin.Inputs)!.ScriptHex);

            // Bitcoin shows it: settled, and the vault's coin is now under the new key.
            await SignAndConfirm(pin);
            Assert.Equal(VaultWithdrawalState.Settled, BtcAttestStore.Withdrawal(sweep.Ref)!.State);
            var moved = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(pin.Txid, 0))!;
            Assert.Equal(200_000 - fee, moved.ValueSats);
            Assert.Equal(ceremony.Script.ToHex(), moved.ScriptHex);
            Assert.Equal(VaultCoinStatus.Unspent, moved.Status);
            var total = BtcAttestStore.Total(vault.Uid);
            Assert.Equal(200_000 - fee, total.HeldSats);
            Assert.Equal(fee, total.SweepFeeSats);
            Assert.Equal(ownerBefore - fee / BtcAttestBalance.SatsPerBtc, await Spendable(vault.Uid, Minter));

            // A withdrawal now: built from the new key's coin, its change back to the new key, signed by the new key's holders.
            var request = await Mine(Request(vault.Uid, 0.0005M));
            var wpin = Assert.Single(Row(request, vault.Uid).Pins);
            Assert.Equal(new[] { moved.Outpoint }, wpin.Inputs);
            var paying = BtcTx.Parse(wpin.TxHex, Network.Main);
            Assert.Equal(ceremony.Script, paying.Outputs[1].ScriptPubKey);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(ceremony.KeyUid, ceremony.Script, request.Hash, paying, Signers[1].Address));
            Assert.True(ok, why);
            (ok, _) = FrostSigningAuthorization.Authorize(Announce(ceremony.KeyUid, ceremony.Script, request.Hash, paying, Signers[3].Address));
            Assert.False(ok); // not a holder of the new key
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, ceremony.Script, request.Hash, paying, Signers[1].Address));
            Assert.False(ok); // the vault's UID names its first key, which does not hold this coin
            await SignAndConfirm(wpin);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(ceremony.Script.ToHex(), BtcAttestStore.Coin(BtcAttestStore.OutpointKey(wpin.Txid, 1))!.ScriptHex);
        }

        [Fact]
        public async Task AWithdrawalFixedBeforeTheReKey_IsPaidUnderItsOldKey_AndWhatComesBackThereIsMovedOn()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 200_000);
            var request = await Mine(Request(vault.Uid, 0.0005M));
            var oldPin = Assert.Single(Row(request, vault.Uid).Pins);

            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var ceremony = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address);
            await Mine(RekeyTx(vault.Uid, ceremony.Proof));
            // Every coin of the old key is held by the withdrawal: nothing to sweep yet.
            Assert.Empty(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            // A new withdrawal waits for the new key's coins: it is not mixed onto the old key's.
            var waiting = await Mine(Request(vault.Uid, 0.0002M));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);

            // The old withdrawal is paid as fixed; its change comes back to the old key, and is swept on at once.
            await SignAndConfirm(oldPin);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            var change = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(oldPin.Txid, 1))!;
            Assert.Equal(BtcAttestStore.ScriptRowsForVault(vault.Uid)[0].ScriptHex, change.ScriptHex);
            var sweep = Assert.Single(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            Assert.Equal(new[] { change.Outpoint }, sweep.Pins[0].Inputs);
            await SignAndConfirm(sweep.Pins[0]);
            // The waiting withdrawal is fixed on the moved coin, under the new key.
            var fixedNow = Row(waiting, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, fixedNow.State);
            Assert.Equal(ceremony.Script.ToHex(), BtcVaultKeys.KeyOfCoins(fixedNow.Pins[0].Inputs)!.ScriptHex);
        }

        /// <summary>
        /// A withdrawal fixed on the old key before the re-key, paying no less than the market, that never confirms: a
        /// sweep at the market rate could not take its coins, and they stayed (with the vault's change) under a key whose
        /// holders were leaving. Once it has gone the stopped window unconfirmed, a sweep takes them at double its rate;
        /// the withdrawal is void and refunded in full, and the coins are under the new key (third pass review, H2).
        /// </summary>
        [Fact]
        public async Task AWithdrawalOnTheOldKeyThatNeverConfirms_GivesItsCoinsToASweep()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 200_000);
            var request = await Mine(Request(vault.Uid, 0.0005M, feeRate: 40));
            var oldPin = Assert.Single(Row(request, vault.Uid).Pins);

            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var ceremony = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address);
            await Mine(RekeyTx(vault.Uid, ceremony.Proof));
            Assert.Empty(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            // The market rate (nothing paid in the last 1,000 blocks: the floor) is below what this withdrawal pays: at it,
            // it is not taken, however long it waits.
            Assert.True(BtcWithdrawals.SweepRateAt(Tip + 1) <= 40);

            var stopped = BtcWithdrawals.MovingSince(Row(request, vault.Uid)) + BtcAttestRules.StuckPinBlocks;
            var looks = stopped % BtcAttestRules.TakeRetryBlocks == 0 ? stopped : stopped + BtcAttestRules.TakeRetryBlocks - stopped % BtcAttestRules.TakeRetryBlocks;
            await AdvanceTo(looks - BtcAttestRules.TakeRetryBlocks);
            Assert.Empty(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            await AdvanceTo(looks);

            var sweep = Assert.Single(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            Assert.Equal(80, sweep.Pins[0].FeeRate);
            Assert.Equal(oldPin.Inputs, sweep.Pins[0].Inputs);
            await SignAndConfirm(sweep.Pins[0]);
            Assert.NotEqual(VaultWithdrawalState.Pinned, Row(request, vault.Uid).State);
            Assert.NotEqual(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(ceremony.Script.ToHex(), BtcAttestStore.ScriptRowOfCoin(BtcAttestStore.Coin(BtcAttestStore.OutpointKey(sweep.Pins[0].Txid, 0)))!.ScriptHex);
        }

        /// <summary>
        /// A re-key among three validators made a key that all three had to sign (67% of three): with no holder to spare
        /// it was due again at its first count, and the vault was re-keyed and swept again and again. A re-key's key
        /// never needs every holder (fourth review).
        /// </summary>
        [Fact]
        public async Task AReKeyAmongThree_IsTwoOfThree_AndIsNotDueAgain()
        {
            Assert.Equal(2, FrostDkgAttestation.RekeyThreshold(3));
            Assert.Equal(3, FrostDkgAttestation.RekeyThreshold(4));
            Assert.Equal(4, FrostDkgAttestation.RekeyThreshold(5));
            Assert.Equal(7, FrostDkgAttestation.RekeyThreshold(10));
            Assert.Equal(FrostDkgAttestation.ThresholdFor(162, 67), FrostDkgAttestation.RekeyThreshold(162));
            Assert.Equal(51, FrostDkgAttestation.CeremonyThreshold(100, 51, null)); // a creation's is its percentage

            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            await Mine(RekeyTx(vault.Uid, Ceremony(vault.Uid, stillHere, 2, Signers[0].Address).Proof));
            var key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(2, key.MinSigners);
            BtcVaultKeys.CheckHealth(Tip + 1);
            key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(3, key.HealthyHolders);
            Assert.Equal(0, key.RekeyDueSinceVfxHeight);
        }

        /// <summary>
        /// Anyone who joins a re-key's ceremony learns the new key's UID, and could create an S3C vault under it with a
        /// few validators of its own in seconds, mined before the re-key: the re-key was then refused ("a vault's UID"),
        /// attempt after attempt, while the old key's holders kept leaving. The chain now fixes the new key's UID when
        /// the key falls due, nothing can be created under it, and a re-key under any other UID is refused (fourth
        /// review, round 3).
        /// </summary>
        [Fact]
        public async Task TheNextKeysUid_IsFixedByTheChain_AndNothingIsCreatedUnderIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var due = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.True(due.RekeyDueSinceVfxHeight > 0);

            // The fixed UID: no contract is created under it (an S3C vault, an NFT, anything).
            var fixedUid = BtcVaultKeys.NewKeyUid(due);
            Assert.StartsWith(BtcVaultKeys.NextKeyHex(due) + ":", fixedUid);
            Assert.Equal(vault.Uid, BtcVaultKeys.DueKeyClaiming(fixedUid));
            var squat = new VfxTx
            {
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE, FromAddress = Minter.Address, ToAddress = Minter.Address,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = fixedUid, Data = "x" } }),
            };
            Assert.Contains("fixed for vault", LedgerIntegrityRules.CreatedUidWasNeverWatched(squat));
            Assert.Null(BtcVaultKeys.DueKeyClaiming(FrostDkgAttestation.NewContractUid()));

            // A re-key under any other UID is refused; under the fixed one it is the vault's new key.
            var keyUid = FrostDkgAttestation.NewContractUid();
            var groupKey = Convert.ToHexString(new Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var address = FrostDkgAttestation.DeriveTaprootAddress(groupKey, FrostDkgAttestation.ConsensusNetwork)!;
            var canonical = FrostDkgAttestation.Canonical(stillHere.Select(p => p.Address));
            var message = FrostDkgAttestation.Message(keyUid, groupKey, address, Signers[0].Address, 2, canonical, vault.Uid);
            var elsewhere = FrostDkgAttestation.BuildProof(keyUid, groupKey, address, Signers[0].Address, 2, canonical,
                stillHere.Select(p => new FrostDkgAttestation.Attestation { ValidatorAddress = p.Address, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, p.Key, p.Pub) }));
            await Refused(RekeyTx(vault.Uid, elsewhere), "is not the one fixed for this vault's next key");
            var ceremony = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address);
            await Mine(RekeyTx(vault.Uid, ceremony.Proof));
            Assert.Equal(ceremony.KeyUid, BtcAttestStore.ScriptRowForVault(vault.Uid)!.KeyUID);
            // Retries have UIDs of their own.
            Assert.NotEqual(BtcVaultKeys.NewKeyUid(due), BtcVaultKeys.NewKeyUid(due));
        }

        /// <summary>
        /// A re-key's leader relays every participant's commitments, and nobody compared what they were given: a leader
        /// could give some participants other commitments than the rest, all attested the same group key, and no group of
        /// holders could ever sign with it (fourth review, round 3). Every attestation now signs the hash of the public
        /// key package its participant finished with, and a re-key's proof must name the one hash all of them signed.
        /// </summary>
        [Fact]
        public async Task AReKey_StandsOnlyIfEveryParticipantFinishedWithTheSameKeyPackage()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var current = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            var keyUid = BtcVaultKeys.NewKeyUid(current);
            var groupKey = Convert.ToHexString(new Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var address = FrostDkgAttestation.DeriveTaprootAddress(groupKey, FrostDkgAttestation.ConsensusNetwork)!;
            var canonical = FrostDkgAttestation.Canonical(stillHere.Select(p => p.Address));
            string Proof(Func<V, string?> hashSeenBy, string? hashNamed)
            {
                var attestations = stillHere.Select(p => new FrostDkgAttestation.Attestation
                {
                    ValidatorAddress = p.Address,
                    Signature = VerifiedXCore.Services.SignatureService.CreateSignature(
                        FrostDkgAttestation.Message(keyUid, groupKey, address, Signers[0].Address, 2, canonical, vault.Uid, hashSeenBy(p)), p.Key, p.Pub),
                });
                return FrostDkgAttestation.BuildProof(keyUid, groupKey, address, Signers[0].Address, 2, canonical, attestations, hashNamed);
            }
            var honest = FrostDkgAttestation.KeyPackageHash("the package all finished with");
            var other = FrostDkgAttestation.KeyPackageHash("the package one was misled into");

            // No package named: refused.
            await Refused(RekeyTx(vault.Uid, Proof(_ => null, null)), "does not name the public key package");
            // One participant finished with another package: whichever hash the proof names, one attestation fails.
            await Refused(RekeyTx(vault.Uid, Proof(p => p == stillHere[2] ? other : honest, honest)), "has no valid attestation");
            await Refused(RekeyTx(vault.Uid, Proof(p => p == stillHere[2] ? other : honest, other)), "has no valid attestation");
            // All the same: the vault's new key.
            await Mine(RekeyTx(vault.Uid, Proof(_ => honest, honest)));
            Assert.Equal(keyUid, BtcAttestStore.ScriptRowForVault(vault.Uid)!.KeyUID);
        }

        /// <summary>
        /// Anyone could send a vault coins worth less than the fee their inputs add, and every sweep spent more on them
        /// than they held. A sweep leaves such a coin where it is (fourth review, round 3).
        /// </summary>
        [Fact]
        public async Task ASweep_LeavesBehindCoinsWorthLessThanTheirFee()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var big = await Deposit(vault.Script, 100_000);
            var dust = await Deposit(vault.Script, 500);   // its input costs 575 sats at 10 sat/vB
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            await Mine(RekeyTx(vault.Uid, Ceremony(vault.Uid, stillHere, 2, Signers[0].Address).Proof));
            var sweep = Assert.Single(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            Assert.Equal(new[] { BtcAttestStore.OutpointKey(big.GetHash().ToString(), 0) }, sweep.Pins[0].Inputs);
            Assert.Equal(VaultCoinStatus.Unspent, BtcAttestStore.Coin(BtcAttestStore.OutpointKey(dust.GetHash().ToString(), 0))!.Status);
        }

        /// <summary>
        /// A vault's ledger is one list in one database document, two rows a transfer, never pruned, and a write LiteDB
        /// refused (above its document limit) was dropped silently: transfers and requests then wrote nothing while their
        /// withdrawals were still pinned and paid (fourth review, round 5). Rows become one row an address once that saves
        /// enough, with every balance as it was; a vault holds a bounded number of holders with a balance.
        /// </summary>
        [Fact]
        public void AVaultsLedger_IsCompactedToOneRowAnAddress_WithEveryBalanceAsItWas()
        {
            string A(int i) => "xHolder" + i.ToString("D5");
            var rows = new List<SmartContractStateTreiTokenizationTX>();
            for (var i = 0; i < 3_000; i++)
            {
                rows.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = A(i % 7), Amount = 0.00001M * (i + 1) });
                rows.Add(new SmartContractStateTreiTokenizationTX { FromAddress = A((i + 3) % 7), ToAddress = "-", Amount = -0.00001M * (i + 1) });
            }
            rows.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = "xOwner", Amount = 0.5M });
            var contract = new SmartContractStateTrei { SmartContractUID = "v", SCStateTreiTokenizationTXes = rows.ToList() };
            decimal Balance(SmartContractStateTrei c, string a) => c.SCStateTreiTokenizationTXes!.Where(x => x.FromAddress == a || x.ToAddress == a).Sum(x => x.Amount);
            var before = Enumerable.Range(0, 7).Select(A).Append("xOwner").ToDictionary(a => a, a => Balance(contract, a));
            var total = rows.Sum(x => x.Amount);

            var tail = rows.Skip(rows.Count - BtcVaultLedger.RawTailRows).ToList();
            Assert.True(BtcVaultLedger.Compact(contract));
            Assert.True(contract.SCStateTreiTokenizationTXes!.Count <= 8 + BtcVaultLedger.RawTailRows);
            Assert.Equal(tail, contract.SCStateTreiTokenizationTXes.Skip(contract.SCStateTreiTokenizationTXes.Count - BtcVaultLedger.RawTailRows)); // the newest rows as they were
            foreach (var (a, b) in before) Assert.Equal(b, Balance(contract, a));
            Assert.Equal(total, contract.SCStateTreiTokenizationTXes.Sum(x => x.Amount));
            Assert.Equal(before.Count, BtcVaultLedger.LedgerAddresses(contract));
            Assert.False(BtcVaultLedger.Compact(contract)); // nothing more to save

            // An address whose rows sum to zero is dropped, unless it may yet be refunded: then it keeps a row of zero.
            var zero = new List<SmartContractStateTreiTokenizationTX>();
            for (var i = 0; i < 3_000; i++)
            {
                zero.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = "xGone" + (i % 2), Amount = 0.00001M });
                zero.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "xGone" + (i % 2), ToAddress = "-", Amount = -0.00001M });
            }
            zero.AddRange(rows.Skip(rows.Count - BtcVaultLedger.RawTailRows)); // the newest rows name others
            var kept = new SmartContractStateTrei { SmartContractUID = "z", SCStateTreiTokenizationTXes = zero.ToList() };
            Assert.True(BtcVaultLedger.Compact(kept, keepAtZero: () => new HashSet<string> { "xGone1" }));
            Assert.DoesNotContain(kept.SCStateTreiTokenizationTXes!, x => x.FromAddress == "xGone0" || x.ToAddress == "xGone0");
            var marker = Assert.Single(kept.SCStateTreiTokenizationTXes!, x => x.FromAddress == "xGone1" || x.ToAddress == "xGone1");
            Assert.Equal(0M, marker.Amount);
            Assert.Equal("-", marker.ToAddress);

            // A row naming two addresses (or none) is not a shape compaction knows: left as it is.
            var odd = new SmartContractStateTrei { SmartContractUID = "w", SCStateTreiTokenizationTXes = rows.Append(new SmartContractStateTreiTokenizationTX { FromAddress = "xA", ToAddress = "xB", Amount = 1M }).ToList() };
            Assert.False(BtcVaultLedger.Compact(odd));
        }

        /// <summary>
        /// The sum of the holders' negative positions was read by summing the whole ledger once a holder: holders times
        /// rows, on every owner action on every node. One pass gives the same figure (fourth review, round 6).
        /// </summary>
        [Fact]
        public void TheHoldersNegativePositions_AreReadInOnePass_WithTheSameFigure()
        {
            var random = new Random(6);
            for (var round = 0; round < 50; round++)
            {
                var rows = new List<SmartContractStateTreiTokenizationTX>();
                var names = Enumerable.Range(0, 1 + random.Next(30)).Select(i => "xH" + i).Append("xOwner").ToList();
                for (var i = random.Next(200); i > 0; i--)
                {
                    var who = names[random.Next(names.Count)];
                    var amount = random.Next(1, 1000) / 100_000M;
                    rows.Add(random.Next(2) == 0
                        ? new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = who, Amount = amount }
                        : new SmartContractStateTreiTokenizationTX { FromAddress = who, ToAddress = "-", Amount = -amount });
                }
                var state = new SmartContractStateTrei { SmartContractUID = "v", OwnerAddress = "xOwner", SCStateTreiTokenizationTXes = rows };
                var expected = names.Where(n => n != "xOwner")
                    .Select(n => rows.Where(r => r.FromAddress == n || r.ToAddress == n).Sum(r => r.Amount)).Where(p => p < 0M).Sum();
                Assert.Equal(expected, VBTCService.NegativeNonOwnerPositions(state, "xOwner"));
            }
        }

        [Fact]
        public async Task AWatchedVaultsLedger_StaysBounded_AndHoldsAtMostItsHolders()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 500_000);
            var holder = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = holder.Address, Balance = 100M, Nonce = 0 });
            await Mine(SignedBy(Minter, holder.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = holder.Address, Amount = 0.001M }));

            // A long history of transfers back and forth (written as the ledger holds them).
            var state = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
            for (var i = 0; i < 2_500; i++)
            {
                state.SCStateTreiTokenizationTXes!.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = holder.Address, Amount = 0.00000001M });
                state.SCStateTreiTokenizationTXes.Add(new SmartContractStateTreiTokenizationTX { FromAddress = holder.Address, ToAddress = "-", Amount = -0.00000001M });
            }
            SmartContractStateTrei.GetSCST().Update(state);
            var spendableBefore = await Spendable(vault.Uid, Minter);

            // The next write on the vault compacts it; every balance is as it was, and the transfer is applied.
            await Mine(SignedBy(holder, Minter.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = holder.Address, ToAddress = Minter.Address, Amount = 0.0004M }));
            state = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
            Assert.True(state.SCStateTreiTokenizationTXes!.Count < 10 + BtcVaultLedger.RawTailRows);
            Assert.Equal(0.0006M, state.SCStateTreiTokenizationTXes.Where(x => x.FromAddress == holder.Address || x.ToAddress == holder.Address).Sum(x => x.Amount));
            Assert.Equal(spendableBefore + 0.0004M, await Spendable(vault.Uid, Minter));

            // At the holder limit, a new holder is refused; an existing one is not.
            BtcVaultLedger.MaxHoldersOverride = BtcVaultLedger.LedgerAddresses(state);
            try
            {
                var newcomer = NewKey();
                await Refused(SignedBy(holder, newcomer.Address, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = holder.Address, ToAddress = newcomer.Address, Amount = 0.0001M }), "the most a vault holds");
                var (ok, why) = await Verify(SignedBy(holder, Minter.Address, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = holder.Address, ToAddress = Minter.Address, Amount = 0.0001M }), Tip + 1);
                Assert.True(ok, why);
            }
            finally { BtcVaultLedger.MaxHoldersOverride = null; }
        }

        /// <summary>
        /// Contract state is looked up through an index on its UID. Should a UID ever be stored twice, the lookup takes the
        /// oldest document, as the scan before the index did; through a non-unique index the first match depends on the
        /// index's random levels, which differ between nodes (fourth review, round 8).
        /// </summary>
        [Fact]
        public async Task AUidStoredTwice_ReadsAsItsOldestDocument_OnEveryNode()
        {
            await Start();
            var uid = FrostDkgAttestation.NewContractUid();
            var scs = SmartContractStateTrei.GetSCST();
            scs.Insert(new SmartContractStateTrei { SmartContractUID = uid, OwnerAddress = "xFirst" });
            for (var i = 0; i < 40; i++) scs.Insert(new SmartContractStateTrei { SmartContractUID = uid, OwnerAddress = "xLater" + i });
            Assert.Equal("xFirst", SmartContractStateTrei.GetSmartContractState(uid)!.OwnerAddress);
            Assert.Equal("xFirst", StateData.GetSpecificSmartContractStateTrei(uid).OwnerAddress);
        }

        /// <summary>
        /// Refunds are credits nobody can refuse, and the count was of balances: a requester who escrowed everything left
        /// it, a new holder took the place, and the refund put the requester back above the limit; enough at once took the
        /// vault's document past LiteDB's limit and froze it (fourth review, round 6). An address stays on the ledger, at
        /// zero, while its withdrawal is open: compaction keeps it, the count includes it, and its refund needs no place.
        /// </summary>
        [Fact]
        public async Task ARequesterWhoEscrowedEverything_KeepsItsPlace_UntilItsWithdrawalCloses()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 500_000);
            var holder = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = holder.Address, Balance = 100M, Nonce = 0 });
            await Mine(SignedBy(Minter, holder.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = holder.Address, Amount = 0.001M }));
            var helper = NewKey();
            await Mine(SignedBy(Minter, helper.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = helper.Address, Amount = 0.0001M }));

            // The vault's one coin is held by the owner's withdrawal; the holder's, for all it has, waits.
            await Mine(Request(vault.Uid, 0.002M));
            var waiting = await Mine(SignedBy(holder, holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault.Uid, BTCAddress = BtcDest, Amount = 0.001M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }));
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting, vault.Uid).State);

            // A long history, then a write: the ledger is compacted, and the holder (at zero) is kept.
            var state = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
            for (var i = 0; i < 2_500; i++)
            {
                state.SCStateTreiTokenizationTXes!.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = helper.Address, Amount = 0.00000001M });
                state.SCStateTreiTokenizationTXes.Add(new SmartContractStateTreiTokenizationTX { FromAddress = helper.Address, ToAddress = "-", Amount = -0.00000001M });
            }
            SmartContractStateTrei.GetSCST().Update(state);
            await Mine(SignedBy(Minter, helper.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = helper.Address, Amount = 0.0001M }));
            state = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
            Assert.True(state.SCStateTreiTokenizationTXes!.Count < 10 + BtcVaultLedger.RawTailRows);
            var place = Assert.Single(state.SCStateTreiTokenizationTXes, x => x.FromAddress == holder.Address || x.ToAddress == holder.Address);
            Assert.Equal(0M, place.Amount);

            // The vault is full with it counted: a new address is refused, and the refund still lands.
            BtcVaultLedger.MaxHoldersOverride = BtcVaultLedger.LedgerAddresses(state);
            try
            {
                await Refused(SignedBy(helper, NewKey().Address, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = helper.Address, ToAddress = "xNobodyYet", Amount = 0.00001M }), "the most a vault holds");
                await Mine(SignedBy(holder, holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                    new { Function = "VBTCWithdrawalCancel()", ContractUID = vault.Uid, WithdrawalRequestHash = waiting.Hash, FailureProof = "" }));
                state = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
                Assert.Equal(0.001M, state.SCStateTreiTokenizationTXes!.Where(x => x.FromAddress == holder.Address || x.ToAddress == holder.Address).Sum(x => x.Amount));
                Assert.True(BtcVaultLedger.LedgerAddresses(state) <= BtcVaultLedger.MaxHoldersOverride);
            }
            finally { BtcVaultLedger.MaxHoldersOverride = null; }
        }

        /// <summary>
        /// Unlocks from the bridge credit an address too, a pool unlock any address its Base burn names: on a full vault
        /// one that is not named yet is refused, and the casters' plan leaves such a vault out (fourth review, round 6).
        /// The vault's owner, and an address already named, are never refused.
        /// </summary>
        [Fact]
        public async Task AFullVault_RefusesACreditToANewAddressFromAnyPath_ButNeverItsOwnerOrAHolder()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 500_000);
            var holder = NewKey();
            await Mine(SignedBy(Minter, holder.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = holder.Address, Amount = 0.001M }));
            var state = SmartContractStateTrei.GetSmartContractState(vault.Uid)!;
            var lockId = "lock-" + Guid.NewGuid().ToString("N");
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = lockId, SmartContractUID = vault.Uid, OwnerAddress = holder.Address, Amount = 0.0005M, AmountSats = 50_000,
                LockTxHash = "h", LockTimestamp = 1, EvmDestination = "0x0",
            });
            VfxTx PoolUnlock(string destination) => SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, new
            {
                Function = "VBTCBridgePoolUnlock()", TotalAmount = 0.0001M, TotalAmountSats = 10_000, VfxDestinationAddress = destination,
                ExitBurnTxHash = new string('a', 64), Allocations = new[] { new { LockId = lockId, UnlockAmount = 0.0001M, SmartContractUID = vault.Uid } },
            });
            BtcVaultLedger.MaxHoldersOverride = BtcVaultLedger.LedgerAddresses(state);
            try
            {
                Assert.Null(BtcVaultLedger.CreditRefusal(state, Minter.Address));       // the owner
                Assert.Null(BtcVaultLedger.CreditRefusal(state, holder.Address));       // named already
                Assert.Contains("the most a vault holds", BtcVaultLedger.CreditRefusal(state, "xNew"));
                await Refused(PoolUnlock("xNew"), "the most a vault holds");
                // A field apply ignores does not hide the vault a transfer credits (an empty "Inputs" did; round 7).
                var stranger = NewKey().Address;
                await Refused(SignedBy(holder, stranger, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = holder.Address, ToAddress = stranger, Amount = 0.00001M, Inputs = new object[0] }), "the most a vault holds");
                // A full vault changes hands only to an address its ledger names: each owner a chain of owners left behind
                // was one holder more (round 7).
                VfxTx HandOver(string to) => SignedBy(Minter, to, TransactionType.TKNZ_TX, new[] { new { Function = "Transfer()", ContractUID = vault.Uid, ToAddress = to, Data = "x", MD5List = "NA" } });
                Assert.Contains("the most a vault holds", BtcVaultLedger.HolderLimitRefusal(HandOver("xNew")));
                Assert.Null(BtcVaultLedger.HolderLimitRefusal(HandOver(holder.Address)));
                // A sale's completion hands the vault to its sender, the buyer: held to the same rule (round 8).
                var buyer = NewKey();
                VfxTx Buy(V who) => SignedBy(who, Minter.Address, TransactionType.NFT_SALE, new { Function = "M_Sale_Complete()", ContractUID = vault.Uid, KeySign = "k" });
                Assert.Contains("the most a vault holds", BtcVaultLedger.HolderLimitRefusal(Buy(buyer)));
                Assert.Null(BtcVaultLedger.HolderLimitRefusal(Buy(holder)));
                Assert.Null(BtcVaultLedger.HolderLimitRefusal(PoolUnlock(holder.Address)));
                Assert.Null(BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.0001M, Tip + 1, "xNew"));   // its one lock is on the full vault
                Assert.Equal(lockId, Assert.Single(BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.0001M, Tip + 1, holder.Address)!).LockId);
            }
            finally
            {
                BtcVaultLedger.MaxHoldersOverride = null;
                VBTCBridgeLockState.GetCollection().Delete(lockId);
            }
        }

        /// <summary>
        /// A UID the chain fixed for a due key's successor is that vault's re-key's only: an unsigned creation copied
        /// under the leader's session id and UID was opened first, and the leader's own start was then refused (fourth
        /// review, round 6).
        /// </summary>
        [Fact]
        public async Task ACeremonyUnderTheUidFixedForADueKey_IsOnlyThatVaultsSignedReKey()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var fixedUid = BtcVaultKeys.NewKeyUid(BtcAttestStore.ScriptRowForVault(vault.Uid)!);
            using var server = new Microsoft.AspNetCore.TestHost.TestServer(new Microsoft.AspNetCore.Hosting.WebHostBuilder().UseStartup<FrostStartup>());
            var body = JsonConvert.SerializeObject(new FrostDKGStartRequest
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = fixedUid, LeaderAddress = Signers[0].Address, LeaderSignature = "sig", Timestamp = TimeUtil.GetTime(),
                ParticipantAddresses = stillHere.Select(s => s.Address).ToList(), RequiredThreshold = 67,
            });
            var priorSynced = Globals.IsChainSynced;
            Globals.IsChainSynced = true; // the route reads the active validators of a node at the tip
            try
            {
                var response = await server.CreateClient().PostAsync("/frost/dkg/start", new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Contains("fixed for vault", await response.Content.ReadAsStringAsync());
            }
            finally { Globals.IsChainSynced = priorSynced; }
        }

        /// <summary>
        /// The fee raise is new with the attestation. Its own refusal runs from the recording height, and before it a 9.x
        /// node took a type 44 that 8.x nodes (peers until then) refuse: one fee to split them (fourth review, round 5).
        /// </summary>
        [Fact]
        public async Task AFeeRaise_IsNotTakenBeforeTheRecordingHeight()
        {
            await Start();
            var raise = SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_COIN_PIN, new { Function = "VBTCWithdrawalFeeBump()" });
            var (ok, why) = await Verify(raise, H1 - 1);
            Assert.False(ok);
            Assert.Contains("only from the Bitcoin attestation's recording height", why);
        }

        /// <summary>A re-key's UID ends in random digits after its time, so nobody opens sessions under it ahead of its leader.</summary>
        [Fact]
        public void AReKeysUid_EndsInDigitsNobodyCanGuess()
        {
            var key = new VaultDepositScriptRow { SmartContractUID = "v", Generation = 0, RekeyDueSinceVfxHeight = 5 };
            var uids = Enumerable.Range(0, 20).Select(_ => BtcVaultKeys.NewKeyUid(key)).ToList();
            Assert.Equal(20, uids.Distinct().Count());
            Assert.All(uids, u => Assert.Matches("^[0-9a-f]{32}:[0-9]+$", u));
            Assert.All(uids, u => Assert.StartsWith(BtcVaultKeys.NextKeyHex(key) + ":", u));
            Assert.True(uids.Select(u => u[^6..]).Distinct().Count() > 1); // the random tail
            // The time is read without the random digits (read whole, the check that the ceremony ran after the key fell
            // due never refused anything; fourth review, round 6).
            Assert.All(uids, u => Assert.InRange(long.Parse(BtcVaultKeys.KeyUidTime(u)), TimeUtil.GetTime() - 5, TimeUtil.GetTime() + 3_600)); // one process clock, a second a UID
            Assert.Equal("1700000000", BtcVaultKeys.KeyUidTime(new string('a', 32) + ":1700000000"));
        }

        /// <summary>
        /// A withdrawal request mined a second time (by a producer, once the recent-blocks check has forgotten its hash)
        /// debited its requester again with no row to pay, expire or cancel. A watched vault's request is mined once
        /// (fourth review, round 2).
        /// </summary>
        [Fact]
        public async Task AWithdrawalRequest_IsMinedOnce()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 200_000);
            var request = await Mine(Request(vault.Uid, 0.0005M));
            Assert.NotNull(Row(request, vault.Uid));
            await Refused(request, "was mined before");
        }

        /// <summary>
        /// An exception the transaction's own data causes is thrown on every node alike: the step is a no-op everywhere,
        /// and flagging it let anyone leave every node's state marked failed with one cheap request (an unreadable
        /// OriginalRequestTime, a field validation never reads), stopping snapshots and failing every later rebuild
        /// (fourth review, round 4). Only a fault of the node's own storage flags the block.
        /// </summary>
        [Fact]
        public async Task ARequestWithAnUnreadableField_DoesNotFlagAnyBlock()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 200_000);
            var prior = StateData.BlockStartFailedAtHeight;
            try
            {
                StateData.BlockStartFailedAtHeight = -1;
                await Mine(SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    new { Function = "VBTCWithdrawalRequest()", ContractUID = vault.Uid, BTCAddress = BtcDest, Amount = 0.0005M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N"), OriginalRequestTime = "not a number" }));
                Assert.Equal(-1, StateData.BlockStartFailedAtHeight);
                Assert.False(StateData.IsNodeLocalFault(new FormatException()));
                Assert.False(StateData.IsNodeLocalFault(new Newtonsoft.Json.JsonReaderException()));
                Assert.True(StateData.IsNodeLocalFault(new System.IO.IOException()));
                Assert.True(StateData.IsNodeLocalFault(new InvalidOperationException("wrapped", new ObjectDisposedException("db"))));
            }
            finally { StateData.BlockStartFailedAtHeight = prior; }
        }

        /// <summary>
        /// A step inside a transaction's apply (recording a withdrawal, settling after a report, a cancel, a fee raise, a
        /// bridge exit) caught its own error and only logged it: a store fault on one node left it debited with no
        /// withdrawal row, unlike its peers, and nothing flagged it. It now marks the block (fourth review).
        /// </summary>
        [Fact]
        public async Task AnApplyStepThatFails_MarksItsBlock()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var broken = new LiteDB.LiteDatabase(new System.IO.MemoryStream());
            broken.Dispose();
            var prior = StateData.BlockStartFailedAtHeight;
            try
            {
                StateData.BlockStartFailedAtHeight = -1;
                BtcWithdrawals.OnRequestApplied(vault.Uid, "a-request", Tip + 1, broken);
                Assert.Equal(Tip + 1, StateData.BlockStartFailedAtHeight);
                // A block applied after it starts clean.
                var request = await Mine(Request(vault.Uid, 0.0001M));
                Assert.NotNull(request);
            }
            finally { StateData.BlockStartFailedAtHeight = prior; }
        }

        // ── What the review of the re-key found (third pass, 14 October 2026) ──────────────────────

        /// <summary>
        /// The new key's threshold was the leader's choice (up to every holder signing together, which any one holder
        /// could then withhold, and which fell due again at once), and the proof named no vault: a creation's proof, or
        /// one made for another vault, served as any due vault's re-key. A re-key's ceremony now signs the vault's UID
        /// into every attestation and uses 67% of its holders, and validators join it only for a vault that is due.
        /// </summary>
        [Fact]
        public async Task AReKeysVaultAndThreshold_AreFixed_NotTheLeaders()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var other = await CreateVault(H1 + 2, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var stillHere = Signers.Take(3).ToList();
            Assert.Contains("is not due", BtcVaultKeys.RekeyCeremonyRefusal(vault.Uid, 67));
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));

            // Validators join only a due vault's ceremony, and only at 67%.
            Assert.Null(BtcVaultKeys.RekeyCeremonyRefusal(vault.Uid, 67));
            Assert.Contains("threshold of 67%", BtcVaultKeys.RekeyCeremonyRefusal(vault.Uid, 100));
            Assert.Contains("no vault", BtcVaultKeys.RekeyCeremonyRefusal(new string('a', 32) + ":1", 67));

            // Every holder signing together: refused.
            var allOfThem = Ceremony(vault.Uid, Signers.Take(3), 3, Signers[0].Address);
            await Refused(RekeyTx(vault.Uid, allOfThem.Proof), "signs with 2 of its 3 holders");
            // A proof made for another vault, or for a creation (no vault named): no valid attestation for this re-key.
            await Refused(RekeyTx(vault.Uid, Ceremony(other.Uid, stillHere, 2, Signers[0].Address, uidOfVault: vault.Uid).Proof), "has no valid attestation");
            var keyUid = BtcVaultKeys.NewKeyUid(BtcAttestStore.ScriptRowForVault(vault.Uid)!); // the right UID: only the binding is wrong
            var groupKey = Convert.ToHexString(new Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var address = FrostDkgAttestation.DeriveTaprootAddress(groupKey, FrostDkgAttestation.ConsensusNetwork)!;
            var canonical = FrostDkgAttestation.Canonical(stillHere.Select(p => p.Address));
            var creationHash = FrostDkgAttestation.KeyPackageHash("pkp|" + keyUid);
            var creationMessage = FrostDkgAttestation.Message(keyUid, groupKey, address, Signers[0].Address, 2, canonical, keyPackageHash: creationHash);
            var creationProof = FrostDkgAttestation.BuildProof(keyUid, groupKey, address, Signers[0].Address, 2, canonical,
                stillHere.Select(p => new FrostDkgAttestation.Attestation { ValidatorAddress = p.Address, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(creationMessage, p.Key, p.Pub) }), creationHash);
            await Refused(RekeyTx(vault.Uid, creationProof), "has no valid attestation");
        }

        /// <summary>
        /// Anybody who saw a re-key's ceremony start (every participant) could mint an NFT under the new key's UID before
        /// the re-key was mined, and the re-key was refused for good. Only a vault's or a key's UID blocks it now, and no
        /// contract can be created under a key's UID.
        /// </summary>
        [Fact]
        public async Task AContractMintedUnderTheNewKeysUid_DoesNotBlockTheReKey()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var ceremony = Ceremony(vault.Uid, stillHere, 2, Signers[0].Address);
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = ceremony.KeyUid, ContractData = VbtcTestContracts.BuildContractData(ceremony.KeyUid, Minter.Address, null, name: "Squatter"),
                MinterAddress = Minter.Address, OwnerAddress = Minter.Address,
            });
            await Mine(RekeyTx(vault.Uid, ceremony.Proof));
            Assert.Equal(ceremony.KeyUid, BtcAttestStore.ScriptRowForVault(vault.Uid)!.KeyUID);
            // And from now on nothing is created under that UID.
            var create = new VfxTx
            {
                TransactionType = TransactionType.NFT_MINT, FromAddress = Minter.Address, ToAddress = Minter.Address,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = ceremony.KeyUid, Data = "x" } }),
            };
            Assert.Contains("is a vault key's", LedgerIntegrityRules.CreatedUidWasNeverWatched(create));
        }

        /// <summary>
        /// A sweep's Bitcoin fee falls on the owner's figure (what is left of the vault's Bitcoin after every other
        /// claim). An owner who had given away all its vBTC had nothing to carry it with, and the vault then held less
        /// than its holders' claims: the last of them could never be paid in full. Each payment from such a vault now
        /// leaves its share of the shortfall in the vault, so the claims and the Bitcoin come out even.
        /// </summary>
        [Fact]
        public async Task SweepFeesTheOwnerCannotCarry_AreSharedByThePaymentsThatFollow_AndEveryoneIsPaid()
        {
            await Start();
            var holder = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = holder.Address, Balance = 100M, Nonce = 0 });
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 200_000);
            await Mine(SignedBy(Minter, holder.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = holder.Address, Amount = 0.002M }));
            Assert.Equal(0M, await Spendable(vault.Uid, Minter));

            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            await Mine(RekeyTx(vault.Uid, Ceremony(vault.Uid, stillHere, 2, Signers[0].Address).Proof));
            var sweep = Assert.Single(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            var fee = sweep.Pins[0].FeeSats;
            await SignAndConfirm(sweep.Pins[0]);
            Assert.Equal(200_000 - fee, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.True(await Spendable(vault.Uid, Minter) < 0M); // the owner's figure carries the fee it cannot pay

            // The holder withdraws everything it holds: the payment leaves the shortfall in the vault, and is fixed.
            var request = await Mine(SignedBy(holder, holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault.Uid, BTCAddress = BtcDest, Amount = 0.002M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }));
            var row = Row(request, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, row.State);
            Assert.Equal(fee, row.ShortfallShareSats);
            Assert.Equal(200_000 - fee - row.Pins[0].FeeSats, row.Pins[0].DestinationSats);
            await SignAndConfirm(row.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(0M, await Spendable(vault.Uid, Minter)); // even again
        }

        /// <summary>A sweep is fixed at what withdrawals of the last thousand blocks paid, not at a rate Bitcoin may ignore.</summary>
        [Fact]
        public async Task ASweepIsFixedAtTheRateWithdrawalsPay()
        {
            await Start();
            Assert.Equal(BtcWithdrawals.SweepFeeRate, BtcWithdrawals.SweepRateAt(Tip + 1));
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 2_000_000);

            // One requester, however many paid withdrawals at a high rate: one sample (fourth review, round 4).
            foreach (var rate in new[] { 900, 950, 1000 })
            {
                var request = await Mine(Request(vault.Uid, 0.002M, rate));
                // Requested and pinned, not paid: it says nothing of what Bitcoin accepts (and could be given up).
                Assert.Equal(BtcWithdrawals.SweepFeeRate, BtcWithdrawals.SweepRateAt(Tip + 1));
                await SignAndConfirm(Row(request, vault.Uid).Pins.Last(p => !p.Dead));
                Assert.Equal(VaultWithdrawalState.Settled, Row(request, vault.Uid).State);
            }
            Assert.Equal(BtcWithdrawals.SweepFeeRate, BtcWithdrawals.SweepRateAt(Tip + 1));

            // Five requesters' paid withdrawals: the lower quartile of their rates (the owner's latest is 1000).
            var rates = new[] { 30, 60, 90, 120 };
            foreach (var rate in rates)
            {
                var holder = NewKey();
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = holder.Address, Balance = 100M, Nonce = 0 });
                await Mine(SignedBy(Minter, holder.Address, TransactionType.VBTC_V2_TRANSFER,
                    new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = holder.Address, Amount = 0.002M }));
                var request = await Mine(SignedBy(holder, holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    new { Function = "VBTCWithdrawalRequest()", ContractUID = vault.Uid, BTCAddress = BtcDest, Amount = 0.002M, FeeRate = rate, UniqueId = Guid.NewGuid().ToString("N") }));
                await SignAndConfirm(Row(request, vault.Uid).Pins.Last(p => !p.Dead));
            }
            Assert.Equal(60, BtcWithdrawals.SweepRateAt(Tip + 1)); // 30, 60, 90, 120, 1000: the second of five
            Assert.Equal(BtcWithdrawals.SweepFeeRate, BtcWithdrawals.SweepRateAt(Tip + 2_000));
        }

        // ── Who leads ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ValidatorsLeadTheCeremonyInTurns_NoneAppointed_AndNotWhileOneIsUnderWay()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 100_000);
            var stillHere = Signers.Take(3).ToList();
            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            var key = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            var eligible = stillHere.Select(s => s.Address).OrderBy(a => a, StringComparer.Ordinal).ToList();

            var led = new List<(string Vault, string Leader)>();
            var priorLead = BtcVaultKeyService.LeadRekey;
            var priorEligible = BtcVaultKeyService.Eligible;
            BtcVaultKeyService.LeadRekey = (v, k, me) => { led.Add((v, me)); return Task.FromResult((true, "stub")); };
            BtcVaultKeyService.Eligible = _ => eligible;
            try
            {
                var order = eligible.OrderBy(a => BtcVaultKeyService.Turn(key, a, eligible)).ToList();
                Assert.Equal(eligible.Count, order.Select(a => BtcVaultKeyService.Turn(key, a, eligible)).Distinct().Count());
                var second = order[1];
                // The second in line waits its turn.
                Assert.Empty(await BtcVaultKeyService.TickAsync(second, Tip));
                Assert.Empty(await BtcVaultKeyService.TickAsync(second, key.RekeyDueSinceVfxHeight + BtcVaultKeyService.TurnBlocks - 1));
                Assert.Equal(new[] { vault.Uid }, await BtcVaultKeyService.TickAsync(second, key.RekeyDueSinceVfxHeight + BtcVaultKeyService.TurnBlocks));
                // The first leads at once (another node: its own memory of attempts); a validator that is not eligible never does.
                BtcVaultKeyService.ForgetAttempts();
                Assert.Equal(new[] { vault.Uid }, await BtcVaultKeyService.TickAsync(order[0], Tip));
                Assert.Empty(await BtcVaultKeyService.TickAsync(Signers[3].Address, Tip + 10_000));
                // Not again at once after its own attempt.
                Assert.Empty(await BtcVaultKeyService.TickAsync(order[0], Tip));
                Assert.Equal(2, led.Count);
            }
            finally
            {
                BtcVaultKeyService.LeadRekey = priorLead;
                BtcVaultKeyService.Eligible = priorEligible;
            }
        }
    }
}
