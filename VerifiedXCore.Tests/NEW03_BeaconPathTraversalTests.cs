using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore;
using VerifiedXCore.Beacon;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-03 (HIGH; found by the independent review; same class as VX-04): beacon nodes built file paths by
    /// concatenating a network-supplied asset name and contract UID onto the beacon folder. Any NFT owner can register
    /// asset names (the owner signs only the UID), so an asset named "../../../x" was written outside the folder on
    /// upload (a new file anywhere: code execution via a startup/profile script) and read from anywhere on download.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW03_BeaconPathTraversalTests : IDisposable
    {
        private const string ScUid = "8b8b8b8b8b8b8b8b8b8b8b8b8b8b8b8b:1790500000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly string _priorSaveArea = BeaconStartup.SaveArea;
        private readonly string _beaconRoot;

        public NEW03_BeaconPathTraversalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new03_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            _beaconRoot = Path.Combine(_tempRoot, "beacon", "assets") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_beaconRoot);
            BeaconStartup.SaveArea = _beaconRoot;
        }

        public void Dispose()
        {
            BeaconStartup.SaveArea = _priorSaveArea;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void Register(string assetName) =>
            BeaconData.GetBeacon()!.Insert(new BeaconData { SmartContractUID = ScUid, AssetName = assetName, IPAdress = null!, DownloadIPAddress = null! });

        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<BeaconStartup>());

        private static MultipartFormDataContent Upload(string fileName)
        {
            var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(new byte[] { 0x23, 0x21 });
            content.Add(file, "file", fileName);
            // HttpClient quotes/escapes the name; set the raw header value so the server sees the attacker's name exactly
            file.Headers.ContentDisposition!.FileName = fileName;
            file.Headers.ContentDisposition.FileNameStar = null;
            return content;
        }

        [Fact]
        public async Task NEW03_PoC_UploadWithTraversalName_WritesNothingOutsideTheBeaconFolder()
        {
            const string evil = "../../../escaped-by-upload.txt";
            Register(evil);
            using var server = NewServer();

            await server.CreateClient().PostAsync($"/upload/{ScUid}", Upload(evil));

            var escaped = Path.GetFullPath(Path.Combine(_beaconRoot, ScUid.Replace(":", ""), evil));
            Assert.False(File.Exists(escaped), $"file written outside the beacon folder: {escaped}");
        }

        [Fact]
        public async Task NEW03_PoC_DownloadWithTraversalName_ReadsNothingOutsideTheBeaconFolder()
        {
            var secret = Path.Combine(_tempRoot, "wallet-secret.db");
            File.WriteAllText(secret, "SECRET-WALLET-BYTES");
            var name = "..\\..\\..\\wallet-secret.db";
            BeaconData.GetBeacon()!.Insert(new BeaconData { SmartContractUID = ScUid, AssetName = name, DownloadIPAddress = null! });
            using var server = NewServer();

            var r = await server.CreateClient().GetAsync($"/download/{ScUid}/{Uri.EscapeDataString(name)}");
            var body = await r.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SECRET-WALLET-BYTES", body);
        }

        [Fact]
        public void NEW03_Resolver()
        {
            Assert.True(BeaconPaths.TryResolve(_beaconRoot, "ab:12", "image.png", out var ok));
            Assert.Equal(Path.GetFullPath(Path.Combine(_beaconRoot, "ab12", "image.png")), ok);
            Assert.True(BeaconPaths.TryResolve(_beaconRoot, null, "image.png", out _)); // legacy flat layout

            foreach (var bad in new[] { "../x.sh", "..\\x.sh", "a/b.png", "a\\b.png", "C:x.png", "..", "", "x\0.png" })
                Assert.False(BeaconPaths.TryResolve(_beaconRoot, "ab:12", bad, out _), bad);
            foreach (var badUid in new[] { "../ab", "..\\ab", "ab/cd", "a b" })
                Assert.False(BeaconPaths.TryResolve(_beaconRoot, badUid, "image.png", out _), badUid);
        }

        [Fact]
        public void NEW03_ExtensionRejectList_IsCaseInsensitive()
        {
            Globals.RejectAssetExtensionTypes.Add(".exe");
            Assert.False(BeaconPaths.ExtensionAllowed("payload.EXE"));
            Assert.False(BeaconPaths.ExtensionAllowed("payload.exe"));
            Assert.True(BeaconPaths.ExtensionAllowed("image.png"));
        }

        [Theory]
        [InlineData("a.exe ")]
        [InlineData("a.exe.")]
        [InlineData("a.exe . ")]
        public void NEW03_FollowUp_TrailingSpaceOrDot_Refused(string name)
        {
            // Second review: on Windows "a.exe " passed the extension list (".exe ") and was written as "a.exe".
            Assert.False(VerifiedXCore.Beacon.BeaconPaths.ExtensionAllowed(name));
            if (OperatingSystem.IsWindows())
                Assert.False(VerifiedXCore.Beacon.BeaconPaths.TryResolve(System.IO.Path.GetTempPath(), "abc:1", name, out _));
        }

        [Fact]
        public void NEW03_FollowUp_Control_PlainNameResolves()
        {
            Assert.True(VerifiedXCore.Beacon.BeaconPaths.ExtensionAllowed("art.png"));
            Assert.True(VerifiedXCore.Beacon.BeaconPaths.TryResolve(System.IO.Path.GetTempPath(), "abc:1", "art.png", out _));
        }
    }
}
