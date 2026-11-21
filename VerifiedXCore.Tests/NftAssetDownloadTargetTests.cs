using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Beacon;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// An NFT's beacon is named by whoever sent the NFT. An asset download never connects to a local-only address
    /// (loopback, unspecified, link-local, multicast), where this node's own APIs listen, and follows no redirect.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NftAssetDownloadTargetTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly System.Net.Http.IHttpClientFactory? _priorFactory = Globals.HttpClientFactory;

        public NftAssetDownloadTargetTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"assetdl_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            // As Program.cs sets it at startup.
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            NodeHttp.AddNodeHttpClients(services);
            Globals.HttpClientFactory = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services)
                .GetService(typeof(System.Net.Http.IHttpClientFactory)) as System.Net.Http.IHttpClientFactory;
        }

        public void Dispose()
        {
            Globals.CustomPath = _priorCustomPath;
            Globals.HttpClientFactory = _priorFactory;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("localhost")]
        public async Task ABeaconOnThisMachine_IsNeverConnectedTo(string host)
        {
            // Stands in for this node's own API on loopback: it would answer any request with a file.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = 0;
            using var stop = new CancellationTokenSource();
            var serve = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptSocketAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                    await socket.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), SocketFlags.None);
                }
            });
            try
            {
                var scUID = Guid.NewGuid().ToString("N") + ":1790000000";
                var rsp = await BeaconClient.Receive_New("art.png", host, port, scUID);

                Assert.Equal(-1, rsp.Status);
                Assert.Equal(0, Volatile.Read(ref accepted));
                Assert.False(File.Exists(NFTAssetFileUtility.CreateNFTAssetPath("art.png", scUID)));
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
                try { await serve; } catch { }
            }
        }

        [Fact]
        public async Task ABeaconHubOnThisMachine_IsNeverConnectedTo()
        {
            // The hub a node opens to an NFT's beacon before downloading from it: its negotiate request never reaches
            // this machine either, named directly or by a redirect.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = 0;
            using var stop = new CancellationTokenSource();
            var serve = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptSocketAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                }
            });
            var priorRef = Globals.BeaconReference.Reference;
            Globals.BeaconReference.Reference = "test-beacon-reference";
            try
            {
                var connected = await VerifiedXCore.P2P.P2PClient.ConnectBeacon_New($"http://127.0.0.1:{port}/beacon",
                    new VerifiedXCore.Models.Beacons { IPAddress = "127.0.0.1", Port = port, Name = "local" }, "n", "y");
                Assert.False(connected);
                Assert.Equal(0, Volatile.Read(ref accepted));
            }
            finally
            {
                Globals.BeaconReference.Reference = priorRef;
                stop.Cancel();
                listener.Stop();
                try { await serve; } catch { }
            }
        }

        [Fact]
        public async Task AnAddressThatCannotBeReached_DoesNotStopTheNextBeingTried()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var accept = listener.AcceptSocketAsync();
                // Nothing listens at the first address on that port; the second takes the connection.
                await using var stream = await BeaconClient.ConnectInTurn(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback }, port, CancellationToken.None);
                using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(accepted.Connected);
            }
            finally { listener.Stop(); }
        }

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("127.8.9.10", true)]
        [InlineData("0.0.0.0", true)]
        [InlineData("169.254.1.1", true)]
        [InlineData("224.0.0.1", true)]
        [InlineData("255.255.255.255", true)]
        [InlineData("::1", true)]
        [InlineData("::ffff:127.0.0.1", true)]
        [InlineData("fe80::1", true)]
        [InlineData("10.0.0.5", false)]      // a beacon on a private network still works
        [InlineData("192.168.1.20", false)]
        [InlineData("52.1.2.3", false)]
        [InlineData("2001:db8::1", false)]
        public void LocalOnlyAddresses_AreTheLoopbackUnspecifiedLinkLocalAndMulticastOnes(string address, bool localOnly)
        {
            Assert.Equal(localOnly, BeaconClient.IsLocalOnly(IPAddress.Parse(address)));
        }
    }
}
