using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.PathAuditRulesHeight a reserve send's 21 h UnlockTime floor is judged in a block against the block's own
    /// timestamp (less the block-time margin), not the validating node's clock: the same verdict on every node, at
    /// production and at late validation.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ReserveUnlockFloorBlockTimeTests : IDisposable
    {
        private const string FloorRefusal = "24 hour requirement";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly (PrivateKey Key, string Pub) _vault;
        private readonly string _reserve;

        public ReserveUnlockFloorBlockTimeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rsrv_floor_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            do
            {
                var key = new PrivateKey("secp256k1");
                _vault = (key, "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
                _reserve = ReserveAccount.GetHumanAddress(_vault.Pub);
            } while (!_reserve.StartsWith("xRBX", StringComparison.Ordinal));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _reserve, Balance = 1000M, Nonce = 0, RecoveryAccount = "xRecovery" });
            Globals.LastBlock = new Block { Height = Globals.PathAuditRulesHeight - 1 };
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction Send(long timestamp, long unlockTime)
        {
            var tx = new Transaction
            {
                FromAddress = _reserve, ToAddress = AccountData.GetHumanAddress(_vault.Pub), Amount = 1M, Fee = 0.00001M, Nonce = 0,
                Timestamp = timestamp, TransactionType = TransactionType.TX, UnlockTime = unlockTime,
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _vault.Key, _vault.Pub);
            return tx;
        }

        [Fact]
        public async Task InABlock_TheFloorIsTheBlocksTimestamp_WhateverThisNodesClock()
        {
            // A block stamped two days from now: the node's clock would put both sends far below the floor.
            var blockTime = TimeUtil.GetTime() + 2 * 86_400;
            var floor = LedgerIntegrityRules.NowForAgeRules(blockTime) + 21 * 3600;
            var height = Globals.PathAuditRulesHeight;

            var atFloor = await TransactionValidatorService.VerifyTX(Send(blockTime - 60, floor), false, true, false, null, false, height, blockTime);
            var belowFloor = await TransactionValidatorService.VerifyTX(Send(blockTime - 60, floor - 1), false, true, false, null, false, height, blockTime);

            Assert.DoesNotContain(FloorRefusal, atFloor.Item2);
            Assert.Contains(FloorRefusal, belowFloor.Item2);
        }
    }
}
