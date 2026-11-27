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
        private static void CandidateAnswers(Func<string, string?> blockBody, int delayMs = 0) =>
            Globals.HttpClientFactory = new Producer(blockBody, delayMs);

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

        [Fact]
        public async Task TheCheckTakesNoLongerThanItsBudget()
        {
            var body = JsonConvert.SerializeObject(Draft());
            CandidateAnswers(_ => body, delayMs: 1_200); // each request answers slowly: five of them take 6 s

            var sw = Stopwatch.StartNew();
            var (passed, _) = await ProofUtility.VerifyWinnerAvailability(CandidateProof(), Tip + 1, 2_000);
            sw.Stop();

            Assert.False(passed);
            Assert.InRange(sw.ElapsedMilliseconds, 1_900, 3_000);
        }

        /// <summary>A candidate's validator API: current version, at the tip, ready, and its block by path suffix.</summary>
        private sealed class Producer : IHttpClientFactory
        {
            private readonly Func<string, string?> _blockBody;
            private readonly int _delayMs;
            public Producer(Func<string, string?> blockBody, int delayMs) { _blockBody = blockBody; _delayMs = delayMs; }
            public HttpClient CreateClient(string name) => new HttpClient(new Handler(this));

            private sealed class Handler : HttpMessageHandler
            {
                private readonly Producer _p;
                public Handler(Producer p) => _p = p;
                protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                {
                    if (_p._delayMs > 0) await Task.Delay(_p._delayMs, ct);
                    var path = request.RequestUri!.AbsolutePath;
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
