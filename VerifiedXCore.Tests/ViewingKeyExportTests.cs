using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The exported viewing key can spend, so ExportViewingKey releases it on the bar of a spend (the wallet password
    /// unwraps the spending key) and says it can spend. A row without a spending key does not export.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ViewingKeyExportTests : IDisposable
    {
        private const string SeedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
        private const string Password = "correct horse battery";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly ShieldedKeyMaterial _owner;

        public ViewingKeyExportTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vk_export_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            _owner = ShieldedHdDerivation.DeriveShieldedKeyMaterial(SeedHex, ShieldedAddressConstants.DefaultBip44CoinType, 31);
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private async Task<JObject> Export(string zfx, string? password) =>
            JObject.Parse(await new PrivacyV1Controller().ExportViewingKey(new ExportViewingKeyRequest { ZfxAddress = zfx, WalletPassword = password }));

        [Fact]
        public async Task WithTheWalletPassword_TheKeyIsExported_AndSaysItCanSpend()
        {
            ShieldedWalletService.Upsert(ShieldedWalletService.CreateFromKeyMaterial(_owner, null, Password));

            var r = await Export(_owner.ZfxAddress, Password);

            Assert.True((bool)r["Success"]!, r.ToString());
            Assert.Equal(Convert.ToBase64String(_owner.ViewingKey32), (string)r["Result"]!["ViewingKeyBase64"]!);
            Assert.True((bool)r["Result"]!["CanSpend"]!);
            Assert.Equal(PrivacyApiHelper.ViewingKeySpendWarning, (string)r["Result"]!["Warning"]!);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("wrong password!")]
        public async Task WithoutTheWalletPassword_NothingIsExported(string? password)
        {
            ShieldedWalletService.Upsert(ShieldedWalletService.CreateFromKeyMaterial(_owner, null, Password));

            var r = await Export(_owner.ZfxAddress, password);

            Assert.False((bool)r["Success"]!);
            Assert.Null(r["Result"]);
            Assert.Contains("password", (string)r["Message"]!);
            Assert.DoesNotContain(Convert.ToBase64String(_owner.ViewingKey32), r.ToString());
        }

        [Fact]
        public async Task AViewOnlyRow_DoesNotExport()
        {
            ShieldedWalletService.Upsert(ShieldedWalletService.CreateFromKeyMaterial(_owner, null, null));

            var r = await Export(_owner.ZfxAddress, Password);

            Assert.False((bool)r["Success"]!);
            Assert.DoesNotContain(Convert.ToBase64String(_owner.ViewingKey32), r.ToString());
        }
    }
}
