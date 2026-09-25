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

        [Fact]
        public async Task NEW03_FollowUp_UploadIsBoundToTheRegisteredContract()
        {
            // Third review: upload authorization matched IP and asset name only, not the route's contract, so an asset
            // registered under one's own contract was written into another contract's folder.
            const string victim = "abcdefabcdefabcdefabcdefabcdefab:1790500030";
            Register("photo.png"); // registered under ScUid (the uploader's own contract)
            using var server = NewServer();

            await server.CreateClient().PostAsync($"/upload/{victim}", Upload("photo.png"));
            Assert.False(File.Exists(Path.Combine(_beaconRoot, victim.Replace(":", ""), "photo.png")), "asset planted in another contract's folder");

            var own = await server.CreateClient().PostAsync($"/upload/{ScUid}", Upload("photo.png")); // control: own contract
            Assert.True(File.Exists(Path.Combine(_beaconRoot, ScUid.Replace(":", ""), "photo.png")), $"{(int)own.StatusCode} {await own.Content.ReadAsStringAsync()}");
        }

        [Fact]
        public void NEW03_FollowUp_LegacyTcpBeaconIsNotStarted()
        {
            // Fourth review: the legacy TCP beacon allocated a caller-chosen buffer (up to ~2 GB) per connection before
            // authorization; no client uses it. StartBeacon no longer starts it.
            var repo = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var src = File.ReadAllText(Path.Combine(repo, "VerifiedXCore", "Services", "StartupService.cs"));
            Assert.DoesNotContain("new BeaconServer(", src);
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

        [Fact]
        public async Task NEW03_FollowUp_UnregisteredUpload_RefusedBeforeTheBodyIsUsed()
        {
            // Fourth review: the handler read the whole form and created the contract folder before any authorization.
            const string other = "efefefefefefefefefefefefefefefef:1790500060";
            using var server = NewServer();
            var r = await server.CreateClient().PostAsync($"/upload/{other}", Upload("photo.png"));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, r.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(_beaconRoot, other.Replace(":", ""))));
        }
    }
}
