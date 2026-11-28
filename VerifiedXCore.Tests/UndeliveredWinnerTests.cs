using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A winner the casters agreed on that served no block, or served copies no hash agreement could form on, was elected
    /// again in every round at the height: the proofs, and so the lowest VRF, do not change within a height, and the
    /// failures it was charged with were never read. The chain waited on it round after round. Each caster that went
    /// through such a round now takes the next candidate at that height, and only there.
    /// </summary>
    [Collection("DbContextSequential")]
    public class UndeliveredWinnerTests : IDisposable
    {
        private const long Tip = 500;
        private const string TipHash = "4c1d0a6e2b9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170600";

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _priorAddress = Globals.ValidatorAddress;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly long _priorSeedHeight = Globals.VrfSeedHeight;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;
        private readonly List<(string Address, string Pub)> _validators = new();

        public UndeliveredWinnerTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"undelivered_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            BlockcasterNode.ForgetUndeliveredWinners();
            Globals.VrfSeedHeight = long.MaxValue; // the parent's hash seeds the VRF here
            Globals.ValidatorAddress = "self";
            Globals.LastBlock = new Block { Height = Tip, Hash = TipHash };
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = "self", PeerIP = "10.8.0.9" },
                new Peers { ValidatorAddress = "c1", PeerIP = "10.8.0.1" },
                new Peers { ValidatorAddress = "c2", PeerIP = "10.8.0.2" },
                new Peers { ValidatorAddress = "c3", PeerIP = "10.8.0.3" },
                new Peers { ValidatorAddress = "c4", PeerIP = "10.8.0.4" },
            };
            for (var i = 0; i < 5; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                _validators.Add((AccountData.GetHumanAddress(pub), pub));
            }
        }

        public void Dispose()
        {
            BlockcasterNode.ForgetUndeliveredWinners();
            Globals.HttpClientFactory = _priorFactory;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorAddress;
            Globals.LastBlock = _priorLastBlock;
            Globals.VrfSeedHeight = _priorSeedHeight;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>Every validator's proof for the next height, as GenerateProofs makes them.</summary>
        private async Task<List<Proof>> ProofsForNextHeight()
        {
            var height = Globals.LastBlock.Height + 1;
            var proofs = new List<Proof>();
            foreach (var (address, pub) in _validators)
            {
                var (vrf, hash) = await ProofUtility.CreateProof(address, pub, height, Globals.LastBlock.Hash);
                proofs.Add(new Proof { Address = address, PublicKey = pub, BlockHeight = height, PreviousBlockHash = Globals.LastBlock.Hash, VRFNumber = vrf, ProofHash = hash, IPAddress = "10.7.0.1" });
            }
            return proofs;
        }

        private static List<string> ByVrf(List<Proof> proofs) => proofs.OrderBy(p => p.VRFNumber).Select(p => p.Address).ToList();

        [Fact]
        public async Task AWinnerThatServedNoBlock_IsNotThisCastersCandidateAgainAtThatHeight_ButIsAtTheNext()
        {
            var height = Tip + 1;
            var proofs = await ProofsForNextHeight();
            var order = ByVrf(proofs);
            Assert.Equal(order[0], (await ProofUtility.SortProofs(BlockcasterNode.WithoutUndeliveredWinners(proofs, height)))!.Address);
            var reasonBefore = ProofUtility.ProducerIneligibility(order[0], height);
            ProofUtility.ValidateIncomingProof(proofs.First(p => p.Address == order[0]), height, TipHash, out var incomingBefore);

            // The round's end: agreed on the lowest VRF, no block from it or from any caster.
            await BlockcasterNode.SettleAgreedRoundAsync(height, order[0], order[0], null, "10.7.0.1");

            Assert.Equal(order[1], (await ProofUtility.SortProofs(BlockcasterNode.WithoutUndeliveredWinners(proofs, height)))!.Address);
            // Only this caster's own pick changes: the proof checks, which also judge the other casters' votes, do not.
            Assert.Equal(reasonBefore, ProofUtility.ProducerIneligibility(order[0], height));
            ProofUtility.ValidateIncomingProof(proofs.First(p => p.Address == order[0]), height, TipHash, out var incomingAfter);
            Assert.Equal(incomingBefore, incomingAfter);

            // A second agreed winner that served nothing at the same height is left out as well.
            await BlockcasterNode.SettleAgreedRoundAsync(height, order[1], order[1], null, "10.7.0.1");
            Assert.Equal(order[2], (await ProofUtility.SortProofs(BlockcasterNode.WithoutUndeliveredWinners(proofs, height)))!.Address);

            // The tip moves: the height is over and nothing is left out at the next one.
            Globals.LastBlock = new Block { Height = height, Hash = "5d2e1b7f3c0a9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928170611" };
            var next = await ProofsForNextHeight();
            Assert.Equal(next.Count, BlockcasterNode.WithoutUndeliveredWinners(next, height + 1).Count);
            Assert.Equal(proofs.Count, BlockcasterNode.WithoutUndeliveredWinners(proofs, height).Count);
        }

        [Fact]
        public async Task LeavingOutEveryCandidate_LeavesOutNone_AndARestartForgetsThem()
        {
            var height = Tip + 1;
            var proofs = await ProofsForNextHeight();
            foreach (var p in proofs)
                Assert.True(BlockcasterNode.NoteUndeliveredWinner(height, p.Address, "test"));
            Assert.Equal(proofs.Count, BlockcasterNode.WithoutUndeliveredWinners(proofs, height).Count);

            BlockcasterNode.ForgetUndeliveredWinners();
            var order = ByVrf(proofs);
            Assert.True(BlockcasterNode.NoteUndeliveredWinner(height, order[0], "test"));
            BlockcasterNode.ForgetUndeliveredWinners(); // as a restart does: nothing is kept
            Assert.Equal(proofs.Count, BlockcasterNode.WithoutUndeliveredWinners(proofs, height).Count);
        }

        [Fact]
        public async Task ARoundEndedByABlockThatArrivedMeanwhile_LeavesNobodyOut()
        {
            var height = Tip + 1;
            var proofs = await ProofsForNextHeight();
            var order = ByVrf(proofs);
            Globals.LastBlock = new Block { Height = height, Hash = "6e3f2c8a4d1b0a9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a3928172" };

            await BlockcasterNode.SettleAgreedRoundAsync(height, order[0], order[0], null, "10.7.0.1");

            Globals.LastBlock = new Block { Height = Tip, Hash = TipHash };
            Assert.Equal(proofs.Count, BlockcasterNode.WithoutUndeliveredWinners(proofs, height).Count);
        }

        /// <summary>
        /// The agreed winner served each caster a different block, each its own hash: no hash reaches a quorum, the round is
        /// rejected, and the winner is not this caster's candidate again at the height.
        /// </summary>
        [Fact]
        public async Task AWinnerWhoseCopiesReachNoHashAgreement_IsNotThisCastersCandidateAgain()
        {
            var height = Tip + 1;
            var proofs = await ProofsForNextHeight();
            var order = ByVrf(proofs);
            var winner = order[0];
            Block Copy(long stamp)
            {
                var b = new Block { Height = height, PrevHash = TipHash, Validator = winner, Timestamp = stamp, Transactions = new List<Transaction>(), Version = 4, ChainRefId = "test" };
                b.Hash = b.GetBlockHash();
                return b;
            }
            var ours = Copy(1_000);
            var answers = new Dictionary<string, string>();
            for (var i = 1; i <= 4; i++)
                answers[$"10.8.0.{i}"] = $"{{\"Hash\":\"{Copy(1_000 + i).Hash}\",\"Validator\":\"{winner}\",\"Height\":{height},\"Committed\":false}}";
            Globals.HttpClientFactory = new HashAnswers(answers);

            await BlockcasterNode.SettleAgreedRoundAsync(height, winner, winner, ours, "10.7.0.1");

            Assert.Equal(Tip, Globals.LastBlock.Height);
            Assert.Equal(order[1], (await ProofUtility.SortProofs(BlockcasterNode.WithoutUndeliveredWinners(proofs, height)))!.Address);
        }

        /// <summary>
        /// Five casters' candidates. The three that went through the failed round name the next candidate, the two that
        /// did not still name the failed winner; every caster's vote is then the next candidate, a quorum of the five.
        /// With the names split evenly, a caster that went through the failed round never votes for that winner.
        /// </summary>
        [Fact]
        public async Task CastersThatSawTheWinnerFail_CarryTheVoteToTheNextCandidate()
        {
            var height = Tip + 1;
            var proofs = await ProofsForNextHeight();
            var order = ByVrf(proofs);
            Proof Of(string a) => proofs.First(p => p.Address == a);
            var votes = new List<Proof> { Of(order[1]), Of(order[1]), Of(order[1]), Of(order[0]), Of(order[0]) };

            var split = new List<Proof> { Of(order[0]), Of(order[0]), Of(order[1]), Of(order[1]), Of(order[2]) };

            // The two casters that did not see it fail, then the three that did.
            var agreementVotes = new List<string>();
            var splitVoteBefore = BlockcasterNode.ThisCastersVote(split, height)!.Key;
            for (var i = 0; i < 2; i++) agreementVotes.Add(BlockcasterNode.ThisCastersVote(votes, height)!.Key);
            Assert.True(BlockcasterNode.NoteUndeliveredWinner(height, order[0], "test"));
            for (var i = 0; i < 3; i++) agreementVotes.Add(BlockcasterNode.ThisCastersVote(votes, height)!.Key);

            Assert.All(agreementVotes, v => Assert.Equal(order[1], v));
            Assert.True(agreementVotes.Count(v => v == order[1]) >= ConsensusQuorum.Required(5));
            // An even split goes to the lowest VRF, except for a caster that saw that winner fail here.
            Assert.Equal(order[0], splitVoteBefore);
            Assert.Equal(order[1], BlockcasterNode.ThisCastersVote(split, height)!.Key);
        }

        /// <summary>Answers GetBlockHash by host; every other host or route answers "0".</summary>
        private sealed class HashAnswers : IHttpClientFactory
        {
            private readonly Dictionary<string, string> _byHost;
            public HashAnswers(Dictionary<string, string> byHost) => _byHost = byHost;
            public HttpClient CreateClient(string name) => new HttpClient(new Handler(_byHost));

            private sealed class Handler : HttpMessageHandler
            {
                private readonly Dictionary<string, string> _byHost;
                public Handler(Dictionary<string, string> byHost) => _byHost = byHost;
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    var body = request.RequestUri!.AbsolutePath.Contains("/GetBlockHash/") && _byHost.TryGetValue(request.RequestUri.Host, out var a) ? a : "0";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
                }
            }
        }
    }
}
