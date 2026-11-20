using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The FROST server gives each caller a bounded number of connections, decided when the connection arrives: the
    /// registered validators that hold the validator balance have room of their own, every other source shares a pool
    /// in which one network holds a bounded part, and the operator at the machine has its own. A connection with no
    /// request on it gives its place up when another caller needs it. This node's own clients stay inside one source's
    /// limit.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostConnectionLimitsTests : IDisposable
    {
        private const string ValidatorIp = "198.51.100.7";
        private const string UnfundedValidatorIp = "198.51.100.200";
        private const long IdleLongEnoughMs = FrostConnectionLimits.IdleSecondsBeforeMakingRoom * 1000L + 1;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly List<FakeConnection> _open = new();
        private long _clockMs = 1_000_000;

        public FrostConnectionLimitsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"frostconn_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            // Two validators registered on chain, as the running server learns of validators; one holds the validator balance.
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = 10, Hash = "h10",
                Transactions = new List<Transaction> { Register("xConnValidator", ValidatorIp, "r10a"), Register("xConnUnfunded", UnfundedValidatorIp, "r10b") },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = "xConnValidator", Balance = 5_000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = "xConnUnfunded", Balance = 4_999M, Nonce = 0 });
            Globals.LastBlock = new Block { Height = 20 };
            Globals.IsChainSynced = true;
            VBTCValidatorRegistry.InvalidateCache();
        }

        public void Dispose()
        {
            foreach (var connection in _open) connection.Close();
            Globals.IsChainSynced = _priorSynced;
            Globals.LastBlock = _priorLastBlock;
            VBTCValidatorRegistry.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Register(string address, string ip, string hash) => new()
        {
            TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = address, ToAddress = address, Hash = hash,
            Data = JsonConvert.SerializeObject(new { ValidatorAddress = address, IPAddress = ip, FrostPublicKey = "04ab", IsS3C = false }),
        };

        /// <summary>The limits as the running server builds them (validators read from the chain's registry), on this test's clock.</summary>
        private FrostConnectionLimits ServerLimits()
        {
            var limits = new FrostConnectionLimits(FrostConnectionLimits.FundedValidatorAddresses, () => Interlocked.Read(ref _clockMs));
            limits.ReadValidators();
            return limits;
        }

        private void Wait(long ms) => Interlocked.Add(ref _clockMs, ms);

        /// <summary>A connection as the server's connection middleware sees it.</summary>
        private sealed class FakeConnection
        {
            private readonly TaskCompletionSource _served = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly DefaultConnectionContext _context;
            public bool Admitted { get; private set; }

            public FakeConnection(FrostConnectionLimits limits, string remoteIp)
            {
                _context = new DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Parse(remoteIp), 50000) };
                // The server serves the connection until either side closes it.
                _context.ConnectionClosed.Register(() => _served.TrySetResult());
                var guarded = limits.Guard(_ => { Admitted = true; return _served.Task; });
                _ = guarded(_context); // runs to the server's first wait, or returns at once when turned away
            }

            public bool ClosedByServer => _context.ConnectionClosed.IsCancellationRequested;
            public void Close() => _served.TrySetResult();
            public void StartRequest() => _context.Features.Get<FrostConnectionLimits.Place>()!.RequestStarted();
            public void EndRequest() => _context.Features.Get<FrostConnectionLimits.Place>()!.RequestEnded();

            public async Task<bool> ClosedByServerWithin(int ms)
            {
                for (var waited = 0; waited < ms && !ClosedByServer; waited += 10) await Task.Delay(10);
                return ClosedByServer;
            }
        }

        private FakeConnection Connect(FrostConnectionLimits limits, string remoteIp)
        {
            var connection = new FakeConnection(limits, remoteIp);
            _open.Add(connection);
            return connection;
        }

        private List<FakeConnection> ConnectMany(FrostConnectionLimits limits, IEnumerable<string> remoteIps) =>
            remoteIps.Select(ip => Connect(limits, ip)).Where(c => c.Admitted).ToList();

        [Fact]
        public void One_source_holds_a_bounded_number_of_connections_and_others_still_get_in()
        {
            var limits = ServerLimits();

            // One address opens far more connections than its limit.
            Assert.Equal(FrostConnectionLimits.PerOtherSource, ConnectMany(limits, Enumerable.Repeat("203.0.113.9", 300)).Count);

            // More addresses in the same network get nothing more.
            Assert.Empty(ConnectMany(limits, Enumerable.Range(10, 50).Select(i => $"203.0.113.{i}")));

            // Everyone else is served.
            Assert.True(Connect(limits, "192.0.2.44").Admitted);
            Assert.True(Connect(limits, ValidatorIp).Admitted);
            Assert.True(Connect(limits, "127.0.0.1").Admitted);
        }

        [Fact]
        public void Sources_that_are_not_validators_cannot_take_a_validators_room()
        {
            var limits = ServerLimits();

            // Many networks, each at its own limit, hold every connection there is for sources that are not validators.
            var networks = Enumerable.Range(0, 40).Select(i => $"100.{64 + i}.1.1");
            var held = ConnectMany(limits, networks.SelectMany(ip => Enumerable.Repeat(ip, FrostConnectionLimits.PerOtherSource)));
            Assert.Equal(FrostConnectionLimits.OtherTotal, held.Count);

            // A registered validator is still served, up to its own limit, and so is the operator.
            Assert.Equal(FrostConnectionLimits.PerValidator, ConnectMany(limits, Enumerable.Repeat(ValidatorIp, FrostConnectionLimits.PerValidator + 5)).Count);
            Assert.Equal(FrostConnectionLimits.Loopback, ConnectMany(limits, Enumerable.Repeat("127.0.0.1", FrostConnectionLimits.Loopback + 5)).Count);
            Assert.All(held, c => Assert.False(c.ClosedByServer)); // nobody else's connection paid for them
        }

        [Fact]
        public void A_validators_room_goes_to_the_address_of_a_funded_validator_however_the_connection_spells_it()
        {
            var limits = ServerLimits();
            // The validator's network is full of connections from a neighbour that is not a validator.
            Assert.Equal(FrostConnectionLimits.PerOtherSource, ConnectMany(limits, Enumerable.Repeat("198.51.100.8", 40)).Count);

            // The validator's own address is not counted with its neighbours, in IPv4 or IPv4-mapped IPv6 form.
            Assert.True(Connect(limits, ValidatorIp).Admitted);
            Assert.Equal(FrostConnectionLimits.PerValidator - 1, ConnectMany(limits, Enumerable.Repeat("::ffff:" + ValidatorIp, 40)).Count);

            // A registration without the validator balance buys no room: that address is counted with its network.
            Assert.False(Connect(limits, UnfundedValidatorIp).Admitted);
        }

        [Fact]
        public void A_validators_room_follows_the_registry_and_is_kept_only_while_the_registry_cannot_be_read()
        {
            var registryFails = false;
            var limits = new FrostConnectionLimits(
                () => registryFails ? throw new InvalidOperationException("the registry cannot be read") : FrostConnectionLimits.FundedValidatorAddresses(),
                () => Interlocked.Read(ref _clockMs));
            limits.ReadValidators();
            // The validator's network is full of connections from a neighbour that is not a validator.
            Assert.Equal(FrostConnectionLimits.PerOtherSource, ConnectMany(limits, Enumerable.Repeat("198.51.100.8", 40)).Count);
            Assert.True(Connect(limits, ValidatorIp).Admitted);

            // A node that has fallen out of sync cannot say who the validators are: the ones it knew keep their room.
            Globals.IsChainSynced = false;
            limits.ReadValidators();
            Assert.True(Connect(limits, ValidatorIp).Admitted);

            // So do they when the read fails.
            Globals.IsChainSynced = true;
            registryFails = true;
            limits.ReadValidators();
            Assert.True(Connect(limits, ValidatorIp).Admitted);
            registryFails = false;

            // Synced again, and the validator no longer holds the validator balance: nobody has a validator's room now.
            Globals.IsChainSynced = true;
            var account = StateData.GetSpecificAccountStateTrei("xConnValidator");
            account.Balance = 4_999M;
            StateData.GetAccountStateTrei().UpdateSafe(account);
            limits.ReadValidators();
            Assert.False(Connect(limits, ValidatorIp).Admitted);
        }

        [Fact]
        public void A_closed_connection_gives_its_place_back()
        {
            var limits = ServerLimits();
            var connections = ConnectMany(limits, Enumerable.Repeat("203.0.113.9", FrostConnectionLimits.PerOtherSource));
            Assert.False(Connect(limits, "203.0.113.9").Admitted);

            connections[0].Close();
            // The server's wait for the connection ends on another thread: give it a moment, on the real clock.
            Assert.True(SpinWait.SpinUntil(() => limits.Counts().Held == FrostConnectionLimits.PerOtherSource - 1, TimeSpan.FromSeconds(5)));
            Assert.True(Connect(limits, "203.0.113.9").Admitted);
            Assert.False(Connect(limits, "203.0.113.9").Admitted); // one place came back, not more
            Assert.All(connections.Skip(1), c => Assert.False(c.ClosedByServer));
        }

        [Fact]
        public async Task A_source_at_its_limit_gets_in_at_the_cost_of_its_own_idle_connection()
        {
            var limits = ServerLimits();
            var connections = ConnectMany(limits, Enumerable.Repeat("203.0.113.9", FrostConnectionLimits.PerOtherSource));
            var busy = connections[0];
            busy.StartRequest();          // the oldest connection is serving a request
            connections[2].StartRequest();
            Wait(500);
            connections[2].EndRequest();  // served one a moment ago: idle for less time than the rest

            // Too soon: nothing has been idle long enough to be closed for a newcomer.
            Assert.False(Connect(limits, "203.0.113.9").Admitted);

            Wait(IdleLongEnoughMs);
            Assert.True(Connect(limits, "203.0.113.9").Admitted);

            // The connection idle longest paid for it; the one serving a request and the others stay.
            Assert.True(await connections[1].ClosedByServerWithin(5000));
            Assert.False(busy.ClosedByServer);
            Assert.All(connections.Skip(2), c => Assert.False(c.ClosedByServer));
            Assert.Equal(FrostConnectionLimits.PerOtherSource, limits.Counts().Held);
        }

        [Fact]
        public async Task When_the_shared_room_is_full_a_newcomer_takes_an_idle_place_from_the_source_holding_most()
        {
            var limits = ServerLimits();
            // One network holds a single connection; sixteen more hold the rest; the room for non-validators is full.
            var small = Connect(limits, "192.0.2.1");
            var large = ConnectMany(limits, Enumerable.Range(0, 16).SelectMany(i => Enumerable.Repeat($"100.{64 + i}.1.1", FrostConnectionLimits.PerOtherSource)));
            Assert.Equal(FrostConnectionLimits.OtherTotal - 1, large.Count);
            Assert.Equal(FrostConnectionLimits.OtherTotal, limits.Counts().Held);

            // While every connection is serving a request, a newcomer has nowhere to go.
            foreach (var c in large) c.StartRequest();
            small.StartRequest();
            Wait(IdleLongEnoughMs);
            Assert.False(Connect(limits, "198.18.0.1").Admitted);

            // Once they are idle, a newcomer gets a place from a network holding many, never from the one holding one.
            foreach (var c in large) c.EndRequest();
            small.EndRequest();
            Wait(IdleLongEnoughMs);
            var newcomers = ConnectMany(limits, Enumerable.Range(0, 20).Select(i => $"192.0.{10 + i}.1"));
            Assert.Equal(20, newcomers.Count);
            for (var waited = 0; waited < 5000 && large.Count(c => c.ClosedByServer) < 20; waited += 10) await Task.Delay(10);
            Assert.Equal(20, large.Count(c => c.ClosedByServer));
            Assert.False(small.ClosedByServer);
            Assert.Equal(FrostConnectionLimits.OtherTotal, limits.Counts().Held);

            // A source that already holds as many as any other gets nothing at another's cost.
            var even = ServerLimits();
            var each = ConnectMany(even, Enumerable.Range(0, FrostConnectionLimits.OtherTotal).Select(i => $"100.{64 + i / 200}.{i % 200}.1"));
            Assert.Equal(FrostConnectionLimits.OtherTotal, each.Count);
            Wait(IdleLongEnoughMs);
            Assert.False(Connect(even, "100.64.0.1").Admitted);
        }

        [Fact]
        public void This_nodes_own_clients_fit_inside_one_sources_limit()
        {
            var own = FrostPeerHttp.CeremonyConnectionsPerValidator + FrostPeerHttp.ShortConnectionsPerValidator;
            Assert.True(own <= FrostConnectionLimits.PerValidator);
            Assert.True(own <= FrostConnectionLimits.PerOtherSource);
        }

        [Fact]
        public async Task This_nodes_requests_to_one_validator_wait_for_a_connection_instead_of_opening_more()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = new List<TcpClient>();
            using var stop = new CancellationTokenSource();
            // A validator that accepts every connection and answers each request only after a while.
            var serving = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var peer = await listener.AcceptTcpClientAsync(stop.Token);
                        lock (accepted) accepted.Add(peer);
                        _ = AnswerSlowlyAsync(peer, stop.Token);
                    }
                }
                catch (OperationCanceledException) { } // the test is over
            });
            try
            {
                using var client = FrostPeerHttp.ForShortRequests(TimeSpan.FromSeconds(20));
                var requests = Enumerable.Range(0, 3 * FrostPeerHttp.ShortConnectionsPerValidator)
                    .Select(_ => client.GetStringAsync($"http://127.0.0.1:{port}/health")).ToList();
                var answers = await Task.WhenAll(requests);

                Assert.All(answers, a => Assert.Equal("ok", a));
                lock (accepted) Assert.Equal(FrostPeerHttp.ShortConnectionsPerValidator, accepted.Count);
            }
            finally
            {
                stop.Cancel();
                listener.Stop();
                lock (accepted) foreach (var peer in accepted) peer.Dispose();
                await serving;
            }
        }

        private static async Task AnswerSlowlyAsync(TcpClient peer, CancellationToken stop)
        {
            try
            {
                var stream = peer.GetStream();
                var buffer = new byte[4096];
                while (await stream.ReadAsync(buffer, stop) > 0)
                {
                    await Task.Delay(150, stop);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"), stop);
                }
            }
            catch (OperationCanceledException) { } // the test is over
            catch (IOException) { }                // the client closed the connection
            catch (ObjectDisposedException) { }    // the test closed it
        }

        [Fact]
        public async Task The_listening_server_turns_away_what_is_past_the_limit_and_closes_only_idle_connections_to_make_room()
        {
            var port = FreePortPair();
            var limits = ServerLimits();
            using var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel(options => FrostServer.ConfigureKestrel(options, port, limits))
                    .UseStartup<FrostStartup>())
                .Build();
            await host.StartAsync();
            var clients = new List<TcpClient>();
            try
            {
                // This machine holds every connection it is allowed, each one answered and kept open.
                for (var i = 0; i < FrostConnectionLimits.Loopback; i++)
                {
                    var client = new TcpClient();
                    clients.Add(client);
                    Assert.StartsWith("HTTP/1.1 200", await HealthAsync(client, port));
                }

                // One more, while none of them has been idle for long, gets no answer; the open ones still work.
                using (var extra = new TcpClient())
                    Assert.Equal("", await HealthAsync(extra, port));
                Assert.StartsWith("HTTP/1.1 200", await HealthAsync(clients[5], port, connect: false));

                // The first connection starts a request and has not finished sending it: the server is serving it.
                var slow = clients[0].GetStream();
                await slow.WriteAsync(Encoding.ASCII.GetBytes("POST /frost/btcreport/signature HTTP/1.1\r\nHost: frost\r\nContent-Type: application/json\r\nContent-Length: 64\r\n\r\n{"));
                Assert.True(SpinWait.SpinUntil(() => limits.Counts().Serving == 1, TimeSpan.FromSeconds(5)));

                // Later, a new connection is let in. The idle connection that paid for it is closed; the one in the
                // middle of a request is not, and finishes it.
                Wait(IdleLongEnoughMs);
                using (var next = new TcpClient())
                    Assert.StartsWith("HTTP/1.1 200", await HealthAsync(next, port));
                Assert.Equal("", await HealthAsync(clients[1], port, connect: false));
                await slow.WriteAsync(Encoding.ASCII.GetBytes(new string(' ', 62) + "}"));
                Assert.StartsWith("HTTP/1.1 ", await ReadAsync(clients[0]));
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
                await host.StopAsync(TimeSpan.FromSeconds(2));
            }
        }

        [Fact]
        public async Task The_listening_server_drops_a_request_whose_body_arrives_too_slowly()
        {
            var port = FreePortPair();
            var limits = ServerLimits();
            using var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel(options => FrostServer.ConfigureKestrel(options, port, limits))
                    .UseStartup<FrostStartup>())
                .Build();
            await host.StartAsync();
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                // A request that announces a body and then sends it at about a kilobyte a second.
                await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /frost/btcreport/signature HTTP/1.1\r\nHost: frost\r\nContent-Type: application/json\r\nContent-Length: 4096\r\n\r\n{"));
                Assert.True(SpinWait.SpinUntil(() => limits.Counts().Serving == 1, TimeSpan.FromSeconds(5)));

                // The server gives up on it soon after the grace period (real time), long before the body would have
                // arrived, and the connection's place comes back.
                var slice = Encoding.ASCII.GetBytes(new string(' ', 250));
                var dropped = false;
                for (var sent = 1; sent + slice.Length < 4096 && !dropped; sent += slice.Length)
                {
                    await Task.Delay(250);
                    try { await stream.WriteAsync(slice); }
                    catch (IOException) { }     // the server has closed the connection
                    catch (SocketException) { } // the server has reset it
                    dropped = limits.Counts().Held == 0;
                }
                Assert.True(dropped);
            }
            finally
            {
                await host.StopAsync(TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>GET /health on a kept-open connection: the start of the answer, or "" when the server closed it unanswered.</summary>
        private static async Task<string> HealthAsync(TcpClient client, int port, bool connect = true)
        {
            try
            {
                if (connect) await client.ConnectAsync(IPAddress.Loopback, port);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: frost\r\n\r\n"));
                return await ReadAsync(client);
            }
            catch (IOException) { return ""; }      // closed by the server while writing
            catch (SocketException) { return ""; }  // reset by the server
        }

        /// <summary>One whole HTTP answer from the connection, or "" when the server closed it without one.</summary>
        internal static async Task<string> ReadAsync(TcpClient client)
        {
            var answer = new StringBuilder();
            try
            {
                var buffer = new byte[4096];
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!Whole(answer.ToString()))
                {
                    var read = await client.GetStream().ReadAsync(buffer, deadline.Token);
                    if (read == 0) break; // closed by the server
                    answer.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (IOException) { }      // closed by the server while reading
            catch (SocketException) { }  // reset by the server
            return answer.ToString();
        }

        /// <summary>Whether the text holds a complete answer: its headers, and its body by length or to the last chunk.</summary>
        private static bool Whole(string answer)
        {
            var headersEnd = answer.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headersEnd < 0) return false;
            var headers = answer[..headersEnd];
            var body = answer[(headersEnd + 4)..];
            if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
                return body.EndsWith("0\r\n\r\n", StringComparison.Ordinal);
            var length = headers.Split("\r\n").FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            return length == null || body.Length >= int.Parse(length["Content-Length:".Length..].Trim());
        }

        /// <summary>A port free together with the one after it (the server listens on both).</summary>
        internal static int FreePortPair()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                try
                {
                    var second = new TcpListener(IPAddress.Loopback, port + 1);
                    second.Start();
                    second.Stop();
                    return port;
                }
                catch (SocketException) { } // the next port is taken: try another pair
            }
            throw new InvalidOperationException("no free pair of ports");
        }
    }
}
