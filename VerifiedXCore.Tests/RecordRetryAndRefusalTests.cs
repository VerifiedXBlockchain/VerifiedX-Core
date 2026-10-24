using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A node that refuses the next block for want of a record asks its peers again within seconds rather than at the next
    /// minute, and a record it cannot adopt is named with the reason. Mainnet, 24 October 2026: a node that could not adopt
    /// the record its peers held waited at the tip with nothing in its log to say why.
    /// </summary>
    [Collection("DbContextSequential")]
    public class RecordRetryAndRefusalTests : IDisposable
    {
        private const long Tip = 100;

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorCaster = Globals.IsBlockCaster;
        private readonly string _priorAddress = Globals.ValidatorAddress;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly IReadOnlyList<long> _priorHeads = CasterDiscoveryService.LastObservedPeerHeads;
        private readonly long _priorCertEnforceHeight = Globals.CertEnforceHeight;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;

        public RecordRetryAndRefusalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"record_retry_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            ValidatorMembershipPull.ResetForTests();
            BlockchainData.ChainRef = "test-chain";
            Globals.IsBlockCaster = false;
            Globals.IsChainSynced = true;
            Globals.CertEnforceHeight = long.MaxValue;
            Globals.ValidatorAddress = "a-validator";
            Globals.LastBlock = new Block { Height = Tip, Hash = "tip-100", Timestamp = TimeUtil.GetTime() - 20 };
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = "c1", PeerIP = "10.4.0.1" },
                new Peers { ValidatorAddress = "c2", PeerIP = "10.4.0.2" },
            };
        }

        public void Dispose()
        {
            CasterDiscoveryService.SetObservedPeerHeadsForTests(_priorHeads.ToArray());
            ValidatorMembershipPull.ResetForTests();
            Globals.HttpClientFactory = _priorFactory;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorAddress;
            Globals.LastBlock = _priorLastBlock;
            Globals.IsChainSynced = _priorSynced;
            Globals.IsBlockCaster = _priorCaster;
            Globals.CertEnforceHeight = _priorCertEnforceHeight;
            BlockchainData.ChainRef = _priorChainRef;
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>A block at Tip + 1 that passes the producer signature and proof checks.</summary>
        private static Block NextBlock()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var height = Tip + 1;
            var block = new Block
            {
                Height = height, Version = BlockVersionUtility.GetBlockVersion(height), PrevHash = "tip-100",
                Timestamp = TimeUtil.GetTime(), NumOfTx = 0, Validator = AccountData.GetHumanAddress(pub),
                ValidatorAnswer = ProofUtility.ComputeVrf(pub, height, "tip-100").ProofHash, ChainRefId = "test-chain",
                Transactions = new List<Transaction>(),
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, key, pub);
            return block;
        }

        [Fact]
        public async Task RefusingTheNextBlockForWantOfARecord_BringsTheNextPullForward()
        {
            // Every peer reports a record (head 7) and hands over nothing this node can adopt.
            Globals.HttpClientFactory = new FixedAnswerFactory(JsonConvert.SerializeObject(new MembershipRecordResponse { HeadSeq = 7 }));
            Assert.True(await ValidatorMembershipPull.PullIfDueAsync());
            Assert.True(ValidatorMembershipPull.SecondsUntilDue() > ValidatorMembershipPull.RetrySeconds);

            var since = DateTime.UtcNow;
            Assert.False(await BlockValidatorService.ValidateBlock(NextBlock(), false, blockDownloads: true));
            Assert.Contains("BlockValidatorService.ValidateBlock()-noRecord",
                BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location));

            Assert.True(ValidatorMembershipPull.SecondsUntilDue() <= ValidatorMembershipPull.RetrySeconds);
        }

        [Fact]
        public async Task ARecordThatIsRefused_IsNamedWithTheReason()
        {
            var unsigned = CasterMembershipStore.BuildGenesisRecord(2_000);
            Globals.HttpClientFactory = new FixedAnswerFactory(JsonConvert.SerializeObject(
                new MembershipRecordResponse { HeadSeq = 3, Records = new List<CasterMembershipRecord> { unsigned } }));

            await CasterDiscoveryService.FetchAndAdoptMembershipAsync(new List<string> { "10.4.0.1" }, reconcileWhenUnchanged: false);

            Assert.Null(CasterMembershipStore.GetCurrent());
            Assert.Contains("10.4.0.1", CasterDiscoveryService.LastAdoptionRefusal);
            Assert.Contains("seq 0: genesis signatures", CasterDiscoveryService.LastAdoptionRefusal);
        }

        /// <summary>Every GetMembershipRecord answers <paramref name="body"/>; everything else answers "0".</summary>
        private sealed class FixedAnswerFactory : IHttpClientFactory
        {
            private readonly string _body;
            public FixedAnswerFactory(string body) => _body = body;
            public HttpClient CreateClient(string name) => new HttpClient(new Handler(_body));

            private sealed class Handler : HttpMessageHandler
            {
                private readonly string _body;
                public Handler(string body) => _body = body;
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    var body = request.RequestUri!.AbsolutePath.Contains("/GetMembershipRecord/") ? _body : "0";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
                }
            }
        }
    }
}
