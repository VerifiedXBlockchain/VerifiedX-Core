using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// An unknown action under /api/V1 or /scapi/SCV1 is a 404 (403 without the API token). One path segment used to
    /// match both the status route and the command route, and the ambiguous match surfaced as a 500.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ApiUnknownActionTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly SecureString? _priorToken = Globals.APIToken;

        public ApiUnknownActionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"api404_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
            Globals.APIToken = null;
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.APIToken = _priorToken;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static async Task<(HttpStatusCode, string)> Get(string path, string? token = null)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<Startup>());
            var client = server.CreateClient();
            if (token != null) client.DefaultRequestHeaders.Add("apitoken", token);
            var r = await client.GetAsync(path);
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("/api/V1/GetLatestBlock")]
        [InlineData("/api/V1/NoSuch/Thing")]
        [InlineData("/scapi/SCV1/NoSuchAction")]
        [InlineData("/scapi/SCV1/NoSuch/Thing")]
        public async Task UnknownAction_Is404(string path)
        {
            var (status, _) = await Get(path);
            Assert.Equal(HttpStatusCode.NotFound, status);
        }

        [Fact]
        public async Task UnknownAction_WithoutTheApiToken_Is403()
        {
            var token = new SecureString();
            foreach (var c in "api-token-404") token.AppendChar(c);
            Globals.APIToken = token;

            Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/V1/GetLatestBlock")).Item1);
            Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/V1/GetLatestBlock", "api-token-404")).Item1);
        }

        [Fact]
        public async Task KnownRoutes_StillAnswer()
        {
            Assert.Equal((HttpStatusCode.OK, "[\"VFX-Wallet\",\"API\"]"), await Get("/api/V1"));
            Assert.Equal((HttpStatusCode.OK, "[\"Smart\",\"Contracts\",\"API\"]"), await Get("/scapi/SCV1"));
            Assert.Equal((HttpStatusCode.OK, "Online"), await Get("/api/V1/CheckStatus"));
            Assert.Equal((HttpStatusCode.OK, "Online"), await Get("/api/V1/anypassword/CheckStatus"));
        }

        [Fact]
        public async Task GetGenesisBlockCommand_Answers()
        {
            BlockchainData.GetBlocks().Insert(new Block { Height = 0, Hash = "genesis-hash-404", Transactions = new System.Collections.Generic.List<Transaction>() });

            var (status, body) = await Get("/api/V1/getgenesisblock");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("genesis-hash-404", body);
        }
    }
}
