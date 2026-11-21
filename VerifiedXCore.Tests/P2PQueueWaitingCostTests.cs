using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using System.Net;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A request's cost counts against the per-peer and global budgets from the moment it is admitted,
    /// including while it waits for its processing slot.
    /// </summary>
    public class P2PQueueWaitingCostTests : IDisposable
    {
        private const int OneMb = 1_000_000;

        private readonly int _priorGlobalConnections;
        private readonly long _priorGlobalBuffer;
        private readonly Account? _priorAdjudicateAccount;

        public P2PQueueWaitingCostTests()
        {
            _priorGlobalConnections = Globals.GlobalConnectionCount;
            _priorGlobalBuffer = Globals.GlobalBufferCost;
            _priorAdjudicateAccount = Globals.AdjudicateAccount;
            Globals.GlobalConnectionCount = 0;
            Globals.GlobalBufferCost = 0;
            Globals.MessageLocks.Clear();
            Globals.AdjNodes.Clear();
            Globals.Nodes.Clear();
            Globals.AdjudicateAccount = null;
        }

        public void Dispose()
        {
            Globals.MessageLocks.Clear();
            Globals.GlobalConnectionCount = _priorGlobalConnections;
            Globals.GlobalBufferCost = _priorGlobalBuffer;
            Globals.AdjudicateAccount = _priorAdjudicateAccount;
        }

        [Fact]
        public async Task WaitingBlockRequests_CountAgainstThePeerBudget()
        {
            const string ip = "10.42.0.1";
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The first request takes the peer's single block slot and holds it; the rest wait behind it.
            var requests = new List<Task<bool>>();
            for (var i = 0; i < 10; i++)
                requests.Add(Send(ip, OneMb, SignalRMessageType.Block, gate.Task));

            var peer = Globals.MessageLocks[ip];
            var refused = requests.Count(t => t.IsFaulted && t.Exception!.InnerException is HubException);

            Assert.Equal(5, refused);
            Assert.Equal(5, requests.Count(t => !t.IsCompleted));
            Assert.True(peer.BufferCost <= Globals.MaxBufferCostPerIP, $"peer budget charged {peer.BufferCost}");
            Assert.Equal(5L * OneMb, Globals.GlobalBufferCost);

            Interlocked.Exchange(ref peer.DelayLevel, 0);
            gate.SetResult(true);
            foreach (var admitted in requests.Where(t => !t.IsFaulted))
                Assert.True(await admitted);

            Assert.Equal(0, peer.BufferCost);
            Assert.Equal(0L, Globals.GlobalBufferCost);
        }

        [Fact]
        public async Task WaitingRequests_CountAgainstTheGlobalBudget()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            const int cost = 1_600_000;

            // 30 peers, three requests each (one running, two waiting): 144 MB asked of a 100 MB budget.
            var requests = new List<Task<bool>>();
            for (var p = 0; p < 30; p++)
                for (var i = 0; i < 3; i++)
                    requests.Add(Send($"10.43.0.{p + 1}", cost, SignalRMessageType.Block, gate.Task));

            var refused = requests.Count(t => t.IsFaulted && t.Exception!.InnerException is HubException);
            Assert.True(refused > 0, "requests past the global budget must be refused");
            Assert.True(Globals.GlobalBufferCost <= Globals.MaxGlobalBufferCost, $"global budget charged {Globals.GlobalBufferCost}");
            Assert.Equal((long)(requests.Count - refused) * cost, Globals.GlobalBufferCost);

            foreach (var peer in Globals.MessageLocks.Values)
                Interlocked.Exchange(ref peer.DelayLevel, 0);
            gate.SetResult(true);
            foreach (var admitted in requests.Where(t => !t.IsFaulted))
                Assert.True(await admitted);

            Assert.Equal(0L, Globals.GlobalBufferCost);
            Assert.All(Globals.MessageLocks.Values, peer => Assert.Equal(0, peer.BufferCost));
        }

        [Fact]
        public async Task RefusedRequest_LeavesBothBudgetsUnchanged()
        {
            const string ip = "10.44.0.1";
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var held = Send(ip, 3 * OneMb, SignalRMessageType.Block, gate.Task);
            var peer = Globals.MessageLocks[ip];

            await Assert.ThrowsAsync<HubException>(() => Send(ip, 3 * OneMb, SignalRMessageType.Block, gate.Task));
            Assert.Equal(3 * OneMb, peer.BufferCost);
            Assert.Equal(3L * OneMb, Globals.GlobalBufferCost);

            Interlocked.Exchange(ref peer.DelayLevel, 0);
            gate.SetResult(true);
            Assert.True(await held);
        }

        [Fact]
        public async Task NewPeer_LargeFirstRequestAndFollowUps_AreServed()
        {
            const string ip = "10.45.0.1";

            // A first request above half the peer budget is served, and the budget is free again afterwards.
            Assert.True(await Send(ip, 3 * OneMb, SignalRMessageType.Block, Task.FromResult(true)));
            var peer = Globals.MessageLocks[ip];
            Assert.Equal(0, peer.BufferCost);

            Interlocked.Exchange(ref peer.DelayLevel, 0);
            Assert.True(await Send(ip, 3 * OneMb, SignalRMessageType.Block, Task.FromResult(true)));
            Interlocked.Exchange(ref peer.DelayLevel, 0);
            Assert.True(await Send(ip, 1024, SignalRMessageType.Transaction, Task.FromResult(true)));

            Assert.Equal(0, peer.BufferCost);
            Assert.Equal(0L, Globals.GlobalBufferCost);
            Assert.Equal(0, Globals.GlobalConnectionCount);
        }

        [Fact]
        public async Task NewPeer_FirstRequestAboveThePeerBudget_IsRefusedAndChargesNothing()
        {
            const string ip = "10.46.0.1";

            await Assert.ThrowsAsync<HubException>(() =>
                Send(ip, Globals.MaxBufferCostPerIP + 1, SignalRMessageType.Block, Task.FromResult(true)));

            Assert.Equal(0, Globals.MessageLocks[ip].BufferCost);
            Assert.Equal(0L, Globals.GlobalBufferCost);
        }

        [Fact]
        public async Task ConcurrentPeers_NeverHoldMoreThanTheGlobalBudget()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            const int peers = 150;
            const int cost = 2 * OneMb;
            var running = 0;
            var refused = 0;

            // 150 peers at once, 2 MB each: 300 MB asked of a 100 MB budget, with the admissions racing.
            var requests = Enumerable.Range(1, peers).Select(p => Task.Run(async () =>
            {
                try
                {
                    return await P2PServer.SignalRQueue(Context($"10.47.{p / 200}.{p % 200 + 1}"), cost, SignalRMessageType.Block, async () =>
                    {
                        Interlocked.Increment(ref running);
                        return await gate.Task;
                    });
                }
                catch (HubException)
                {
                    Interlocked.Increment(ref refused);
                    return false;
                }
            })).ToList();

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref running) + Volatile.Read(ref refused) < peers && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.Equal(peers, running + refused);
            Assert.True((long)running * cost <= Globals.MaxGlobalBufferCost, $"{running} requests admitted");
            Assert.Equal((long)running * cost, Globals.GlobalBufferCost);

            gate.SetResult(true);
            await Task.WhenAll(requests);

            Assert.Equal(0L, Globals.GlobalBufferCost);
            Assert.All(Globals.MessageLocks.Values, peer => Assert.Equal(0, peer.BufferCost));
        }

        [Fact]
        public void TransactionCost_CountsEveryTextField()
        {
            var padded = new string('a', 500_000);
            var tx = new Transaction { Hash = padded, ToAddress = padded, FromAddress = padded, Signature = padded, Data = null };

            Assert.True(P2PServer.TransactionQueueCost(tx) >= 4 * padded.Length);
            Assert.Equal(1024 + 10, P2PServer.TransactionQueueCost(new Transaction { Data = "0123456789" }));
            Assert.Equal(1024, P2PServer.TransactionQueueCost(null));
        }

        private static Task<bool> Send(string ip, int cost, SignalRMessageType type, Task<bool> work)
        {
            return P2PServer.SignalRQueue(Context(ip), cost, type, () => work);
        }

        private static HubCallerContext Context(string ipAddress)
        {
            var context = new Mock<HubCallerContext>();
            var features = new FeatureCollection();
            var connectionFeature = new Mock<IHttpConnectionFeature>();
            connectionFeature.Setup(x => x.RemoteIpAddress).Returns(IPAddress.Parse(ipAddress));
            features.Set(connectionFeature.Object);
            context.Setup(x => x.Features).Returns(features);
            context.Setup(x => x.ConnectionId).Returns(Guid.NewGuid().ToString());
            return context.Object;
        }
    }
}
