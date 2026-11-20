using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A transaction over its size cap is refused for its size before any rule reads its data, at admission and in a
    /// block, so the rules that parse the data and look up what it names never work on more than the cap allows.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TxSizeCheckedFirstTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;

        public TxSizeCheckedFirstTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"tx_size_first_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 5000 };
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>A bridge transaction naming <paramref name="allocations"/> locks, from an address with no account.</summary>
        private static Transaction BridgeTx(TransactionType type, int allocations)
        {
            var list = Enumerable.Range(0, allocations)
                .Select(i => new { LockId = $"lock-{i:D8}-{new string('a', 64)}", SmartContractUID = $"{i:x32}:1790600000", UnlockAmount = 0.00000001M })
                .ToList();
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = "xNobodyHoldsThisAddress0000000000000", ToAddress = "xNobodyHoldsThisAddress0000000000000",
                Amount = 0M, Fee = 0M, Nonce = 0, TransactionType = type,
                Data = JsonConvert.SerializeObject(new { Function = "Bridge()", LockId = "lock-x", ContractUID = "c:1", BaseBurnTxHash = "0xabc", Allocations = list }),
            };
            tx.Build();
            tx.Signature = "unsigned";
            return tx;
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_UNLOCK)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL)]
        public async Task OversizedBridgeTransaction_IsRefusedForItsSize_AtAdmissionAndInABlock(TransactionType type)
        {
            var tx = BridgeTx(type, 6000); // about 1 MB, the most the peer transport carries
            Assert.True(tx.Data!.Length > 900_000);

            var (admitted, admissionReason) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admitted);
            Assert.Contains("too large", admissionReason);

            var (inBlock, blockReason) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, 5001);
            Assert.False(inBlock);
            Assert.Contains("too large", blockReason);
        }

        [Fact]
        public async Task BridgeTransactionWithinTheCap_IsJudgedByTheRulesThatFollow()
        {
            var tx = BridgeTx(TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, 20);
            Assert.True(TransactionValidatorService.VerifyTXSize(TransactionValidatorService.AsCarriedInABlock(tx), LedgerIntegrityRules.DefaultMaxTxSizeBytes));

            var (ok, reason) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok); // nobody holds the sender
            Assert.DoesNotContain("too large", reason);
        }
    }
}
