using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Attestation;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// C-194: the report loop's gossip asked each signer for its signatures with the response read past its headers, and
    /// the client's timeout ends at the headers: a signer that sent "200, chunked" and then nothing held the gossip, and so
    /// the whole report loop, for good. Every honest validator gossips to a signer that never signs, so one such signer
    /// stopped attestation everywhere. The exchange, body included, now has one deadline.
    /// </summary>
    public class BtcReportGossipDeadlineTests
    {
        [Fact]
        public async Task ASignerThatSendsHeadersAndThenNothing_DoesNotHoldTheGossip()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var stop = new CancellationTokenSource();
            var held = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                await stream.ReadAsync(new byte[8192], stop.Token);
                var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n");
                await stream.WriteAsync(headers, stop.Token);
                await Task.Delay(Timeout.Infinite, stop.Token);              // and then nothing
            });
            try
            {
                var watch = Stopwatch.StartNew();
                var ask = BtcReportService.AskForSignaturesAsync($"http://127.0.0.1:{port}/frost/btcreport/signature", "{}", 65_536, CancellationToken.None);
                var finished = await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(30))) == ask;
                Assert.True(finished, "the gossip is still waiting on the signer's body");
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);   // the gossip's own catch takes it
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
                try { await held; } catch { }
            }
        }
    }
}
