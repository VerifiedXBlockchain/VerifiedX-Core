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
    /// A reset marker written before resets recorded the era they end holds only the tip. It cannot tell the era it
    /// ended from the network's current one when both take effect below it. A genesis there is adopted only from two or
    /// more peers that hold it, as the newest valid era on offer: one peer cannot hand the node back the era it ended,
    /// and a node reset after the current genesis was minted still recovers. The old marker refused every such genesis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class LegacyResetMarkerTests : IDisposable
    {
        private const long LegacyResetTip = 5_000;
        private const string PeerA = "10.3.0.1";
        private const string PeerB = "10.3.0.2";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorCertHeight = Globals.CertEnforceHeight;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _seedSigners = new();

        public LegacyResetMarkerTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"legacy_reset_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            Globals.LastBlock = new Block { Height = 7_000 };
            for (var i = 0; i < CasterMembershipStore.GenesisMinSeedSignatures; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                var address = AccountData.GetHumanAddress(pub);
                _seedSigners.Add((key, pub, address));
                Globals.BootstrapCasterAddresses.Add(address);
            }
            // As the old code left it: the tip at the reset, and nothing about the era it ended.
            DbContext.DB_Config!.GetCollection<CasterMembershipStore.ResetMarker>("rsrv_membership_reset")
                .Upsert(new CasterMembershipStore.ResetMarker { Id = 1, ResetAtHeight = LegacyResetTip });
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

        private CasterMembershipRecord SignedGenesis(long height)
        {
            var genesis = CasterMembershipStore.BuildGenesisRecord(height);
            var payload = CasterMembershipStore.CanonicalPayload(genesis);
            genesis.Signatures = _seedSigners
                .Select(s => new RecordSignature { SignerAddress = s.Address, Signature = SignatureService.CreateSignature(payload, s.Key, s.Pub) })
                .ToList();
            return genesis;
        }

        private Task PullFrom(params (string Ip, CasterMembershipRecord Genesis)[] peers)
        {
            var answers = peers.ToDictionary(p => p.Ip,
                p => JsonConvert.SerializeObject(new MembershipRecordResponse { HeadSeq = 0, Records = new List<CasterMembershipRecord> { p.Genesis } }));
            Globals.HttpClientFactory = new FixedAnswerFactory(answers);
            return CasterDiscoveryService.FetchAndAdoptMembershipAsync(answers.Keys.ToList(), reconcileWhenUnchanged: false);
        }

        [Fact]
        public async Task AGenesisBelowTheOldReset_HeldByOnePeer_IsNotAdopted()
        {
            Assert.Equal(LegacyResetTip, CasterMembershipStore.LegacyResetHeight());

            await PullFrom((PeerA, SignedGenesis(2_000)));

            Assert.Null(CasterMembershipStore.GetCurrent());
            Assert.Contains("2 or more peers", CasterDiscoveryService.LastAdoptionRefusal);
        }

        [Fact]
        public async Task AGenesisBelowTheOldReset_HeldByTwoPeers_IsAdopted()
        {
            await PullFrom((PeerA, SignedGenesis(2_000)), (PeerB, SignedGenesis(2_000)));

            Assert.Equal(2_000, CasterMembershipStore.GetCurrent()?.EffectiveFromHeight);
        }

        [Fact]
        public async Task AGenesisAboveTheOldReset_HeldByOnePeer_IsAdopted()
        {
            await PullFrom((PeerA, SignedGenesis(6_000)));

            Assert.Equal(6_000, CasterMembershipStore.GetCurrent()?.EffectiveFromHeight);
        }

        [Fact]
        public void AnAnnouncedGenesisBelowTheOldReset_IsRefused()
        {
            Assert.False(CasterMembershipStore.TryAppend(SignedGenesis(2_000), out var reason));
            Assert.Contains("2 or more peers", reason);
        }

        [Fact]
        public void AResetOverTheOldMarker_ThatClearsNothing_KeepsIt_AndOneThatClearsAnEra_RecordsThatEra()
        {
            CasterMembershipStore.ClearAllForOperatorReset();
            Assert.Equal(LegacyResetTip, CasterMembershipStore.LegacyResetHeight());

            Assert.True(CasterMembershipStore.TryAppend(SignedGenesis(6_000), out var held), held);
            CasterMembershipStore.ClearAllForOperatorReset();

            Assert.Equal(0, CasterMembershipStore.LegacyResetHeight());
            Assert.Equal(6_000, CasterMembershipStore.ClearedGenesisFromHeight());
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
