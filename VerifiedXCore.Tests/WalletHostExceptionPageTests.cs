using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VerifiedXCore;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>The wallet API host answers an unhandled exception with a bare 500 in every environment.</summary>
    [Collection("DbContextSequential")]
    public class WalletHostExceptionPageTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly DateTime? _priorUnlock = Globals.APIUnlockTime;

        public WalletHostExceptionPageTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"devpage_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
            Globals.APIUnlockTime = null;
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.APIUnlockTime = _priorUnlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private sealed class ThrowingEndpoint : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                next(app);
                app.Run(_ => throw new InvalidOperationException("devpage-probe"));
            };
        }

        private static TestServer Server(string environment) => new TestServer(new WebHostBuilder()
            .UseEnvironment(environment)
            .ConfigureServices(s => s.AddSingleton<IStartupFilter, ThrowingEndpoint>())
            .UseStartup<Startup>());

        [Theory]
        [InlineData("Development")]
        [InlineData("Production")]
        public async Task AnUnhandledException_ReturnsABare500(string environment)
        {
            using var server = Server(environment);

            var r = await server.CreateClient().GetAsync("/devpage-probe-path");
            var body = await r.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
            Assert.DoesNotContain("devpage-probe", body);
            Assert.DoesNotContain("InvalidOperationException", body);
        }

        [Fact]
        public async Task AnApiRoute_StillAnswersInDevelopment()
        {
            using var server = Server("Development");

            var r = await server.CreateClient().GetAsync("/api/V1/CheckStatus");

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
    }
}
