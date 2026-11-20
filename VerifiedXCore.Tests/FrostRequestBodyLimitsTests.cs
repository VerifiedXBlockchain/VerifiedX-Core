using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The FROST server bounds how much request body it holds: each route takes a body no longer than its own callers
    /// send, the larger bodies of ceremonies are counted as they are read so that one source and one kind of caller
    /// hold a bounded amount, and a connection carries one request at a time on both listeners. Bodies the ceremonies
    /// send are read as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostRequestBodyLimitsTests : IDisposable
    {
        private const int Mb = 1024 * 1024;
        private const int Small = FrostRequestBodyLimits.SmallBodyBytes;
        private const int Largest = FrostRequestBodyLimits.LargestBodyBytes;
        private const long PerSource = FrostRequestBodyLimits.CountedPerSourceBytes;
        private const long PerKind = FrostRequestBodyLimits.CountedPerKindBytes;
        private static readonly string[] ValidatorIps = { "198.51.100.1", "198.51.100.2", "198.51.100.3", "198.51.100.4", "198.51.100.5" };
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly string _sessionId = $"body-limits-{Guid.NewGuid():N}";
        private readonly string _round2;

        public FrostRequestBodyLimitsTests()
        {
            Globals.FrostFFIAvailable = true; // a node whose FROST library passed its check
            // A key ceremony under way here, started by a leader that signs what it sends.
            FrostSessionStorage.DKGSessions[_sessionId] = new DKGSession
            {
                SessionId = _sessionId, SmartContractUID = "sc-body-limits", LeaderAddress = "xBodyLimitsLeader", LeaderSigned = true,
                ParticipantAddresses = new List<string>(), StartTimestamp = TimeUtil.GetTime(),
            };
            _round2 = "/frost/dkg/round2/" + _sessionId;
        }

        public void Dispose()
        {
            FrostSessionStorage.DKGSessions.TryRemove(_sessionId, out _);
            Globals.FrostFFIAvailable = _priorLibrary;
        }

        private static FrostConnectionLimits Limits()
        {
            var limits = new FrostConnectionLimits(() => ValidatorIps);
            limits.ReadValidators();
            return limits;
        }

        private static IHost Server(int port, FrostConnectionLimits limits) => new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(options => FrostServer.ConfigureKestrel(options, port, limits))
                .UseStartup<FrostStartup>())
            .Build();

        [Theory]
        [InlineData("/frost/backup/store", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/backup/recover", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/backup/check", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/sign/abort", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/dkg/start", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/dkg/abort", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/dkg/round1", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/dkg/share", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/no/such/route", FrostRequestBodyLimits.SmallBodyBytes)]
        [InlineData("/frost/sign/start", FrostRequestBodyLimits.SigningBodyBytes)]
        [InlineData("/frost/sign/round2/any-session", FrostRequestBodyLimits.SigningBodyBytes)]
        [InlineData("/FROST/SIGN/START", FrostRequestBodyLimits.SigningBodyBytes)]
        [InlineData("/frost/dkg/round2/any-session", FrostRequestBodyLimits.LargestBodyBytes)]
        [InlineData("/frost/dkg/shares/any-session", FrostRequestBodyLimits.LargestBodyBytes)]
        public void A_route_takes_a_body_no_longer_than_its_callers_send(string path, int bytes)
        {
            Assert.Equal(bytes, FrostRequestBodyLimits.MaxBytesFor(path));
        }

        [Fact]
        public async Task A_request_announcing_a_longer_body_than_its_route_takes_is_refused_before_any_of_it_is_read()
        {
            var port = FrostConnectionLimitsTests.FreePortPair();
            using var host = Server(port, Limits());
            await host.StartAsync();
            try
            {
                var routes = new[]
                {
                    "/frost/backup/store", "/frost/backup/recover", "/frost/backup/check", "/frost/sign/abort",
                    "/frost/dkg/start", "/frost/dkg/abort", "/frost/sign/start", "/frost/sign/round2/" + _sessionId,
                    _round2, "/frost/dkg/shares/" + _sessionId,
                };
                foreach (var route in routes)
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    // The headers only: the body they announce is never sent.
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Headers(route, FrostRequestBodyLimits.MaxBytesFor(route) + 1L)));
                    var answer = await FrostConnectionLimitsTests.ReadAsync(client);
                    Assert.True(answer.StartsWith("HTTP/1.1 413", StringComparison.Ordinal), $"{route}: {FirstLine(answer)}");
                }

                // The encrypted listener refuses the same.
                using var handler = new SocketsHttpHandler();
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true; // the server's certificate is self-signed
                using var https = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                using var refused = await https.PostAsync($"https://127.0.0.1:{port + 1}/frost/backup/store", new StringContent(Json(Small + 1), Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
            }
            finally { await host.StopAsync(TimeSpan.FromSeconds(2)); }
        }

        [Theory]
        [InlineData("")]
        [InlineData("Content-Length: 1\r\n")] // a length written beside the pieces does not make the body shorter
        public async Task A_body_sent_in_pieces_is_read_no_further_than_its_route_takes(string lengthHeader)
        {
            var port = FrostConnectionLimitsTests.FreePortPair();
            using var host = Server(port, Limits());
            await host.StartAsync();
            try
            {
                // Just under the route's size (the pieces' own framing is part of what the server reads) the body is
                // read whole, and the route judges what it says.
                var answer = await PostInPiecesAsync(port, "/frost/backup/store", Json(Small - 1024), lengthHeader);
                Assert.True(answer.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), FirstLine(answer));
                Assert.Contains("Missing required fields", answer);

                // Longer, it is not: the route never comes to judge it, and the caller is told why.
                answer = await PostInPiecesAsync(port, "/frost/backup/store", Json(Small + 16 * 1024), lengthHeader);
                Assert.True(answer.StartsWith("HTTP/1.1 413", StringComparison.Ordinal), FirstLine(answer));
                Assert.Contains("longer than this route takes", answer);
                Assert.DoesNotContain("Missing required fields", answer);

                // The same on a route that takes more, past its own size.
                answer = await PostInPiecesAsync(port, "/frost/sign/start", Json(FrostRequestBodyLimits.SigningBodyBytes + 16 * 1024), lengthHeader);
                Assert.True(answer.StartsWith("HTTP/1.1 413", StringComparison.Ordinal), FirstLine(answer));
                Assert.Contains("longer than this route takes", answer);
            }
            finally { await host.StopAsync(TimeSpan.FromSeconds(2)); }
        }

        [Fact]
        public async Task Bodies_as_long_as_the_routes_callers_send_are_read_and_judged_by_their_routes()
        {
            var port = FrostConnectionLimitsTests.FreePortPair();
            var limits = Limits();
            using var host = Server(port, limits);
            await host.StartAsync();
            try
            {
                // A short route, at the size every route takes: its route reads it and finds it is no request of its kind.
                var answer = await PostAsync(port, "/frost/sign/abort", Small);
                Assert.True(answer.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), FirstLine(answer));
                Assert.Contains("SessionId, LeaderAddress and LeaderSignature required", answer);

                // A signing start as long as the largest transaction's: read, and refused for what it says.
                answer = await PostAsync(port, "/frost/sign/start", FrostRequestBodyLimits.SigningBodyBytes);
                Assert.True(answer.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), FirstLine(answer));
                Assert.Contains("Invalid SessionId", answer);

                // A key ceremony's commitment set, at 2 MB (200 participants) and at the most the server takes: read
                // whole, then refused because this one is not signed by the session's leader.
                foreach (var bytes in new[] { 2 * Mb, Largest })
                {
                    answer = await PostAsync(port, _round2, bytes);
                    Assert.True(answer.StartsWith("HTTP/1.1 403", StringComparison.Ordinal), FirstLine(answer));
                    Assert.Contains("signed by its leader", answer);
                }

                // Its share batch, the same.
                answer = await PostAsync(port, "/frost/dkg/shares/" + _sessionId, 2 * Mb);
                Assert.True(answer.StartsWith("HTTP/1.1 403", StringComparison.Ordinal), FirstLine(answer));
                Assert.Contains("sent by its leader", answer);

                // A signing start as this node's own client sends it (in pieces, without a length), for the most
                // signers a session has and a transaction of 50 coins, then of 1,500: each is read and judged.
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                foreach (var coins in new[] { 50, 1_500 })
                {
                    var start = new FrostSigningStartRequest
                    {
                        SessionId = new string('s', FrostSessionStorage.MAX_SESSION_ID_LENGTH + 1), // the route's first check refuses it
                        MessageHash = new string('a', 64), SmartContractUID = "sc-body-limits", LeaderAddress = "xBodyLimitsLeader",
                        Timestamp = TimeUtil.GetTime(), LeaderSignature = new string('b', 140), ContentSignature = new string('c', 140),
                        SignerAddresses = Enumerable.Range(0, FrostSessionStorage.MAX_PARTICIPANTS).Select(i => $"xSigner{i:D27}").ToList(),
                        RequiredThreshold = 51, WithdrawalRequestHash = new string('d', 64), InputCount = coins,
                        AllInputSighashes = Enumerable.Range(0, coins).Select(_ => new string('e', 64)).ToList(),
                        TxInputOutpoints = Enumerable.Range(0, coins).Select(i => new string('f', 64) + ":" + i).ToList(),
                        BtcTxId = new string('0', 64), UnsignedTxHex = new string('1', 20 + coins * 82 + 86),
                        Prevouts = Enumerable.Range(0, coins).Select(i => new VerifiedXCore.Bitcoin.Models.PinnedWithdrawalCoin
                            { TxId = new string('f', 64), Vout = (uint)i, ValueSats = 2_100_000_000_000_000, ScriptPubKeyHex = new string('2', 68) }).ToList(),
                    };
                    using var response = await http.PostAsJsonAsync($"http://127.0.0.1:{port}/frost/sign/start", start);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    Assert.Contains("Invalid SessionId", await response.Content.ReadAsStringAsync());
                }

                // Nothing stays counted once the requests are served.
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == 0, TimeSpan.FromSeconds(5)));
            }
            finally { await host.StopAsync(TimeSpan.FromSeconds(2)); }
        }

        [Fact]
        public async Task One_source_holds_a_bounded_amount_of_large_bodies_in_progress_and_other_requests_are_still_served()
        {
            var port = FrostConnectionLimitsTests.FreePortPair();
            var limits = Limits();
            using var host = Server(port, limits);
            await host.StartAsync();
            var held = new List<TcpClient>();
            try
            {
                // Nothing is counted for a body that is announced and not sent.
                held.Add(await BeginAsync(port, Headers(_round2, Largest)));
                Assert.True(SpinWait.SpinUntil(() => limits.Counts().Serving == 1, TimeSpan.FromSeconds(5)));
                Assert.Equal(0, limits.Bodies.Counted());
                held[0].Dispose();

                // This machine has as many of the largest bodies in progress as one source may: each sent but for its
                // last byte, and counted by what has arrived.
                for (var i = 0; i < PerSource / Largest; i++)
                    held.Add(await BeginAsync(port, Headers(_round2, Largest) + new string(' ', Largest - 1)));
                var full = PerSource - PerSource / Largest;
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == full, TimeSpan.FromSeconds(10)));

                // One more large body, of a size ceremonies send, is told to ask again before any of it is sent.
                using (var extra = await BeginAsync(port, Headers(_round2, 2 * Mb)))
                {
                    var busy = await FrostConnectionLimitsTests.ReadAsync(extra);
                    Assert.True(busy.StartsWith("HTTP/1.1 503", StringComparison.Ordinal), FirstLine(busy));
                }
                Assert.Equal(full, limits.Bodies.Counted());

                // Requests with short bodies and with none are served meanwhile.
                var small = await PostAsync(port, "/frost/sign/abort", Small);
                Assert.True(small.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), FirstLine(small));
                using (var health = await BeginAsync(port, "GET /health HTTP/1.1\r\nHost: frost\r\n\r\n"))
                    Assert.StartsWith("HTTP/1.1 200", await FrostConnectionLimitsTests.ReadAsync(health));

                // When one of the bodies in progress ends, a ceremony's body is taken again.
                held[1].Dispose();
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == full - (Largest - 1), TimeSpan.FromSeconds(5)));
                var taken = await PostAsync(port, _round2, 2 * Mb);
                Assert.True(taken.StartsWith("HTTP/1.1 403", StringComparison.Ordinal), FirstLine(taken));
                Assert.Contains("signed by its leader", taken);
            }
            finally
            {
                foreach (var client in held) client.Dispose();
                await host.StopAsync(TimeSpan.FromSeconds(2));
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("Content-Length: 1\r\n")] // a length written beside the pieces does not make the body shorter
        public async Task A_large_body_sent_in_pieces_is_counted_as_it_is_read_and_read_no_further_than_its_source_has_room(string lengthHeader)
        {
            var port = FrostConnectionLimitsTests.FreePortPair();
            var limits = Limits();
            using var host = Server(port, limits);
            await host.StartAsync();
            var held = new List<TcpClient>();
            try
            {
                // A long signing start sent in pieces is read whole and judged by its route.
                var whole = await PostInPiecesAsync(port, "/frost/sign/start", Json(512 * 1024), lengthHeader);
                Assert.True(whole.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), FirstLine(whole));
                Assert.Contains("Invalid SessionId", whole);
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == 0, TimeSpan.FromSeconds(5)));

                // One in progress is counted by what has been read of it.
                const int begun = 300_000;
                var growing = await BeginAsync(port, PiecesHeaders("/frost/sign/start", lengthHeader) + Piece(new string(' ', begun)));
                held.Add(growing);
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == begun, TimeSpan.FromSeconds(5)));

                // The same source then has other bodies in progress, until one more byte is all it has room for.
                held.Add(await BeginAsync(port, Headers(_round2, Largest) + new string(' ', Largest - 1)));
                var second = (int)(PerSource - begun - Largest);
                held.Add(await BeginAsync(port, Headers(_round2, second) + new string(' ', second - 1)));
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == PerSource - 2, TimeSpan.FromSeconds(10)));

                // More of the body in pieces is not read: its request is answered there, unfinished, as one to ask
                // again, and what was counted for it is given back.
                await growing.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Piece(new string(' ', Small))));
                var cutOff = await FrostConnectionLimitsTests.ReadAsync(growing);
                Assert.True(cutOff.StartsWith("HTTP/1.1 503", StringComparison.Ordinal), FirstLine(cutOff));
                Assert.Contains("ask again", cutOff);
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == PerSource - 2 - begun, TimeSpan.FromSeconds(5)));

                // This node's own client, sending a body in pieces that the source has no room for, gets that answer
                // (the one its retries wait for), not a failure of the route.
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                using (var busy = await http.PostAsJsonAsync($"http://127.0.0.1:{port}/frost/sign/start", new { Padding = new string('a', 512 * 1024) }))
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                    Assert.Contains("ask again", await busy.Content.ReadAsStringAsync());
                }
                Assert.True(SpinWait.SpinUntil(() => limits.Bodies.Counted() == PerSource - 2 - begun, TimeSpan.FromSeconds(5)));

                // A short signing start in pieces, as this node's own client sends one, is still read and judged.
                var served = await PostInPiecesAsync(port, "/frost/sign/start", Json(40 * 1024), lengthHeader);
                Assert.True(served.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), FirstLine(served));
                Assert.Contains("Invalid SessionId", served);
            }
            finally
            {
                foreach (var client in held) client.Dispose();
                await host.StopAsync(TimeSpan.FromSeconds(2));
            }
        }

        [Fact]
        public async Task One_kind_of_caller_holds_a_bounded_amount_of_large_bodies_and_cannot_take_another_kinds_room()
        {
            var limits = Limits();
            var served = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inProgress = new List<Task>();
            var perSource = (int)(PerSource / Largest);
            var sourcesToFill = (int)(PerKind / PerSource);

            // A request from the address whose body the route reads whole and which is then served until the test
            // ends: null while it is being served, or its status when it was answered without the route.
            int? Request(string ip, int bytes = Largest)
            {
                var context = new DefaultHttpContext();
                context.Features.Set(limits.TryAdmit(IPAddress.Parse(ip))!);
                context.Request.Method = "POST";
                context.Request.Path = _round2;
                context.Request.ContentLength = bytes;
                context.Request.Body = new MemoryStream(new byte[bytes]);
                var request = FrostRequestBodyLimits.Bound(context, async () =>
                {
                    var buffer = new byte[Small];
                    while (await context.Request.Body.ReadAsync(buffer) > 0) { }
                    await served.Task;
                });
                if (request.IsCompleted) return context.Response.StatusCode;
                inProgress.Add(request);
                return null;
            }

            // Sources that are not validators, each in its own network, fill what their kind may hold.
            for (var network = 0; network < sourcesToFill; network++)
            {
                for (var i = 0; i < perSource; i++) Assert.Null(Request($"203.0.{network}.9"));
                // One source holds no more than its part, whatever the size.
                Assert.Equal(StatusCodes.Status503ServiceUnavailable, Request($"203.0.{network}.9", Small + 1));
            }
            Assert.Equal(PerKind, limits.Bodies.Counted());

            // A source of that kind which holds nothing is told to ask again; its short bodies are taken.
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Request("203.0.200.9"));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Request("203.0.200.9", Small + 1));
            Assert.Null(Request("203.0.200.9", Small));

            // Validators and the operator at the machine have their own room.
            foreach (var validator in ValidatorIps.Take(sourcesToFill))
                for (var i = 0; i < perSource; i++) Assert.Null(Request(validator));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Request(ValidatorIps[sourcesToFill]));
            Assert.Null(Request("127.0.0.1"));
            Assert.Equal(2 * PerKind + Largest, limits.Bodies.Counted());

            // Served requests give their room back.
            served.SetResult();
            await Task.WhenAll(inProgress);
            Assert.Equal(0, limits.Bodies.Counted());
            Assert.Equal(StatusCodes.Status200OK, Request("203.0.200.9"));
        }

        [Fact]
        public async Task Both_listeners_speak_http_1_1_only_also_to_a_client_that_offers_http_2()
        {
            var port = FrostConnectionLimitsTests.FreePortPair();
            using var host = Server(port, Limits());
            await host.StartAsync();
            try
            {
                using var handler = new SocketsHttpHandler();
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true; // the server's certificate is self-signed
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port + 1}/health")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(HttpVersion.Version11, response.Version);

                // Where the platform's TLS cannot offer HTTP/2 at all (macOS on this runtime) that answer is HTTP/1.1
                // whatever the listeners are set to, so their setting is read as well: both, HTTP/1.1 only.
                var kestrel = host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
                var listenersProperty = typeof(KestrelServerOptions).GetProperty("CodeBackedListenOptions", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.True(listenersProperty != null, "this runtime's Kestrel keeps its listeners elsewhere: read their protocols from there");
                var listeners = (IEnumerable<ListenOptions>)listenersProperty!.GetValue(kestrel)!;
                Assert.Equal(new[] { HttpProtocols.Http1, HttpProtocols.Http1 }, listeners.Select(listener => listener.Protocols));
            }
            finally { await host.StopAsync(TimeSpan.FromSeconds(2)); }
        }

        private static string Headers(string route, long contentLength) =>
            $"POST {route} HTTP/1.1\r\nHost: frost\r\nContent-Type: application/json\r\nContent-Length: {contentLength}\r\n\r\n";

        private static string PiecesHeaders(string route, string lengthHeader) =>
            $"POST {route} HTTP/1.1\r\nHost: frost\r\nContent-Type: application/json\r\n{lengthHeader}Transfer-Encoding: chunked\r\n\r\n";

        private static string Piece(string data) => $"{data.Length:x}\r\n{data}\r\n";

        private const string LastPiece = "0\r\n\r\n";

        /// <summary>A JSON object of exactly <paramref name="bytes"/>.</summary>
        private static string Json(int bytes)
        {
            const string open = "{\"Padding\":\"", close = "\"}";
            return open + new string('a', bytes - open.Length - close.Length) + close;
        }

        /// <summary>A new connection on which <paramref name="sent"/> has been written.</summary>
        private static async Task<TcpClient> BeginAsync(int port, string sent)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(sent));
            return client;
        }

        /// <summary>Posts a JSON body of exactly <paramref name="bytes"/>, its length announced, on a new connection; the server's answer.</summary>
        private static async Task<string> PostAsync(int port, string route, int bytes)
        {
            using var client = await BeginAsync(port, Headers(route, bytes) + Json(bytes));
            return await FrostConnectionLimitsTests.ReadAsync(client);
        }

        /// <summary>Posts the body whole, in pieces, on a new connection; the server's answer.</summary>
        private static async Task<string> PostInPiecesAsync(int port, string route, string body, string lengthHeader)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            try
            {
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(PiecesHeaders(route, lengthHeader) + Piece(body[..(body.Length / 2)]) + Piece(body[(body.Length / 2)..]) + LastPiece));
            }
            catch (IOException) { } // the server stopped reading
            return await FrostConnectionLimitsTests.ReadAsync(client);
        }

        private static string FirstLine(string answer) => answer.Split("\r\n")[0];
    }
}
