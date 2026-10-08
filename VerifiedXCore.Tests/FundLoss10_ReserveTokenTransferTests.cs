using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 10 (Globals.ReserveTokenRulesHeight, owner decision: refuse rather than defer): a reserve
    /// account cannot send or burn fungible tokens. Token votes and every non-reserve sender are untouched.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss10_ReserveTokenTransferTests : IDisposable
    {
        private const long Gate = 1000;
        private const string Refused = "A reserve account cannot send or burn fungible tokens";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly string _reserve;
        private readonly string _normal;

        public FundLoss10_ReserveTokenTransferTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl10_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.ReserveTokenRulesHeight;
            Globals.ReserveTokenRulesHeight = Gate;
            DbContext.Initialize();
            // Not every key yields the xRBX prefix (the audit's own tests hit this); keep drawing until one does.
            do
            {
                var k1 = new PrivateKey("secp256k1");
                _reserve = ReserveAccount.GetHumanAddress("04" + Convert.ToHexString(k1.publicKey().toString()).ToLowerInvariant());
            } while (!_reserve.StartsWith("xRBX", StringComparison.Ordinal));
            var k2 = new PrivateKey("secp256k1");
            _normal = AccountData.GetHumanAddress("04" + Convert.ToHexString(k2.publicKey().toString()).ToLowerInvariant());
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.ReserveTokenRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Token(string from, string function) => new Transaction
        {
            FromAddress = from, ToAddress = "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH", Amount = 0M, Fee = 0.00000100M, Nonce = 0,
            Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.FTKN_TX, Hash = Guid.NewGuid().ToString("N"),
            Data = JsonConvert.SerializeObject(new { Function = function, ContractUID = "token:1", FromAddress = from, ToAddress = "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH", Amount = 10M }),
        };

        [Theory]
        [InlineData("TokenTransfer()")]
        [InlineData("TokenBurn()")]
        public async Task ReserveTokenMove_RefusedAtGate_NotBelow(string function)
        {
            Globals.LastBlock = new Block { Height = Gate - 2 };
            var (_, msgBelow) = await TransactionValidatorService.VerifyTX(Token(_reserve, function));
            Assert.DoesNotContain(Refused, msgBelow);

            Globals.LastBlock = new Block { Height = Gate - 1 };
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Token(_reserve, function));
            Assert.False(ok);
            Assert.StartsWith(Refused, msg);
        }

        [Fact]
        public async Task TokenVoteFromReserve_AndNormalSenders_Untouched()
        {
            Globals.LastBlock = new Block { Height = Gate + 1 };
            var (_, vote) = await TransactionValidatorService.VerifyTX(Token(_reserve, "TokenVoteTopicCast()"));
            Assert.DoesNotContain(Refused, vote);
            var (_, normal) = await TransactionValidatorService.VerifyTX(Token(_normal, "TokenTransfer()"));
            Assert.DoesNotContain(Refused, normal);
        }

        [Fact]
        public async Task BlockPath_JudgesAtTheBlocksHeight()
        {
            Globals.LastBlock = new Block { Height = Gate + 5000 };
            var (_, historical) = await TransactionValidatorService.VerifyTX(Token(_reserve, "TokenTransfer()"), blockDownloads: true, blockVerify: true, blockHeight: Gate - 1);
            Assert.DoesNotContain(Refused, historical);

            Globals.LastBlock = new Block { Height = 10 };
            var (ok, atGate) = await TransactionValidatorService.VerifyTX(Token(_reserve, "TokenTransfer()"), blockVerify: true, blockHeight: Gate);
            Assert.False(ok);
            Assert.StartsWith(Refused, atGate);
        }

        [Fact]
        public void Rule_IsPure()
        {
            Assert.NotNull(LedgerIntegrityRules.ReserveTokenTransfer(Token(_reserve, "TokenTransfer()"), Gate));
            Assert.Null(LedgerIntegrityRules.ReserveTokenTransfer(Token(_reserve, "TokenTransfer()"), Gate - 1));
            Assert.Null(LedgerIntegrityRules.ReserveTokenTransfer(Token(_reserve, "TokenVoteTopicCast()"), Gate));
            Assert.Null(LedgerIntegrityRules.ReserveTokenTransfer(Token(_normal, "TokenTransfer()"), Gate));
        }
    }
}
