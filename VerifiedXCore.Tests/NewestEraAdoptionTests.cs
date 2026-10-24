using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A node with no record that hears from peers on different eras adopts the newest valid one. It used to find seq 0
    /// contested and adopt nothing, pull after pull, for as long as one peer it asked was on an older era. Mainnet,
    /// 24 October 2026: two casters still served the era of 7,414,847 (head seq 16) while the seeds were on 7,562,264.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NewestEraAdoptionTests : IDisposable
    {
        private const string OldEraPeer = "10.5.0.1";
        private const string NewEraPeer = "10.5.0.2";
        private const string ThirdPeer = "10.5.0.3";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorCertHeight = Globals.CertEnforceHeight;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _seedSigners = new();

        public NewestEraAdoptionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"newest_era_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            Globals.LastBlock = new Block { Height = 5_000 };
            for (var i = 0; i < CasterMembershipStore.GenesisMinSeedSignatures; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                var address = AccountData.GetHumanAddress(pub);
                _seedSigners.Add((key, pub, address));
                Globals.BootstrapCasterAddresses.Add(address);
            }
        }

        public void Dispose()
        {
            foreach (var s in _seedSigners) Globals.BootstrapCasterAddresses.Remove(s.Address);
            Globals.HttpClientFactory = _priorFactory;
            Globals.LastBlock = _priorLastBlock;
            Globals.CertEnforceHeight = _priorCertHeight;
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            CasterMembershipStore.InvalidateCache();
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private CasterMembershipRecord SignedGenesis(long height, bool signed = true)
        {
            var genesis = CasterMembershipStore.BuildGenesisRecord(height);
            if (!signed) return genesis;
            var payload = CasterMembershipStore.CanonicalPayload(genesis);
            genesis.Signatures = _seedSigners
                .Select(s => new RecordSignature { SignerAddress = s.Address, Signature = SignatureService.CreateSignature(payload, s.Key, s.Pub) })
                .ToList();
            return genesis;
        }

        private static string Response(long headSeq, params CasterMembershipRecord[] records) =>
            JsonConvert.SerializeObject(new MembershipRecordResponse { HeadSeq = headSeq, Records = records.ToList() });

        private static Task PullFrom(Dictionary<string, string> answers)
        {
            Globals.HttpClientFactory = new FixedAnswerFactory(answers);
            return CasterDiscoveryService.FetchAndAdoptMembershipAsync(answers.Keys.ToList(), reconcileWhenUnchanged: false);
        }

        [Fact]
        public async Task WithNoRecord_PeersOnAnOlderEraWithAHigherSeq_DoNotStopTheNewestEraBeingAdopted()
        {
            await PullFrom(new Dictionary<string, string>
            {
                [OldEraPeer] = Response(16, SignedGenesis(1_000)),
                [NewEraPeer] = Response(6, SignedGenesis(2_000)),
            });

            Assert.Equal(2_000, CasterMembershipStore.GetCurrent()?.EffectiveFromHeight);
        }

        [Fact]
        public async Task ANewerGenesisThatIsNotValid_IsPassedOver_ForTheNewestValidOne()
        {
            await PullFrom(new Dictionary<string, string>
            {
                [OldEraPeer] = Response(16, SignedGenesis(1_000)),
                [NewEraPeer] = Response(6, SignedGenesis(2_000)),
                [ThirdPeer] = Response(9, SignedGenesis(3_000, signed: false)),
            });

            Assert.Equal(2_000, CasterMembershipStore.GetCurrent()?.EffectiveFromHeight);
        }

        [Fact]
        public async Task AnEraEndedByAnOperatorReset_IsNotChosen()
        {
            Assert.True(CasterMembershipStore.TryAppend(SignedGenesis(2_000), out var held), held);
            CasterMembershipStore.ClearAllForOperatorReset();

            await PullFrom(new Dictionary<string, string>
            {
                [OldEraPeer] = Response(16, SignedGenesis(1_000)),
                [NewEraPeer] = Response(6, SignedGenesis(2_000)),
                [ThirdPeer] = Response(0, SignedGenesis(2_500)),
            });

            Assert.Equal(2_500, CasterMembershipStore.GetCurrent()?.EffectiveFromHeight);
        }

        [Fact]
        public void AnUnsignedCopyOfTheNewestGenesis_DoesNotHideTheSignedOne()
        {
            // A record's hash leaves out its signatures: the stripped copy and the signed one share it. Listed first, the
            // stripped copy used to have the whole era judged by it, and the older era was chosen.
            var stripped = SignedGenesis(2_000, signed: false);
            var signed = SignedGenesis(2_000);
            Assert.Equal(stripped.RecordHash, signed.RecordHash);
            var responses = new List<(string Ip, long HeadSeq, List<CasterMembershipRecord> Records)>
            {
                (ThirdPeer, 6, new List<CasterMembershipRecord> { stripped }),
                (NewEraPeer, 6, new List<CasterMembershipRecord> { signed }),
                (OldEraPeer, 16, new List<CasterMembershipRecord> { SignedGenesis(1_000) }),
            };

            var chosen = CasterDiscoveryService.NewestEraResponses(-1, responses, g => CasterMembershipStore.ValidateSuccessor(null, g, out _));

            Assert.DoesNotContain(chosen, r => r.Ip == OldEraPeer);
            Assert.Contains(chosen, r => r.Ip == NewEraPeer);
        }

        [Fact]
        public async Task WithAStrippedCopyServedToo_TheNewestValidEraIsAdopted()
        {
            await PullFrom(new Dictionary<string, string>
            {
                [ThirdPeer] = Response(6, SignedGenesis(2_000, signed: false)),
                [NewEraPeer] = Response(6, SignedGenesis(2_000)),
                [OldEraPeer] = Response(16, SignedGenesis(1_000)),
            });

            Assert.Equal(2_000, CasterMembershipStore.GetCurrent()?.EffectiveFromHeight);
        }

        [Fact]
        public void TwoValidGenesesAtTheSameHeight_AreLeftContested()
        {
            var a = SignedGenesis(2_000);
            var b = SignedGenesis(2_000);
            b.ChangedAddress = "another-restart";
            b.RecordHash = CasterMembershipStore.ComputeRecordHash(b);
            var responses = new List<(string Ip, long HeadSeq, List<CasterMembershipRecord> Records)>
            {
                (OldEraPeer, 0, new List<CasterMembershipRecord> { a }),
                (NewEraPeer, 0, new List<CasterMembershipRecord> { b }),
            };

            Assert.Equal(2, CasterDiscoveryService.NewestEraResponses(-1, responses, _ => true).Count);
            Assert.Equal(0, CasterDiscoveryService.ContestedSeq(-1, CasterDiscoveryService.NewestEraResponses(-1, responses, _ => true)));
        }

        /// <summary>Answers GetMembershipRecord by host; every other host or route answers "0".</summary>
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
                    var body = request.RequestUri!.AbsolutePath.Contains("/GetMembershipRecord/") && _byHost.TryGetValue(request.RequestUri.Host, out var a) ? a : "0";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
                }
            }
        }
    }
}
