using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Self-update installs a release zip only when it carries a signature, from a signer this build names, over the SHA-256
    /// of the bytes downloaded, and it verifies before it departs the caster pool. With no signer named it refuses.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ReleaseSignatureTests : IDisposable
    {
        private readonly string[] _priorSigners;
        private readonly Dictionary<string, string> _priorAssets;
        private readonly PrivateKey _key = new PrivateKey("secp256k1");
        private readonly string _pub;
        private readonly string _signer;

        public ReleaseSignatureTests()
        {
            _priorSigners = Globals.ReleaseSignerAddresses;
            _priorAssets = new Dictionary<string, string>(Globals.GitHubLatestReleaseAssetsDict);
            _pub = "04" + Convert.ToHexString(_key.publicKey().toString()).ToLowerInvariant();
            _signer = AccountData.GetHumanAddress(_pub);
        }

        public void Dispose()
        {
            Globals.ReleaseSignerAddresses = _priorSigners;
            Globals.GitHubLatestReleaseAssetsDict.Clear();
            foreach (var kv in _priorAssets) Globals.GitHubLatestReleaseAssetsDict[kv.Key] = kv.Value;
        }

        private static readonly byte[] Asset = Encoding.UTF8.GetBytes("release bytes");

        private string Sign(byte[] asset, PrivateKey key, string pub) =>
            SignatureService.CreateSignature(ReleaseSignature.Sha256Hex(asset), key, pub);

        [Fact]
        public void WithNoSignerNamed_EverythingIsRefused_EvenASignedAsset()
        {
            Globals.ReleaseSignerAddresses = Array.Empty<string>();
            Assert.Equal(ReleaseSignature.NoSignerReason, ReleaseSignature.Refusal(Asset, Sign(Asset, _key, _pub)));
        }

        [Fact]
        public void AnAssetSignedByANamedSigner_IsAccepted()
        {
            Globals.ReleaseSignerAddresses = new[] { _signer };
            Assert.Null(ReleaseSignature.Refusal(Asset, Sign(Asset, _key, _pub)));
            Assert.Null(ReleaseSignature.Refusal(Asset, Sign(Asset, _key, _pub) + "\n")); // a .sig file's trailing newline
        }

        [Fact]
        public void AnAssetWithoutASignature_IsRefused()
        {
            Globals.ReleaseSignerAddresses = new[] { _signer };
            Assert.Equal(ReleaseSignature.NoSignatureReason, ReleaseSignature.Refusal(Asset, null));
            Assert.Equal(ReleaseSignature.NoSignatureReason, ReleaseSignature.Refusal(Asset, "  "));
        }

        [Fact]
        public void ASignatureOverOtherBytes_OrFromAnotherKey_IsRefused()
        {
            Globals.ReleaseSignerAddresses = new[] { _signer };
            var other = Encoding.UTF8.GetBytes("other bytes");
            Assert.Equal(ReleaseSignature.BadSignatureReason, ReleaseSignature.Refusal(other, Sign(Asset, _key, _pub)));

            var stranger = new PrivateKey("secp256k1");
            var strangerPub = "04" + Convert.ToHexString(stranger.publicKey().toString()).ToLowerInvariant();
            Assert.Equal(ReleaseSignature.BadSignatureReason, ReleaseSignature.Refusal(Asset, Sign(Asset, stranger, strangerPub)));
            Assert.Equal(ReleaseSignature.BadSignatureReason, ReleaseSignature.Refusal(Asset, "garbage"));
        }

        [Fact]
        public async Task WithNoSignerNamed_SafeUpdate_DoesNotDepart()
        {
            Globals.ReleaseSignerAddresses = Array.Empty<string>();
            Globals.GitHubLatestReleaseAssetsDict["vfx-linux-x64.zip"] = "http://127.0.0.1:9/unreachable.zip";
            var departed = false;

            var zip = await DepartureService.VerifyThenDepartAsync("vfx-linux-x64.zip", () => { departed = true; return Task.CompletedTask; });

            Assert.Null(zip);
            Assert.False(departed);
        }

        [Fact]
        public async Task ADownloadThatFails_DoesNotDepart()
        {
            Globals.ReleaseSignerAddresses = new[] { _signer };
            Globals.GitHubLatestReleaseAssetsDict["vfx-linux-x64.zip"] = "http://127.0.0.1:9/unreachable.zip";
            var departed = false;

            var zip = await DepartureService.VerifyThenDepartAsync("vfx-linux-x64.zip", () => { departed = true; return Task.CompletedTask; });

            Assert.Null(zip);
            Assert.False(departed);
        }

        [Fact]
        public async Task OnlyAReleaseZipOfTheLatestRelease_IsDownloaded()
        {
            Globals.ReleaseSignerAddresses = new[] { _signer };
            Globals.GitHubLatestReleaseAssetsDict.Clear();
            Globals.GitHubLatestReleaseAssetsDict["vfx-linux-x64.tar.gz"] = "http://127.0.0.1:9/a.tar.gz";

            var (notZip, why) = await VersionControlService.DownloadVerifiedAsync("vfx-linux-x64.tar.gz");
            Assert.Null(notZip);
            Assert.Contains(".zip", why);

            var (missing, missingWhy) = await VersionControlService.DownloadVerifiedAsync("vfx-win-x64.zip");
            Assert.Null(missing);
            Assert.Contains("not an asset", missingWhy);
        }

        [Fact]
        public void AssetSelection_IgnoresSignatureFilesAndOtherArchives()
        {
            var assets = new List<string> { "vfx-linux-x64.zip", "vfx-linux-x64.zip.sig", "vfx-linux-x64.tar.gz", "vfx-win-x64.zip" };
            Assert.Equal("vfx-linux-x64.zip", DepartureService.SelectAssetByKeywords(assets, new[] { "linux" }));
            Assert.Equal(new List<string> { "vfx-linux-x64.zip", "vfx-win-x64.zip" }, VersionControlService.ReleaseZips(assets));
        }
    }
}
