using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.FROST;
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
    /// vBTC end to end, through the node's own API, the two ways people use it (third pass, 14 October 2026):
    /// <list type="bullet">
    /// <item><b>Local</b>: the node's wallet holds the keys; the routes build, sign and send.</item>
    /// <item><b>Raw</b>: a web wallet holds its keys; a route builds a transaction and returns its hash, the wallet
    /// signs the hash, and another route checks and sends it.</item>
    /// </list>
    /// From a vault's creation (its key ceremony attested by the validators, under the rule consensus applies) through
    /// deposits on Bitcoin, transfers, withdrawals by the owner and by holders who are not the owner, a fee raise, a
    /// release, a cancellation, to the vault being moved to a new key and paying out from there. Every transaction a
    /// route sends goes to the mempool and is mined the way a block producer chooses and every node judges a block
    /// (ProcessTxPool, then VerifyTX at the block's height, then UpdateTreis). Bitcoin is a regtest chain made for the
    /// test; the vault's signers are stood in for by mining exactly the transaction consensus pinned.
    /// </summary>
    [Collection("DbContextSequential")]
    public class E2E_VbtcLocalAndRawPathsTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly VBTCController _api = new VBTCController();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly long _priorDkgRule = Globals.VbtcV2DkgAttestationHeight;
        private readonly V _localHolder = NewKey();   // in this node's wallet
        private readonly V _web = NewKey();           // a web wallet: never in this node's wallet
        private readonly V _webHolder = NewKey();     // a web wallet that holds vBTC it did not mint

        public E2E_VbtcLocalAndRawPathsTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            Globals.VbtcV2DkgAttestationHeight = H1;   // a vault's creation carries its validators' attestations
            foreach (var who in new[] { _localHolder, _web, _webHolder })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = who.Address, Balance = 100M, Nonce = 0 });
            foreach (var who in new[] { Minter, _localHolder })
                AccountData.GetAccounts().InsertSafe(new Account { Address = who.Address, PublicKey = who.Pub, PrivateKey = "0" + who.Key.secret.ToString("x") });
            BtcWithdrawalSigner.SettleBlocksOverride = 0;
        }

        public override void Dispose()
        {
            Globals.BTCNetwork = _priorNetwork;
            Globals.VbtcV2DkgAttestationHeight = _priorDkgRule;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;

        // ── The chain ─────────────────────────────────────────────────────────────────

        /// <summary>A block: what the producer takes from the mempool, judged as every node judges a block, applied.</summary>
        private async Task<List<VfxTx>> MineMempool()
        {
            var chosen = await TransactionData.ProcessTxPool();
            var mined = new List<VfxTx>();
            foreach (var tx in chosen)
            {
                var (ok, why) = await Verify(tx, Tip + 1);
                Assert.True(ok, $"the producer chose a {tx.TransactionType} that its block is refused for: {why}");
                mined.Add(tx);
            }
            await Apply(Tip + 1, mined.ToArray());
            foreach (var tx in mined) TransactionData.GetPool().DeleteManySafe(x => x.Hash == tx.Hash);
            return mined;
        }

        private async Task<VfxTx> MineOne(TransactionType type)
        {
            var pooled = TransactionData.GetPool().FindAll().ToList();
            var mined = await MineMempool();
            if (!mined.Any(t => t.TransactionType == type))
            {
                var why = new List<string>();
                foreach (var tx in pooled)
                    why.Add($"{tx.TransactionType} {tx.Hash}: verify={(await TransactionValidatorService.VerifyTX(tx)).Item2}; replay={await TransactionData.DoubleSpendReplayCheck(tx, skipDebitGuard: true)}; crafted={await TransactionData.HasTxBeenCraftedIntoBlock(tx)}; sized={VerifiedXCore.Utilities.MempoolSizeUtility.SizeMempoolDown(new List<VfxTx> { tx }).Count}; guard={SameBlockDebitGuard.DropSameBlockOverspends(new List<VfxTx> { tx }).Count}; keep={BtcAttestBlockRules.KeepOnePerBlock(new List<VfxTx> { tx }).Count}; dup={TransactionData.DropDuplicateContractCreations(new List<VfxTx> { tx }).Count}; stillPooled={TransactionData.GetPool().Exists(x => x.Hash == tx.Hash)}");
                Assert.True(false, $"no {type} was mined; the mempool held {pooled.Count}: " + string.Join(" | ", why));
            }
            return Assert.Single(mined, t => t.TransactionType == type);
        }

        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            foreach (var part in BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, BtcAttestRules.EpochOf(Tip + 1)))
            {
                var tx = Signed(part);
                var (ok, why) = await Verify(tx, Tip + 1);
                Assert.True(ok, why);
                await Apply(Tip + 1, tx);
            }
        }

        private async Task<BtcTx> DepositOnBitcoin(string address, long sats)
        {
            var tx = Pay(ScriptOf(address), sats);
            await Report(_btc.Mine(tx));
            return tx;
        }

        /// <summary>The vault's signers sign exactly the pinned transaction and it confirms; the validators report it.</summary>
        private async Task ConfirmPinned(VaultPin pin)
        {
            var tx = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(pin.Txid, tx.GetHash().ToString());
            await Report(_btc.Mine(tx));
        }

        // ── Key ceremonies ────────────────────────────────────────────────────────────

        private static readonly FieldInfo Ceremonies = typeof(VBTCController).GetField("_ceremonies", BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>
        /// A key ceremony that has completed as the validators run it: a new contract UID, a real key, every funded
        /// validator attesting. (Running one takes the validators' FROST servers; its result is what is put here.)
        /// </summary>
        private string CompletedCeremony(V owner)
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var groupKey = Convert.ToHexString(new Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var address = FrostDkgAttestation.DeriveTaprootAddress(groupKey, FrostDkgAttestation.ConsensusNetwork)!;
            var participants = FrostDkgAttestation.Canonical(Signers.Select(s => s.Address));
            const int threshold = 3; // 67% of four
            var message = FrostDkgAttestation.Message(uid, groupKey, address, owner.Address, threshold, participants);
            var proof = FrostDkgAttestation.BuildProof(uid, groupKey, address, owner.Address, threshold, participants,
                Signers.Select(s => new FrostDkgAttestation.Attestation { ValidatorAddress = s.Address, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, s.Key, s.Pub) }));
            var ceremonies = (ConcurrentDictionary<string, MPCCeremonyState>)Ceremonies.GetValue(null)!;
            ceremonies[uid] = new MPCCeremonyState
            {
                CeremonyId = uid, ContractUID = uid, Status = CeremonyStatus.Completed, OwnerAddress = owner.Address, DepositAddress = address,
                FrostGroupPublicKey = groupKey, DKGProof = proof, ValidatorSnapshot = participants, ProofBlockHeight = Tip, RequiredThreshold = 67,
                ProgressPercentage = 100, CompletedTimestamp = TimeUtil.GetTime(),
            };
            return uid;
        }

        // ── Raw: what a web wallet does ─────────────────────────────────────────────────────

        private static async Task<JObject> SignAndSend(string built, V signer, Func<RawVBTCTxSubmission, Task<string>> send)
        {
            var answer = JObject.Parse(built);
            Assert.True((bool)answer["Success"]!, (string?)answer["Message"]);
            var hash = (string)answer["Hash"]!;
            var sent = JObject.Parse(await send(new RawVBTCTxSubmission { Hash = hash, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(hash, signer.Key, signer.Pub), PublicKey = signer.Pub }));
            Assert.True((bool)sent["Success"]!, (string?)sent["Message"]);
            return sent;
        }

        private static void Ok(string answer)
        {
            var json = JObject.Parse(answer);
            Assert.True((bool)json["Success"]!, (string?)json["Message"]);
        }

        private async Task<decimal> Balance(string address, string vault) =>
            (decimal)JObject.Parse(await _api.GetVBTCBalance(address, vault))["Balance"]!;

        private static VaultWithdrawalRow Row(string requestHash, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(requestHash, vault))!;

        // ── The local path ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheLocalWallet_CreatesAVault_TakesDeposits_Transfers_AndWithdraws_AsOwnerAndAsHolder()
        {
            await Start();

            // Creation: the route writes the contract from the completed ceremony and sends it; the chain checks every
            // attestation and starts watching the address.
            var ceremony = CompletedCeremony(Minter);
            Ok(await _api.CreateVBTCContract(new VBTCContractPayload { OwnerAddress = Minter.Address, Name = "Local vault", Description = "e2e", CeremonyId = ceremony }));
            await MineOne(TransactionType.VBTC_V2_CONTRACT_CREATE);
            Assert.True(BtcAttestBalance.IsAttestedVault(ceremony));
            var address = (string)JObject.Parse(await _api.GetMPCDepositAddress(ceremony))["DepositAddress"]!;
            Assert.Equal(BtcAttestStore.ScriptRowForVault(ceremony)!.DepositAddress, address);

            // Deposits on Bitcoin, reported by the validators: the owner's vBTC.
            await DepositOnBitcoin(address, 400_000);
            await DepositOnBitcoin(address, 100_000);
            Assert.Equal(0.005M, await Balance(Minter.Address, ceremony));

            // Transfer to a holder (in this wallet too).
            Ok(await _api.TransferVBTC(new VBTCTransferPayload { SmartContractUID = ceremony, FromAddress = Minter.Address, ToAddress = _localHolder.Address, Amount = 0.002M }));
            await MineOne(TransactionType.VBTC_V2_TRANSFER);
            Assert.Equal(0.002M, await Balance(_localHolder.Address, ceremony));
            Assert.Equal(0.003M, await Balance(Minter.Address, ceremony));

            // The owner withdraws: fixed on the vault's coins in its block, paid when Bitcoin shows it.
            Ok(await _api.RequestWithdrawal(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = Minter.Address, BTCAddress = BtcDest, Amount = 0.001M, FeeRate = 10 }));
            var ownerRequest = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            var ownerRow = Row(ownerRequest.Hash, ceremony);
            Assert.Equal(VaultWithdrawalState.Pinned, ownerRow.State);
            Assert.Equal("Pinned", (string)JObject.Parse(await _api.GetWithdrawalPin(ceremony, ownerRequest.Hash))["Withdrawal"]!["State"]!);
            await ConfirmPinned(ownerRow.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Row(ownerRequest.Hash, ceremony).State);
            Assert.Single((JArray)JObject.Parse(await _api.GetWithdrawalHistory(ceremony))["WithdrawalHistory"]!);

            // The holder withdraws, raises the fee, and then gives it up: its coins come back to the vault and it is
            // refunded less that Bitcoin fee.
            Ok(await _api.RequestWithdrawal(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = _localHolder.Address, BTCAddress = BtcDest, Amount = 0.0015M, FeeRate = 2 }));
            var holderRequest = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            Assert.Equal(0.0005M, await Balance(_localHolder.Address, ceremony));
            Ok(await _api.BumpWithdrawalFee(new VBTCWithdrawalFeeBumpPayload { SmartContractUID = ceremony, RequestorAddress = _localHolder.Address, WithdrawalRequestHash = holderRequest.Hash, FeeRate = 4 }));
            await MineOne(TransactionType.VBTC_V2_COIN_PIN);
            Assert.Equal(2, Row(holderRequest.Hash, ceremony).Pins.Count);
            Ok(await _api.ReleaseWithdrawal(new VBTCWithdrawalFeeBumpPayload { SmartContractUID = ceremony, RequestorAddress = _localHolder.Address, WithdrawalRequestHash = holderRequest.Hash, FeeRate = 20 }));
            await MineOne(TransactionType.VBTC_V2_COIN_PIN);
            var release = Row(holderRequest.Hash, ceremony).Pins.Last();
            Assert.True(release.IsRelease);
            await ConfirmPinned(release);
            Assert.Equal(VaultWithdrawalState.Released, Row(holderRequest.Hash, ceremony).State);
            Assert.Equal(0.0005M + (150_000 - release.FeeSats) / BtcAttestBalance.SatsPerBtc, await Balance(_localHolder.Address, ceremony));

            // The holder asks for more than the vault's free coins hold, and cancels it while it waits.
            Ok(await _api.RequestWithdrawal(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = Minter.Address, BTCAddress = BtcDest, Amount = 0.0019M, FeeRate = 10 }));
            var pinnedSecond = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            var before = await Balance(_localHolder.Address, ceremony);
            Ok(await _api.RequestWithdrawal(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = _localHolder.Address, BTCAddress = BtcDest, Amount = before, FeeRate = 10 }));
            var waiting = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting.Hash, ceremony).State);
            Ok(await _api.CancelWithdrawal(new VBTCCancellationPayload { SmartContractUID = ceremony, OwnerAddress = _localHolder.Address, WithdrawalRequestHash = waiting.Hash, BTCTxHash = "", FailureProof = "" }));
            await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_CANCEL);
            Assert.Equal(VaultWithdrawalState.Cancelled, Row(waiting.Hash, ceremony).State);
            Assert.Equal(before, await Balance(_localHolder.Address, ceremony));
            // The history lists the two that were refunded, and how: given up (released), and cancelled.
            var refunded = (JArray)JObject.Parse(await _api.GetWithdrawalHistory(ceremony))["Refunded"]!;
            Assert.Equal(new[] { "Released", "Cancelled" }, refunded.Select(r => (string)r["Outcome"]!));
            await ConfirmPinned(Row(pinnedSecond.Hash, ceremony).Pins[0]);

            // What the vault holds on Bitcoin is what everyone holds on the ledger, and what was paid out left it.
            var held = BtcAttestStore.Total(ceremony).HeldSats;
            Assert.Equal(held, (long)((await Balance(Minter.Address, ceremony) + await Balance(_localHolder.Address, ceremony)) * BtcAttestBalance.SatsPerBtc));
        }

        // ── The raw path ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The same local-wallet path with the nonce rule (TXHeightRule4) on: the block builder's nonce ordering skipped
        /// every Bitcoin report and re-key (their shared sender name's "next nonce" counted the mempool's own copy), so
        /// none would ever have been proposed (fourth review, round 2).
        /// </summary>
        [Fact]
        public async Task TheLocalWalletPath_AlsoRuns_WithTheNonceRuleOn()
        {
            var prior = Globals.TXHeightRule4;
            Globals.TXHeightRule4 = 0;
            try { await TheLocalWallet_CreatesAVault_TakesDeposits_Transfers_AndWithdraws_AsOwnerAndAsHolder(); }
            finally { Globals.TXHeightRule4 = prior; }
        }

        [Fact]
        public async Task AWebWallet_CreatesAVault_TakesDeposits_Transfers_AndWithdraws_AsOwnerAndAsHolder_SigningOnlyHashes()
        {
            await Start();

            // Creation, raw: the node builds the contract from the completed ceremony and returns its hash.
            var ceremony = CompletedCeremony(_web);
            var unique = Guid.NewGuid().ToString("N");
            var now = TimeUtil.GetTime();
            var signedRequest = VerifiedXCore.Services.SignatureService.CreateSignature($"{_web.Address}Web vaulte2eVBTC{ceremony}{now}{unique}", _web.Key, _web.Pub);
            await SignAndSend(await _api.GetRawCreateContractTxData(new VBTCContractRawPayload
            {
                OwnerAddress = _web.Address, Name = "Web vault", Description = "e2e", Ticker = "VBTC", CeremonyId = ceremony, Timestamp = now, UniqueId = unique, OwnerSignature = signedRequest,
            }), _web, _api.SendRawCreateContractTx);
            await MineOne(TransactionType.VBTC_V2_CONTRACT_CREATE);
            Assert.True(BtcAttestBalance.IsAttestedVault(ceremony));
            var address = BtcAttestStore.ScriptRowForVault(ceremony)!.DepositAddress;

            await DepositOnBitcoin(address, 300_000);
            Assert.Equal(0.003M, await Balance(_web.Address, ceremony));

            // Transfer, raw.
            await SignAndSend(await _api.GetRawTransferVBTCData(new VBTCTransferPayload { SmartContractUID = ceremony, FromAddress = _web.Address, ToAddress = _webHolder.Address, Amount = 0.0012M }),
                _web, _api.SendRawTransferVBTCTx);
            await MineOne(TransactionType.VBTC_V2_TRANSFER);
            Assert.Equal(0.0012M, await Balance(_webHolder.Address, ceremony));

            // The holder (not the owner) withdraws, raw: fixed, paid.
            await SignAndSend(await _api.GetRawRequestWithdrawalTxData(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = _webHolder.Address, BTCAddress = BtcDest, Amount = 0.0005M, FeeRate = 3 }),
                _webHolder, _api.SendRawRequestWithdrawalTx);
            var holderRequest = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            Assert.Equal(VaultWithdrawalState.Pinned, Row(holderRequest.Hash, ceremony).State);
            // A raised fee, raw; then Bitcoin confirms the raised one.
            await SignAndSend(await _api.GetRawBumpWithdrawalFeeTxData(new VBTCWithdrawalFeeBumpPayload { SmartContractUID = ceremony, RequestorAddress = _webHolder.Address, WithdrawalRequestHash = holderRequest.Hash, FeeRate = 9 }),
                _webHolder, _api.SendRawBumpWithdrawalFeeTx);
            await MineOne(TransactionType.VBTC_V2_COIN_PIN);
            var raised = Row(holderRequest.Hash, ceremony).Pins.Last();
            Assert.Equal(9, raised.FeeRate);
            await ConfirmPinned(raised);
            Assert.Equal(VaultWithdrawalState.Settled, Row(holderRequest.Hash, ceremony).State);
            Assert.Equal(0.0007M, await Balance(_webHolder.Address, ceremony));

            // The owner withdraws, raw, and gives it up, raw.
            await SignAndSend(await _api.GetRawRequestWithdrawalTxData(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = _web.Address, BTCAddress = BtcDest, Amount = 0.001M, FeeRate = 2 }),
                _web, _api.SendRawRequestWithdrawalTx);
            var ownerRequest = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            await SignAndSend(await _api.GetRawReleaseWithdrawalTxData(new VBTCWithdrawalFeeBumpPayload { SmartContractUID = ceremony, RequestorAddress = _web.Address, WithdrawalRequestHash = ownerRequest.Hash, FeeRate = 15 }),
                _web, _api.SendRawReleaseWithdrawalTx);
            await MineOne(TransactionType.VBTC_V2_COIN_PIN);
            var release = Row(ownerRequest.Hash, ceremony).Pins.Last();
            Assert.True(release.IsRelease);
            await ConfirmPinned(release);
            Assert.Equal(VaultWithdrawalState.Released, Row(ownerRequest.Hash, ceremony).State);

            // A request that waits (more than the free coins hold) is cancelled, raw.
            var all = await Balance(_web.Address, ceremony);
            await SignAndSend(await _api.GetRawRequestWithdrawalTxData(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = _webHolder.Address, BTCAddress = BtcDest, Amount = 0.0007M, FeeRate = 10 }),
                _webHolder, _api.SendRawRequestWithdrawalTx);
            var second = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            await SignAndSend(await _api.GetRawRequestWithdrawalTxData(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = _web.Address, BTCAddress = BtcDest, Amount = all, FeeRate = 10 }),
                _web, _api.SendRawRequestWithdrawalTx);
            var waiting = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            Assert.Equal(VaultWithdrawalState.Waiting, Row(waiting.Hash, ceremony).State);
            await SignAndSend(await _api.GetRawCancelWithdrawalTxData(new VBTCWithdrawalCancelTxPayload { SmartContractUID = ceremony, RequestorAddress = _web.Address, WithdrawalRequestHash = waiting.Hash }),
                _web, _api.SendRawCancelWithdrawalTx);
            await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_CANCEL);
            Assert.Equal(VaultWithdrawalState.Cancelled, Row(waiting.Hash, ceremony).State);
            Assert.Equal(all, await Balance(_web.Address, ceremony));
            await ConfirmPinned(Row(second.Hash, ceremony).Pins[0]);
            Assert.Equal(0M, await Balance(_webHolder.Address, ceremony));

            var held = BtcAttestStore.Total(ceremony).HeldSats;
            Assert.Equal(held, (long)(await Balance(_web.Address, ceremony) * BtcAttestBalance.SatsPerBtc));
            // Nothing of this went through the node's wallet.
            Assert.Null(AccountData.GetSingleAccount(_web.Address));
            Assert.Null(AccountData.GetSingleAccount(_webHolder.Address));
        }

        // ── A vault moved to a new key, then used both ways ──────────────────────────────────

        [Fact]
        public async Task AfterTheVaultIsMovedToANewKey_BothPathsWithdrawFromIt_AndNothingIsLost()
        {
            await Start();
            var ceremony = CompletedCeremony(Minter);
            Ok(await _api.CreateVBTCContract(new VBTCContractPayload { OwnerAddress = Minter.Address, Name = "Moved vault", Description = "e2e", CeremonyId = ceremony }));
            await MineOne(TransactionType.VBTC_V2_CONTRACT_CREATE);
            await DepositOnBitcoin(BtcAttestStore.ScriptRowForVault(ceremony)!.DepositAddress, 500_000);
            Ok(await _api.TransferVBTC(new VBTCTransferPayload { SmartContractUID = ceremony, FromAddress = Minter.Address, ToAddress = _webHolder.Address, Amount = 0.001M }));
            await MineOne(TransactionType.VBTC_V2_TRANSFER);

            // One of the four key holders leaves: at the next epoch the key is due.
            var stillHere = Signers.Take(3).ToList();
            var epochStart = H1 + ((Tip - H1) / BtcAttestRules.EpochBlocks + 1) * BtcAttestRules.EpochBlocks;
            var h = epochStart - 20;
            foreach (var v in stillHere)
                BlockchainData.GetBlocks().InsertSafe(new VfxBlock { Height = h, Hash = $"h{h++}-{Guid.NewGuid():N}", Transactions = new List<VfxTx> { Lifecycle(v, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT) } });
            BlockchainData.GetBlocks().InsertSafe(new VfxBlock { Height = h, Hash = $"h{h}-{Guid.NewGuid():N}", Transactions = new List<VfxTx> { Lifecycle(Signers[3], TransactionType.VBTC_V2_VALIDATOR_EXIT) } });
            await Apply(epochStart);
            Assert.True(BtcAttestStore.ScriptRowForVault(ceremony)!.RekeyDueSinceVfxHeight > 0);

            // The validators of the day make the new key; the re-key goes through the mempool like any report.
            var keyUid = BtcVaultKeys.NewKeyUid(BtcAttestStore.ScriptRowForVault(ceremony)!); // the UID the chain fixed for it
            var groupKey = Convert.ToHexString(new Key().PubKey.ToBytes()[1..]).ToLowerInvariant();
            var newAddress = FrostDkgAttestation.DeriveTaprootAddress(groupKey, FrostDkgAttestation.ConsensusNetwork)!;
            var participants = FrostDkgAttestation.Canonical(stillHere.Select(s => s.Address));
            var message = FrostDkgAttestation.Message(keyUid, groupKey, newAddress, stillHere[0].Address, FrostDkgAttestation.RekeyThreshold(participants.Count), participants, rekeyOf: ceremony);
            var proof = FrostDkgAttestation.BuildProof(keyUid, groupKey, newAddress, stillHere[0].Address, FrostDkgAttestation.RekeyThreshold(participants.Count), participants,
                stillHere.Select(s => new FrostDkgAttestation.Attestation { ValidatorAddress = s.Address, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, s.Key, s.Pub) }));
            var rekey = BtcAttestTxValidator.Create(TransactionType.VBTC_V2_VAULT_REKEY, new BtcRekey { ContractUID = ceremony, DKGProof = proof }.ToCanonicalJson());
            var (ok, why) = await TransactionValidatorService.VerifyTX(rekey);
            Assert.True(ok, why);
            await TransactionData.AddToPool(rekey);
            await MineOne(TransactionType.VBTC_V2_VAULT_REKEY);

            // The deposit address the API gives is the new key's; the old key's coin is fixed on its way there.
            Assert.Equal(newAddress, (string)JObject.Parse(await _api.GetMPCDepositAddress(ceremony))["DepositAddress"]!);
            var sweep = Assert.Single(BtcAttestStore.WithdrawalsOf(ceremony, VaultWithdrawalState.Pinned).Where(w => w.IsSweep));
            var ownerBefore = await Balance(Minter.Address, ceremony);
            await ConfirmPinned(sweep.Pins[0]);
            var fee = sweep.Pins[0].FeeSats;
            Assert.Equal(ownerBefore - fee / BtcAttestBalance.SatsPerBtc, await Balance(Minter.Address, ceremony));
            Assert.Equal(0.001M, await Balance(_webHolder.Address, ceremony));    // the holder is untouched

            // A deposit to the new address, then a holder's raw withdrawal and the owner's local one: both from the new key.
            await DepositOnBitcoin(newAddress, 50_000);
            await SignAndSend(await _api.GetRawRequestWithdrawalTxData(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = _webHolder.Address, BTCAddress = BtcDest, Amount = 0.001M, FeeRate = 10 }),
                _webHolder, _api.SendRawRequestWithdrawalTx);
            var holderRequest = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            Ok(await _api.RequestWithdrawal(new VBTCWithdrawalPayload { SmartContractUID = ceremony, RequestorAddress = Minter.Address, BTCAddress = BtcDest, Amount = 0.002M, FeeRate = 10 }));
            var ownerRequest = await MineOne(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST);
            var newScript = ScriptOf(newAddress).ToHex();
            foreach (var request in new[] { holderRequest, ownerRequest })
            {
                var row = Row(request.Hash, ceremony);
                Assert.Equal(VaultWithdrawalState.Pinned, row.State);
                Assert.Equal(newScript, BtcVaultKeys.KeyOfCoins(row.Pins[0].Inputs)!.ScriptHex);
                await ConfirmPinned(row.Pins[0]);
                Assert.Equal(VaultWithdrawalState.Settled, Row(request.Hash, ceremony).State);
            }
            Assert.Equal(0M, await Balance(_webHolder.Address, ceremony));

            // Everything is accounted for: what the vault holds is the owner's, and the sweep's fee is recorded.
            var total = BtcAttestStore.Total(ceremony);
            Assert.Equal(fee, total.SweepFeeSats);
            Assert.Equal(total.HeldSats, (long)(await Balance(Minter.Address, ceremony) * BtcAttestBalance.SatsPerBtc));
            Assert.All(BtcAttestStore.FreeCoinsOf(ceremony), c => Assert.Equal(newScript, c.ScriptHex));
        }

        private static VfxTx Lifecycle(V v, TransactionType type) => new()
        {
            TransactionType = type, FromAddress = v.Address, ToAddress = v.Address, Hash = $"{type}-{Guid.NewGuid():N}",
            Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "10.0.0.1", FrostPublicKey = v.Pub, IsS3C = false }),
        };
    }
}
