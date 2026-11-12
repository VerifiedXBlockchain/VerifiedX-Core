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
        private sealed class Capture : ILoggerProvider
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

        [Fact]
        public async Task ABodyCutOffOnTheValidatorApi_IsNotLoggedAsAnError()
        {
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = "xTestValidator";
            var capture = new Capture();
            using var host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web => web
                    .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
                    .UseStartup<StartupP2PCaster>()
                    .ConfigureLogging(l => { l.ClearProviders(); l.AddProvider(capture); }))
                .Build();
            try
            {
                await host.StartAsync();
                var port = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

                for (var i = 0; i < 3; i++)
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    var stream = client.GetStream();
                    var head = "POST /valapi/Validator/BurnAlert HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" +
                               "Content-Length: 100000\r\n\r\n{\"a\":";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    client.Client.Shutdown(SocketShutdown.Send);
                    var buffer = new byte[4096];
                    while (await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) > 0) { }
                }

                // Kestrel closes the connection before the request's handling ends; stopping waits for it to end.
                await host.StopAsync();
                Assert.Equal(0, Errors(capture));
            }
            finally
            {
                await host.StopAsync();
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
