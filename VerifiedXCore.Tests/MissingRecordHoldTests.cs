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
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A synced node without a membership record takes no next block while its peers report a record: without one no
    /// certificate is required at all. Mainnet 7,562,232 (23 October 2026): 66 of one operator's 70 validators had no
    /// record and switched to the uncertified block. When peers have none either (legacy era, seeds restarting a chain
    /// before they mint the genesis record) nothing changes. The validator's minute pull records what peers report.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MissingRecordHoldTests : IDisposable
    {
        private const long Tip = 100;
        private const string NoRecordStep = "BlockValidatorService.ValidateBlock()-noRecord";

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

        public MissingRecordHoldTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"missing_record_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            BlockchainData.ChainRef = "test-chain";
            Globals.IsBlockCaster = false;
            Globals.IsChainSynced = true;
            Globals.CertEnforceHeight = long.MaxValue; // no record: nothing armed
            Globals.LastBlock = new Block { Height = Tip, Hash = "tip-100", Timestamp = TimeUtil.GetTime() - 20 };
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

        private static async Task<List<string>> StepsOf(Block block, bool blockDownloads)
        {
            var since = DateTime.UtcNow;
            await BlockValidatorService.ValidateBlock(block, false, blockDownloads: blockDownloads);
            return BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location).ToList();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task WithoutARecord_WhilePeersReportOne_TheNextBlockWaits(bool blockDownloads)
        {
            CasterDiscoveryService.SetObservedPeerHeadsForTests(4, 4, -1);

            var steps = await StepsOf(NextBlock(), blockDownloads);

            Assert.Equal(NoRecordStep, steps.LastOrDefault());
            Assert.Equal(Tip, Globals.LastBlock.Height);
        }

        [Fact]
        public async Task WhenPeersHaveNoRecordEither_TheNextBlockIsNotHeldForOne()
        {
            CasterDiscoveryService.SetObservedPeerHeadsForTests(-1, -1, 4);

            Assert.DoesNotContain(NoRecordStep, await StepsOf(NextBlock(), blockDownloads: true));
        }

        [Fact]
        public void TheRule_NeedsAMajorityOfAtLeastTwoPeers()
        {
            Assert.True(CasterMembershipStore.MissingWhilePeersHaveOne(null, new long[] { 0, 3 }));
            Assert.False(CasterMembershipStore.MissingWhilePeersHaveOne(null, new long[] { 3 }));
            Assert.False(CasterMembershipStore.MissingWhilePeersHaveOne(null, new long[] { 3, -1 }));
            Assert.False(CasterMembershipStore.MissingWhilePeersHaveOne(2, new long[] { 3, 3, 3 }));
            Assert.False(CasterMembershipStore.MissingWhilePeersHaveOne(null, Array.Empty<long>()));
        }

        [Fact]
        public async Task TheValidatorPull_RecordsTheHeadsPeersReport()
        {
            Globals.ValidatorAddress = "a-validator";
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = "c1", PeerIP = "10.6.0.1" },
                new Peers { ValidatorAddress = "c2", PeerIP = "10.6.0.2" },
            };
            Globals.HttpClientFactory = new HeadOnlyFactory(7);
            CasterDiscoveryService.SetObservedPeerHeadsForTests();
            ValidatorMembershipPull.ResetForTests();

            Assert.True(await ValidatorMembershipPull.PullIfDueAsync());

            Assert.True(CasterDiscoveryService.LastObservedPeerHeads.Count >= 2);
            Assert.All(CasterDiscoveryService.LastObservedPeerHeads, h => Assert.Equal(7, h));
        }

        /// <summary>Every GetMembershipRecord answers a head seq and no records; everything else answers "0".</summary>
        private sealed class HeadOnlyFactory : IHttpClientFactory
        {
            private readonly long _head;
            public HeadOnlyFactory(long head) => _head = head;
            public HttpClient CreateClient(string name) => new HttpClient(new Handler(_head));

            private sealed class Handler : HttpMessageHandler
            {
                private readonly long _head;
                public Handler(long head) => _head = head;
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    var body = request.RequestUri!.AbsolutePath.Contains("/GetMembershipRecord/")
                        ? "{\"HeadSeq\":" + _head + ",\"Records\":[]}"
                        : "0";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
                }
            }
        }
    }
}
