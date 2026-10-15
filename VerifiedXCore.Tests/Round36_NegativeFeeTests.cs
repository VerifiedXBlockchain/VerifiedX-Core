using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
    /// A transaction's fee is never negative: the fee-exempt types skipped the "more than zero" rule and apply takes
    /// Amount + Fee from the sender, so a negative fee credited it (fifth review, round 36).
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round36_NegativeFeeTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorAudit;
        private readonly (PrivateKey Key, string Pub, string Address) _sender;

        public Round36_NegativeFeeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r36fee_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorAudit = Globals.PathAuditRulesHeight;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };
            Globals.PathAuditRulesHeight = 50;
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _sender = (key, pub, AccountData.GetHumanAddress(pub));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 10M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorAudit;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction Signed(TransactionType type, decimal fee)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _sender.Address, ToAddress = _sender.Address, Amount = 0M, Fee = fee, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(new { LockId = "lock", ContractUID = "c:1" }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_UNLOCK)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK)]
        [InlineData(TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE)]
        [InlineData(TransactionType.TX)]
        public async Task ANegativeFee_IsRefused(TransactionType type)
        {
            var tx = Signed(type, -1000.00000000M);
            var (admitted, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admitted);
            Assert.Equal(TransactionValidatorService.NegativeFeeReason, why);
            var (inBlock, whyBlock) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: 60);
            Assert.False(inBlock);
            Assert.Equal(TransactionValidatorService.NegativeFeeReason, whyBlock);

            // Below the path-audit height a block is judged as before (other rules may still refuse it).
            var (_, whyOld) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: 40);
            Assert.NotEqual(TransactionValidatorService.NegativeFeeReason, whyOld);
        }
    }
}
