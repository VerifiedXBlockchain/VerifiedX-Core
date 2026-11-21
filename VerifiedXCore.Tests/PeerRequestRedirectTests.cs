using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using VerifiedXCore;
using VerifiedXCore.Beacon;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A request this node sends another node ends at that node: an answer that points somewhere else is not followed.
    /// Driven through the node's own senders (the attestation after a block, a validator API read, a liveness check, a
    /// FROST request, a hub connection) with the clients the node builds at startup. Two local listeners stand in for
    /// the peer and for the place its answer points to; the second keeps every request that reaches it.
    /// The client for fixed outside services is the one that follows a redirect, and it never connects to an address
    /// that reaches only this machine.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PeerRequestRedirectTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly string _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;
        private readonly int _priorValApiPort = Globals.ValAPIPort;
        private readonly int _priorPort = Globals.Port;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly string[] _priorSigners = Globals.ReleaseSignerAddresses;
        private readonly Dictionary<string, string> _priorAssets = new(Globals.GitHubLatestReleaseAssetsDict);
        private readonly List<IDisposable> _hosts = new();
        private readonly Listener _peer = new();
        private readonly Listener _elsewhere = new();
        private readonly Account _me, _b;

        public PeerRequestRedirectTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"peerredirect_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();

            _me = Wallet(); _b = Wallet();
            Globals.ValidatorAddress = _me.Address;
            var me = AccountData.GetAccounts().FindOne(x => x.Address == _me.Address);
            me.IsValidating = true;
            AccountData.GetAccounts().Update(me);

            Globals.LastBlock = new Block { Height = 1_095 };
            Globals.IsBlockCaster = true;
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = _me.Address, PeerIP = "", ValidatorPublicKey = _me.PublicKey, IsValidator = true },
                new Peers { ValidatorAddress = _b.Address, PeerIP = "127.0.0.1", ValidatorPublicKey = _b.PublicKey, IsValidator = true },
            };
            Globals.ValAPIPort = _peer.Port;
            Globals.Port = _peer.Port;
            Globals.HttpClientFactory = StartupClients();
            AttestationGuard.ResetForTests();
        }

        public void Dispose()
        {
            foreach (var host in _hosts) { try { host.Dispose(); } catch { } }
            _peer.Dispose();
            _elsewhere.Dispose();
            AttestationGuard.ResetForTests();
            Globals.ReleaseSignerAddresses = _priorSigners;
            Globals.GitHubLatestReleaseAssetsDict.Clear();
            foreach (var kv in _priorAssets) Globals.GitHubLatestReleaseAssetsDict[kv.Key] = kv.Value;
            Globals.Port = _priorPort;
            Globals.ValAPIPort = _priorValApiPort;
            Globals.HttpClientFactory = _priorFactory;
            Globals.IsBlockCaster = _priorIsBlockCaster;
            Globals.BlockCasters = _priorCasters;
            Globals.LastBlock = _priorLastBlock;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.ValidatorAddress = _priorValidatorAddress;
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            CasterMembershipStore.InvalidateCache();
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>The client factory as the node registers it at startup (Program.Main).</summary>
        private static IHttpClientFactory StartupClients() =>
            new ServiceCollection().AddNodeHttpClients().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        /// <summary>
        /// The startup factory with one difference: 127.0.0.1, where the listeners are, stands for an outside host.
        /// Every other local-only address is refused as the node refuses it.
        /// </summary>
        private static IHttpClientFactory ClientsWithTheListenersAsOutsideHosts() =>
            NodeHttp.AddNodeHttpClients(new ServiceCollection(), () => NodeHttp.NewOutsideServiceHandler(a => BeaconClient.IsLocalOnly(a) && !a.Equals(IPAddress.Loopback)))
                .BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        private static Account Wallet()
        {
            var acct = AccountData.CreateNewAccount(skipSave: true)!;
            acct.IsValidating = false;
            AccountData.GetAccounts().Insert(acct);
            return acct;
        }

        /// <summary>
        /// A small HTTP server on loopback. It keeps each request as "METHOD target | body" (and its head) and answers
        /// with what <see cref="Answer"/> returns for the request's first line (200 and an empty body by default).
        /// </summary>
        private sealed class Listener : IDisposable
        {
            private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
            public ConcurrentQueue<string> Requests { get; } = new();
            public ConcurrentQueue<string> Heads { get; } = new();
            public int Port { get; }
            public Func<string, string> Answer { get; set; } = _ => Ok("");

            public Listener()
            {
                _tcp.Start();
                Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        TcpClient client;
                        try { client = await _tcp.AcceptTcpClientAsync(); } catch { return; }
                        _ = Task.Run(() => Serve(client));
                    }
                });
            }

            private async Task Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var text = new StringBuilder();
                        int headEnd;
                        while ((headEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
                        {
                            var n = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (n <= 0) return;
                            text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                        }
                        var head = text.ToString(0, headEnd);
                        var length = head.Split("\r\n").Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
                        while (text.Length - (headEnd + 4) < length)
                        {
                            var n = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (n <= 0) break;
                            text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                        }
                        var requestLine = head.Split("\r\n")[0];
                        var parts = requestLine.Split(' ');
                        Heads.Enqueue(head);
                        Requests.Enqueue($"{parts[0]} {parts[1]} | {text.ToString(headEnd + 4, text.Length - (headEnd + 4))}");
                        var answer = Encoding.ASCII.GetBytes(Answer(requestLine));
                        await stream.WriteAsync(answer, 0, answer.Length);
                    }
                    catch { }
                }
            }

            public static string Ok(string body, string type = "text/plain") =>
                $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

            public static string Redirect(int status, string location) =>
                $"HTTP/1.1 {status} Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

            /// <summary>Waits up to <paramref name="milliseconds"/> for a request to arrive.</summary>
            public async Task<bool> SawARequest(int milliseconds = 5_000)
            {
                for (var waited = 0; waited < milliseconds && Requests.IsEmpty; waited += 25) await Task.Delay(25);
                return !Requests.IsEmpty;
            }

            public void Dispose() { try { _tcp.Stop(); } catch { } }
        }

        /// <summary>Where the peer's answers point: a route of the second listener.</summary>
        private string Elsewhere => $"http://127.0.0.1:{_elsewhere.Port}/elsewhere/route";

        private void PeerAnswersWithARedirect(int status) => _peer.Answer = _ => Listener.Redirect(status, Elsewhere);

        /// <summary>The peer was asked, and after a moment nothing has reached the place its answer pointed to.</summary>
        private async Task AssertThePeerWasAskedAndNothingWentElsewhere()
        {
            Assert.True(await _peer.SawARequest(), "the request did not reach the peer");
            await Task.Delay(400);
            Assert.Empty(_elsewhere.Requests);
        }

        private Block AcceptedBlock(long height) =>
            new() { Height = height, Hash = $"hash{height}", PrevHash = $"hash{height - 1}", Validator = _b.Address, Version = ConsensusCertificateRules.MinBlockVersion };

        // ── Requests through the startup client factory ──────────────────────────────────────────

        [Theory]
        [InlineData(300)]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(303)]
        [InlineData(307)]
        [InlineData(308)]
        public async Task TheAttestationAfterABlock_EndsAtTheCasterItWasSentTo(int status)
        {
            PeerAnswersWithARedirect(status);

            await ConsensusAttestationPublisher.PublishLocalAsync(AcceptedBlock(100 + status));

            await AssertThePeerWasAskedAndNothingWentElsewhere();
            Assert.StartsWith("POST /valapi/validator/SubmitAttestation | {", Assert.Single(_peer.Requests));
        }

        [Theory]
        [InlineData(302)]
        [InlineData(307)]
        public async Task AValidatorApiRead_EndsAtThePeerItWasSentTo(int status)
        {
            PeerAnswersWithARedirect(status);

            Assert.Null(await VreeService.HttpGet("127.0.0.1", "Health"));

            await AssertThePeerWasAskedAndNothingWentElsewhere();
        }

        [Fact]
        public async Task AValidatorApiPost_EndsAtThePeerItWasSentTo()
        {
            PeerAnswersWithARedirect(307);

            Assert.Null(await VreeService.HttpPost("127.0.0.1", "ProbeTip", "{\"a\":1}"));

            await AssertThePeerWasAskedAndNothingWentElsewhere();
        }

        // ── Requests through clients built where they are used ───────────────────────────────────

        [Fact]
        public async Task ALivenessCheck_EndsAtTheValidatorItWasSentTo()
        {
            PeerAnswersWithARedirect(302);

            Assert.False(await NetworkValidator.CheckValidatorLiveness("127.0.0.1"));

            await AssertThePeerWasAskedAndNothingWentElsewhere();
        }

        [Fact]
        public async Task AFrostRequest_EndsAtTheValidatorItWasSentTo()
        {
            PeerAnswersWithARedirect(307);
            var url = $"http://127.0.0.1:{_peer.Port}/frost/dkg/start";

            using var ceremony = await VerifiedXCore.Bitcoin.FROST.FrostPeerHttp.ForCeremonies(TimeSpan.FromSeconds(5)).PostAsync(url, new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));
            using var probe = await VerifiedXCore.Bitcoin.FROST.FrostPeerHttp.ForShortRequests(TimeSpan.FromSeconds(5)).GetAsync(url);

            Assert.Equal(307, (int)ceremony.StatusCode);
            Assert.Equal(307, (int)probe.StatusCode);
            await AssertThePeerWasAskedAndNothingWentElsewhere();
        }

        // ── Hub connections ──────────────────────────────────────────────────────────────────────

        private const string Negotiated = "{\"negotiateVersion\":1,\"connectionId\":\"c\",\"connectionToken\":\"t\",\"availableTransports\":[{\"transport\":\"WebSockets\",\"transferFormats\":[\"Text\",\"Binary\"]}]}";

        [Theory]
        [InlineData(302)]
        [InlineData(307)]
        public async Task AHubConnection_EndsAtThePeer_WhenItsFirstAnswerPointsElsewhere(int status)
        {
            PeerAnswersWithARedirect(status);

            Assert.False(await P2PClient.PingBackPeer("127.0.0.1"));

            await AssertThePeerWasAskedAndNothingWentElsewhere();
        }

        /// <summary>The hub's own protocol lets the first answer name another address to connect to.</summary>
        [Fact]
        public async Task AHubConnection_EndsAtThePeer_WhenItsFirstAnswerNamesAnotherAddress()
        {
            _peer.Answer = _ => Listener.Ok($"{{\"url\":\"{Elsewhere}\",\"accessToken\":\"a\"}}", "application/json");

            Assert.False(await P2PClient.PingBackPeer("127.0.0.1"));

            await AssertThePeerWasAskedAndNothingWentElsewhere();
        }

        [Fact]
        public async Task AHubConnection_EndsAtThePeer_WhenItsSocketAnswerPointsElsewhere()
        {
            _peer.Answer = line => line.StartsWith("POST") ? Listener.Ok(Negotiated, "application/json") : Listener.Redirect(302, Elsewhere);

            Assert.False(await P2PClient.PingBackPeer("127.0.0.1"));

            Assert.True(await _peer.SawARequest());
            await Task.Delay(400);
            Assert.Contains(_peer.Requests, r => r.StartsWith("GET /blockchain?id=t"));
            Assert.Empty(_elsewhere.Requests);
        }
        // ── An answer that does not point elsewhere ──────────────────────────────────────────────

        [Fact]
        public async Task TheAttestationAfterABlock_ReachesACasterThatAnswers()
        {
            await ConsensusAttestationPublisher.PublishLocalAsync(AcceptedBlock(90));

            Assert.True(await _peer.SawARequest());
            Assert.StartsWith("POST /valapi/validator/SubmitAttestation | {\"BlockHeight\":90,", Assert.Single(_peer.Requests));
        }

        [Fact]
        public async Task AValidatorApiRead_ReturnsWhatThePeerAnswers()
        {
            _peer.Answer = _ => Listener.Ok("answer");

            Assert.Equal("answer", await VreeService.HttpGet("127.0.0.1", "Health"));
            Assert.Equal("answer", await VreeService.HttpPost("127.0.0.1", "ProbeTip", "{\"a\":1}"));
            await NetworkValidator.CheckValidatorLiveness("127.0.0.1");

            Assert.Equal(new[] { "GET /valapi/validator/Health | ", "POST /valapi/validator/ProbeTip | {\"a\":1}", "GET /valapi/validator/GetWalletVersion | " }, _peer.Requests);
        }

        /// <summary>Answers the call a node makes to see whether a peer can be dialed back (P2PServer.PingBackPeer), and returns what it is sent.</summary>
        public sealed class AnsweringHub : Microsoft.AspNetCore.SignalR.Hub
        {
            /// <summary>The transport of each connection that called.</summary>
            public static readonly ConcurrentQueue<string> Callers = new();

            public string PingBackPeer()
            {
                Callers.Enqueue(Context.Features.Get<Microsoft.AspNetCore.Http.Connections.Features.IHttpTransportFeature>()!.TransportType.ToString());
                return "HelloBackPeer";
            }

            public string Return(string text) => text;
        }

        /// <summary>A hub on a loopback port, which becomes the P2P port the node dials.</summary>
        private void ServeAHub()
        {
            var host = new WebHostBuilder()
                .UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                .ConfigureServices(services => { services.AddRouting(); services.AddSignalR(options => options.MaximumReceiveMessageSize = 1179648); })
                .Configure(app => { app.UseRouting(); app.UseEndpoints(endpoints => endpoints.MapHub<AnsweringHub>("/blockchain")); })
                .Build();
            host.Start();
            _hosts.Add(host);
            Globals.Port = new Uri(host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        }

        [Fact]
        public async Task AHubConnection_ToAPeerThatAnswers_IsOpenedAndUsed()
        {
            ServeAHub();
            AnsweringHub.Callers.Clear();

            Assert.True(await P2PClient.PingBackPeer("127.0.0.1"));
            Assert.Equal("WebSockets", Assert.Single(AnsweringHub.Callers));
        }

        /// <summary>A block is the largest thing a hub connection carries: a message of that order crosses the socket both ways.</summary>
        [Fact]
        public async Task AHubConnection_CarriesALargeMessageBothWays()
        {
            ServeAHub();
            var text = new string('b', 1_000_000);
            await using var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{Globals.Port}/blockchain", options => options.StayAtItsHost()).Build();
            await connection.StartAsync();

            Assert.Equal(text, await connection.InvokeAsync<string>("Return", text));
        }

        /// <summary>A validator's hub connections also allow long polling: its requests are answered at the hub as before.</summary>
        [Fact]
        public async Task AHubConnection_OverLongPolling_IsOpenedAndUsed()
        {
            ServeAHub();
            AnsweringHub.Callers.Clear();
            await using var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{Globals.Port}/blockchain", options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.StayAtItsHost();
            }).Build();
            await connection.StartAsync();

            Assert.Equal("HelloBackPeer", await connection.InvokeAsync<string>("PingBackPeer"));
            Assert.Equal("LongPolling", Assert.Single(AnsweringHub.Callers));
        }

        /// <summary>A hub that is never reached on this machine (a beacon's) is not reached there by its socket either.</summary>
        [Fact]
        public async Task AHubSocket_ThatRefusesLocalOnlyAddresses_IsNotOpenedOnThisMachine()
        {
            var hub = new Uri($"http://127.0.0.1:{_peer.Port}/beacon");

            var refused = await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await NodeHttp.OpenSocketAsync(new Uri($"ws://127.0.0.1:{_peer.Port}/beacon?id=t"), hub, new Dictionary<string, string>(), refuseLocalOnly: true, CancellationToken.None));

            Assert.Contains("local-only", refused.ToString());
            await Task.Delay(200);
            Assert.Empty(_peer.Requests);
        }

        /// <summary>The request that opens a hub connection's socket carries the connection's headers, as before.</summary>
        [Fact]
        public async Task TheRequestThatOpensAHubSocket_CarriesTheConnectionsHeaders()
        {
            var hub = new Uri($"http://127.0.0.1:{_peer.Port}/consensus");
            var headers = new Dictionary<string, string> { ["address"] = "xValidator", ["signature"] = "sig.pub" };

            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(async () =>
                await NodeHttp.OpenSocketAsync(new Uri($"ws://127.0.0.1:{_peer.Port}/consensus?id=t"), hub, headers, refuseLocalOnly: false, CancellationToken.None));

            var head = Assert.Single(_peer.Heads);
            Assert.StartsWith("GET /consensus?id=t HTTP/1.1", head);
            foreach (var line in new[] { "address: xValidator", "signature: sig.pub", "Upgrade: websocket", "Sec-WebSocket-Version: 13" })
                Assert.Contains(line, head);
        }

        [Fact]
        public async Task AHubSocket_IsNotOpenedAtAnotherHostThanTheHubs()
        {
            var hub = new Uri($"http://127.0.0.1:{_peer.Port}/consensus");

            await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(async () =>
                await NodeHttp.OpenSocketAsync(new Uri($"ws://127.0.0.1:{_elsewhere.Port}/consensus?id=t"), hub, new Dictionary<string, string>(), refuseLocalOnly: false, CancellationToken.None));

            await Task.Delay(200);
            Assert.Empty(_elsewhere.Requests);
        }

        // ── The client for fixed outside services ────────────────────────────────────────────────

        [Fact]
        public async Task OnlyTheOutsideServiceClient_FollowsARedirect()
        {
            var clients = ClientsWithTheListenersAsOutsideHosts();
            PeerAnswersWithARedirect(302);
            _elsewhere.Answer = _ => Listener.Ok("the download");
            var url = $"http://127.0.0.1:{_peer.Port}/download";

            Assert.Equal("the download", await clients.CreateClient(NodeHttp.OutsideService).GetStringAsync(url));
            Assert.Equal("GET /elsewhere/route | ", Assert.Single(_elsewhere.Requests));

            using var unnamed = await clients.CreateClient().GetAsync(url);
            using var otherName = await clients.CreateClient("another-name").GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, unnamed.StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, otherName.StatusCode);
            Assert.Single(_elsewhere.Requests);
        }

        /// <summary>The updater reads a release asset and its signature through the redirects a release host answers with.</summary>
        [Fact]
        public async Task AReleaseDownload_FollowsTheRedirectToItsAsset()
        {
            var key = new VerifiedXCore.EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var asset = "release bytes";
            var signature = SignatureService.CreateSignature(ReleaseSignature.Sha256Hex(Encoding.UTF8.GetBytes(asset)), key, pub);
            Globals.ReleaseSignerAddresses = new[] { AccountData.GetHumanAddress(pub) };
            Globals.GitHubLatestReleaseAssetsDict["vfx-linux-x64.zip"] = $"http://127.0.0.1:{_peer.Port}/vfx-linux-x64.zip";
            Globals.GitHubLatestReleaseAssetsDict["vfx-linux-x64.zip.sig"] = $"http://127.0.0.1:{_peer.Port}/vfx-linux-x64.zip.sig";
            Globals.HttpClientFactory = ClientsWithTheListenersAsOutsideHosts();
            _peer.Answer = line => Listener.Redirect(302, $"http://127.0.0.1:{_elsewhere.Port}/stored{line.Split(' ')[1]}");
            _elsewhere.Answer = line => Listener.Ok(line.Split(' ')[1].EndsWith(".sig") ? signature : asset);

            var (zip, refusal) = await VersionControlService.DownloadVerifiedAsync("vfx-linux-x64.zip");

            Assert.Null(refusal);
            Assert.Equal(asset, Encoding.UTF8.GetString(zip!));
            Assert.Equal(new[] { "GET /stored/vfx-linux-x64.zip | ", "GET /stored/vfx-linux-x64.zip.sig | " }, _elsewhere.Requests);
        }

        [Theory]
        [InlineData("127.0.0.2")]
        [InlineData("[::1]")]
        [InlineData("0.0.0.0")]
        [InlineData("169.254.10.10")]
        [InlineData("[::ffff:127.0.0.2]")]
        public async Task TheOutsideServiceClient_DoesNotFollowARedirectToThisMachine(string host)
        {
            var clients = ClientsWithTheListenersAsOutsideHosts();
            _peer.Answer = _ => Listener.Redirect(302, $"http://{host}:{_elsewhere.Port}/elsewhere/route");

            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => clients.CreateClient(NodeHttp.OutsideService).GetStringAsync($"http://127.0.0.1:{_peer.Port}/download"));

            Assert.Contains("local-only", refused.ToString());
            Assert.Single(_peer.Requests);
            Assert.Empty(_elsewhere.Requests);
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("localhost")]
        public async Task TheOutsideServiceClient_AsTheNodeBuildsIt_DoesNotConnectToThisMachine(string host)
        {
            var url = $"http://{host}:{_peer.Port}/download";

            var fromFactory = await Assert.ThrowsAsync<HttpRequestException>(() => StartupClients().CreateClient(NodeHttp.OutsideService).GetStringAsync(url));
            using var built = NodeHttp.NewOutsideServiceClient();
            var whereBuilt = await Assert.ThrowsAsync<HttpRequestException>(() => built.GetStringAsync(url));

            Assert.Contains("local-only", fromFactory.ToString());
            Assert.Contains("local-only", whereBuilt.ToString());
            await Task.Delay(200);
            Assert.Empty(_peer.Requests);
        }

        /// <summary>On a machine set to use a proxy, the connection is to the proxy, wherever the proxy is.</summary>
        [Fact]
        public async Task TheOutsideServiceClient_OnAMachineSetToUseAProxy_AsksTheProxy()
        {
            var handler = NodeHttp.NewOutsideServiceHandler();
            handler.Proxy = new WebProxy($"http://127.0.0.1:{_peer.Port}");
            _peer.Answer = _ => Listener.Ok("through the proxy");
            using var client = new HttpClient(handler);

            Assert.Equal("through the proxy", await client.GetStringAsync("http://outside.example/download"));

            Assert.Equal("GET http://outside.example/download | ", Assert.Single(_peer.Requests));
        }

        /// <summary>An https request through a proxy opens a tunnel at the proxy first; that connection is to the proxy too.</summary>
        [Fact]
        public async Task TheOutsideServiceClient_OnAMachineSetToUseAProxy_OpensItsTunnelAtTheProxy()
        {
            var handler = NodeHttp.NewOutsideServiceHandler();
            handler.Proxy = new WebProxy($"http://127.0.0.1:{_peer.Port}", BypassOnLocal: true);
            _peer.Answer = _ => "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://outside.example/download"));

            Assert.Equal("CONNECT outside.example:443 | ", Assert.Single(_peer.Requests));
        }

        // ── Where clients are built ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A client is built in the three files that settle what it does with a redirect, and every hub connection is
        /// kept at its host. A new sender uses the factory or NodeHttp and needs no decision of its own.
        /// </summary>
        [Fact]
        public void ClientsAreBuiltWhereRedirectsAreSettled_AndEveryHubConnectionStaysAtItsHost()
        {
            var root = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!, "VerifiedXCore");
            var settled = new[] { "NodeHttp.cs", "BeaconClient.cs", "FrostPeerHttp.cs" };
            const string kinds = "(HttpClient|HttpClientHandler|SocketsHttpHandler|HttpMessageInvoker|ClientWebSocket|WebClient)";
            var builds = new System.Text.RegularExpressions.Regex(
                @"new\s+([\w.]+\.)?" + kinds + @"\b"                        // new HttpClient(...), new System.Net.Http.HttpClient { ... }
                + @"|\b" + kinds + @"\??\s+\w+\s*=\s*new\s*\("            // HttpClient x = new(...)
                + @"|WebRequest\.Create|\.AddHttpClient\s*[<(]"             // older clients, another factory registration
                + @"|AllowAutoRedirect\s*=\s*true"                         // a handler told to follow
                + @"|HttpMessageHandlerFactory\s*=|WebSocketFactory\s*=");   // a hub connection given its own handler or socket
            // A client over a handler NodeHttp built is settled there.
            var overANodeHttpHandler = new System.Text.RegularExpressions.Regex(@"new\s*(HttpClient)?\s*\(([\w.]+\.)?NodeHttp\.New\w*Handler\(\)\)");
            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .ToList();

            var builtElsewhere = files.Where(f => !settled.Contains(Path.GetFileName(f)))
                .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
                .Where(x => !x.l.TrimStart().StartsWith("//") && builds.IsMatch(overANodeHttpHandler.Replace(x.l, "")))
                .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
                .ToList();
            var hubsLeftFree = files.Select(f => (f, text: File.ReadAllText(f)))
                .Where(x => Count(x.text, "HubConnectionBuilder()") != Count(x.text, ".StayAtItsHost("))
                .Select(x => Path.GetFileName(x.f))
                .ToList();

            Assert.NotEmpty(files);
            Assert.Empty(builtElsewhere);
            Assert.Empty(hubsLeftFree);
        }

        private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
