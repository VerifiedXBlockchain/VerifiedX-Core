using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// FrostKeyRecover and FrostKeyBroadcastAll ask every peer and write this validator's key store: they answer only the
    /// node's own operator (loopback), and only one of them runs at a time.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostKeyOperatorRouteTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _me;

        public FrostKeyOperatorRouteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"frostops_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _me = AccountData.GetHumanAddress(pub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = _me, PublicKey = pub, PrivateKey = "0" + key.secret.ToString("x") });
            Globals.ValidatorAddress = _me;
            // A peer registered as a vBTC validator, so there is someone to ask.
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = 10, Hash = "h10",
                Transactions = new List<Transaction>
                {
                    new Transaction
                    {
                        TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = "xPeerValidator", ToAddress = "xPeerValidator", Hash = "r10",
                        Data = JsonConvert.SerializeObject(new { ValidatorAddress = "xPeerValidator", IPAddress = "10.0.0.1", FrostPublicKey = pub, IsS3C = false }),
                    },
                },
            });
            Globals.LastBlock = new Block { Height = 20 };
            Globals.IsChainSynced = true;
        }

        public void Dispose()
        {
            FrostKeyBackupService.FetchFromPeerForTests = null;
            Globals.ValidatorAddress = _priorValidator;
            Globals.IsChainSynced = _priorSynced;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static ValidatorController Controller(string remoteIp)
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = http } };
        }

        [Theory]
        [InlineData("203.0.113.7")]
        [InlineData("10.0.0.5")]
        public async Task ARemoteCaller_IsRefused(string remoteIp)
        {
            var calls = 0;
            FrostKeyBackupService.FetchFromPeerForTests = _ => { calls++; return Task.FromResult<List<FrostKeyBackupService.BackupRecoveryItem>?>(new()); };
            Assert.IsType<UnauthorizedObjectResult>((await Controller(remoteIp).FrostKeyRecover()).Result);
            Assert.IsType<UnauthorizedObjectResult>((await Controller(remoteIp).FrostKeyBroadcastAll()).Result);
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task TheNodesOwnOperator_RunsOneAtATime()
        {
            var release = new TaskCompletionSource<List<FrostKeyBackupService.BackupRecoveryItem>?>();
            var started = new TaskCompletionSource();
            FrostKeyBackupService.FetchFromPeerForTests = _ => { started.TrySetResult(); return release.Task; };

            var first = Controller("127.0.0.1").FrostKeyRecover();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // While it runs, a second recovery and a broadcast are turned away.
            Assert.IsType<ConflictObjectResult>((await Controller("127.0.0.1").FrostKeyRecover()).Result);
            Assert.IsType<ConflictObjectResult>((await Controller("::1").FrostKeyBroadcastAll()).Result);

            release.SetResult(new List<FrostKeyBackupService.BackupRecoveryItem>());
            Assert.IsType<OkObjectResult>((await first).Result);

            // Once it has finished, the next one runs.
            FrostKeyBackupService.FetchFromPeerForTests = _ => Task.FromResult<List<FrostKeyBackupService.BackupRecoveryItem>?>(new());
            Assert.IsType<OkObjectResult>((await Controller("127.0.0.1").FrostKeyRecover()).Result);
        }
    }
}
