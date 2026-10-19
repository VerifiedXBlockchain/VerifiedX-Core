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

            // Its block is already reported: refused by state before its signatures are checked (a copy costs nothing).
            await Refused(tx, H1 + 3, "is at or below the events-through height");
            var again = Tx(report); // the same signed report in a new transaction
            again.Timestamp += 1;
            again.Build();
            Assert.NotEqual(tx.Hash, again.Hash);
            await Refused(again, H1 + 3, "is at or below the events-through height");
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
                BlockHeight = at, BlockHash = _btc.Header(at).Hash, Headers = _btc.HeaderHexes(First, _btc.Tip),
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

            async Task Changed(Action<BtcReport> change, string because = "is not valid")
            {
                var report = Forward(at, throughTip: _btc.Tip - 1);
                report.Signatures = Signers.Take(3).Select(v => Sign(v, report)).ToList();
                change(report);
                await Refused(Tx(report), H1 + 2, because);
            }

            await Changed(r => r.Events.RemoveAt(1));                     // an event left out after signing
            await Changed(r => r.Events.RemoveAt(0));
            await Changed(r => r.Headers.Add(_btc.Header(_btc.Tip).Hex)); // a header added
            await Changed(r => r.Headers.RemoveAt(r.Headers.Count - 1));
            await Changed(r => r.Scope = vault.Uid, "a backfill report is for a block whose events were already reported"); // refused by state first
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
            pool.InsertSafe(otherSigners);
            Assert.True((await TransactionValidatorService.VerifyTX(first)).Item1);
            // A copy at the held one's timestamp that sorts before it by hash is refused before its signatures are checked:
            // anyone could make such variants from the gossiped signatures, each a full check (fourth review, round 6).
            // The copy held is the one that sorts last, so that every other copy at its timestamp sorts before it.
            var variants = Signers.Select((_, i) => Signers.Where((_, j) => j != i))
                .Select(subset => { var t = Signed(Forward(at), subset); t.Timestamp = first.Timestamp; t.Build(); return t; })
                .Append(first).GroupBy(t => t.Hash).Select(g => g.First()).ToList();
            Assert.True(variants.Count > 1);
            var held = variants.OrderByDescending(t => t.Hash, StringComparer.Ordinal).First();
            if (held.Hash != first.Hash)
            {
                pool.DeleteMany(x => x.Hash == first.Hash);
                held.TransactionRating = await TransactionRatingService.GetTransactionRating(held);
                pool.InsertSafe(held);
            }
            foreach (var tie in variants.Where(t => t.Hash != held.Hash))
            {
                var (tieOk, tieWhy) = await TransactionValidatorService.VerifyTX(tie);
                Assert.False(tieOk);
                Assert.Contains("already in the mempool", tieWhy);
            }
            if (held.Hash != first.Hash)
            {
                pool.DeleteMany(x => x.Hash == held.Hash);
                pool.InsertSafe(first);
            }
            // Admitting the earliest takes the later copies of the same report out of the mempool (they used to pile up).
            Assert.Null(pool.FindOne(x => x.Hash == second.Hash));
            Assert.Null(pool.FindOne(x => x.Hash == otherSigners.Hash));
            Assert.NotNull(pool.FindOne(x => x.Hash == first.Hash));
            await Accepted(second, H1 + 2);

            // A copy enters a mempool only while it is fresh: one timed at the far edge of what is still accepted would
            // be "the earliest", push the honest copies out, and be too old to mine a moment later. A copy already held
            // is judged as before, and a block may carry a copy of any accepted age.
            var old = Tx(report);
            old.Timestamp = first.Timestamp - 600;
            old.Build();
            var (fresh, staleWhy) = await TransactionValidatorService.VerifyTX(old);
            Assert.False(fresh);
            Assert.Contains("admitted within 120 seconds", staleWhy);
            Assert.NotNull(pool.FindOne(x => x.Hash == first.Hash));        // and it pushed nothing out
            await Accepted(old, H1 + 2);

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
            Assert.Equal(new[] { "c", "a", "c" }, BtcAttestBlockRules.KeepOnePerBlock(new List<VfxTx> { plain, first, second, plain }).Select(t => t.Hash));
            // A re-key after the report held back is kept: attestation transactions share a system name, not a sender (fourth review, round 3).
            var rekey = BtcAttestTxValidator.Create(TransactionType.VBTC_V2_VAULT_REKEY, new BtcRekey { ContractUID = "00000000000000000000000000000000:1", DKGProof = "" }.ToCanonicalJson());
            Assert.Equal(new[] { "a", rekey.Hash }, BtcAttestBlockRules.KeepOnePerBlock(new List<VfxTx> { first, second, rekey }).Select(t => t.Hash));
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

        /// <summary>
        /// A quiet report whose range the attested headers already bury carries no headers. It used to carry nothing
        /// else either, so its signed text was the same for every range of its epoch: sent again, it passed over the
        /// next day of blocks unread, and a deposit in them was never credited (a spend never recorded).
        /// </summary>
        [Fact]
        public async Task AQuietReport_IsAboutItsOwnRange_AndCannotBeSentAgainForTheNext()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var height = H1 + 2;
            var q1 = Checkpoint + BtcAttestRules.QuietBlocks;
            var q2 = q1 + BtcAttestRules.QuietBlocks;
            var q3 = q2 + BtcAttestRules.QuietBlocks;

            _btc.MineEmpty(BtcAttestRules.QuietBlocks + BtcAttestRules.Depth);
            var first = Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, q1 + BtcAttestRules.Depth), 0, _btc.Header(q1).Hash));
            await Accepted(first, height);
            await Apply(height++, first);

            // The attested headers run far ahead of the reported blocks (here by a replacement of the last header
            // with a long heavier branch; a deep reorganisation does the same).
            var branch = _btc.ForkAfter(Meta.TipHeight - 1);
            branch.MineEmpty(BtcAttestRules.QuietBlocks - BtcAttestRules.Depth + 2);
            var deposit = Pay(vault.Script, 70_000);
            var at = branch.Mine(deposit);                      // a deposit in the third day
            Assert.True(at > q2 && at <= q3);
            branch.MineEmpty(2 * BtcAttestRules.QuietBlocks);
            var replaceAbove = Meta.TipHeight - 1;
            var replacement = Signed(BtcBlockScanner.BuildHeaderReplacement(replaceAbove, branch.HeaderHexes(replaceAbove + 1, q3 + BtcAttestRules.Depth + 2), 0));
            await Accepted(replacement, height);
            await Apply(height++, replacement);
            Assert.True(Meta.TipHeight >= q3 + BtcAttestRules.Depth);

            // The second day is quiet and already buried: its report carries no headers.
            var second = BtcBlockScanner.BuildQuiet(new List<string>(), 0, branch.Header(q2).Hash);
            var secondTx = Signed(second);
            await Accepted(secondTx, height);
            await Apply(height++, secondTx);
            Assert.Equal(q2, Meta.EventsThroughHeight);

            // The same signed report in a new transaction, for the third day, which holds the deposit.
            var again = BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REPORT, secondTx.Data, VerifiedXCore.Utilities.TimeUtil.GetTime() + 1);
            Assert.NotEqual(secondTx.Hash, again.Hash);
            await Refused(again, height, "names the hash of the last block of its range");
            Assert.Equal(q2, Meta.EventsThroughHeight);

            // The deposit is reported as it should be.
            var report = Signed(Assert.Single(BtcBlockScanner.BuildForward(branch.Block(at), at, new List<string>(), 0)));
            await Accepted(report, height);
            await Apply(height++, report);
            Assert.Equal(70_000, BtcAttestStore.Total(vault.Uid).HeldSats);
        }

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
            var last = _btc.Header(quietThrough).Hash;
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, _btc.Tip), 0, last)), h, "no more and no fewer");
            _btc.MineEmpty(2);
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt + 1), 0, last)), h, "no more and no fewer");
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt - 1), 0, last)), h, "no more and no fewer");
            var withEvent = BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt), 0, last);
            withEvent.Events.Add(EventFor(_btc.Block(at), 1));
            await Refused(Signed(withEvent), h, "carries headers only");
            // It names the last block of its range: not another block, and not nothing.
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt), 0, _btc.Header(quietThrough - 1).Hash)), h, "names the hash of the last block of its range");
            await Refused(Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt), 0, null!)), h, "names the hash of the last block of its range");

            var quiet = Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(First, buriedAt), 0, last));
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
            var next = Signed(BtcBlockScanner.BuildQuiet(_btc.HeaderHexes(Meta.TipHeight + 1, at + BtcAttestRules.QuietBlocks + BtcAttestRules.Depth), 0, _btc.Header(at + BtcAttestRules.QuietBlocks).Hash));
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
            Assert.All(txs, t => Assert.True(TransactionValidatorService.VerifyTXSize(t, BtcAttestRules.MaxReportTxBytes)));

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
            await Refused(txs[0], height, "is expected next");
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

        /// <summary>
        /// The tree was built again for every event and each part serialised again after every event: a Bitcoin block with
        /// a few thousand small payments to a vault took longer to plan than a VFX block lasts, and the plan was dropped
        /// each time the VFX tip moved, so reporting stopped (fourth review, round 9). The tree is built once, a part's
        /// length is a running sum, and the planner builds only the part it signs. The parts are the same as before.
        /// </summary>
        [Fact]
        public async Task ABlockWithThousandsOfEvents_IsPlannedQuickly_InTheSameParts()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var at = _btc.Mine(Enumerable.Range(0, 3_000).Select(i => Pay(vault.Script, 330 + i)).ToArray());
            _btc.MineEmpty(3);
            var headers = _btc.HeaderHexes(Meta.TipHeight + 1, _btc.Tip);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(at), at, headers, 0, maxParts: 1));
            var planned = clock.Elapsed;
            Assert.True(planned < TimeSpan.FromSeconds(4), $"planning the first part took {planned.TotalSeconds:N1} s");
            Assert.False(first.Final);

            var parts = BtcBlockScanner.BuildForward(_btc.Block(at), at, headers, 0);
            Assert.Equal(3_000, parts.Sum(p => p.Events.Count));
            Assert.Equal(parts[0].ToCanonicalJson(), first.ToCanonicalJson());
            // The split the old way: every part but the last is full (the next event would pass the budget or the count).
            var slow = BtcBlockScanner.SplitSlowly(parts.SelectMany(p => p.Events).ToList(), isFirst =>
            {
                var template = isFirst ? parts[0] : parts[1];
                return new BtcReport
                {
                    Scope = template.Scope, BlockHeight = template.BlockHeight, BlockHash = template.BlockHash, Headers = template.Headers.ToList(),
                    CoinbaseBranch = template.CoinbaseBranch, SignerEpoch = template.SignerEpoch, CoinbaseTxHex = template.CoinbaseTxHex,
                    CoinbaseState = template.CoinbaseState, CoinbaseTailHex = template.CoinbaseTailHex, CoinbaseBytes = template.CoinbaseBytes,
                };
            });
            Assert.Equal(parts.Select(p => p.Events.Count), slow.Select(p => p.Events.Count));
            Assert.All(parts, p => Assert.True(p.ToCanonicalJson().Length <= BtcAttestRules.PartTextBudget));
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
            var empty = new BtcReport { BlockHeight = at, BlockHash = _btc.Header(at).Hash, Part = Meta.PendingNextPart, Final = true };
            await Accepted(Signed(empty), height);
            // It names the block it closes. One that names no block, or another block of that height, closes nothing:
            // signed once, such a part could otherwise be sent again for whatever block stood at that height later.
            await Refused(Signed(new BtcReport { BlockHeight = at, Part = Meta.PendingNextPart, Final = true }), height, "names that block's hash");
            await Refused(Signed(new BtcReport { BlockHeight = at, BlockHash = _btc.Header(at - 1).Hash, Part = Meta.PendingNextPart, Final = true }), height, "names that block's hash");
            var notLast = new BtcReport { BlockHeight = at, BlockHash = _btc.Header(at).Hash, Part = Meta.PendingNextPart, Final = false };
            await Refused(Signed(notLast), height, "carries at least one event");

            await Apply(height++, Signed(empty));
            Assert.Equal(0, Meta.PendingNextPart);
            Assert.Equal(at, Meta.EventsThroughHeight);
            // What the closed block still holds is unrecorded, and a backfill brings it in.
            Assert.Equal(new[] { vault.Uid }, BtcBlockScanner.VaultsWithUnrecordedEvents(_btc.Block(at), at));
            var first = new BtcReport { BlockHeight = at + 1, BlockHash = _btc.Header(at + 1).Hash, Part = 1, Final = true };
            await Refused(Signed(first), height, "carries at least one event");
        }

        /// <summary>
        /// Every event is a Bitcoin transaction with its bytes and its Merkle proof; the report's signatures add
        /// nothing to it. Any transaction a Bitcoin node relays fits (100,000 bytes without witness data). A larger one
        /// is not an event, and nothing can be said about it: two thirds of the signers stating what a transaction
        /// pays, with no proof, is the one thing that could have created vBTC out of nothing.
        /// </summary>
        [Fact]
        public async Task EveryEventIsProven_ALargeTransactionToo_AndOneTooLargeToProveIsNotAnEvent()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var other = Elsewhere();
            var outputs = new List<(NBitcoin.Script, long)> { (vault.Script, 70_000) };
            outputs.AddRange(Enumerable.Range(0, 2_800).Select(_ => (other, 1_000L)));
            var big = Pay(null, outputs.ToArray());
            var size = big.WithOptions(TransactionOptions.None).ToBytes().Length;
            Assert.InRange(size, 90_000, BtcTxInclusion.MaxTxBytes);     // a payout to 2,800 addresses, one of them a vault
            var at = _btc.Mine(big);
            _btc.MineEmpty(3);

            var report = Forward(at);
            var ev = Assert.Single(report.Events);
            Assert.Equal(BtcTxInclusion.ToStrippedHex(big), ev.TxHex);
            Assert.NotNull(ev.Branch);
            var tx = Signed(report);
            Assert.True(Newtonsoft.Json.JsonConvert.SerializeObject(tx).Length < BtcAttestRules.MaxReportTxBytes);
            await Accepted(tx, H1 + 2);

            // An event with no bytes is nothing, whoever signed it.
            var bare = Forward(at);
            bare.Events[0].TxHex = null;
            bare.Events[0].Branch = null;
            await Refused(Signed(bare), H1 + 2, "an event carries its transaction and its proof");
            // And the earlier form that only named a transaction and what it paid is not a report at all.
            var listed = Signed(bare);
            listed.Data = listed.Data.Replace("{\"Position\":1}", "{\"Position\":1,\"Txid\":\"" + big.GetHash() + "\",\"Creates\":[{\"Vout\":0,\"ScriptHex\":\"" + vault.Script.ToHex() + "\",\"Sats\":7000000}]}");
            Assert.Contains("\"Creates\"", listed.Data);
            listed.Build();
            await Refused(listed, H1 + 2, "does not parse");

            await Apply(H1 + 2, tx);
            Assert.Equal(70_000, BtcAttestStore.Coin(Outpoint(big, 0))!.ValueSats);
            Assert.Equal(70_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            // Larger than any node relays (a miner would have to be handed it): not an event. Nothing is built for
            // it, nothing can be reported for it, and what it pays the vault is not credited.
            outputs.AddRange(Enumerable.Range(0, 500).Select(_ => (other, 1_000L)));
            var huge = Pay(null, outputs.ToArray());
            Assert.True(huge.WithOptions(TransactionOptions.None).ToBytes().Length > BtcTxInclusion.MaxTxBytes);
            var ordinary = Pay(vault.Script, 5_000);
            var next = _btc.Mine(huge, ordinary);
            _btc.MineEmpty(3);
            var after = Forward(next);
            Assert.Equal(BtcTxInclusion.ToStrippedHex(ordinary), Assert.Single(after.Events).TxHex);
            var forced = Forward(next);
            forced.Events.Insert(0, new BtcReportEvent { Position = 1, TxHex = BtcTxInclusion.ToStrippedHex(huge), Branch = BtcMerkle.BranchFor(_btc.Block(next).Transactions.Select(t => t.GetHash().ToString()).ToList(), 1) });
            await Refused(Signed(forced), H1 + 3, $"larger than {BtcTxInclusion.MaxTxBytes} bytes");
            var afterTx = Signed(after);
            await Accepted(afterTx, H1 + 3);
            await Apply(H1 + 3, afterTx);
            Assert.Null(BtcAttestStore.Coin(Outpoint(huge, 0)));
            Assert.Equal(75_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Empty(BtcBlockScanner.VaultsWithUnrecordedEvents(_btc.Block(next), next));   // and no backfill is looked for
        }

        /// <summary>A Bitcoin transaction spending these coins, padded past what a report can carry.</summary>
        private static NBitcoin.Transaction Padded(params NBitcoin.Transaction[] spent)
        {
            var other = Elsewhere();
            return Pay(spent.Select(t => new OutPoint(t.GetHash(), 0)), Enumerable.Range(0, 3_300).Select(_ => (other, 1_000L)).ToArray());
        }

        /// <summary>
        /// #171: a spend of a recorded vault coin inside a transaction too large to carry was never reported, so the coin
        /// kept counting as backing after it had left: a vault's key holders and one miner could leave the owner's vBTC
        /// with nothing behind it. Such a transaction is reported spend-only (its id, proven, and the coins it spends) and
        /// the coins are frozen: no longer backing, never selected, on the signers' word that they were spent.
        /// </summary>
        [Fact]
        public async Task ASpendTooLargeToCarry_OfARecordedCoin_IsReportedSpendOnly_AndFreezesIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var a = Pay(vault.Script, 50_000);
            var at = _btc.Mine(a);
            _btc.MineEmpty(3);
            await Apply(H1 + 2, Signed(Forward(at)));
            Assert.Equal(50_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            var padded = Padded(a);
            Assert.True(padded.WithOptions(TransactionOptions.None).ToBytes().Length > BtcTxInclusion.MaxTxBytes);
            var next = _btc.Mine(padded);
            _btc.MineEmpty(3);
            var ev = Assert.Single(Forward(next).Events);
            Assert.Null(ev.TxHex);
            Assert.Equal(padded.GetHash().ToString(), ev.Txid);
            Assert.Equal(new[] { Outpoint(a, 0) }, ev.Spends);

            var tx = Signed(Forward(next));
            await Refused(tx, H1 - 1, "before the recording height");
            await Accepted(tx, H1 + 3);
            await Apply(H1 + 3, tx);

            var coin = BtcAttestStore.Coin(Outpoint(a, 0))!;
            Assert.Equal(VaultCoinStatus.Frozen, coin.Status);
            Assert.Equal(padded.GetHash().ToString(), coin.SpentByTxid);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).HeldSats);                     // no longer backing
            Assert.Equal(0M, BtcAttestBalance.DepositBtc(vault.Uid));
            Assert.Empty(BtcAttestStore.FreeCoinsOf(vault.Uid));                            // never selected
            Assert.Empty(BtcBlockScanner.VaultsWithUnrecordedEvents(_btc.Block(next), next)); // nothing left to backfill
        }

        /// <summary>
        /// #171: a spend-only event names a proven, non-coinbase transaction of the block and recorded live coins, each
        /// once, in order; an event carrying bytes names neither. Which coins the transaction spends cannot be checked
        /// without its bytes: that is the signers' word, and it freezes, never spends.
        /// </summary>
        [Fact]
        public async Task ASpendOnlyEvent_NamesRecordedLiveCoinsOnce_AtAProvenNonCoinbasePosition()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var a = Pay(vault.Script, 50_000);
            var b = Pay(vault.Script, 20_000);
            var at = _btc.Mine(a, b);
            _btc.MineEmpty(3);
            await Apply(H1 + 2, Signed(Forward(at)));
            var next = _btc.Mine(Padded(a));
            _btc.MineEmpty(3);
            BtcReport With(Action<BtcReportEvent> change) { var r = Forward(next); change(r.Events[0]); return r; }

            await Refused(Signed(With(e => e.Spends = new() { BtcAttestStore.OutpointKey(new uint256(RandomUtils.GetBytes(32)).ToString(), 0) })), H1 + 3, "not a recorded unspent or pinned coin");
            await Refused(Signed(With(e => e.Spends = new() { Outpoint(a, 0), Outpoint(a, 0) })), H1 + 3, "once each, in ordinal order");
            await Refused(Signed(With(e => e.Spends = new() { Outpoint(a, 0).ToUpperInvariant() })), H1 + 3, "is not an outpoint");
            await Refused(Signed(With(e => e.Spends = new())), H1 + 3, "names its txid and the vault coins");
            await Refused(Signed(With(e => e.Position = 0)), H1 + 3, "position 0 is the coinbase");
            await Refused(Signed(With(e => e.Txid = b.GetHash().ToString())), H1 + 3, "is not in block");
            await Refused(Signed(With(e => e.TxHex = "00")), H1 + 3, "names no txid or coins");
            await Accepted(Signed(With(e => e.Spends = new() { Outpoint(b, 0) })), H1 + 3);   // the signers' word: it would freeze b
            await Apply(H1 + 3, Signed(Forward(next)));

            var c = Pay(vault.Script, 5_000);
            var again = _btc.Mine(Padded(a), c);                                            // the frozen coin, named again
            _btc.MineEmpty(3);
            var forged = Forward(again);
            var ids = _btc.Block(again).Transactions.Select(t => t.GetHash().ToString()).ToList();
            forged.Events.Insert(0, new BtcReportEvent { Position = 1, Txid = ids[1], Spends = new() { Outpoint(a, 0) }, Branch = BtcMerkle.BranchFor(ids, 1) });
            await Refused(Signed(forged), H1 + 4, "not a recorded unspent or pinned coin");
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

        /// <summary>
        /// Every proof in a block rests on the depth of the block's tree, taken from its coinbase transaction. A miner
        /// can make a coinbase as large as it likes. The block is reported all the same, with every event proven: the
        /// coinbase's last hash block and its length show that the first leaf of the tree is a transaction, which is
        /// all the depth needs.
        /// </summary>
        [Fact]
        public async Task ABlockWhoseCoinbaseIsTooLargeToCarry_IsReportedWithProofs_ItsDepthShownByTheCoinbasesLength()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var payee = Elsewhere();
            var payouts = Enumerable.Range(0, 400).Select(_ => (payee, 1_000L)).Append((vault.Script, 9_000L)).ToList();
            var deposit = Pay(vault.Script, 80_000);
            var at = _btc.MineWithCoinbase(payouts, Pay(Elsewhere(), 5), deposit);
            _btc.MineEmpty(3);
            var coinbase = _btc.Block(at).Transactions[0];
            var coinbaseBytes = coinbase.WithOptions(TransactionOptions.None).ToBytes();
            Assert.True(coinbaseBytes.Length > BtcAttestRules.MaxCoinbaseProofBytes);

            var report = Forward(at);
            Assert.Null(report.CoinbaseTxHex);
            Assert.Equal(coinbaseBytes.Length, report.CoinbaseBytes);
            Assert.Equal(64, report.CoinbaseState!.Length);
            Assert.InRange(report.CoinbaseTailHex!.Length, 2, 128);
            Assert.NotNull(report.CoinbaseBranch);
            // The coinbase pays the vault and is itself an event, carried like any transaction; so is the deposit.
            Assert.Equal(new long[] { 0, 2 }, report.Events.Select(e => e.Position));
            Assert.All(report.Events, e => Assert.NotNull(e.TxHex));
            var tx = Signed(report);
            await Accepted(tx, H1 + 2);

            // The proof is of this coinbase and no other: nothing in it can be changed.
            var longer = Forward(at);
            longer.CoinbaseBytes += 64;
            await Refused(Signed(longer), H1 + 2, "coinbase proof: the coinbase proof does not lead to block");
            var otherState = Forward(at);
            otherState.CoinbaseState = new string('0', 64);
            await Refused(Signed(otherState), H1 + 2, "coinbase proof: the coinbase proof does not lead to block");
            var wrongTail = Forward(at);
            wrongTail.CoinbaseTailHex = wrongTail.CoinbaseTailHex + "00";
            await Refused(Signed(wrongTail), H1 + 2, "coinbase proof");
            var both = Forward(at);
            both.CoinbaseTxHex = BtcTxInclusion.ToStrippedHex(_btc.Block(at - 1).Transactions[0]);
            await Refused(Signed(both), H1 + 2, "a report carries one coinbase proof");
            var none = Forward(at);
            none.CoinbaseState = null; none.CoinbaseTailHex = null; none.CoinbaseBytes = null;
            await Refused(Signed(none), H1 + 2, "coinbase proof: no coinbase proof");
            // A branch one level short (a level of the tree passed off as its leaves) fails against the depth it fixes.
            var shallow = Forward(at);
            shallow.Events[1].Branch = shallow.Events[1].Branch!.Skip(1).ToList();
            await Refused(Signed(shallow), H1 + 2, "every transaction of a block sits at the same depth");

            await Apply(H1 + 2, tx);
            Assert.Equal(89_000, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.True(BtcAttestStore.Coin(Outpoint(coinbase, 401))!.IsCoinbase);
            Assert.False(BtcAttestStore.Coin(Outpoint(deposit, 0))!.IsCoinbase);
            Assert.Equal(at, Meta.EventsThroughHeight);

            // A coinbase small enough to carry is carried: one form per coinbase.
            var small = _btc.Mine(Pay(vault.Script, 4_000));
            _btc.MineEmpty(3);
            var carried = Forward(small);
            Assert.NotNull(carried.CoinbaseTxHex);
            Assert.Null(carried.CoinbaseState);
            var smallBytes = _btc.Block(small).Transactions[0].WithOptions(TransactionOptions.None).ToBytes();
            var asTail = Forward(small);
            asTail.CoinbaseTxHex = null;
            var tail = smallBytes.Length % 64 == 0 ? 64 : smallBytes.Length % 64;
            asTail.CoinbaseState = BtcSha256.StateAfter(smallBytes, (smallBytes.Length - tail) / 64);
            asTail.CoinbaseTailHex = Convert.ToHexString(smallBytes.AsSpan(smallBytes.Length - tail)).ToLowerInvariant();
            asTail.CoinbaseBytes = smallBytes.Length;
            await Refused(Signed(asTail), H1 + 3, "is carried whole");
        }

        /// <summary>The hash a coinbase proof is finished with is SHA-256: the same result as the platform's, from any block boundary.</summary>
        [Fact]
        public void AHashFinishedFromItsStatePartWay_IsTheHash()
        {
            var random = new Random(11);
            foreach (var length in new[] { 0, 1, 55, 56, 63, 64, 65, 119, 120, 127, 128, 129, 1_000, 10_001, 65_536 })
            {
                var message = new byte[length];
                random.NextBytes(message);
                var expected = System.Security.Cryptography.SHA256.HashData(message);
                Assert.Equal(expected, BtcSha256.Hash(message));
                for (var blocks = 0; blocks <= length / 64; blocks += Math.Max(1, length / 64 / 5))
                {
                    var state = Convert.FromHexString(BtcSha256.StateAfter(message, blocks));
                    Assert.Equal(expected, BtcSha256.Finish(state, message.AsSpan(blocks * 64), length));
                }
            }
            // A state taken at anything but a block boundary, or a length that does not match, is not a proof.
            Assert.Null(BtcSha256.Finish(new byte[32], new byte[10], 100));
            Assert.Null(BtcSha256.Finish(new byte[31], new byte[10], 74));
            Assert.Null(BtcSha256.Finish(new byte[32], new byte[10], 5));
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

        /// <summary>
        /// More of a vault's unrecorded deposits in one block than one report carries: the backfill was built in parts,
        /// its first marked "not final", and every node refused it ("a backfill report has one part"); the sweep stopped
        /// at that block for every vault, and the deposits were never credited. Each backfill is one final report now,
        /// and the next carries what is left (fourth review).
        /// </summary>
        [Fact]
        public async Task ABackfillTooLargeForOneReport_IsCarriedByOneAfterAnother()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var bAddress = NewTaprootAddress();
            var at = _btc.Mine(new[] { Pay(a.Script, 5_000) }.Concat(Enumerable.Range(0, 450).Select(i => Pay(ScriptOf(bAddress), 1_000 + i))).ToArray());
            _btc.MineEmpty(3);
            var height = H1 + 2;
            await Apply(height++, Signed(Forward(at)));
            Assert.Equal(at, Meta.EventsThroughHeight);

            var b = await CreateVault(height++, bAddress);
            var reports = 0;
            while (BtcBlockScanner.VaultsWithUnrecordedEvents(_btc.Block(at), at).Contains(b.Uid))
            {
                var backfill = Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(at), at, b.Uid, 0));
                Assert.True(backfill.Final);
                Assert.Equal(1, backfill.Part);
                var tx = Signed(backfill);
                await Accepted(tx, height);
                await Apply(height++, tx);
                Assert.True(++reports < 10);
            }
            Assert.True(reports > 1, "the deposits needed more than one report");
            Assert.Equal(Enumerable.Range(0, 450).Sum(i => 1_000L + i), BtcAttestStore.Total(b.Uid).HeldSats);
            Assert.Equal(450, BtcAttestStore.CoinsOf(b.Uid).Count);
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
                Scope = c.Uid, BlockHeight = oldAt, BlockHash = _btc.Header(oldAt).Hash,
                CoinbaseTxHex = BtcTxInclusion.ToStrippedHex(_btc.Block(oldAt).Transactions[0]), CoinbaseBranch = EventFor(_btc.Block(oldAt), 0).Branch,
                Events = { EventFor(_btc.Block(oldAt), 1) },
            };
            await Refused(Signed(forced), H1 + 4, "is before vault");

            await Apply(H1 + 4, Signed(Assert.Single(BtcBlockScanner.BuildBackfill(_btc.Block(recentAt), recentAt, c.Uid, 0))));
            Assert.Equal(55_000, BtcAttestStore.Total(c.Uid).HeldSats);
        }
    }
}
