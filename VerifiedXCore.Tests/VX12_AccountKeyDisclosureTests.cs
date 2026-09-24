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
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-12 (HIGH): "Account private keys are returned by an unauthenticated endpoint".
    ///
    /// Audit PoC: GET /api/V1/GetAllAddresses with no credential returned PrivateKey, GetKey and GetPrivKey
    /// (the secret scalar) for every account, including the validating one; the route was also excluded
    /// from the API log. Verification: the disclosed key reproduced the published public key.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX12_AccountKeyDisclosureTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly Account _account;

        public VX12_AccountKeyDisclosureTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx12_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();

            _account = AccountData.CreateNewAccount(skipSave: true);
            _account.IsValidating = true;
            _account.Balance = VerifiedXCore.Services.ValidatorService.ValidatorRequiredAmount() + 1;
            AccountData.GetAccounts().Insert(_account);
            StateData.GetAccountStateTrei().Insert(new AccountStateTrei { Key = _account.Address, Balance = _account.Balance, Nonce = 0 });
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

        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<Startup>());

        private string Canonical => KeyParsing.CanonicalKeyHexFromStored(_account.PrivateKey);

        private void AssertNoKey(string body)
        {
            Assert.DoesNotContain(_account.PrivateKey.TrimStart('0'), body);
            Assert.DoesNotContain("\"PrivateKey\"", body);
            Assert.DoesNotContain("\"GetKey\"", body);
            Assert.DoesNotContain("\"GetPrivKey\"", body);
        }

        [Theory]
        [InlineData("/api/V1/GetAllAddresses")]
        [InlineData("/api/V1/GetValidatorAddresses")]
        public async Task VX12_AuditPoC_AccountListings_CarryNoKeyMaterial(string route)
        {
            using var server = NewServer();
            var body = await server.CreateClient().GetStringAsync(route);
            Assert.Contains(_account.Address, body); // control: the account is listed
            AssertNoKey(body);
        }

        [Fact]
        public async Task VX12_AddressInfo_CarriesNoKeyMaterial()
        {
            using var server = NewServer();
            var body = await server.CreateClient().GetStringAsync($"/api/V1/GetAddressInfo/{_account.Address}");
            Assert.Contains(_account.Address, body);
            AssertNoKey(body);
        }

        [Fact]
        public async Task VX12_ExplicitExport_ReturnsTheCanonicalKey_WhenUnlocked()
        {
            using var server = NewServer();
            var body = await server.CreateClient().GetStringAsync($"/api/V1/GetPrivateKey/{_account.Address}");
            Assert.Contains(Canonical, body);
        }

        [Fact]
        public async Task VX12_ExplicitExport_IsRefused_WhileTheWalletIsLocked()
        {
            Globals.IsWalletEncrypted = true;
            Globals.EncryptPassword = new SecureString();
            using var server = NewServer();
            var r = await server.CreateClient().GetAsync($"/api/V1/GetPrivateKey/{_account.Address}");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
    }
}
