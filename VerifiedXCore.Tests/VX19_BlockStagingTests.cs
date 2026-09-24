using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-19 (MEDIUM): "Forged far future blocks are retained in memory indefinitely".
    ///
    /// Audit PoC: the general hub's ReceiveBlock staged any block above the tip whose ChainRefId matched — no header,
    /// size or duplicate check — and ValidateBlocks evicts only heights below the tip, so forged blocks at far-future
    /// heights stayed in BlockDict for the life of the process. The queue cost was the wire Size.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX19_BlockStagingTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly int _priorMaxBlockSize = Globals.MaxBlockSizeBytes;
        private readonly (PrivateKey Key, string Pub, string Address) _producer = NewKey();

        public VX19_BlockStagingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx19_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.MessageLocks.Clear();
            Globals.Nodes.Clear();
            Globals.LastBlock = new Block { Height = 0, Hash = "tip-hash" };
            BlockDownloadService.BlockDict.Clear();
            Globals.NetworkValidators.Clear();
            Globals.NetworkValidators[_producer.Address] = new NetworkValidator { Address = _producer.Address, PublicKey = _producer.Pub, IPAddress = "10.9.9.9", IsFullyTrusted = true };
        }

        public void Dispose()
        {
            BlockDownloadService.BlockDict.Clear();
            Globals.NetworkValidators.Clear();
            Globals.LastBlock = _priorLastBlock;
            Globals.MaxBlockSizeBytes = _priorMaxBlockSize;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private int _relayed;

        private P2PServer HubCaller(string ip)
        {
            var context = new Mock<HubCallerContext>();
            var features = new FeatureCollection();
            var connection = new Mock<IHttpConnectionFeature>();
            connection.Setup(x => x.RemoteIpAddress).Returns(IPAddress.Parse(ip));
            features.Set(connection.Object);
            context.Setup(x => x.Features).Returns(features);
            context.Setup(x => x.ConnectionId).Returns(Guid.NewGuid().ToString());
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref _relayed)).Returns(Task.CompletedTask);
            var clients = new Mock<IHubCallerClients>();
            clients.Setup(c => c.All).Returns(proxy.Object);
            clients.Setup(c => c.Caller).Returns(proxy.Object);
            return new P2PServer { Context = context.Object, Clients = clients.Object };
        }

        /// <summary>The audit's forgery: right ChainRefId, arbitrary height and fields, no valid hash or signature.</summary>
        private static Block Forged(long height, int n) => new Block
        {
            Height = height, ChainRefId = BlockchainData.ChainRef, Hash = $"forged-{height}-{n}", PrevHash = "x",
            Timestamp = TimeUtil.GetTime(), Size = 1, Validator = "xFORGER", Transactions = new List<Transaction>(),
        };

        /// <summary>A well-formed next block signed by a known producer (what legitimate gossip looks like).</summary>
        private Block Genuine(long height, long timestampOffset = 0, (PrivateKey Key, string Pub, string Address)? by = null)
        {
            var signer = by ?? _producer;
            var b = new Block
            {
                Height = height, ChainRefId = BlockchainData.ChainRef, PrevHash = Globals.LastBlock.Hash,
                Version = BlockVersionUtility.GetBlockVersion(height), Timestamp = TimeUtil.GetTime() + timestampOffset,
                Validator = signer.Address, MerkleRoot = "m", Transactions = new List<Transaction>(),
            };
            b.Hash = b.GetBlockHash();
            b.ValidatorSignature = SignatureService.CreateSignature(b.Hash, signer.Key, signer.Pub);
            b.Size = BlockStaging.LocalSize(b);
            return b;
        }

        private static int Staged(long height) => BlockDownloadService.BlockDict.TryGetValue(height, out var l) ? l.Count : 0;

        // ── Audit PoC ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX19_AuditPoC_100ForgedFarFutureBlocks_NoneStaged()
        {
            // One source per block: the SignalR queue's per-IP back-off is not what this test measures.
            for (int i = 0; i < 100; i++)
                await HubCaller($"172.28.1.{i + 1}").ReceiveBlock(Forged(200_000, i));

            Assert.Equal(0, Staged(200_000));
            Assert.DoesNotContain(BlockDownloadService.BlockDict.Keys, k => k > Globals.LastBlock.Height + 1);
        }

        [Fact]
        public async Task VX19_AuditPoC_ForgedNextBlock_NotStaged()
        {
            await HubCaller("172.28.0.66").ReceiveBlock(Forged(1, 0));
            Assert.Equal(0, Staged(1));
            Assert.Equal(0, _relayed); // the base re-gossiped it to every peer
        }

        [Fact]
        public async Task VX19_AuditPoC_WireSizeLiesSmall_OversizedBodyRejected_AndCostIsLocal()
        {
            Globals.MaxBlockSizeBytes = 200_000;
            var big = Genuine(1);
            big.Transactions = new List<Transaction> { new Transaction { Data = new string('A', 900_000), Hash = "t", FromAddress = "a", ToAddress = "b" } };
            big.Size = 1; // the wire claim

            Assert.True(BlockStaging.QueueCost(big) > 900_000); // the queue is charged for what actually arrived
            await HubCaller("172.28.0.66").ReceiveBlock(big);
            Assert.Equal(0, Staged(1));
            Assert.Equal(0, _relayed);
        }

        [Fact]
        public void VX19_NegativeWireSize_Rejected()
        {
            var b = Genuine(1);
            b.Size = -5_000_000;
            Assert.False(BlockStaging.PassesGossipPreChecks(b, out _));
        }

        // ── Pre-checks keep legitimate gossip ──────────────────────────────────────────────

        [Fact]
        public void VX19_Control_GenuineNextBlockFromAKnownProducer_Passes()
        {
            Assert.True(BlockStaging.PassesGossipPreChecks(Genuine(1), out var why), why);
        }

        [Fact]
        public void VX19_TamperedHeaderOrSignature_Rejected()
        {
            var tampered = Genuine(1);
            tampered.Timestamp += 1; // header no longer matches its hash
            Assert.False(BlockStaging.PassesGossipPreChecks(tampered, out _));

            var badSig = Genuine(1);
            badSig.ValidatorSignature = Genuine(1, 5).ValidatorSignature;
            Assert.False(BlockStaging.PassesGossipPreChecks(badSig, out _));
        }

        [Fact]
        public void VX19_UnknownProducer_Rejected_WhenARegistryExists()
        {
            var stranger = NewKey();
            Assert.False(BlockStaging.PassesGossipPreChecks(Genuine(1, by: stranger), out _));
        }

        // ── Staging: de-duplicated, capped, evicted ────────────────────────────────────────

        [Fact]
        public void VX19_DuplicateHash_StagedOnce()
        {
            var b = Genuine(1);
            Assert.True(BlockStaging.TryStageGossip(b, "172.28.0.66"));
            Assert.False(BlockStaging.TryStageGossip(b, "172.28.0.66"));
            Assert.False(BlockStaging.TryStageGossip(b, "172.28.0.67"));
            Assert.False(BlockStaging.Stage(b, "10.0.0.1"));
            Assert.Equal(1, Staged(1));
        }

        [Fact]
        public void VX19_PerSourceCap()
        {
            int accepted = 0;
            for (int i = 0; i < BlockStaging.MaxStagedPerSource + 5; i++)
                if (BlockStaging.TryStageGossip(Genuine(1, i), "172.28.0.66")) accepted++;
            Assert.Equal(BlockStaging.MaxStagedPerSource, accepted);
            Assert.True(BlockStaging.TryStageGossip(Genuine(1, 1000), "172.28.0.67")); // another source is independent
        }

        [Fact]
        public void VX19_StaleNonContiguousEntries_AreEvicted_NextHeightKept()
        {
            var far = Genuine(5);
            var next = Genuine(1);
            BlockStaging.Stage(far, "10.0.0.1");   // e.g. a downloaded block whose gap never filled
            BlockStaging.Stage(next, "10.0.0.1");
            BlockStaging.Backdate(far.Hash, BlockStaging.StaleSeconds + 60);
            BlockStaging.Backdate(next.Hash, BlockStaging.StaleSeconds + 60);

            BlockStaging.EvictStaleNow();

            Assert.Equal(0, Staged(5));
            Assert.Equal(1, Staged(1)); // the next height is what ValidateBlocks consumes; never evicted by age
        }
    }
}
