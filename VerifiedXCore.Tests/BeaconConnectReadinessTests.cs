using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// BeaconUtility.EstablishBeaconConnection, which every lazy beacon connect calls (NFT transfer, smart contracts,
    /// tokenization, vBTC, reserve accounts), reports success only once a beacon has taken the connection, and waits a
    /// bounded time for a beacon slower than a warm connect. The beacon is the node's own /beacon hub on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BeaconConnectReadinessTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly int _priorPort;
        private readonly string _priorCliVersion;
        private readonly BeaconReference _priorBeaconReference;
        private readonly Beacons? _priorSelfBeacon;
        private readonly Beacons[] _priorBeacons;
        private readonly BeaconNodeInfo[] _priorBeacon;
        private IWebHost? _host;

        public BeaconConnectReadinessTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"beacon_connect_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();

            _priorPort = Globals.Port;
            _priorCliVersion = Globals.CLIVersion;
            _priorBeaconReference = Globals.BeaconReference;
            _priorSelfBeacon = Globals.SelfBeacon;
            _priorBeacons = Globals.Beacons.Values.ToArray();
            _priorBeacon = Globals.Beacon.Values.ToArray();

            Globals.CLIVersion = $"{Globals.MajorVer}.0.0.1-beta";
            Globals.BeaconReference = new BeaconReference { Reference = "BeaconConnectTest", CreateDate = DateTime.UtcNow };
            Globals.SelfBeacon = null;
            Globals.Beacons.Clear();
            Globals.Beacon.Clear();
        }

        public void Dispose()
        {
            foreach (var b in Globals.Beacon.Values)
                try { b.Connection?.DisposeAsync().AsTask().Wait(5000); } catch { }
            try { _host?.StopAsync().Wait(5000); } catch { }
            _host?.Dispose();

            Globals.Beacons.Clear();
            foreach (var b in _priorBeacons) Globals.Beacons[b.IPAddress] = b;
            Globals.Beacon.Clear();
            foreach (var b in _priorBeacon) Globals.Beacon[b.IPAddress] = b;
            Globals.SelfBeacon = _priorSelfBeacon;
            Globals.BeaconReference = _priorBeaconReference;
            Globals.CLIVersion = _priorCliVersion;
            Globals.Port = _priorPort;

            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>Hosts the /beacon hub on a loopback port, delaying every request by <paramref name="delay"/>.</summary>
        private void StartBeacon(TimeSpan delay)
        {
            _host = new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .ConfigureServices(s => s.AddSignalR())
                .Configure(app =>
                {
                    app.Use(async (ctx, next) => { if (delay > TimeSpan.Zero) await Task.Delay(delay); await next(); });
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapHub<P2PBeaconServer>("/beacon"));
                })
                .Build();
            _host.Start();
            var address = _host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.First();
            Globals.Port = new Uri(address).Port;
            Globals.Beacons["127.0.0.1"] = new Beacons
            {
                IPAddress = "127.0.0.1", Name = "Loopback Beacon", Port = Globals.Port, BeaconUID = "LoopbackBeacon",
                DefaultBeacon = false, SelfBeacon = false, SelfBeaconActive = false, BeaconLocator = "", Region = 1,
            };
        }

        private static bool AnyConnectedBeacon() => Globals.Beacon.Values.Any(x => x.IsConnected);

        [Fact]
        public async Task ABeaconThatTakesTheConnection_IsConnectedWhenTheCallReturns()
        {
            StartBeacon(TimeSpan.Zero);

            Assert.True(await BeaconUtility.EstablishBeaconConnection(true, false));
            Assert.True(AnyConnectedBeacon());
            await Task.Delay(500); // the beacon has decided by now
            Assert.True(AnyConnectedBeacon());
        }

        [Fact]
        public async Task ABeaconThatRefusesTheConnection_IsNotReportedAsConnected()
        {
            StartBeacon(TimeSpan.Zero);
            Globals.CLIVersion = "1.0.0.1-beta"; // below the minimum major version a beacon accepts

            Assert.False(await BeaconUtility.EstablishBeaconConnection(true, false));
            await Task.Delay(500);
            Assert.False(AnyConnectedBeacon());
        }

        [Fact]
        public async Task ABeaconSlowerThanAWarmConnect_IsConnectedOnTheFirstCall()
        {
            StartBeacon(TimeSpan.FromSeconds(4));

            Assert.True(await BeaconUtility.EstablishBeaconConnection(true, false));
            Assert.True(AnyConnectedBeacon());
        }

        [Fact]
        public async Task ABeaconThatNeverAnswers_FailsWithinTheBound()
        {
            StartBeacon(TimeSpan.FromSeconds(60));
            var sw = Stopwatch.StartNew();

            Assert.False(await BeaconUtility.EstablishBeaconConnection(true, false));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
            Assert.False(AnyConnectedBeacon());
        }
    }
}
