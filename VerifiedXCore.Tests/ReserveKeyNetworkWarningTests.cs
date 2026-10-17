using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Restoring a reserve key says it must never be used on both networks: the address and the signed transaction are
    /// the same on mainnet and testnet, so a reserve transaction signed on one can be replayed on the other.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ReserveKeyNetworkWarningTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ReserveKeyNetworkWarningTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rsv_warn_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task RestoringAReserveAccount_CarriesTheCrossNetworkWarning()
        {
            var created = ReserveAccount.CreateNewReserveAccount("reserve pw 1234", skipSave: true);
            var payload = JsonConvert.SerializeObject(new { RestoreCode = created.RestoreCode, Password = "reserve pw 1234", StoreRecoveryAccount = false, RescanForTx = false });

            var response = JObject.Parse(await new RSV1Controller().RestoreReserveAddress(JObject.Parse(payload)));

            Assert.True((bool?)response["Success"]);
            Assert.Equal(created.Address, (string?)response["ReserveAccount"]?["Address"]);
            Assert.Equal(ReserveAccount.CrossNetworkKeyWarning, (string?)response["Warning"]);
            Assert.Contains("mainnet and testnet", ReserveAccount.CrossNetworkKeyWarning);
        }
    }
}
