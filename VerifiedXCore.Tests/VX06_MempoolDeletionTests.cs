using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-06 (HIGH): "Any pending transaction can be removed from every node by an anonymous party".
    ///
    /// Audit PoC: three unauthenticated hub calls SendTxToMempool({Hash: &lt;victim hash&gt;,
    /// Signature: "forged", nonsense fields, Timestamp: 0}) emptied the mempool of all three nodes.
    /// When the hash matched a stored TX, staleness was evaluated on the RECEIVED object and the stored
    /// TX was deleted by hash — no signature check, no hash recompute.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX06_MempoolDeletionTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VX06_MempoolDeletionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx06_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.MessageLocks.Clear();
            Globals.Nodes.Clear();
            Globals.AdjNodes.Clear();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static P2PServer AnonymousHubCaller(string ip)
        {
            var context = new Mock<HubCallerContext>();
            var features = new FeatureCollection();
            var connection = new Mock<IHttpConnectionFeature>();
            connection.Setup(x => x.RemoteIpAddress).Returns(IPAddress.Parse(ip));
            features.Set(connection.Object);
            context.Setup(x => x.Features).Returns(features);
            context.Setup(x => x.ConnectionId).Returns(Guid.NewGuid().ToString());
            return new P2PServer { Context = context.Object };
        }

        private static Transaction StoredVictim(long timestamp)
        {
            var tx = new Transaction
            {
                Timestamp = timestamp, FromAddress = "xQbL7kdZmUjB8u3vCvB9tF6Mb4gnv6QBZY", ToAddress = "xEY5KtYTkhM3DuNStbLzTLX9bxMvAAa3nA",
                Amount = 750000M, Fee = 0.01M, Nonce = 1, TransactionType = TransactionType.TX, Signature = "stored-signature",
            };
            tx.Build();
            TransactionData.GetPool().InsertSafe(tx);
            return tx;
        }

        private static bool InMempool(string hash) => TransactionData.GetPool().FindOne(x => x.Hash == hash) != null;

        [Fact]
        public async Task VX06_AuditPoC_ForgedObjectWithVictimHash_DoesNotRemoveTheVictim()
        {
            var victim = StoredVictim(TimeUtil.GetTime());
            var forged = new Transaction
            {
                Hash = victim.Hash, Signature = "forged", FromAddress = "nonsense", ToAddress = "nonsense",
                Amount = 0, Fee = 0, Timestamp = 0, TransactionType = TransactionType.TX,
            };

            await AnonymousHubCaller("172.28.0.99").SendTxToMempool(forged);

            Assert.True(InMempool(victim.Hash));
        }

        [Fact]
        public async Task VX06_Control_StoredTxThatIsReallyStale_IsStillCleanedUp()
        {
            // The legitimate purpose of the branch — dropping a TX that is genuinely expired — is kept,
            // but decided on the stored TX (same rule as the background reaper).
            var stale = StoredVictim(TimeUtil.GetTime() - Globals.MaxTxAgeSeconds - 60);

            await AnonymousHubCaller("172.28.0.99").SendTxToMempool(new Transaction { Hash = stale.Hash, Timestamp = TimeUtil.GetTime() });

            Assert.False(InMempool(stale.Hash));
        }

        [Fact]
        public async Task VX06_Control_GenuineDuplicate_ReportsAlreadyInMempool()
        {
            var victim = StoredVictim(TimeUtil.GetTime());

            var result = await AnonymousHubCaller("172.28.0.99").SendTxToMempool(victim);

            Assert.Equal("AIMP", result);
            Assert.True(InMempool(victim.Hash));
        }
    }
}
