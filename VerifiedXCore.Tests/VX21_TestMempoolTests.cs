using System;
using System.IO;
using System.Net;
using System.Security;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-21 (LOW): "An unauthenticated request terminates the node process".
    ///
    /// Audit PoC: GET /txapi/TXV1/TestMempool with no credential and no body → "Stack overflow." in TestMempool and the
    /// dotnet process terminated: the route serialised the live LiteDB collection handle, not its rows.
    /// (Run against the pre-fix build, this test crashes the test host — that is the red result.)
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX21_TestMempoolTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;

        public VX21_TestMempoolTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx21_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task VX21_AuditPoC_TestMempool_ReturnsRows_ProcessSurvives()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<Startup>());
            var r = await server.CreateClient().GetAsync("/txapi/TXV1/TestMempool");
            var body = await r.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("\"Pool\":[", body); // a list of rows, not a collection handle
        }
    }
}
