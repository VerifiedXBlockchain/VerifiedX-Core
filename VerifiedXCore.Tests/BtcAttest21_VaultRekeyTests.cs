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
        private static (string KeyUid, string Address, NBitcoin.Script Script, string Proof) Ceremony(IEnumerable<V> participants, int threshold, string leader)
        {
            var keyUid = FrostDkgAttestation.NewContractUid();
            var groupKey = Convert.ToHexString(new Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var address = FrostDkgAttestation.DeriveTaprootAddress(groupKey, FrostDkgAttestation.ConsensusNetwork)!;
            var list = participants.ToList();
            var canonical = FrostDkgAttestation.Canonical(list.Select(p => p.Address));
            var message = FrostDkgAttestation.Message(keyUid, groupKey, address, leader, threshold, canonical);
            var proof = FrostDkgAttestation.BuildProof(keyUid, groupKey, address, leader, threshold, canonical,
                list.Select(p => new FrostDkgAttestation.Attestation { ValidatorAddress = p.Address, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, p.Key, p.Pub) }));
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
            var ceremony = Ceremony(stillHere, 2, Signers[0].Address);

            // Not due yet: refused.
            await Refused(RekeyTx(vault.Uid, ceremony.Proof), "is not due for replacement");

            await ToNextEpoch(stillHere, leaving: Signers.Skip(3));
            Assert.True(BtcAttestStore.ScriptRowForVault(vault.Uid)!.RekeyDueSinceVfxHeight > 0);

            // What a creation's ceremony must be, a re-key's must be: nearly every eligible validator, each attesting.
            await Refused(RekeyTx(vault.Uid, Ceremony(stillHere.Take(2), 2, Signers[0].Address).Proof), "must list 3 to 512 distinct participants");
            await Refused(RekeyTx(vault.Uid, Ceremony(Signers, 3, Signers[0].Address).Proof), "is not an eligible validator"); // the one that left
            var forged = Ceremony(stillHere, 2, Signers[0].Address);
            var proof = FrostDkgAttestation.ParseProof(forged.Proof)!;
            proof.Attestations[0].Signature = proof.Attestations[1].Signature;
            await Refused(RekeyTx(vault.Uid, FrostDkgAttestation.BuildProof(proof.ContractUID, proof.GroupPublicKey, proof.TaprootAddress, proof.Owner, proof.Threshold, proof.Participants, proof.Attestations)), "has no valid attestation");
            // The new key's UID is its own: not the vault's, not a contract's.
            var asVault = FrostDkgAttestation.ParseProof(Ceremony(stillHere, 2, Signers[0].Address).Proof)!;
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
            Assert.Equal(BtcAttestBlockRules.OneRekeyReason, BtcAttestBlockRules.TryRegister(RekeyTx(vault.Uid, Ceremony(stillHere, 2, Signers[1].Address).Proof), state).Reason);
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
            Assert.Equal(2, current.MinSigners);
            Assert.Equal(vault.Uid, BtcVaultKeys.VaultOf(ceremony.KeyUid));
            Assert.Equal(current.Holders, BtcWithdrawalSigner.SignersOf(ceremony.KeyUid));
            Assert.Equal(Signers.Count, BtcWithdrawalSigner.SignersOf(vault.Uid).Count); // the vault's UID still names its first key

            // Once: the vault is not due any more, and the key UID is taken.
            await Refused(RekeyTx(vault.Uid, Ceremony(stillHere, 2, Signers[0].Address).Proof), "is not due for replacement");
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
            var ceremony = Ceremony(stillHere, 2, Signers[0].Address);
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
            var ceremony = Ceremony(stillHere, 2, Signers[0].Address);
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
