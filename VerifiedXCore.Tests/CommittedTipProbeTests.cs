using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Block-hash sync and the fork probe compare committed tips: a peer's round draft for a height above its own tip
    /// (GetBlockHash answers Committed=false) is not a block it holds. Mainnet 7,562,232 (23 October 2026): three casters
    /// still one behind answered with their draft, block-hash sync read "3 of 5 committee members hold another block"
    /// against this caster's certified tip, and its self-heal rolled the certified block back.
    /// </summary>
    [Collection("DbContextSequential")]
    public class CommittedTipProbeTests : IDisposable
    {
        private const long Height = 200;
        private const string Ours = "ours-200";
        private const string Other = "other-200";

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _priorAddress = Globals.ValidatorAddress;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;

        public CommittedTipProbeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"committed_tip_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            AttestationGuard.ResetForTests();
            Globals.ValidatorAddress = "self";
            Globals.LastBlock = new Block { Height = Height, Hash = Ours };
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = "self", PeerIP = "10.9.0.9" },
                new Peers { ValidatorAddress = "c1", PeerIP = "10.9.0.1" },
                new Peers { ValidatorAddress = "c2", PeerIP = "10.9.0.2" },
                new Peers { ValidatorAddress = "c3", PeerIP = "10.9.0.3" },
                new Peers { ValidatorAddress = "c4", PeerIP = "10.9.0.4" },
            };
        }

        public void Dispose()
        {
            AttestationGuard.ResetForTests();
            Globals.HttpClientFactory = _priorFactory;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorAddress;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>c1..c3 answer <paramref name="hash"/> at our height with the given Committed flag; c4 and we hold ours.</summary>
        private static void PeersAnswer(string hash, bool committed)
        {
            var answers = new Dictionary<string, string>
            {
                ["10.9.0.1"] = Answer(hash, committed), ["10.9.0.2"] = Answer(hash, committed), ["10.9.0.3"] = Answer(hash, committed),
                ["10.9.0.4"] = Answer(Ours, true), ["10.9.0.9"] = Answer(Ours, true),
            };
            Globals.HttpClientFactory = new FixedAnswerFactory(answers);
        }

        private static string Answer(string hash, bool committed) =>
            $"{{\"Hash\":\"{hash}\",\"Validator\":\"producer\",\"Height\":{Height},\"Committed\":{(committed ? "true" : "false")}}}";

        private static Task SyncBlockHashWithPeersAsync() =>
            (Task)typeof(BlockcasterNode).GetMethod("SyncBlockHashWithPeersAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        [Fact]
        public async Task PeersOneBehind_AnsweringWithTheirDraft_DoNotPutACasterOnAMinorityFork()
        {
            PeersAnswer(Other, committed: false);

            await SyncBlockHashWithPeersAsync();

            Assert.False(BlockcasterNode.IsHoldingOnMinorityFork());
        }

        [Fact]
        public async Task AQuorumThatCommittedAnotherBlock_StillPutsACasterOnAMinorityFork()
        {
            PeersAnswer(Other, committed: true);

            await SyncBlockHashWithPeersAsync();

            Assert.True(BlockcasterNode.IsHoldingOnMinorityFork());
        }

        [Fact]
        public async Task TheForkProbe_CountsOnlyCommittedBlocks()
        {
            PeersAnswer(Other, committed: false);
            Assert.False((await ForkDetectionService.ProbeMajorityAsync(Height, Ours)).IsMinority);

            PeersAnswer(Other, committed: true);
            var verdict = await ForkDetectionService.ProbeMajorityAsync(Height, Ours);
            Assert.True(verdict.IsMinority);
            Assert.Equal(Other, verdict.MajorityHash);
        }

        /// <summary>Answers GetBlockHash by host; every other host or route answers "0".</summary>
        private sealed class FixedAnswerFactory : IHttpClientFactory
        {
            private readonly Dictionary<string, string> _byHost;
            public FixedAnswerFactory(Dictionary<string, string> byHost) => _byHost = byHost;
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
