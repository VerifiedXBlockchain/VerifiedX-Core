using System;
using System.IO;
using System.Net;
using System.Security;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-18 (MEDIUM): "The fee replacement route is omitted from the encryption gate".
    ///
    /// Audit PoC: wallet encrypted and locked; SendTransaction → 401, but ReplaceByFee/deadbeef…/5000 → 200
    /// "Transaction for TXID … was null" — execution halted on the missing transaction, not on any security check.
    /// Controls: a bogus route 404; feeRate 0 → controller-level "Incorrect URL parameters". The fund-loss step (an
    /// unbounded fee rate multiplied into the fee) was confirmed in code but not executed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX18_ReplaceByFeeTests : IDisposable
    {
        private const string Txid = "deadbeef00000000000000000000000000000000000000000000000000000000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly NBitcoin.Network _priorNetwork = Globals.BTCNetwork;
        private readonly NBitcoin.ScriptPubKeyType _priorScriptType = Globals.ScriptPubKeyType;

        public VX18_ReplaceByFeeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx18_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            TestWalletKeystore.Ensure("correct horse 18"); // VX-13 follow-up: sealing verifies the wallet password
            Startup.APIEnabled = true;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.BTCNetwork = NBitcoin.Network.TestNet;
            Globals.ScriptPubKeyType = NBitcoin.ScriptPubKeyType.Segwit;
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.BTCNetwork = _priorNetwork;
            Globals.ScriptPubKeyType = _priorScriptType;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static SecureString Pw(string s) { var ss = new SecureString(); foreach (var c in s) ss.AppendChar(c); return ss; }
        private static void Lock() { Globals.IsWalletEncrypted = true; Globals.EncryptPassword = new SecureString(); }
        private static void Unlock() { Globals.IsWalletEncrypted = true; Globals.EncryptPassword = Pw("correct horse 18"); }
        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<Startup>());

        // ── Audit PoC ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX18_AuditPoC_LockedWallet_ReplaceByFeeRoute_Refused()
        {
            Lock();
            using var server = NewServer();
            var client = server.CreateClient();

            var send = await client.GetAsync("/btcapi/BTCV2/SendTransaction/a/b/1/5");
            Assert.Equal(HttpStatusCode.Unauthorized, send.StatusCode); // control: the gate is active

            var rbf = await client.GetAsync($"/btcapi/BTCV2/ReplaceByFee/{Txid}/5000");
            Assert.Equal(HttpStatusCode.Unauthorized, rbf.StatusCode);
        }

        [Fact]
        public async Task VX18_AuditControls_BogusRoute404_ZeroFeeRateRejectedAtController()
        {
            using var server = NewServer();
            var client = server.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/btcapi/BTCV2/ReplaceByFeeBogus/{Txid}/5000")).StatusCode);
            Assert.Contains("Incorrect URL parameters", await client.GetStringAsync($"/btcapi/BTCV2/ReplaceByFee/{Txid}/0"));
        }

        [Fact]
        public async Task VX18_AuditPoC_ServiceItself_RefusesWhileLocked_BeforeAnyLookup()
        {
            // Defence in depth: the method no longer relies on the route gate alone.
            Lock();
            var result = await TransactionService.ReplaceByFeeTransaction(Txid, 5000);
            Assert.DoesNotContain("was null", result);
            Assert.Contains("encryption password", result);
        }

        // ── Fee bound (the fund-loss step) ─────────────────────────────────────────────────

        [Fact]
        public async Task VX18_AuditPoC_UnboundedFeeRate_Refused()
        {
            var result = await TransactionService.ReplaceByFeeTransaction(Txid, 1_000_000);
            Assert.DoesNotContain("was null", result); // refused on the fee rate, before the TX lookup
            Assert.Contains("exceeds the maximum", result);
        }

        [Fact]
        public async Task VX18_SendPath_UnboundedFeeRate_Refused()
        {
            var (ok, message) = await TransactionService.SendTransaction("tb1qanything", "tb1qelse", 0.001M, 1_000_000);
            Assert.False(ok);
            Assert.Contains("exceeds the maximum", message);
        }

        [Fact]
        public void VX18_FeePolicy_Bounds()
        {
            Assert.False(BitcoinFeePolicy.TryValidateFeeRate(0, out _));
            Assert.False(BitcoinFeePolicy.TryValidateFeeRate(-5, out _));
            Assert.True(BitcoinFeePolicy.TryValidateFeeRate(1, out _));
            Assert.True(BitcoinFeePolicy.TryValidateFeeRate(Globals.MaxBtcFeeRateSatPerVb, out _));
            Assert.False(BitcoinFeePolicy.TryValidateFeeRate(Globals.MaxBtcFeeRateSatPerVb + 1, out _));

            Assert.True(BitcoinFeePolicy.TryValidateTotalFee(1_000, 100_000, allowHighFee: false, out _));    // 1%
            Assert.False(BitcoinFeePolicy.TryValidateTotalFee(20_000, 100_000, allowHighFee: false, out _));  // 20%
            Assert.True(BitcoinFeePolicy.TryValidateTotalFee(20_000, 100_000, allowHighFee: true, out _));    // explicit
        }

        // ── Locked means no Bitcoin signing, sealed or not ─────────────────────────────────

        [Fact]
        public void VX18_UnsealedKeyInALockedEncryptedWallet_IsNotReturned()
        {
            var a = BitcoinAccount.CreateAddress(save: true); // plaintext (wallet not encrypted yet)
            var stored = BitcoinAccount.GetBitcoin()!.FindOne(x => x.Address == a.Address);
            Assert.False(stored.IsEncrypted);

            Lock(); // encrypted, locked, key never sealed (the node has not been unlocked since the upgrade)
            Assert.Null(BitcoinKeystore.GetPrivateKeyHex(stored));
            Assert.Null(BitcoinKeystore.GetWif(stored));

            Unlock(); // control: available again once unlocked (and sealed on this first use)
            Assert.Equal(a.PrivateKey, BitcoinKeystore.GetPrivateKeyHex(stored));
        }

        [Fact]
        public async Task VX18_FeeEstimate_NeedsNoKey_WorksWhileLocked()
        {
            Unlock();
            var a = BitcoinAccount.CreateAddress(save: false);
            a.Balance = 1M;
            Assert.True(BitcoinAccount.SaveBitcoinAddress(a)); // sealed at rest
            Lock();

            var (_, message) = await TransactionService.CalcuateFee(a.Address, BitcoinAccount.CreateAddress(save: false).Address, 0.001M, 10);
            Assert.DoesNotContain("encryption password", message); // it fails later for lack of UTXOs, not for the key
        }

        [Fact]
        public async Task VX23_FollowUp_ServiceErrorsReachTheCallerWithoutAStackTrace()
        {
            // The review's trigger: CalculateFee (allowed while locked) with an invalid receiver returned the full exception.
            var a = BitcoinAccount.CreateAddress(save: false);
            a.Balance = 1M;
            Assert.True(BitcoinAccount.SaveBitcoinAddress(a));

            var (_, message) = await TransactionService.CalcuateFee(a.Address, "notanaddress", 0.001M, 10);
            Assert.DoesNotContain(" at VerifiedXCore", message);
            Assert.DoesNotContain(".cs:line", message);
        }
    }
}
