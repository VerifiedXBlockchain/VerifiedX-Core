using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VerifiedXCore;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// GetAllSmartContracts/{pageNumber}/{search} reaches the search overload. The excludeToken overload's two-segment
    /// route used to take it and fail to bind the search text as a bool (400).
    /// </summary>
    [Collection("DbContextSequential")]
    public class GetAllSmartContractsRouteTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;

        public GetAllSmartContractsRouteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"scroute_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>Records the route the request was served by (the endpoint stays on the context after the pipeline).</summary>
        private sealed class RouteCapture : IStartupFilter
        {
            public string? Pattern;
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (ctx, nxt) =>
                {
                    await nxt();
                    Pattern = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                });
                next(app);
            };
        }

        private static async Task<(HttpStatusCode Status, string? Route)> Get(string path)
        {
            var capture = new RouteCapture();
            using var server = new TestServer(new WebHostBuilder()
                .ConfigureServices(s => s.AddSingleton<IStartupFilter>(capture))
                .UseStartup<Startup>());
            var r = await server.CreateClient().GetAsync(path);
            return (r.StatusCode, capture.Pattern);
        }

        private const string Search = "scapi/SCV1/GetAllSmartContracts/{pageNumber}/{**search}";

        [Theory]
        [InlineData("/scapi/SCV1/GetAllSmartContracts/1/abc")]
        [InlineData("/scapi/SCV1/GetAllSmartContracts/1/abc/def")]
        public async Task PageAndSearch_ReachesTheSearchOverload(string path)
        {
            var (status, route) = await Get(path);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(Search, route);
        }

        [Theory]
        [InlineData("/scapi/SCV1/GetAllSmartContracts/1", "scapi/SCV1/GetAllSmartContracts/{pageNumber}")]
        [InlineData("/scapi/SCV1/GetAllSmartContracts/1/true", "scapi/SCV1/GetAllSmartContracts/{pageNumber}/{excludeToken:bool?}")]
        [InlineData("/scapi/SCV1/GetAllSmartContracts/1/false/true", "scapi/SCV1/GetAllSmartContracts/{pageNumber}/{excludeToken:bool?}/{tokensOnly?}")]
        [InlineData("/scapi/SCV1/GetAllSmartContracts/1/false/false/abc", "scapi/SCV1/GetAllSmartContracts/{pageNumber}/{excludeToken:bool?}/{tokensOnly?}/{**search}")]
        public async Task BoolForms_StillReachTheFilterOverload(string path, string expectedRoute)
        {
            var (status, route) = await Get(path);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(expectedRoute, route);
        }
    }
}
