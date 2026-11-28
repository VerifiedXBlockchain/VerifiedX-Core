using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The casters check a candidate before they vote for it, and fetch the block from it once it is agreed. The check
    /// asked the candidate for its block on another path and with longer timeouts than the fetch, and took any 200 as a
    /// pass, so a candidate that answered the check and then served no block, or a block that is not its own hash, was
    /// elected and waited on for the whole fetch, round after round. The check now fetches the block as the fetch does and
    /// keeps it for the round.
    /// </summary>
    [Collection("DbContextSequential")]
    public class WinnerServesItsBlockTests : IDisposable
    {
        private const long Tip = 700;
        private const string TipHash = "7a1d0a6e2b9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170600";
        private const string Candidate = "xCandidateAddress";
        private const string CandidateIp = "10.6.0.1";

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _priorAddress = Globals.ValidatorAddress;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;

        public WinnerServesItsBlockTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"winner_serves_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.ValidatorAddress = "self";
            Globals.LastBlock = new Block { Height = Tip, Hash = TipHash };
            Globals.NetworkValidators.TryRemove(Candidate, out _);
        }

        public void Dispose()
        {
            Globals.NetworkValidators.TryRemove(Candidate, out _);
            Globals.HttpClientFactory = _priorFactory;
            Globals.ValidatorAddress = _priorAddress;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block Draft()
        {
            var reward = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = Candidate, TransactionType = TransactionType.TX, Timestamp = 1_000, Amount = 0M };
            reward.Build();
            var b = new Block { Height = Tip + 1, PrevHash = TipHash, Validator = Candidate, Timestamp = 1_000, Version = 4, ChainRefId = "test", Transactions = new List<Transaction> { reward } };
            b.MerkleRoot = b.MerkleRootOf();
            b.Hash = b.GetBlockHash();
            return b;
        }

        private static Proof CandidateProof() =>
            new Proof { Address = Candidate, BlockHeight = Tip + 1, PreviousBlockHash = TipHash, ProofHash = "candidate-proof", IPAddress = CandidateIp };

        /// <summary>The candidate answers a request for its block by the request path's last segment.</summary>
        private static Producer CandidateAnswers(Func<string, string?> blockBody, int delayMs = 0) =>
            CandidateAnswers(blockBody, _ => delayMs);

        private static Producer CandidateAnswers(Func<string, string?> blockBody, Func<string, int> delayFor)
        {
            var producer = new Producer(blockBody, delayFor);
            Globals.HttpClientFactory = producer;
            return producer;
        }

        /// <summary>The candidate as this caster knows it: not seen lately, so its version is asked for.</summary>
        private static NetworkValidator KnownCandidate()
        {
            var nv = new NetworkValidator { Address = Candidate, IPAddress = CandidateIp, IsFullyTrusted = true, LastSeen = 0, CheckFailCount = 0 };
            Globals.NetworkValidators[Candidate] = nv;
            return nv;
        }

        [Fact]
        public async Task ACandidateThatAnswersTheCheckButServesTheFetchNoBlock_FailsTheCheck()
        {
            var good = JsonConvert.SerializeObject(Draft());
            CandidateAnswers(suffix => suffix == "candidate-proof" ? good : "");
            Assert.False((await ProofUtility.VerifyWinnerAvailability(CandidateProof(), Tip + 1)).Item1);
            Assert.Null(ProofUtility.VerifiedDraftOf(Candidate, Tip + 1));

            var notItsHash = Draft();
            notItsHash.Timestamp += 1; // the hash it carries is no longer its own
            var bad = JsonConvert.SerializeObject(notItsHash);
            CandidateAnswers(suffix => suffix == "candidate-proof" ? good : bad);
            Assert.False((await ProofUtility.VerifyWinnerAvailability(CandidateProof(), Tip + 1)).Item1);
        }

        [Fact]
        public async Task ACandidateThatServesItsBlock_PassesTheCheck_AndTheFetchTakesThatBlock()
        {
            var draft = Draft();
            var body = JsonConvert.SerializeObject(draft);
            CandidateAnswers(_ => body);

            var (passed, served) = await ProofUtility.VerifyWinnerAvailability(CandidateProof(), Tip + 1, 5_000);

            Assert.True(passed);
            Assert.Equal(draft.Hash, served!.Hash);
            Assert.Equal(draft.Hash, ProofUtility.VerifiedDraftOf(Candidate, Tip + 1)!.Hash);
            Assert.Null(ProofUtility.VerifiedDraftOf("someone-else", Tip + 1));
            Assert.True(ProofUtility.WasServedByItsProducer(served));

            // Once the tip moves, the kept block is not the next block any more.
            Globals.LastBlock = new Block { Height = Tip + 1, Hash = draft.Hash };
            Assert.Null(ProofUtility.VerifiedDraftOf(Candidate, Tip + 1));
        }

        /// <summary>
        /// When its budget runs out the check is over: it has returned, no request of it is still running or sent after,
        /// and it changes nothing afterwards (a cached height, a served block, the kept block).
        /// </summary>
        [Fact]
        public async Task TheCheckTakesNoLongerThanItsBudget_AndNothingOfItRunsAfter()
        {
            var draft = Draft();
            var body = JsonConvert.SerializeObject(draft);
            var nv = KnownCandidate();
            var producer = CandidateAnswers(_ => body, delayMs: 1_200); // each request answers slowly: all of them take 6 s

            var sw = Stopwatch.StartNew();
            var (passed, _) = await ProofUtility.VerifyWinnerAvailability(CandidateProof(), Tip + 1, 2_000);
            sw.Stop();
            var sentByThen = producer.Requests;

            Assert.False(passed);
            Assert.InRange(sw.ElapsedMilliseconds, 1_900, 3_000);
            await Task.Delay(5_000); // longer than the rest of the check would have taken
            Assert.Equal(sentByThen, producer.Requests);
            Assert.Equal(0, producer.InFlight);
            Assert.Equal(0, nv.LastKnownHeight);       // the height asked for when the budget ran out is not cached
            Assert.False(ProofUtility.WasServedByItsProducer(draft));
            Assert.Null(ProofUtility.VerifiedDraftOf(Candidate, Tip + 1));
        }

        /// <summary>A version request cut off by the budget is not counted against the candidate, then or later.</summary>
        [Fact]
        public async Task AVersionRequestCutOffByTheBudget_CountsNothingAgainstTheCandidate()
        {
            var nv = KnownCandidate();
            var producer = CandidateAnswers(_ => JsonConvert.SerializeObject(Draft()), path => path.EndsWith("/GetWalletVersion") ? 3_500 : 0);

            var (passed, _) = await ProofUtility.VerifyWinnerAvailability(CandidateProof(), Tip + 1, 1_000);

            Assert.False(passed);
            await Task.Delay(3_500); // past the version request's own timeout
            Assert.Equal(0, nv.CheckFailCount);
            Assert.Equal(1, producer.Requests);
            Assert.Equal(0, producer.InFlight);
        }

        /// <summary>A candidate's validator API: current version, at the tip, ready, and its block by path suffix.</summary>
        private sealed class Producer : IHttpClientFactory
        {
            private readonly Func<string, string?> _blockBody;
            private readonly Func<string, int> _delayFor;
            private int _requests, _inFlight;
            public Producer(Func<string, string?> blockBody, Func<string, int> delayFor) { _blockBody = blockBody; _delayFor = delayFor; }
            /// <summary>Requests received, and requests received and not yet answered or cancelled.</summary>
            public int Requests => Volatile.Read(ref _requests);
            public int InFlight => Volatile.Read(ref _inFlight);
            public HttpClient CreateClient(string name) => new HttpClient(new Handler(this));

            private sealed class Handler : HttpMessageHandler
            {
                private readonly Producer _p;
                public Handler(Producer p) => _p = p;
                protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                {
                    var path = request.RequestUri!.AbsolutePath;
                    Interlocked.Increment(ref _p._requests);
                    Interlocked.Increment(ref _p._inFlight);
                    try { if (_p._delayFor(path) is var wait && wait > 0) await Task.Delay(wait, ct); }
                    finally { Interlocked.Decrement(ref _p._inFlight); }
                    string? body = null;
                    if (path.EndsWith("/GetWalletVersion")) body = "99.0.0";
                    else if (path.EndsWith("/GetBlockHeight")) body = Tip.ToString();
                    else if (path.EndsWith("/ProducerReady")) body = "true";
                    else if (path.Contains("/VerifyBlock/")) body = _p._blockBody(path[(path.LastIndexOf('/') + 1)..]);
                    return body == null
                        ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
                }
            }
        }
    }
}
