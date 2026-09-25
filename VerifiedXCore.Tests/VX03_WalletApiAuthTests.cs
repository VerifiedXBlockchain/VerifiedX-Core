using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-03 (HIGH): "The wallet API moves funds without authentication".
    ///
    /// Audit PoC: POST /wallet/api/send/vfx with no token, no password and no session made the node
    /// sign and broadcast a 750,000 VFX transfer. Cause: the API-lock middleware returned early for any
    /// path starting "/wallet", and WalletController had no [ActionFilterController].
    ///
    /// These tests drive the REAL Startup pipeline in-process.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX03_WalletApiAuthTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly SecureString? _priorToken = Globals.APIToken;
        private readonly bool _priorOpenApi = Globals.OpenAPI;
        private readonly DateTime? _priorUnlock = Globals.APIUnlockTime;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;

        private const string SendBody = "{\"From\":\"xQbL7kdZmUjB8u3vCvB9tF6Mb4gnv6QBZY\",\"To\":\"xEY5KtYTkhM3DuNStbLzTLX9bxMvAAa3nA\",\"Amount\":\"750000\"}";

        public VX03_WalletApiAuthTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx03_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = false;
            Globals.APIToken = null;
            Globals.OpenAPI = false;
            Globals.APIUnlockTime = null;
            Globals.IsWalletEncrypted = false;
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.APIToken = _priorToken;
            Globals.OpenAPI = _priorOpenApi;
            Globals.APIUnlockTime = _priorUnlock;
            Globals.IsWalletEncrypted = _priorEncrypted;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<Startup>());

        private static HttpRequestMessage Post(string path, string body)
            => new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static SecureString Secure(string s) { var ss = new SecureString(); foreach (var c in s) ss.AppendChar(c); return ss; }

        // ── Audit PoC ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX03_AuditPoC_SendVfx_NoCredentials_ApiDisabled_Refused()
        {
            using var server = NewServer();
            var r = await server.CreateClient().SendAsync(Post("/wallet/api/send/vfx", SendBody));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        [Fact]
        public async Task VX03_AuditPoC_SendVfx_ApiEnabledWithToken_NoTokenHeader_Refused()
        {
            Startup.APIEnabled = true;
            Globals.APIToken = Secure("operator-token");
            using var server = NewServer();

            var r = await server.CreateClient().SendAsync(Post("/wallet/api/send/vfx", SendBody));

            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        [Fact]
        public async Task VX03_AuditPoC_SendVfx_ApiPasswordWindowClosed_Refused()
        {
            Startup.APIEnabled = true;
            Globals.APIUnlockTime = DateTime.UtcNow.AddMinutes(-1); // API password set, window closed
            using var server = NewServer();

            var r = await server.CreateClient().SendAsync(Post("/wallet/api/send/vfx", SendBody));

            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        [Fact]
        public async Task VX03_Control_SendVfx_WithToken_ReachesController()
        {
            // Authorized caller still reaches the route (it then fails on the unknown account — the
            // audit's own control: "From address not held by this node").
            Startup.APIEnabled = true;
            Globals.APIToken = Secure("operator-token");
            using var server = NewServer();
            var req = Post("/wallet/api/send/vfx", SendBody);
            req.Headers.Add("apitoken", "operator-token");

            var r = await server.CreateClient().SendAsync(req);

            Assert.NotEqual(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.NotEqual(HttpStatusCode.NotFound, r.StatusCode);
        }

        // ── What stays open, and what is removed ───────────────────────────────────────────

        [Fact]
        public async Task VX03_WalletHtmlShell_And_Explorer_StayReachable_WithApiDisabled()
        {
            using var server = NewServer();
            var client = server.CreateClient();
            Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/wallet")).StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/explorer/api/stats")).StatusCode);
            // …but the wallet API behind the shell does not.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/wallet/api/accounts")).StatusCode);
        }

        [Fact]
        public async Task VX03_PrivacyGetMutators_NoLongerExistAsGet()
        {
            Startup.APIEnabled = true;
            using var server = NewServer();
            var client = server.CreateClient();
            // Password used to travel as a URL segment; shield/unshield/transfer were CSRF-able GETs.
            var r1 = await client.GetAsync("/wallet/api/privacy/createShieldedAddress/xAddr/secretpassword");
            var r2 = await client.GetAsync("/wallet/api/privacy/shield/xFrom/zfx_to/1");
            Assert.True(r1.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, r1.StatusCode.ToString());
            Assert.True(r2.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, r2.StatusCode.ToString());
        }

        [Fact]
        public async Task VX03_ValidatorController_NotServedOnWalletApiHost()
        {
            Startup.APIEnabled = true;
            using var server = NewServer();
            var r = await server.CreateClient().GetAsync("/valapi/Validator");
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        // ── CSRF / DNS rebinding against the loopback API ──────────────────────────────────

        [Fact]
        public async Task VX03_CrossOriginPost_Refused_EvenWhenAuthorized()
        {
            Startup.APIEnabled = true;
            using var server = NewServer();
            var req = Post("/wallet/api/send/vfx", SendBody);
            req.Headers.Add("Origin", "http://attacker.example");

            var r = await server.CreateClient().SendAsync(req);

            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        [Fact]
        public async Task VX03_CrossSiteGet_Refused()
        {
            // An <img src="http://127.0.0.1:7292/api/V1/SendTransaction/..."> on any web page.
            Startup.APIEnabled = true;
            using var server = NewServer();
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/V1/CheckStatus");
            req.Headers.Add("Sec-Fetch-Site", "cross-site");

            var r = await server.CreateClient().SendAsync(req);

            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        [Fact]
        public async Task VX03_DnsRebindingHost_Refused_UnlessOpenApi()
        {
            Startup.APIEnabled = true;
            using var server = NewServer();
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/V1/CheckStatus");
            req.Headers.Host = "rebind.attacker.example";
            Assert.Equal(HttpStatusCode.Forbidden, (await server.CreateClient().SendAsync(req)).StatusCode);

            Globals.OpenAPI = true;
            var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/V1/CheckStatus");
            req2.Headers.Host = "node.operator.example";
            Assert.NotEqual(HttpStatusCode.Forbidden, (await server.CreateClient().SendAsync(req2)).StatusCode);
        }

        [Fact]
        public async Task VX03_Control_SameOriginAndNonBrowserClients_Pass()
        {
            Startup.APIEnabled = true;
            using var server = NewServer();
            var client = server.CreateClient();

            // Non-browser client: no Sec-Fetch-Site, no Origin.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/V1/CheckStatus")).StatusCode);

            // The node's own page (same-origin fetch).
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/V1/CheckStatus");
            req.Headers.Add("Sec-Fetch-Site", "same-origin");
            req.Headers.Add("Origin", "http://localhost");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);
        }

        // ── Guard unit behaviour ───────────────────────────────────────────────────────────

        [Theory]
        [InlineData("127.0.0.1:7292", "127.0.0.1")]
        [InlineData("localhost", "localhost")]
        [InlineData("[::1]:7292", "[::1]")]
        [InlineData("::1", "::1")]
        public void VX03_HostWithoutPort(string host, string expected) => Assert.Equal(expected, ApiRequestGuard.HostWithoutPort(host));

        [Theory]
        [InlineData("GET", "/wallet", true)]
        [InlineData("GET", "/wallet/", true)]
        [InlineData("GET", "/explorer/api/block/1", true)]
        [InlineData("GET", "/wallet/api/accounts", false)]
        [InlineData("POST", "/wallet/api/send/vfx", false)]
        [InlineData("POST", "/wallet", false)]
        [InlineData("GET", "/walletx", false)]
        public void VX03_GateExemptPaths(string method, string path, bool exempt) => Assert.Equal(exempt, ApiRequestGuard.IsGateExemptPath(method, path));

        [Fact]
        public void VX03_OriginNull_Refused() => Assert.NotNull(ApiRequestGuard.GetRejection("localhost:7292", "null", null, false));

        // ── Follow-up (independent review): openapi without a credential ─────────────────

        [Fact]
        public void VX03_OpenApiWithoutAnyCredential_IsRefused_ApiStaysOnLoopback()
        {
            var (open, token, pw, always) = (Globals.OpenAPI, Globals.APIToken, Globals.APIPassword, Globals.AlwaysRequireAPIPassword);
            try
            {
                Globals.OpenAPI = true; Globals.APIToken = null; Globals.APIPassword = null;
                Assert.NotNull(VerifiedXCore.Utilities.ApiRequestGuard.EnforceOpenApiCredential());
                Assert.False(Globals.OpenAPI);

                Globals.OpenAPI = true; Globals.APIToken = VerifiedXCore.Extensions.GenericExtensions.ToSecureString("token-123");
                Assert.Null(VerifiedXCore.Utilities.ApiRequestGuard.EnforceOpenApiCredential()); // control: a token keeps openapi
                Assert.True(Globals.OpenAPI);

                // Follow-up (second review): an API password without AlwaysRequireAPIPassword is a global unlock window
                // (every network caller gets the API after one UnlockWallet), so it is not a network credential.
                Globals.APIToken = null; Globals.APIPassword = "enc"; Globals.AlwaysRequireAPIPassword = false;
                Assert.NotNull(VerifiedXCore.Utilities.ApiRequestGuard.EnforceOpenApiCredential());
                Assert.False(Globals.OpenAPI);

                Globals.OpenAPI = true; Globals.AlwaysRequireAPIPassword = true;
                Assert.Null(VerifiedXCore.Utilities.ApiRequestGuard.EnforceOpenApiCredential()); // control: checked on every request
                Assert.True(Globals.OpenAPI);
            }
            finally { (Globals.OpenAPI, Globals.APIToken, Globals.APIPassword, Globals.AlwaysRequireAPIPassword) = (open, token, pw, always); }
        }
    }
}
