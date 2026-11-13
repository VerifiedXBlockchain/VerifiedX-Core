using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A request whose body ends before its Content-Length is the client's doing: the node drops it without writing an
    /// error and stack trace, so a sender cannot flood the console. Errors in the node's own code are still logged.
    /// </summary>
    [Collection("DbContextSequential")]
    public class CutOffRequestBodyTests
    {
        internal sealed class Capture : ILoggerProvider
        {
            public readonly ConcurrentQueue<LogLevel> Levels = new();
            public ILogger CreateLogger(string categoryName) => new Logger(Levels);
            public void Dispose() { }

            private sealed class Logger : ILogger
            {
                private readonly ConcurrentQueue<LogLevel> _levels;
                public Logger(ConcurrentQueue<LogLevel> levels) => _levels = levels;
                public IDisposable BeginScope<TState>(TState state) => null!;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                    _levels.Enqueue(logLevel);
            }
        }

        private static int Errors(Capture capture) => capture.Levels.Count(l => l >= LogLevel.Error);

        /// <summary>Runs ahead of the node's own pipeline: every request appears to come from this address.</summary>
        private sealed class FromAddress : IStartupFilter
        {
            private readonly IPAddress _address;
            public FromAddress(IPAddress address) => _address = address;
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, rest) => { context.Connection.RemoteIpAddress = _address; return rest(); });
                next(app);
            };
        }

        /// <summary>The validator API as the node starts it (its Kestrel settings and startup), on a free port.</summary>
        internal static async Task<(IHost Host, int Port)> StartValidatorApi(Capture capture, IPAddress? from = null)
        {
            var host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web => web
                    .UseKestrel(o => ValidatorService.CasterApiKestrel(o, 0))
                    .UseStartup<StartupP2PCaster>()
                    .ConfigureServices(services => { if (from != null) services.AddSingleton<IStartupFilter>(new FromAddress(from)); })
                    .ConfigureLogging(l => { l.ClearProviders(); l.AddProvider(capture); }))
                .Build();
            await host.StartAsync();
            var port = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            return (host, port);
        }

        /// <summary>Sends a POST head with this Content-Length and body, stops sending unless told not to, and reads the reply
        /// until the node closes.</summary>
        internal static async Task<string> Post(int port, long contentLength, string body, bool stopSending = true, string route = "BurnAlert")
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var head = $"POST /valapi/Validator/{route} HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" +
                       $"Content-Length: {contentLength}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head + body));
            if (stopSending) client.Client.Shutdown(SocketShutdown.Send);
            var reply = new StringBuilder();
            var buffer = new byte[4096];
            int n;
            try
            {
                while ((n = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10))) > 0)
                    reply.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
            catch (System.IO.IOException) { } // the node reset the connection
            return reply.ToString();
        }

        [Fact]
        public async Task ABodyCutOffOnTheValidatorApi_IsNotLoggedAsAnError()
        {
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = "xTestValidator";
            var capture = new Capture();
            var (host, port) = await StartValidatorApi(capture);
            try
            {
                for (var i = 0; i < 3; i++)
                    await Post(port, 100_000, "{\"a\":");

                // Kestrel closes the connection before the request's handling ends; stopping waits for it to end.
                await host.StopAsync();
                Assert.Equal(0, Errors(capture));
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
                Globals.ValidatorAddress = priorValidator;
            }
        }

        [Fact]
        public async Task ABodyOverFiveMegabytes_IsRefusedUnread_AndALargeOneUnderItIsRead()
        {
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = "xTestValidator";
            var capture = new Capture();
            var (host, port) = await StartValidatorApi(capture);
            try
            {
                Assert.StartsWith("HTTP/1.1 413", await Post(port, 5 * 1024 * 1024 + 1, "", stopSending: false));

                var large = "{\"Pad\":\"" + new string('a', 2 * 1024 * 1024) + "\"}";
                var reply = await Post(port, large.Length, large, stopSending: false);
                Assert.StartsWith("HTTP/1.1 400", reply);
                Assert.Contains("Invalid alert", reply); // the route read it and answered
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
                Globals.ValidatorAddress = priorValidator;
            }
        }

        [Fact]
        public async Task AnAddressThatKeepsCuttingOffBodies_IsBanned_AndThenDroppedUnread()
        {
            var priorValidator = Globals.ValidatorAddress;
            var priorPath = Globals.CustomPath;
            var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cutoff_{Guid.NewGuid():N}") + System.IO.Path.DirectorySeparatorChar;
            System.IO.Directory.CreateDirectory(tempRoot);
            Globals.CustomPath = tempRoot;
            var priorBanned = Globals.BannedIPs;
            StartupService.StartupDatabase(); // as the node starts: opens the databases and loads the ban list
            Globals.ValidatorAddress = "xTestValidator";
            const string Sender = "203.0.113.7";
            var (host, port) = await StartValidatorApi(new Capture(), IPAddress.Parse(Sender));
            try
            {
                const string Valid = "{\"Pad\":\"a\"}";
                Assert.Contains("Invalid alert", await Post(port, Valid.Length, Valid, stopSending: false));

                for (var i = 0; i < CutOffBodyQuieting.BanAfter; i++)
                    await Post(port, 100_000, "{\"a\":");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!Globals.BannedIPs.ContainsKey(Sender) && DateTime.UtcNow < deadline) await Task.Delay(20);
                Assert.True(Globals.BannedIPs.ContainsKey(Sender));

                // Banned: the same valid request no longer reaches the route.
                Assert.DoesNotContain("Invalid alert", await Post(port, Valid.Length, Valid, stopSending: false));
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
                Globals.BannedIPs = priorBanned;
                Globals.ValidatorAddress = priorValidator;
                try { VerifiedXCore.Data.DbContext.CloseDB(); } catch { }
                Globals.CustomPath = priorPath;
                try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        /// <summary>Only bad requests within one window add up: an expired window is closed before the next one counts.</summary>
        [Fact]
        public async Task BadRequestsInAnExpiredWindow_DoNotCountTowardABan()
        {
            var priorValidator = Globals.ValidatorAddress;
            var priorPath = Globals.CustomPath;
            var priorWindow = CutOffBodyQuieting.WindowMilliseconds;
            var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cutoff_{Guid.NewGuid():N}") + System.IO.Path.DirectorySeparatorChar;
            System.IO.Directory.CreateDirectory(tempRoot);
            Globals.CustomPath = tempRoot;
            var priorBanned = Globals.BannedIPs;
            StartupService.StartupDatabase();
            Globals.ValidatorAddress = "xTestValidator";
            CutOffBodyQuieting.WindowMilliseconds = 2_000;
            const string Sender = "203.0.113.8";
            var (host, port) = await StartValidatorApi(new Capture(), IPAddress.Parse(Sender));
            try
            {
                for (var i = 0; i < CutOffBodyQuieting.BanAfter - 1; i++)
                    await Post(port, 100_000, "{\"a\":");
                await Task.Delay(2_500);                                                   // the window runs out
                await Post(port, 100_000, "{\"a\":");
                await Task.Delay(500);
                Assert.False(Globals.BannedIPs.ContainsKey(Sender));

                // The one after the pause counted in the new window: nine more make ten in it.
                for (var i = 0; i < CutOffBodyQuieting.BanAfter - 1; i++)
                    await Post(port, 100_000, "{\"a\":");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!Globals.BannedIPs.ContainsKey(Sender) && DateTime.UtcNow < deadline) await Task.Delay(20);
                Assert.True(Globals.BannedIPs.ContainsKey(Sender));
            }
            finally
            {
                CutOffBodyQuieting.WindowMilliseconds = priorWindow;
                await host.StopAsync();
                host.Dispose();
                Globals.BannedIPs = priorBanned;
                Globals.ValidatorAddress = priorValidator;
                try { VerifiedXCore.Data.DbContext.CloseDB(); } catch { }
                Globals.CustomPath = priorPath;
                try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task ThisMachine_IsNeverBannedForCutOffBodies()
        {
            var priorValidator = Globals.ValidatorAddress;
            var priorBanned = Globals.BannedIPs;
            Globals.ValidatorAddress = "xTestValidator";
            Globals.BannedIPs = new ConcurrentDictionary<string, VerifiedXCore.Models.Peers>();
            var (host, port) = await StartValidatorApi(new Capture());
            try
            {
                for (var i = 0; i < CutOffBodyQuieting.BanAfter + 2; i++)
                    await Post(port, 100_000, "{\"a\":");
                await host.StopAsync();
                Assert.Empty(Globals.BannedIPs);
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
                Globals.BannedIPs = priorBanned;
                Globals.ValidatorAddress = priorValidator;
            }
        }

        [Fact]
        public async Task AnErrorInTheNodesOwnCode_IsStillLogged()
        {
            var capture = new Capture();
            using var server = new TestServer(new WebHostBuilder()
                .ConfigureLogging(l => { l.ClearProviders(); l.AddProvider(capture); })
                .Configure(app =>
                {
                    app.UseExceptionHandler(e => e.Run(c => { c.Response.StatusCode = 500; return Task.CompletedTask; }));
                    app.UseQuietCutOffBodies();
                    app.Run(_ => throw new InvalidOperationException("a bug"));
                }));
            var response = await server.CreateClient().GetAsync("/");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(1, Errors(capture));
        }
    }
}
