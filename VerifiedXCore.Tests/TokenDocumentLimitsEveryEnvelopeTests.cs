using System;
using System.IO;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.PathAuditRulesHeight the token document caps apply under every contract-call type that runs the token
    /// functions, not under FTKN_TX alone: a TokenBanAddress() or TokenVoteTopicCreate() wrapped in NFT_TX, SC_TX or TKNZ_TX
    /// was applied the same way and skipped the caps.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TokenDocumentLimitsEveryEnvelopeTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public TokenDocumentLimitsEveryEnvelopeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"token_limits_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private static Transaction Ban(TransactionType type, string banned) => new()
        {
            TransactionType = type, FromAddress = "xOwner", ToAddress = "Token_Base",
            Data = JsonConvert.SerializeObject(new { Function = "TokenBanAddress()", ContractUID = "uid:1", BanAddress = banned }),
        };

        [Theory]
        [InlineData(TransactionType.FTKN_TX)]
        [InlineData(TransactionType.NFT_TX)]
        [InlineData(TransactionType.SC_TX)]
        [InlineData(TransactionType.TKNZ_TX)]
        public void AnOversizedBan_IsRefusedUnderEveryEnvelope_FromTheRule(TransactionType type)
        {
            var rule = Globals.PathAuditRulesHeight;
            var oversized = new string('a', LedgerIntegrityRules.MaxBanAddressLength + 1);
            Assert.NotNull(LedgerIntegrityRules.TokenDocumentLimits(Ban(type, oversized), rule));
            Assert.Null(LedgerIntegrityRules.TokenDocumentLimits(Ban(type, "xBanned"), rule));
            Assert.Null(LedgerIntegrityRules.TokenDocumentLimits(Ban(type, oversized), rule - 1));
        }

        [Fact]
        public void AFunctionInATypeTheTokenFunctionsDoNotRunFor_IsLeftToItsOwnRules()
        {
            var oversized = new string('a', LedgerIntegrityRules.MaxBanAddressLength + 1);
            Assert.Null(LedgerIntegrityRules.TokenDocumentLimits(Ban(TransactionType.TX, oversized), Globals.PathAuditRulesHeight));
        }
    }
}
