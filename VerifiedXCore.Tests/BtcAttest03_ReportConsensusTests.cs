using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 3: the report transaction (docs/PLAN-vBTC-Bitcoin-Attestation.md, sections 3
    /// and 4). A report is judged by TransactionValidatorService.VerifyTX and applied by StateData.UpdateTreis; every test
    /// here goes through those two. The Bitcoin side is a chain of real blocks mined at regtest difficulty.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest03_ReportConsensusTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032; // the first block of a difficulty interval
        private const long First = Checkpoint + 1;
        private readonly Btc _btc = new(Checkpoint - 11);

        public BtcAttest03_ReportConsensusTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
        }

        private static AttestationMetaRow Meta => BtcAttestStore.GetMeta()!;

        /// <summary>What an honest validator builds for a Bitcoin block: the headers it has not attested yet, and the block's vault events.</summary>
        private List<BtcReport> ForwardParts(long block, long? throughTip = null, long epoch = 0) =>
            BtcBlockScanner.BuildForward(_btc.Block(block), block, _btc.HeaderHexes(Meta.TipHeight + 1, throughTip ?? _btc.Tip), epoch);

        private BtcReport Forward(long block, long? throughTip = null, long epoch = 0) => Assert.Single(ForwardParts(block, throughTip, epoch));

        private static async Task Refused(VfxTx tx, long height, string because)
        {
            var (ok, why) = await Verify(tx, height);
            Assert.False(ok, $"accepted; expected a refusal containing: {because}");
            Assert.Contains(because, why);
        }

        private static async Task Accepted(VfxTx tx, long height)
        {
            var (ok, why) = await Verify(tx, height);
            Assert.True(ok, why);
        }

        // ── Start ───────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AtTheRecordingHeight_TheViewStartsAtTheCheckpoint_AndTheFundedValidatorsBecomeTheSigners()
        {
            await Apply(H1 - 1);
            Assert.Null(BtcAttestStore.GetMeta());
            Assert.Null(BtcAttestStore.SignerSet(0));

            await Start();
            var meta = Meta;
            Assert.Equal(Checkpoint, meta.CheckpointHeight);
            Assert.Equal(Checkpoint, meta.TipHeight);
            Assert.Equal(_btc.Header(Checkpoint).Hash, meta.TipHash);
            Assert.Equal(Checkpoint, meta.EventsThroughHeight);
            Assert.Equal(H1, meta.LastModifiedHeight); // the stamp the snapshot diff copies by
            for (var h = Checkpoint - 11; h <= Checkpoint; h++)
                Assert.Equal(_btc.Header(h).Hash, BtcAttestStore.HeaderAt(h)!.Hash);
            Assert.Null(BtcAttestStore.HeaderAt(Checkpoint - 12));

            var set = BtcAttestStore.SignerSet(0)!;
            Assert.Equal(Signers.Select(s => s.Address).OrderBy(a => a, StringComparer.Ordinal), set.Addresses);
            Assert.DoesNotContain(Unfunded.Address, set.Addresses); // registered and active, but not holding the validator balance
            Assert.Equal(H1, set.AtVfxHeight);
            Assert.Equal(H1, set.LastModifiedHeight);

            // The stake is not locked, so it is checked again at every epoch.
            var moved = StateData.GetSpecificAccountStateTrei(Signers[3].Address)!;
            moved.Balance = 10M;
            StateData.GetAccountStateTrei().UpdateSafe(moved);
            await Apply(H1 + 1);
            Assert.Null(BtcAttestStore.SignerSet(1));
            await Apply(H1 + BtcAttestRules.EpochBlocks);
            var next = BtcAttestStore.SignerSet(1)!;
            Assert.Equal(3, next.Addresses.Count);
            Assert.DoesNotContain(Signers[3].Address, next.Addresses);
            Assert.Equal(4, BtcAttestStore.SignerSet(0)!.Addresses.Count); // the earlier epoch's set is a record, not rewritten
        }

        [Fact]
        public async Task BeforeTheRecordingHeight_AReportIsRefused()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 50_000));
            _btc.MineEmpty(3);
            var tx = Signed(Forward(at));
            await Accepted(tx, H1 + 2);

            await Refused(tx, H1 - 1, "not accepted before the recording height");
        }

        // ── Deposits and spends ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ADeposit_IsCounted_OnceItsBlockIsProvenAndBuried()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var row = BtcAttestStore.ScriptRowForVault(vault.Uid)!;
            Assert.Equal(vault.Script.ToHex(), row.ScriptHex);
            Assert.Equal(First, row.WatchFromBitcoinHeight);
            Assert.Equal(H1 + 1, row.LastModifiedHeight);

            var deposit = Pay(vault.Script, 150_000);
            var at = _btc.Mine(Pay(Elsewhere(), 900), deposit, Pay(Elsewhere(), 700));
            _btc.MineEmpty(2); // two blocks on top: one short of the depth

            await Refused(Signed(Forward(at)), H1 + 2, "is not under 3 attested headers");
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).HeldSats);

            _btc.MineEmpty(1);
            var report = Forward(at);
            Assert.Equal(2, Assert.Single(report.Events).Position); // only the transaction that touches a vault
            Assert.Equal(4, report.Headers.Count);
            var tx = Signed(report);
            await Accepted(tx, H1 + 2);
            Assert.True((await TransactionValidatorService.VerifyTX(tx)).Item1); // admission: judged for the block after the tip

            await Apply(H1 + 2, tx);
            var coin = BtcAttestStore.Coin(Outpoint(deposit, 0))!;
            Assert.Equal(vault.Uid, coin.SmartContractUID);
            Assert.Equal(150_000, coin.ValueSats);
            Assert.Equal(VaultCoinStatus.Unspent, coin.Status);
            Assert.Equal(at, coin.CreatedInHeight);
            Assert.False(coin.IsCoinbase);
            Assert.Equal(H1 + 2, coin.LastModifiedHeight);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).UnspentSats);
            Assert.Equal(_btc.Tip, Meta.TipHeight);
            Assert.Equal(_btc.Header(_btc.Tip).Hash, Meta.TipHash);
            Assert.Equal(at, Meta.EventsThroughHeight);
            Assert.Equal(H1 + 2, Meta.LastModifiedHeight);
            Assert.Equal(H1 + 2, BtcAttestStore.Db.GetCollection<AttestedHeaderRow>(BtcAttestStore.Headers).FindById(at).LastModifiedHeight);

            // No account was made or charged for the sender-less transaction.
            Assert.Null(StateData.GetSpecificAccountStateTrei(BtcAttestRules.SystemAddress));
        }

        [Fact]
        public async Task ASpend_WithChange_SettlesTheCoin_AndTheChangeIsANewCoin()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            var at = _btc.Mine(deposit);
            _btc.MineEmpty(3);
            await Apply(H1 + 2, Signed(Forward(at)));

            var spend = Pay(new[] { new OutPoint(deposit, 0) }, (Elsewhere(), 100_000), (vault.Script, 49_000));
            var spentAt = _btc.Mine(spend);
            _btc.MineEmpty(3);
            var tx = Signed(Forward(spentAt));
            await Accepted(tx, H1 + 3);
            await Apply(H1 + 3, tx);

            var spent = BtcAttestStore.Coin(Outpoint(deposit, 0))!;
            Assert.Equal(VaultCoinStatus.Spent, spent.Status);
            Assert.Equal(spend.GetHash().ToString(), spent.SpentByTxid);
            Assert.Equal(spentAt, spent.SpentInHeight);
            var change = BtcAttestStore.Coin(Outpoint(spend, 1))!;
            Assert.Equal(VaultCoinStatus.Unspent, change.Status);
            Assert.Equal(49_000, change.ValueSats);
            Assert.Null(BtcAttestStore.Coin(Outpoint(spend, 0))); // paid elsewhere: not a vault coin
            Assert.Equal(49_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(2, BtcAttestStore.CoinsOf(vault.Uid).Count); // a spent coin's row stays (a status, never a deletion)
        }

        [Fact]
        public async Task ACoin_CreatedAndSpentInOneBlock_IsFollowed_AndOneTransactionCanPayTwoVaults()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var b = await CreateVault(H1 + 2);
            var both = Pay(null, (a.Script, 80_000), (Elsewhere(), 5), (b.Script, 30_000));
            var spendA = Pay(new[] { new OutPoint(both, 0) }, (Elsewhere(), 79_000));
            var at = _btc.Mine(both, spendA);
            _btc.MineEmpty(3);

            var report = Forward(at);
            Assert.Equal(new long[] { 1, 2 }, report.Events.Select(e => e.Position));
            await Apply(H1 + 3, Signed(report));

            Assert.Equal(VaultCoinStatus.Spent, BtcAttestStore.Coin(Outpoint(both, 0))!.Status);
            Assert.Equal(0, BtcAttestStore.Total(a.Uid).HeldSats);
            Assert.Equal(VaultCoinStatus.Unspent, BtcAttestStore.Coin(Outpoint(both, 2))!.Status);
            Assert.Equal(30_000, BtcAttestStore.Total(b.Uid).HeldSats);
        }

        [Fact]
        public async Task TheSameReport_AppliesOnce()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            var at = _btc.Mine(deposit);
            _btc.MineEmpty(3);
            var report = Forward(at);
            var tx = Signed(report);
            await Apply(H1 + 2, tx);

            await Refused(tx, H1 + 3, "does not extend"); // its headers are already attested
            var again = Tx(report); // the same signed report in a new transaction
            again.Timestamp += 1;
            again.Build();
            Assert.NotEqual(tx.Hash, again.Hash);
            await Refused(again, H1 + 3, "does not extend");
            // The same block reported again without its headers: its events are already in.
            Assert.Empty(ForwardParts(at)); // an honest validator has nothing left to report for it
            report.Headers.Clear();
            await Refused(Signed(report), H1 + 3, "is at or below the events-through height");

            await Apply(H1 + 3, again); // even inside a block, it changes nothing
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Single(BtcAttestStore.CoinsOf(vault.Uid));
            Assert.Equal(H1 + 2, Meta.LastModifiedHeight);
        }

        // ── Which vaults are watched ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AVaultCreatedBeforeTheRecordingHeight_IsNotWatched()
        {
            var legacy = NewTaprootAddress();
            var created = await CreateVault(H1 - 50, legacy);
            await Start();
            var current = await CreateVault(H1 + 1);
            Assert.Null(BtcAttestStore.ScriptRowForVault(created.Uid));

            var toLegacy = Pay(created.Script, 60_000);
            var at = _btc.Mine(toLegacy);
            _btc.MineEmpty(3);
            Assert.Empty(ForwardParts(at)); // nothing to report

            // Reported anyway, by hand: refused, the transaction touches no watched vault.
            var forced = new BtcReport
            {
                BlockHeight = at, Headers = _btc.HeaderHexes(First, _btc.Tip),
                CoinbaseTxHex = BtcTxInclusion.ToStrippedHex(_btc.Block(at).Transactions[0]), CoinbaseBranch = EventFor(_btc.Block(at), 0).Branch,
                Events = { EventFor(_btc.Block(at), 1) },
            };
            await Refused(Signed(forced), H1 + 2, "touches no vault coin");
            Assert.Equal(0, BtcAttestStore.Total(current.Uid).HeldSats);
        }

        [Fact]
        public async Task AContractThatCopiesAVaultsDepositAddress_GetsNoCoins()
        {
            await Start();
            var real = await CreateVault(H1 + 1);
            var copy = await CreateVault(H1 + 2, real.Address);
            Assert.Null(BtcAttestStore.ScriptRowForVault(copy.Uid));
            Assert.Equal(real.Uid, BtcAttestStore.ScriptRow(real.Script.ToHex())!.SmartContractUID);

            var at = _btc.Mine(Pay(real.Script, 40_000));
            _btc.MineEmpty(3);
            await Apply(H1 + 3, Signed(Forward(at)));
            Assert.Equal(40_000, BtcAttestStore.Total(real.Uid).HeldSats);
            Assert.Equal(0, BtcAttestStore.Total(copy.Uid).HeldSats);
        }

        // ── Signatures ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AReport_NeedsTwoThirdsOfTheEpochsSigners_EachOnce_EachValid()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 50_000));
            _btc.MineEmpty(3);
            const long h = H1 + 2;

            Assert.Equal(3, BtcAttestRules.RequiredSignatures(4));
            await Accepted(Signed(Forward(at), Signers.Take(3)), h);
            await Accepted(Signed(Forward(at), Signers), h);

            await Refused(Signed(Forward(at), Signers.Take(2)), h, "3 of 4 are required");
            await Refused(Signed(Forward(at), Array.Empty<V>()), h, "3 of 4 are required");
            await Refused(Signed(Forward(at), Signers.Take(2).Append(Unfunded)), h, "is not a signer in epoch 0");
            await Refused(Signed(Forward(at), Signers.Take(2).Append(NewKey())), h, "is not a signer in epoch 0");

            var twice = Forward(at);
            twice.Signatures = new[] { Signers[0], Signers[1], Signers[1] }.Select(v => Sign(v, twice)).ToList();
            await Refused(Tx(twice), h, "signed twice");

            // A signature over another report.
            var other = Forward(at);
            other.Signatures = Signers.Take(3).Select(v => Sign(v, other)).ToList();
            var borrowed = Forward(at);
            borrowed.Events[0].Position = 7;
            borrowed.Signatures = other.Signatures;
            await Refused(Tx(borrowed), h, "is not valid");

            // Another validator's signature under a signer's name.
            var forged = Forward(at);
            forged.Signatures = Signers.Take(3).Select(v => Sign(v, forged)).ToList();
            forged.Signatures[0].Signature = Sign(Signers[3], forged).Signature;
            await Refused(Tx(forged), h, "is not valid");
        }

        [Fact]
        public async Task TheSignaturesCoverEveryField()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 50_000), Pay(vault.Script, 60_000));
            _btc.MineEmpty(4);

            async Task Changed(Action<BtcReport> change)
            {
                var report = Forward(at, throughTip: _btc.Tip - 1);
                report.Signatures = Signers.Take(3).Select(v => Sign(v, report)).ToList();
                change(report);
                await Refused(Tx(report), H1 + 2, "is not valid");
            }

            await Changed(r => r.Events.RemoveAt(1));                     // an event left out after signing
            await Changed(r => r.Events.RemoveAt(0));
            await Changed(r => r.Headers.Add(_btc.Header(_btc.Tip).Hex)); // a header added
            await Changed(r => r.Headers.RemoveAt(r.Headers.Count - 1));
            await Changed(r => r.Scope = vault.Uid);
            await Changed(r => r.Final = false);
            await Changed(r => r.CoinbaseBranch = new List<string>(r.CoinbaseBranch!) { new string('0', 64) });
        }

        [Fact]
        public async Task AReport_CountsUnderTheCurrentEpochOrTheOneBeforeIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 50_000));
            _btc.MineEmpty(3);

            var tx = Signed(Forward(at)); // epoch 0
            await Accepted(tx, H1 + 2);
            await Accepted(tx, H1 + 2 * BtcAttestRules.EpochBlocks - 1); // the last block of epoch 1
            await Refused(tx, H1 + 2 * BtcAttestRules.EpochBlocks, "the report is signed under epoch 0; the current epoch is 2");

            await Refused(Signed(Forward(at, epoch: 1)), H1 + 2, "the report is signed under epoch 1; the current epoch is 0");
            // In epoch 1, but that epoch's first block has not been applied here: there is no set to count against.
            await Refused(Signed(Forward(at, epoch: 1)), H1 + BtcAttestRules.EpochBlocks + 1, "epoch 1 has no signer set");
        }

        // ── The transaction around a report ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheTransactionAroundAReport_HasNoSender_NoValue_AndOneForm()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 50_000));
            _btc.MineEmpty(3);
            var report = Forward(at);
            report.Signatures = Signers.Take(3).Select(v => Sign(v, report)).ToList();

            VfxTx Make(Action<VfxTx> change, bool rebuild = true)
            {
                var tx = Tx(report);
                change(tx);
                if (rebuild) tx.Build();
                return tx;
            }

            var good = Make(_ => { });
            await Accepted(good, H1 + 2);
            Assert.Equal(BtcAttestRules.SystemAddress, good.FromAddress);
            Assert.Equal(BtcAttestRules.SystemAddress, good.ToAddress);

            // What peers receive is the same transaction: the hash survives a JSON round trip.
            var received = JsonConvert.DeserializeObject<VfxTx>(JsonConvert.SerializeObject(good))!;
            Assert.Equal(good.Hash, received.GetHash());
            await Accepted(received, H1 + 2);

            var bad = new List<VfxTx>
            {
                Make(t => t.FromAddress = Signers[0].Address),
                Make(t => t.ToAddress = Signers[0].Address),
                Make(t => t.Amount = 1.0M),
                Make(t => t.Fee = 0.00001M),
                Make(t => t.Nonce = 1),
                Make(t => t.Signature = SignatureService.CreateSignature(t.Hash, Signers[0].Key, Signers[0].Pub)),
                Make(t => t.UnlockTime = 5),
                Make(t => t.Data = t.Data + " "),
                Make(t => t.Data = JToken.Parse(t.Data!).ToString(Formatting.Indented)),
                Make(t => { var o = JObject.Parse(t.Data!); o["Extra"] = 1; t.Data = o.ToString(Formatting.None); }),
                Make(t => t.Data = ""),
                Make(t => t.Hash = new string('0', 64), rebuild: false),
                Make(t => t.Timestamp -= Globals.MaxTxAgeSeconds + 60),
                Make(t => t.TransactionType = TransactionType.VBTC_V2_COIN_PIN),
                Make(t => t.TransactionType = TransactionType.VBTC_V2_BTC_REORG),
            };
            for (var i = 0; i < bad.Count; i++)
                Assert.False((await Verify(bad[i], H1 + 2)).Ok, $"case {i} was accepted");
        }

        [Fact]
        public async Task TheMempool_KeepsOneCopyOfAReport_AndTheBlockBuilderTakesIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 50_000));
            _btc.MineEmpty(3);
            var report = Forward(at);
            var first = Signed(report);
            var second = Tx(report); // the same signed report, submitted by another validator a moment later
            second.Timestamp += 1;
            second.Build();
            var unsigned = Signed(Forward(at), Signers.Take(1));
            unsigned.Timestamp -= 1;
            unsigned.Build();

            var pool = TransactionData.GetPool();
            foreach (var tx in new[] { first, second, unsigned })
            {
                tx.TransactionRating = await TransactionRatingService.GetTransactionRating(tx); // as the node does on receipt
                Assert.Equal(TransactionRating.A, tx.TransactionRating);
                pool.InsertSafe(tx);
            }

            // One copy per report in the mempool: the earliest. A later copy is not admitted while it is there, with
            // other signatures or not; block validation does not read the mempool and accepts any valid copy.
            var (admitted, why) = await TransactionValidatorService.VerifyTX(second);
            Assert.False(admitted);
            Assert.Contains("already in the mempool", why);
            var otherSigners = Signed(Forward(at), Signers.Skip(1));
            otherSigners.Timestamp = second.Timestamp;
            otherSigners.Build();
            Assert.False((await TransactionValidatorService.VerifyTX(otherSigners)).Item1);
            Assert.True((await TransactionValidatorService.VerifyTX(first)).Item1);
            await Accepted(second, H1 + 2);

            var chosen = await TransactionData.ProcessTxPool();
            Assert.Equal(first.Hash, Assert.Single(chosen).Hash);
            Assert.Null(pool.FindOne(x => x.Hash == unsigned.Hash)); // never valid: deleted
            Assert.Null(pool.FindOne(x => x.Hash == second.Hash));   // the later copy: deleted
            Assert.NotNull(pool.FindOne(x => x.Hash == first.Hash));

            await Apply(H1 + 2, chosen.ToArray());
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            // The report is in: what is left of it in the mempool can no longer be mined and goes.
            Assert.Empty(await TransactionData.ProcessTxPool());
            Assert.Empty(pool.Find(x => x.TransactionType == TransactionType.VBTC_V2_BTC_REPORT));
        }

        [Fact]
        public void ABlock_CarriesAtMostOneReport()
        {
            var first = new VfxTx { Hash = "a", TransactionType = TransactionType.VBTC_V2_BTC_REPORT };
            var second = new VfxTx { Hash = "b", TransactionType = TransactionType.VBTC_V2_BTC_REPORT };
            var plain = new VfxTx { Hash = "c", TransactionType = TransactionType.TX };

            // Block validation (BlockValidatorService registers each transaction of the block in turn).
            var state = new BtcAttestBlockRules.State();
            Assert.True(BtcAttestBlockRules.TryRegister(plain, state).Ok);
            Assert.True(BtcAttestBlockRules.TryRegister(first, state).Ok);
            Assert.True(BtcAttestBlockRules.TryRegister(plain, state).Ok);
            var (ok, reason) = BtcAttestBlockRules.TryRegister(second, state);
            Assert.False(ok);
            Assert.Equal(BtcAttestBlockRules.OneReportReason, reason);

            // Block building (TransactionData.ProcessTxPool): the first stays, the rest wait for the next block.
            Assert.Equal(new[] { "c", "a", "c" }, BtcAttestBlockRules.KeepOneReport(new List<VfxTx> { plain, first, second, plain }).Select(t => t.Hash));
        }

        // ── Proofs ──────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnEvent_MustBeProvenInItsBlock_InBlockOrder_AndTouchAVault()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var d1 = Pay(vault.Script, 50_000);
            var d2 = Pay(vault.Script, 60_000);
            var at = _btc.Mine(d1, Pay(Elsewhere(), 5_000), d2, Pay(Elsewhere(), 6_000));
            _btc.MineEmpty(3);
            var block = _btc.Block(at);
            const long h = H1 + 2;

            await Accepted(Signed(Forward(at)), h);

            async Task Bad(Action<BtcReport> change, string because)
            {
                var report = Forward(at);
                change(report);
                await Refused(Signed(report), h, because);
            }

            // A transaction of the block that touches no vault is not an event.
            await Bad(r => r.Events.Insert(1, EventFor(block, 2)), "touches no vault coin");
            // A branch that does not lead to the header.
            await Bad(r => r.Events[0].Branch![0] = new string('1', 64), "is not in block");
            await Bad(r => r.Events[0].Position = 2, "is not in block");
            // Bitcoin's tree puts every transaction at the coinbase's depth.
            await Bad(r => r.Events[0].Branch!.RemoveAt(r.Events[0].Branch!.Count - 1), "every transaction of a block sits at the same depth");
            // Order, and each transaction once.
            await Bad(r => r.Events.Reverse(), "events are in block order");
            await Bad(r => r.Events.Add(EventFor(block, 3)), "events are in block order");
            await Bad(r => r.Events.Clear(), "a report about a block carries at least one event");
            // The coinbase proof fixes the depth.
            await Bad(r => r.CoinbaseTxHex = null, "coinbase proof");
            await Bad(r => r.CoinbaseTxHex = r.Events[0].TxHex, "coinbase proof");
            // A transaction of another block.
            var elsewhere = _btc.ForkAfter(at - 1);
            var foreign = Pay(vault.Script, 1_000_000);
            elsewhere.Mine(foreign, Pay(Elsewhere(), 1), Pay(Elsewhere(), 2), Pay(Elsewhere(), 3)); // a tree of the same depth
            await Bad(r => r.Events[0] = EventFor(elsewhere.Block(at), 1), "is not in block");
            // The bytes are the transaction without witness data, exactly.
            await Bad(r => r.Events[0].TxHex = r.Events[0].TxHex!.ToUpperInvariant(), "event at position 1");
            await Bad(r => r.Events[0].TxHex = d1.ToHex() + "00", "event at position 1");
        }

        [Fact]
        public async Task Headers_MustBeBitcoin_AndExtendTheAttestedTip()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Pay(vault.Script, 50_000));
            _btc.MineEmpty(3);
            const long h = H1 + 2;

            async Task Bad(Action<BtcReport> change, string because)
            {
                var report = Forward(at);
                change(report);
                await Refused(Signed(report), h, because);
            }

            await Bad(r => r.Headers.RemoveAt(0), "does not extend");
            await Bad(r => r.Headers.Reverse(), "does not extend");
            await Bad(r => r.Headers[1] = r.Headers[1].Substring(0, 158) + (r.Headers[1].EndsWith("00") ? "01" : "00"), "header");
            await Bad(r => r.Headers[0] = r.Headers[0].ToUpperInvariant(), "is not 80 bytes of lowercase hex");
            await Bad(r => r.Headers[0] = r.Headers[0] + "00", "is not 80 bytes of lowercase hex");
            // The target is the one the rules require, not the one the header claims.
            await Bad(r => r.Headers[0] = WithBits(r.Headers[0], "ffff7f21"), "the rules require");

            // Another chain's headers, however valid among themselves.
            var other = new Btc(First, time: 1_770_000_000);
            other.MineEmpty(4);
            await Bad(r => r.Headers = other.HeaderHexes(First, First + 3), "does not extend");

            // A block at or below the checkpoint is never reported.
            await Bad(r => r.BlockHeight = Checkpoint, "at or below the checkpoint");
        }

        private static string WithBits(string headerHex, string bitsLittleEndianHex) => headerHex.Substring(0, 144) + bitsLittleEndianHex + headerHex.Substring(152);

        // ── Headers only, and replacing headers ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task ADayWithNoVaultEvent_IsOneQuietReport_OverAFixedRange()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            const long h = H1 + 2;
            var quietThrough = Checkpoint + BtcAttestRules.QuietBlocks;
            var buriedAt = quietThrough + BtcAttestRules.Depth;

            _btc.MineEmpty(BtcAttestRules.QuietBlocks);
            var deposit = Pay(vault.Script, 90_000);
            var at = _btc.Mine(deposit); // the first block after the quiet day
            Assert.Equal(quietThrough + 1, at);
            _btc.MineEmpty(1);
            Assert.Equal(buriedAt - 1, _btc.Tip); // the day is not buried yet

            // The range and the headers are fixed by state: not fewer, not more, and never with an event.
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, _btc.Tip), 0)), h, "no more and no fewer");
            _btc.MineEmpty(2);
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt + 1), 0)), h, "no more and no fewer");
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt - 1), 0)), h, "no more and no fewer");
            var withEvent = BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt), 0);
            withEvent.Events.Add(EventFor(_btc.Block(at), 1));
            await Refused(Signed(withEvent), h, "carries headers only");

            var quiet = Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt), 0));
            await Accepted(quiet, h);
            await Apply(h, quiet);
            Assert.Equal(buriedAt, Meta.TipHeight);
            Assert.Equal(quietThrough, Meta.EventsThroughHeight);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).HeldSats);
            await Refused(quiet, h + 1, "does not extend"); // once

            // The deposit just after the quiet day is reported as usual, with only the header it still needs.
            var report = Forward(at);
            Assert.Equal(_btc.Header(at + BtcAttestRules.Depth).Hex, Assert.Single(report.Headers));
            var tx = Signed(report);
            await Accepted(tx, h + 1);
            await Apply(h + 1, tx);
            Assert.Equal(90_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(at, Meta.EventsThroughHeight);

            // The next quiet day starts from there; headers already attested are not carried again.
            _btc.MineEmpty(BtcAttestRules.QuietBlocks);
            var next = Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(Meta.TipHeight + 1, at + BtcAttestRules.QuietBlocks + BtcAttestRules.Depth), 0));
            await Accepted(next, h + 2);
            await Apply(h + 2, next);
            Assert.Equal(at + BtcAttestRules.QuietBlocks, Meta.EventsThroughHeight);
        }

        [Fact]
        public async Task ABitcoinReorganisation_AboveTheReportedEvents_ReplacesHeaders_WhenItCarriesMoreWork()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            var at = _btc.Mine(deposit); // First
            _btc.MineEmpty(3);           // tip First + 3
            await Apply(H1 + 2, Signed(Forward(at)));
            const long h = H1 + 3;
            var keep = at + 1;

            var equal = _btc.ForkAfter(keep);
            equal.MineEmpty(2);
            await Refused(Signed(BtcBlockScanner.BuildHeaderReplacement(keep, equal.HeaderHexes(keep + 1, equal.Tip), 0)), h, "do not carry more work");

            var underEvents = _btc.ForkAfter(at - 1);
            underEvents.MineEmpty(6);
            await Refused(Signed(BtcBlockScanner.BuildHeaderReplacement(at - 1, underEvents.HeaderHexes(at, underEvents.Tip), 0)), h, "cannot be replaced by this report");

            await Refused(Signed(BtcBlockScanner.BuildHeaderReplacement(_btc.Tip, new List<string> { equal.Header(equal.Tip).Hex }, 0)), h, "nothing above that height to replace");

            var heavier = _btc.ForkAfter(keep);
            heavier.MineEmpty(3);
            await Refused(Signed(BtcBlockScanner.BuildHeaderReplacement(keep - 1, heavier.HeaderHexes(keep + 1, heavier.Tip), 0)), h, "does not extend");

            var tx = Signed(BtcBlockScanner.BuildHeaderReplacement(keep, heavier.HeaderHexes(keep + 1, heavier.Tip), 0));
            await Accepted(tx, h);
            await Apply(h, tx);

            Assert.Equal(heavier.Tip, Meta.TipHeight);
            Assert.Equal(heavier.Header(heavier.Tip).Hash, Meta.TipHash);
            Assert.Equal(heavier.Header(keep + 1).Hash, BtcAttestStore.HeaderAt(keep + 1)!.Hash);
            Assert.NotEqual(_btc.Header(keep + 1).Hash, BtcAttestStore.HeaderAt(keep + 1)!.Hash);
            Assert.Equal(_btc.Header(keep).Hash, BtcAttestStore.HeaderAt(keep)!.Hash);
            Assert.Equal(at, Meta.EventsThroughHeight);
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).HeldSats); // the deposit's block was not touched

            // The chain goes on from the new branch.
            var next = Pay(vault.Script, 10_000);
            var nextAt = heavier.Mine(next);
            heavier.MineEmpty(3);
            var onNewBranch = BtcBlockScanner.BuildForward(heavier.Block(nextAt), nextAt, heavier.HeaderHexes(Meta.TipHeight + 1, heavier.Tip), 0);
            var forward = Signed(Assert.Single(onNewBranch));
            await Accepted(forward, h + 1);
            await Apply(h + 1, forward);
            Assert.Equal(160_000, BtcAttestStore.Total(vault.Uid).HeldSats);
        }

        // ── Large blocks and large transactions ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task ABlockWithManyEvents_IsReportedInParts_AppliedInOrder()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposits = Enumerable.Range(0, 450).Select(i => Pay(vault.Script, 1_000 + i)).ToArray();
            var at = _btc.Mine(deposits);
            var later = _btc.Mine(Pay(vault.Script, 7));
            _btc.MineEmpty(3);

            var parts = ForwardParts(at);
            Assert.True(parts.Count >= 2, $"expected several parts, got {parts.Count}");
            Assert.Equal(Enumerable.Range(1, parts.Count), parts.Select(p => p.Part));
            Assert.All(parts.Take(parts.Count - 1), p => Assert.False(p.Final));
            Assert.True(parts[^1].Final);
            Assert.NotEmpty(parts[0].Headers);
            Assert.All(parts.Skip(1), p => Assert.Empty(p.Headers));
            Assert.Equal(450, parts.Sum(p => p.Events.Count));
            var txs = parts.Select(p => Signed(p)).ToList();
            Assert.All(txs, t => Assert.True(TransactionValidatorService.VerifyTXSize(t, Globals.MaxVbtcVaultTxSizeBytes)));

            var height = H1 + 2;
            await Refused(txs[1], height, "the first part of this block's report has not been applied");
            await Accepted(txs[0], height);
            await Apply(height, txs[0]);
            Assert.Equal(2, Meta.PendingNextPart);
            Assert.Equal(at, Meta.PendingPartBlockHeight);
            Assert.Equal(Checkpoint, Meta.EventsThroughHeight); // not until the last part
            Assert.Equal(parts[0].Events.Count, BtcAttestStore.CoinsOf(vault.Uid).Count);

            // Nothing else may come between the parts.
            height++;
            await Refused(txs[0], height, "does not extend");
            await Refused(Signed(BtcBlockScanner.BuildForward(_btc.Block(later), later, new List<string>(), 0).Single()), height, "is expected next");
            var fork = _btc.ForkAfter(at);
            fork.MineEmpty(6);
            await Refused(Signed(BtcBlockScanner.BuildHeaderReplacement(at, fork.HeaderHexes(at + 1, fork.Tip), 0)), height, "cannot interrupt a multi-part report");
            if (parts.Count > 2)
                await Refused(txs[2], height, "is expected next");

            // The parts that follow are built from the state the mined part left. Nothing else changed here, so
            // they are the parts planned at the start, byte for byte.
            for (var i = 1; i < parts.Count; i++)
            {
                var rest = BtcBlockScanner.BuildForwardRest(_btc.Block(at), at, Meta.PendingLastPosition, Meta.PendingNextPart, 0);
                Assert.Equal(parts.Count - i, rest.Count);
                parts[i].Signatures = null;
                Assert.Equal(parts[i].ToCanonicalJson(), rest[0].ToCanonicalJson());
                var tx = Signed(rest[0]);
                await Accepted(tx, height);
                await Apply(height++, tx);
            }
            Assert.Equal(0, Meta.PendingNextPart);
            Assert.Equal(0, Meta.PendingPartBlockHeight);
            Assert.Equal(at, Meta.EventsThroughHeight);
            Assert.Equal(450, BtcAttestStore.CoinsOf(vault.Uid).Count);
            Assert.Equal(deposits.Sum(d => d.Outputs[0].Value.Satoshi), BtcAttestStore.Total(vault.Uid).HeldSats);

            // And the next block follows.
            var next = Signed(BtcBlockScanner.BuildForward(_btc.Block(later), later, new List<string>(), 0).Single());
            await Accepted(next, height);
        }

        [Fact]
        public async Task AVaultCreatedBetweenTheParts_IsInTheRest_AndWhatCameBeforeIsABackfill()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var bAddress = NewTaprootAddress();
            var early = Pay(ScriptOf(bAddress), 11_000);
            var late = Pay(ScriptOf(bAddress), 22_000);
            var deposits = Enumerable.Range(0, 450).Select(i => Pay(a.Script, 1_000 + i));
            var at = _btc.Mine(new[] { early }.Concat(deposits).Append(late).ToArray());
            _btc.MineEmpty(3);

            var parts = ForwardParts(at);
            Assert.True(parts.Count >= 2);
            Assert.Equal(450, parts.Sum(p => p.Events.Count)); // vault B is not known yet
            var height = H1 + 2;
            await Apply(height++, Signed(parts[0]));

            var b = await CreateVault(height++, bAddress);
            Assert.True(BtcAttestStore.ScriptRowForVault(b.Uid)!.WatchFromBitcoinHeight <= at);

            // The parts planned before the vault existed are still valid, but the validators build the rest from the
            // state as it is now: it holds vault B's later deposit, and never a transaction of a part already mined.
            while (Meta.PendingNextPart != 0)
            {
                var rest = BtcBlockScanner.BuildForwardRest(_btc.Block(at), at, Meta.PendingLastPosition, Meta.PendingNextPart, 0);
                Assert.All(rest.SelectMany(r => r.Events), e => Assert.True(e.Position > Meta.PendingLastPosition));
                var tx = Signed(rest[0]);
                await Accepted(tx, height);
                await Apply(height++, tx);
            }
            Assert.Equal(at, Meta.EventsThroughHeight);
            Assert.Equal(450, BtcAttestStore.CoinsOf(a.Uid).Count);
            Assert.Equal(22_000, BtcAttestStore.Total(b.Uid).HeldSats);

            // The earlier deposit sits before the parts already mined: a backfill for vault B, found by one pass.
            Assert.Equal(new[] { b.Uid }, BtcBlockScanner.VaultsWithUnrecordedEvents(_btc.Block(at), at));
            var backfill = Signed(Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(at), at, b.Uid, 0)));
            await Accepted(backfill, height);
            await Apply(height++, backfill);
            Assert.Equal(33_000, BtcAttestStore.Total(b.Uid).HeldSats);
            Assert.Empty(BtcBlockScanner.VaultsWithUnrecordedEvents(_btc.Block(at), at));
        }

        [Fact]
        public async Task AClosingPart_WithNothingLeft_EndsTheBlock()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Enumerable.Range(0, 450).Select(i => Pay(vault.Script, 1_000 + i)).ToArray());
            _btc.MineEmpty(3);
            var parts = ForwardParts(at);
            var height = H1 + 2;
            await Apply(height++, Signed(parts[0]));

            // An empty report is only ever the last part of a block already begun.
            var empty = new BtcReport { BlockHeight = at, Part = Meta.PendingNextPart, Final = true };
            await Accepted(Signed(empty), height);
            var notLast = new BtcReport { BlockHeight = at, Part = Meta.PendingNextPart, Final = false };
            await Refused(Signed(notLast), height, "carries at least one event");

            await Apply(height++, Signed(empty));
            Assert.Equal(0, Meta.PendingNextPart);
            Assert.Equal(at, Meta.EventsThroughHeight);
            // What the closed block still holds is unrecorded, and a backfill brings it in.
            Assert.Equal(new[] { vault.Uid }, BtcBlockScanner.VaultsWithUnrecordedEvents(_btc.Block(at), at));
            var first = new BtcReport { BlockHeight = at + 1, Part = 1, Final = true };
            await Refused(Signed(first), height, "carries at least one event");
        }

        [Fact]
        public async Task ATransactionTooLargeToCarry_IsListedOnTheSignaturesAlone()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var other = Elsewhere();
            var outputs = new List<(NBitcoin.Script, long)> { (vault.Script, 70_000) };
            outputs.AddRange(Enumerable.Range(0, 1_300).Select(_ => (other, 1_000L)));
            var big = Pay(null, outputs.ToArray());
            Assert.True(big.ToBytes().Length > BtcTxInclusion.MaxTxBytes);
            var at = _btc.Mine(big);
            _btc.MineEmpty(3);

            var report = Forward(at);
            var ev = Assert.Single(report.Events);
            Assert.Null(ev.TxHex);
            Assert.Null(ev.Branch);
            Assert.Equal(big.GetHash().ToString(), ev.Txid);
            var created = Assert.Single(ev.Creates!);
            Assert.Equal(0, created.Vout);
            Assert.Equal(70_000, created.Sats);

            var tx = Signed(report);
            await Accepted(tx, H1 + 2);

            // The listing is what the signers signed: nothing in it can be changed afterwards.
            var changed = Forward(at);
            changed.Signatures = report.Signatures;
            changed.Events[0].Creates![0].Sats = 7_000_000;
            await Refused(Tx(changed), H1 + 2, "is not valid");
            // A listing may not also carry bytes, and must name outputs of a watched vault.
            var mixed = Forward(at);
            mixed.Events[0].Branch = new List<string>();
            await Refused(Signed(mixed), H1 + 2, "an oversize event carries no branch");
            var nothing = Forward(at);
            nothing.Events[0].Creates![0].ScriptHex = other.ToHex();
            await Refused(Signed(nothing), H1 + 2, "touches no vault coin");

            await Apply(H1 + 2, tx);
            Assert.Equal(70_000, BtcAttestStore.Coin(Outpoint(big, 0))!.ValueSats);
            Assert.Equal(70_000, BtcAttestStore.Total(vault.Uid).HeldSats);
        }

        [Fact]
        public async Task ACoinbasePayingAVault_IsACoin_MarkedAsOne()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.MineWithCoinbase(new[] { (vault.Script, 25_000L) });
            _btc.MineEmpty(3);

            var report = Forward(at);
            Assert.Equal(0, Assert.Single(report.Events).Position);
            var tx = Signed(report);
            await Accepted(tx, H1 + 2);
            await Apply(H1 + 2, tx);

            var coin = Assert.Single(BtcAttestStore.CoinsOf(vault.Uid));
            Assert.True(coin.IsCoinbase); // not spendable on Bitcoin for 100 blocks
            Assert.Equal(25_000, coin.ValueSats);
            Assert.Equal(BtcAttestStore.OutpointKey(_btc.Block(at).Transactions[0].GetHash().ToString(), 1), coin.Outpoint);
        }

        [Fact]
        public async Task ABlockWhoseCoinbaseIsTooLargeToCarry_IsStillReported_OnTheSignaturesAlone()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var payee = Elsewhere();
            var payouts = Enumerable.Range(0, 400).Select(_ => (payee, 1_000L)).Append((vault.Script, 9_000L)).ToList();
            var deposit = Pay(vault.Script, 80_000);
            var at = _btc.MineWithCoinbase(payouts, Pay(Elsewhere(), 5), deposit);
            _btc.MineEmpty(3);
            var coinbase = _btc.Block(at).Transactions[0];
            Assert.True(coinbase.ToBytes().Length > BtcAttestRules.MaxCoinbaseProofBytes);

            var report = Forward(at);
            Assert.Null(report.CoinbaseTxHex);
            Assert.Null(report.CoinbaseBranch);
            Assert.Equal(new long[] { 0, 2 }, report.Events.Select(e => e.Position));
            Assert.All(report.Events, e => Assert.Null(e.TxHex));
            var tx = Signed(report);
            await Accepted(tx, H1 + 2);

            // Listings and a coinbase proof do not mix; a coinbase listing spends nothing.
            var withCoinbase = Forward(at);
            withCoinbase.CoinbaseTxHex = BtcTxInclusion.ToStrippedHex(_btc.Block(at - 1).Transactions[0]);
            withCoinbase.CoinbaseBranch = new List<string>();
            await Refused(Signed(withCoinbase), H1 + 2, "carries no coinbase proof");
            var spending = Forward(at);
            spending.Events[0].Spends = new List<string> { Outpoint(deposit, 0) };
            await Refused(Signed(spending), H1 + 2, "a coinbase transaction spends nothing");

            await Apply(H1 + 2, tx);
            Assert.Equal(89_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.True(BtcAttestStore.Coin(Outpoint(coinbase, 401))!.IsCoinbase);
            Assert.False(BtcAttestStore.Coin(Outpoint(deposit, 0))!.IsCoinbase);
            Assert.Equal(at, Meta.EventsThroughHeight);
        }

        // ── A vault created after its deposit was mined ─────────────────────────────────────────────────────

        [Fact]
        public async Task ADeposit_MinedBeforeItsVaultWasCreated_IsReportedForThatVaultAlone()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var bAddress = NewTaprootAddress(); // the address exists before the contract does
            var toA = Pay(a.Script, 20_000);
            var toB = Pay(ScriptOf(bAddress), 33_000);
            var at = _btc.Mine(toA, toB);
            _btc.MineEmpty(3);

            var forward = Forward(at);
            Assert.Equal(1, Assert.Single(forward.Events).Position); // only vault A is known
            await Apply(H1 + 2, Signed(forward));
            Assert.Equal(at, Meta.EventsThroughHeight);

            var b = await CreateVault(H1 + 3, bAddress);
            Assert.Equal(First, BtcAttestStore.ScriptRowForVault(b.Uid)!.WatchFromBitcoinHeight);
            const long h = H1 + 4;

            var backfill = Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(at), at, b.Uid, 0));
            Assert.Equal(b.Uid, backfill.Scope);
            Assert.Empty(backfill.Headers);
            Assert.Equal(2, Assert.Single(backfill.Events).Position);

            // Scoped to one vault: it cannot bring another vault's transaction, or name a vault that is not there.
            var withA = Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(at), at, b.Uid, 0));
            withA.Events.Insert(0, EventFor(_btc.Block(at), 1));
            await Refused(Signed(withA), h, "touches no vault coin of " + b.Uid);
            var unknown = Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(at), at, b.Uid, 0));
            unknown.Scope = "00000000000000000000000000000000:1";
            await Refused(Signed(unknown), h, "is known to the Bitcoin view");

            var tx = Signed(backfill);
            await Accepted(tx, h);
            await Apply(h, tx);
            Assert.Equal(33_000, BtcAttestStore.Total(b.Uid).HeldSats);
            Assert.Equal(20_000, BtcAttestStore.Total(a.Uid).HeldSats);
            Assert.Equal(at, Meta.EventsThroughHeight);
            await Refused(tx, h + 1, "touches no vault coin"); // once

            // A block not yet reported belongs to the forward report, which now sees both vaults.
            var more = Pay(b.Script, 1_500);
            var moreAt = _btc.Mine(more);
            _btc.MineEmpty(3);
            var early = Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(moreAt), moreAt, b.Uid, 0));
            await Refused(Signed(early), h + 1, "a backfill report is for a block whose events were already reported");
            await Apply(h + 1, Signed(Forward(moreAt)));
            Assert.Equal(34_500, BtcAttestStore.Total(b.Uid).HeldSats);
        }

        [Fact]
        public async Task ABackfill_ReachesBackAboutADay_NoFurther()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var cAddress = NewTaprootAddress();
            var old = Pay(ScriptOf(cAddress), 44_000);
            var oldAt = _btc.Mine(old);                   // First
            _btc.MineEmpty(BtcAttestRules.WatchBackBlocks + 5);
            var recent = Pay(ScriptOf(cAddress), 55_000);
            var recentAt = _btc.Mine(recent);
            var toA = Pay(a.Script, 1_000);
            var at = _btc.Mine(toA);
            _btc.MineEmpty(3);
            await Apply(H1 + 2, Signed(Forward(at)));

            var c = await CreateVault(H1 + 3, cAddress);
            var watchFrom = BtcAttestStore.ScriptRowForVault(c.Uid)!.WatchFromBitcoinHeight;
            Assert.Equal(at - BtcAttestRules.WatchBackBlocks + 1, watchFrom);
            Assert.True(oldAt < watchFrom && recentAt >= watchFrom);

            Assert.Empty(BtcBlockScanner.BuildBackfill(_btc.Block(oldAt), oldAt, c.Uid, 0)); // the scanner does not see it either
            var forced = new BtcReport
            {
                Scope = c.Uid, BlockHeight = oldAt,
                CoinbaseTxHex = BtcTxInclusion.ToStrippedHex(_btc.Block(oldAt).Transactions[0]), CoinbaseBranch = EventFor(_btc.Block(oldAt), 0).Branch,
                Events = { EventFor(_btc.Block(oldAt), 1) },
            };
            await Refused(Signed(forced), H1 + 4, "is before vault");

            await Apply(H1 + 4, Signed(Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(recentAt), recentAt, c.Uid, 0))));
            Assert.Equal(55_000, BtcAttestStore.Total(c.Uid).HeldSats);
        }
    }
}
