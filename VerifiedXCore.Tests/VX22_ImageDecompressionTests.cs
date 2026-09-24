using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-22 (LOW): "Decompression without an output bound amplifies a request by three orders of magnitude".
    ///
    /// Audit PoC: POST /api/V2/GetImageUncompressedByte with an 87,002-byte body returned 89,478,544 bytes (771×);
    /// memory grew 285 → 870 MiB and was not reclaimed; the action had no request size limit. Controls: an equally
    /// sized incompressible body returned a small error; a body compressing 0x00,0x01,0x02… returned those bytes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX22_ImageDecompressionTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;

        public VX22_ImageDecompressionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx22_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private static string GzipBase64(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(raw, 0, raw.Length);
            return Convert.ToBase64String(ms.ToArray());
        }

        private static async Task<string> Post(string route, string base64)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<Startup>());
            var content = new StringContent(JsonConvert.SerializeObject(base64), Encoding.UTF8, "application/json");
            var r = await server.CreateClient().PostAsync(route, content);
            return await r.Content.ReadAsStringAsync();
        }

        [Theory]
        [InlineData("/api/V2/GetImageUncompressedByte")]
        [InlineData("/api/V2/GetImageUncompressedBase")]
        public async Task VX22_AuditPoC_64MBTarget_Refused_ResponseStaysSmall(string route)
        {
            var body = GzipBase64(new byte[64 * 1024 * 1024]); // ~87 KB request, the audit's largest target
            var response = await Post(route, body);

            Assert.True(response.Length < 10_000, $"response was {response.Length} bytes");
            Assert.Contains("\"Success\":false", response);
            Assert.DoesNotContain(" at VerifiedXCore", response); // no exception detail
        }

        [Fact]
        public async Task VX22_AuditPoC_10MBTarget_RefusedByTheExpansionRatio()
        {
            var body = GzipBase64(new byte[10 * 1024 * 1024]); // under the absolute bound, ~770:1
            Assert.Contains("\"Success\":false", await Post("/api/V2/GetImageUncompressedByte", body));
        }

        [Fact]
        public async Task VX22_AuditControl_RealContentRoundTrips()
        {
            var raw = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray(); // 0x00,0x01,0x02…
            var response = await Post("/api/V2/GetImageUncompressedBase", GzipBase64(raw));
            Assert.Contains("\"Success\":true", response);
            Assert.Contains(Convert.ToBase64String(raw), response);
        }

        [Theory]
        [InlineData(nameof(V2Controller.GetUncompressedByte))]
        [InlineData(nameof(V2Controller.GetImageUncompressedBase))]
        public void VX22_Routes_CarryARequestSizeLimit(string action)
        {
            var limit = typeof(V2Controller).GetMethod(action)!.GetCustomAttribute<RequestSizeLimitAttribute>();
            Assert.NotNull(limit);
        }
    }
}
