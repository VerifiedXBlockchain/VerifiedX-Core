using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Work started and not awaited goes through SafeTask.Forget instead of async void: a fault, before or after its first
    /// await, is logged and never raised where nothing can catch it (which ends the process). The work still starts on the
    /// caller's thread, as async void did, and the node has no async void method left.
    /// </summary>
    public class SafeTaskTests
    {
        private static async Task FailsAfterAwait() { await Task.Yield(); throw new InvalidOperationException("after"); }
#pragma warning disable CS1998
        private static async Task FailsAtOnce() => throw new InvalidOperationException("at once");
#pragma warning restore CS1998

        [Fact]
        public async Task A_fault_before_or_after_the_first_await_is_logged_not_raised()
        {
            var unhandled = 0;
            UnhandledExceptionEventHandler watch = (_, _) => Interlocked.Increment(ref unhandled);
            AppDomain.CurrentDomain.UnhandledException += watch;
            try
            {
                var before = SafeTask.Faults;
                SafeTask.Forget(FailsAtOnce(), "SafeTaskTests.AtOnce");
                SafeTask.Forget(FailsAfterAwait(), "SafeTaskTests.AfterAwait");
                for (var i = 0; i < 100 && SafeTask.Faults < before + 2; i++) await Task.Delay(20);
                Assert.True(SafeTask.Faults >= before + 2);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Assert.Equal(0, unhandled);
            }
            finally { AppDomain.CurrentDomain.UnhandledException -= watch; }
        }

        [Fact]
        public void The_work_starts_on_the_callers_thread()
        {
            var thread = -1;
            async Task Work() { thread = Environment.CurrentManagedThreadId; await Task.Yield(); }
            SafeTask.Forget(Work(), "SafeTaskTests.Start");
            Assert.Equal(Environment.CurrentManagedThreadId, thread);
        }

        [Fact]
        public void The_node_has_no_async_void_method()
        {
            Assert.DoesNotContain(RawPrimitiveRatchetTests.Count().Keys, k => k.EndsWith("|async-void", StringComparison.Ordinal));
            Assert.DoesNotContain(RawPrimitiveBaseline.Allowed.Keys, k => k.EndsWith("|async-void", StringComparison.Ordinal));
        }
    }
}
