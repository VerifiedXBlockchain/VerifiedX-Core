using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A fire-and-forget send to a hub caller gives up after its timeout. The timeout's source was disposed as soon as the
    /// send had started, which stops its timer, so a send to a peer that never reads was never cancelled.
    /// </summary>
    public class ReliableSendTimeoutTests
    {
        /// <summary>A caller whose send ends only when its token is cancelled: a peer that never reads.</summary>
        private sealed class NeverReads : IClientProxy
        {
            public readonly TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => { Cancelled.TrySetResult(true); pending.TrySetCanceled(cancellationToken); });
                return pending.Task;
            }
        }

        [Fact]
        public async Task A_send_to_a_caller_that_never_reads_is_cancelled_at_its_timeout()
        {
            var caller = new NeverReads();
            caller.SendToCallerReliable("GetMessage", "data", "test", timeoutMs: 100);
            Assert.Same(caller.Cancelled.Task, await Task.WhenAny(caller.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(5))));

            var withArgs = new NeverReads();
            withArgs.SendToCallerReliable("GetMessage", new object[] { "a", "b" }, "test", timeoutMs: 100);
            Assert.Same(withArgs.Cancelled.Task, await Task.WhenAny(withArgs.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        }

        [Fact]
        public async Task A_send_that_ends_in_time_is_not_cancelled_and_one_that_throws_at_once_is_passed_on()
        {
            CancellationToken seen = default;
            await ReliableSignalRSender.SendWithin(token => { seen = token; return Task.CompletedTask; }, 50);
            Assert.False(seen.IsCancellationRequested);
            Assert.Throws<InvalidOperationException>(() => { ReliableSignalRSender.SendWithin(_ => throw new InvalidOperationException("no connection"), 50); });
        }
    }
}
