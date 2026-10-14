using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Attestation.Net;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;
using BtcBlock = NBitcoin.Block;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 7: validators sign a report and bring their signatures together (plan 3.4).
    /// Nobody is chosen: each validator works out the report, signs its hash, exchanges signatures with the others,
    /// and whoever holds two thirds submits. The mempool keeps one copy and a block carries it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest07_ReportServiceTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly string? _priorValidator = Globals.ValidatorAddress;

        public BtcAttest07_ReportServiceTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            BtcReportSignatures.Clear();
            BtcReportService.SubmitWaitOverride = TimeSpan.Zero;
            // Each validator's key is in its own wallet; here they share one process, so one wallet holds them all.
            foreach (var v in Signers.Append(Unfunded))
                AccountData.GetAccounts().InsertSafe(new Account { Address = v.Address, PublicKey = v.Pub, PrivateKey = "0" + v.Key.secret.ToString("x") });
        }

        public override void Dispose()
        {
            BtcReportSignatures.Clear();
            BtcReportService.SubmitWaitOverride = null;
            Globals.ValidatorAddress = _priorValidator;
            base.Dispose();
        }

        private sealed class View : IBtcChainView
        {
            private readonly Btc _chain;
            public View(Btc chain) { _chain = chain; }
            public long TipHeight => _chain.Tip;
            public BtcHeader? HeaderAt(long height) => height >= Checkpoint - 11 && height <= TipHeight ? _chain.Header(height) : null;
            public BtcBlock? BlockAt(long height) => height >= Checkpoint - 11 && height <= TipHeight ? _chain.Block(height) : null;
        }

        private static long Tip => Globals.LastBlock.Height;
        private static readonly Func<BtcReport, string, BtcReportSignature?, CancellationToken, Task> NoNetwork = (_, _, _, _) => Task.CompletedTask;

        /// <summary>One validator's round. They share this process's signature store, which stands in for the exchange between them.</summary>
        private Task StepAs(V validator, IBtcChainView view)
        {
            Globals.ValidatorAddress = validator.Address;
            return BtcReportService.StepAsync(view, NoNetwork, CancellationToken.None);
        }

        private static List<VerifiedXCore.Models.Transaction> ReportsInMempool() =>
            TransactionData.GetPool().Find(x => x.TransactionType == TransactionType.VBTC_V2_BTC_REPORT).ToList();

        [Fact]
        public async Task Validators_EachSignTheSameReport_AndTheOneThatReachesTwoThirdsSubmitsIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            var at = _btc.Mine(deposit);
            _btc.MineEmpty(BtcAttestRules.Depth);
            var view = new View(_btc);

            await StepAs(Signers[0], view);
            await StepAs(Signers[1], view);
            Assert.Empty(ReportsInMempool()); // two of four: not two thirds

            // A validator that is registered but does not hold the validator balance signs nothing that counts.
            await StepAs(Unfunded, view);
            Assert.Empty(ReportsInMempool());

            await StepAs(Signers[2], view);
            var tx = Assert.Single(ReportsInMempool());
            Assert.Equal(TransactionRating.A, tx.TransactionRating);
            Assert.True(BtcReport.TryParse(tx.Data, out var report, out _));
            Assert.Equal(at, report!.BlockHeight);
            // Exactly the signatures required, in address order.
            Assert.Equal(3, report.Signatures!.Count);
            Assert.Equal(Signers.Take(3).Select(s => s.Address).OrderBy(a => a, StringComparer.Ordinal), report.Signatures.Select(s => s.ValidatorAddress));

            // The fourth validator arrives late: the report is already in the mempool, and it adds nothing.
            await StepAs(Signers[3], view);
            Assert.Single(ReportsInMempool());

            // The block builder takes it, the block is applied, and the deposit is in state.
            var chosen = await TransactionData.ProcessTxPool();
            Assert.Equal(tx.Hash, Assert.Single(chosen).Hash);
            await Apply(Tip + 1, chosen.ToArray());
            Assert.Equal(150_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            // Nothing is left to sign, for any of them.
            foreach (var v in Signers) await StepAs(v, view);
            Assert.Empty(await TransactionData.ProcessTxPool());
            Assert.Empty(BtcReportService.Status.Offered);
        }

        /// <summary>
        /// A signer's old signatures cannot push its signature for the report in hand out of the store. They are public
        /// once mined, valid for ever for their hash, and can be offered under any epoch: 32 of them used to fill the
        /// signer's place, and the report this node was collecting for lost that signer (second path audit, 14 October
        /// 2026). What this node has planned is kept; the rest queues as before.
        /// </summary>
        [Fact]
        public async Task ReplayedOldSignatures_DoNotPushOutTheSignatureForAReportThisNodePlanned()
        {
            await Start();
            string Sig(V v, string h) => SignatureService.CreateSignature(BtcReport.SigningMessage(h), v.Key, v.Pub);
            string Hash(int i) => i.ToString("x").PadLeft(64, 'c');
            var planned = new string('a', 64);
            var notPlanned = new string('b', 64);
            BtcReportSignatures.NotePlanned(planned);
            Assert.True(BtcReportSignatures.Accept(planned, 0, Signers[0].Address, Sig(Signers[0], planned), Tip).Ok);
            Assert.True(BtcReportSignatures.Accept(notPlanned, 0, Signers[0].Address, Sig(Signers[0], notPlanned), Tip).Ok);

            for (var i = 0; i < BtcReportSignatures.MaxReportsPerSigner + 8; i++)
                Assert.True(BtcReportSignatures.Accept(Hash(i), 0, Signers[0].Address, Sig(Signers[0], Hash(i)), Tip).Ok);

            Assert.Single(BtcReportSignatures.For(planned, 0));       // kept
            Assert.Empty(BtcReportSignatures.For(notPlanned, 0));     // the oldest of the rest: dropped, as before
            Assert.Empty(BtcReportSignatures.For(Hash(0), 0));
            Assert.Single(BtcReportSignatures.For(Hash(BtcReportSignatures.MaxReportsPerSigner + 7), 0));

            // A report planned after its signature arrived is kept from then on.
            var early = new string('d', 64);
            Assert.True(BtcReportSignatures.Accept(early, 0, Signers[1].Address, Sig(Signers[1], early), Tip).Ok);
            BtcReportSignatures.NotePlanned(early);
            for (var i = 0; i < BtcReportSignatures.MaxReportsPerSigner + 8; i++)
                Assert.True(BtcReportSignatures.Accept(Hash(100 + i), 0, Signers[1].Address, Sig(Signers[1], Hash(100 + i)), Tip).Ok);
            Assert.Single(BtcReportSignatures.For(early, 0));
        }

        /// <summary>
        /// A signer could answer a signature request with thousands of entries naming signers this node already holds,
        /// each checked again at the cost of a signature verification (and with gigabytes, read whole). A held signer's
        /// copy is not checked again, and an answer is read only as far as one holding every member goes (fourth
        /// review, round 2).
        /// </summary>
        [Fact]
        public async Task ASignatureAlreadyHeld_IsNotCheckedAgain()
        {
            await Start();
            var hash = new string('e', 64);
            var good = SignatureService.CreateSignature(BtcReport.SigningMessage(hash), Signers[0].Key, Signers[0].Pub);
            Assert.True(BtcReportSignatures.Accept(hash, 0, Signers[0].Address, good, Tip).Ok);
            var (ok, why) = BtcReportSignatures.Accept(hash, 0, Signers[0].Address, "not a signature at all", Tip);
            Assert.True(ok);
            Assert.Equal("held", why);
            Assert.Equal(good, Assert.Single(BtcReportSignatures.For(hash, 0)).Signature);
            // Another signer's bad signature is still checked and refused.
            Assert.False(BtcReportSignatures.Accept(hash, 0, Signers[1].Address, "not a signature at all", Tip).Ok);
            Assert.Equal(4096 + 200 * 1024, BtcReportService.MaxAnswerBytes(200));
        }

        [Fact]
        public async Task ASignature_IsKeptOnlyWhenValid_AndFromASignerOfItsEpoch()
        {
            await Start();
            var hash = new string('a', 64);
            string Sig(V v, string h) => SignatureService.CreateSignature(BtcReport.SigningMessage(h), v.Key, v.Pub);

            Assert.True(BtcReportSignatures.Accept(hash, 0, Signers[1].Address, Sig(Signers[1], hash), Tip).Ok);
            Assert.True(BtcReportSignatures.Accept(hash, 0, Signers[0].Address, Sig(Signers[0], hash), Tip).Ok);
            Assert.True(BtcReportSignatures.Accept(hash, 0, Signers[0].Address, Sig(Signers[0], hash), Tip).Ok); // again: no harm
            var held = BtcReportSignatures.For(hash, 0);
            Assert.Equal(new[] { Signers[0].Address, Signers[1].Address }.OrderBy(a => a, StringComparer.Ordinal), held.Select(s => s.ValidatorAddress));

            var stranger = NewKey();
            Assert.Contains("is not a signer", BtcReportSignatures.Accept(hash, 0, stranger.Address, Sig(stranger, hash), Tip).Why);
            Assert.Contains("is not a signer", BtcReportSignatures.Accept(hash, 0, Unfunded.Address, Sig(Unfunded, hash), Tip).Why);
            Assert.Contains("not valid", BtcReportSignatures.Accept(hash, 0, Signers[2].Address, Sig(Signers[3], hash), Tip).Why);
            Assert.Contains("not valid", BtcReportSignatures.Accept(hash, 0, Signers[2].Address, Sig(Signers[2], new string('b', 64)), Tip).Why);
            Assert.Contains("not a report hash", BtcReportSignatures.Accept("xyz", 0, Signers[2].Address, Sig(Signers[2], hash), Tip).Why);
            Assert.Contains("not a report hash", BtcReportSignatures.Accept(hash.ToUpperInvariant(), 0, Signers[2].Address, Sig(Signers[2], hash), Tip).Why);
            Assert.Contains("is not the current epoch", BtcReportSignatures.Accept(hash, 3, Signers[2].Address, Sig(Signers[2], hash), Tip).Why);
            Assert.Equal(2, BtcReportSignatures.For(hash, 0).Count);

            // A signer cannot make this node hold signatures without end: the oldest of its reports gives way.
            for (var i = 0; i < BtcReportSignatures.MaxReportsPerSigner; i++)
            {
                var other = i.ToString("x").PadLeft(64, 'c');
                Assert.True(BtcReportSignatures.Accept(other, 0, Signers[1].Address, Sig(Signers[1], other), Tip).Ok);
            }
            Assert.Equal(Signers[0].Address, Assert.Single(BtcReportSignatures.For(hash, 0)).ValidatorAddress);
        }

        [Fact]
        public async Task TheSignatureRoute_KeepsAValidSignature_AndAnswersWithTheOnesHeld()
        {
            await Start();
            var hash = new string('d', 64);
            BtcReportSignatureMessage From(V v, string h) => new()
            {
                ReportHash = h, SignerEpoch = 0, ValidatorAddress = v.Address,
                Signature = SignatureService.CreateSignature(BtcReport.SigningMessage(h), v.Key, v.Pub),
            };
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            async Task<(HttpStatusCode Code, JObject? Body)> Post(string body)
            {
                var r = await client.PostAsync("/frost/btcreport/signature", new StringContent(body, Encoding.UTF8, "application/json"));
                var text = await r.Content.ReadAsStringAsync();
                return (r.StatusCode, string.IsNullOrEmpty(text) ? null : JObject.Parse(text));
            }

            var first = await Post(JsonConvert.SerializeObject(From(Signers[0], hash)));
            Assert.Equal(HttpStatusCode.OK, first.Code);
            Assert.True(first.Body!.Value<bool>("Success"));
            Assert.Single(first.Body["Signatures"]!);

            // The second signer gets the first one's signature back with its own.
            var second = await Post(JsonConvert.SerializeObject(From(Signers[1], hash)));
            Assert.Equal(2, second.Body!["Signatures"]!.Count());

            // A stranger learns nothing and leaves nothing.
            var stranger = await Post(JsonConvert.SerializeObject(From(NewKey(), hash)));
            Assert.False(stranger.Body!.Value<bool>("Success"));
            Assert.Empty(stranger.Body["Signatures"]!);
            Assert.Equal(2, BtcReportSignatures.For(hash, 0).Count);

            Assert.Equal(HttpStatusCode.BadRequest, (await Post("not json")).Code);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Post(new string('x', 5000))).Code);

            // The status route is for the operator at the machine.
            var status = await client.GetAsync("/frost/btcreport/status");
            Assert.True(status.StatusCode == HttpStatusCode.OK || status.StatusCode == HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task BeforeTheRecordingHeight_AValidatorFollowsBitcoin_AndSignsNothing()
        {
            _btc.MineEmpty(5);
            Globals.ValidatorAddress = Signers[0].Address;
            await BtcReportService.StepAsync(new View(_btc), NoNetwork, CancellationToken.None);
            var status = BtcReportService.Status;
            Assert.True(status.Running);
            Assert.Equal(_btc.Tip, status.BitcoinTipHeight);
            Assert.Equal(_btc.Header(_btc.Tip).Hash, status.BitcoinTipHash);
            Assert.Contains("starts at the recording height", status.Idle);
            Assert.Empty(status.Offered);
            Assert.Empty(ReportsInMempool());
        }
    }
}
