using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The connect-back probe that a remote caller can start (a new P2P connection, the validator status route, a
    /// promotion proposal or announcement) waits without holding a thread, says "open" only when the connection is
    /// accepted, and gives up at its timeout. The blocking probe is held to the paths that use it today by
    /// <see cref="RawPrimitiveRatchetTests"/>.
    /// </summary>
    public class PortProbeTests
    {
        [Fact]
        public async Task A_listening_port_is_open()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Assert.True(await PortUtility.IsPortOpenAsync("127.0.0.1", port));
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public async Task A_refused_port_is_closed()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Assert.False(await PortUtility.IsPortOpenAsync("127.0.0.1", port, 3000));
        }

        [Fact]
        public async Task A_connect_that_never_finishes_is_cancelled_and_closed_at_the_timeout()
        {
            var cancelled = false;
            async ValueTask NeverAnswers(CancellationToken ct)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
            var watch = Stopwatch.StartNew();
            Assert.False(await PortUtility.ConnectsWithin(NeverAnswers, 300));
            Assert.True(cancelled);
            Assert.InRange(watch.ElapsedMilliseconds, 250, 2300);
        }

        [Fact]
        public async Task An_invalid_host_is_closed()
        {
            Assert.False(await PortUtility.IsPortOpenAsync("not a host", 3338, 300));
        }
    }
}
