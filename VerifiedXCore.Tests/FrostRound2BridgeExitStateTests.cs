using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A bridge exit's signing ceremony is judged at sign/start while the exit is open. A validator gives its Round 2
    /// share only while it still is: an exit completed or failed after the ceremony started gets no share.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostRound2BridgeExitStateTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly string _vault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
        private readonly string _burn = "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public FrostRound2BridgeExitStateTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r2exit_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            Globals.LastBlock = new Block { Height = 500 };
            Globals.FrostFFIAvailable = true;   // a validator whose FROST library passed its check
            DbContext.Initialize();
            Assert.True(VBTCBridgeLockState.TryInsertFromLockTx(new Transaction { Hash = "lock-tx", FromAddress = "xLockOwner", Height = 440 }, _vault, "lock-1", 0.01M, 1_000_000, "0x" + new string('a', 40)));
            Assert.True(VBTCBridgeBtcExitState.TryInsert(new VBTCBridgeBtcExitState
            {
                BaseBurnTxHash = _burn, SmartContractUID = _vault, Amount = 0.01M, AmountSats = 1_000_000, BtcDestination = "tb1qexitdestination",
                ExitVfxHeight = 450, CreatedTimestamp = TimeUtil.GetTime(),
                AllocationsJson = JsonConvert.SerializeObject(new List<PoolUnlockAllocation> { new() { LockId = "lock-1", UnlockAmount = 0.01M, SmartContractUID = _vault } }),
            }));
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.FrostFFIAvailable = _priorLibrary;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private string Reference => BtcWithdrawals.ExitSigningReference(_burn, _vault);

        /// <summary>Round 2 of a ceremony this validator accepted for the exit, sent by its leader.</summary>
        private async Task<(System.Net.HttpStatusCode Code, string Body)> Round2()
        {
            var key = new VerifiedXCore.EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var leader = AccountData.GetHumanAddress(pub);
            var session = new SigningSession
            {
                SessionId = Guid.NewGuid().ToString("N"), LeaderAddress = leader, LeaderStartTimestamp = TimeUtil.GetTime(), StartTimestamp = TimeUtil.GetTime(),
                SmartContractUID = _vault, WithdrawalRequestHash = Reference, MessageHash = new string('a', 64), MyKeyPackage = "key-package",
            };
            Assert.True(FrostSessionStorage.SigningSessions.TryAdd(session.SessionId, session));
            try
            {
                using var server = new Microsoft.AspNetCore.TestHost.TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"/frost/sign/round2/{session.SessionId}")
                { Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("X-Frost-Leader", leader);
                req.Headers.TryAddWithoutValidation("X-Frost-Timestamp", session.LeaderStartTimestamp.ToString());
                req.Headers.TryAddWithoutValidation("X-Frost-Signature", VerifiedXCore.Services.SignatureService.CreateSignature($"{session.SessionId}.{leader}.{session.LeaderStartTimestamp}", key, pub));
                var response = await server.CreateClient().SendAsync(req);
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Leader auth failed", body);
                return (response.StatusCode, body);
            }
            finally { FrostSessionStorage.SigningSessions.TryRemove(session.SessionId, out _); }
        }

        [Fact]
        public async Task AnOpenExit_IsNotRefusedByItsState()
        {
            Assert.Null(FrostSigningAuthorization.BridgeExitNoLongerSignable(Reference, _vault));
            var (_, body) = await Round2();
            Assert.DoesNotContain("Bridge exit", body);
        }

        [Fact]
        public async Task AnExitCompletedAfterTheCeremonyStarted_GetsNoShare()
        {
            VBTCBridgeBtcExitState.MarkComplete(_burn, new string('b', 64));
            var (code, body) = await Round2();
            Assert.Equal(System.Net.HttpStatusCode.Conflict, code);
            Assert.Contains("Bridge exit already completed", body);
        }

        [Fact]
        public async Task AnExitFailedAfterTheCeremonyStarted_GetsNoShare()
        {
            var exit = VBTCBridgeBtcExitState.GetByBurnHash(_burn)!;
            exit.IsComplete = true;
            exit.BtcTxHash = $"{VBTCBridgeBtcExitState.FailedMarkerPrefix}expired@501";
            Assert.True(VBTCBridgeBtcExitState.Update(exit));
            var (code, body) = await Round2();
            Assert.Equal(System.Net.HttpStatusCode.Conflict, code);
            Assert.Contains("Bridge exit has failed", body);
        }

        [Fact]
        public void AReferenceToAnotherContract_IsNoLongerSignable()
        {
            Assert.NotNull(FrostSigningAuthorization.BridgeExitNoLongerSignable(Reference, Guid.NewGuid().ToString("N") + ":1"));
        }
    }
}
