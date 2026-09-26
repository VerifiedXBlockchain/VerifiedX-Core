using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-02 / NEW-04 / NEW-05 / NEW-09 (follow-up, owner decision): the mainnet transactions that were accepted when mined
    /// and that these rules now refuse are accepted exactly - in block validation, in their own block, with their own
    /// content. Fixtures are two of them, copied from the owner's mainnet block store: the 250,000 OURFANS transfer to the
    /// sender's own address (block 3,202,099, where a sync from genesis used to stop) and a V1 multi-input transfer whose
    /// input signature does not verify under NEW-05 (block 4,108,774).
    /// </summary>
    [Collection("DbContextSequential")]
    public class HistoricalTransactionExceptionsTests : IDisposable
    {
        private const string SelfTransferJson = "{\"Hash\":\"d4e01cc7754622be5018315ef34976cf9d6fba4245125995dc34127392efba7e\",\"ToAddress\":\"RCgVe39M8XdFPCvuCajo6ND9gn1sVXgFFf\",\"FromAddress\":\"RCgVe39M8XdFPCvuCajo6ND9gn1sVXgFFf\",\"Amount\":0.0,\"Nonce\":23,\"Fee\":0.00001137,\"Timestamp\":1738127024,\"Data\":\"{\\\"Function\\\":\\\"TokenTransfer()\\\",\\\"ContractUID\\\":\\\"8281b383c8b148bc8bd39277f7fd701d:1737456487\\\",\\\"FromAddress\\\":\\\"RCgVe39M8XdFPCvuCajo6ND9gn1sVXgFFf\\\",\\\"ToAddress\\\":\\\"RCgVe39M8XdFPCvuCajo6ND9gn1sVXgFFf\\\",\\\"Amount\\\":250000.0,\\\"TokenTicker\\\":\\\"OURFANS\\\",\\\"TokenName\\\":\\\"OF\\\"}\",\"UnlockTime\":null,\"Signature\":\"MEUCIQCOTPpIk71gwIFjMoAeDtIT825+gpFs9+DbnZeksP+qmAIgNXtJob9an+3yxWXDiPDWyp7pLfB4NAyEVElLVfN3Xm8=.46Kj5Vy7tLgnQGfjinEWvUbGdUaBDBcYztsLz2n2KrfVjdqMkTenDk374GGeRToesKcHR3tNo7x7jy8qsXftwHhz\",\"Height\":3202099,\"TransactionType\":15,\"TransactionRating\":1,\"TransactionStatus\":0}";
        private const string V1MultiJson = "{\"Hash\":\"c3ef49c822be8d1b23e7f9a51224d800c74f789ca460f408110a197deff5f8dc\",\"ToAddress\":\"REv5tHbW84aNVqGCXtEJK3AujGr1Egs2Yj\",\"FromAddress\":\"RJSVaUXjkTysZSVVaRZmkXUwVbNDenavs2\",\"Amount\":0.0,\"Nonce\":45,\"Fee\":0.00001459,\"Timestamp\":1749127411,\"Data\":\"{\\\"Function\\\":\\\"TransferCoinMulti()\\\",\\\"Inputs\\\":[{\\\"SCUID\\\":\\\"d7c08f7bab2941ca95dddd59b63e0f2f:1736961214\\\",\\\"FromAddress\\\":\\\"RJSVaUXjkTysZSVVaRZmkXUwVbNDenavs2\\\",\\\"Amount\\\":1E-08,\\\"Signature\\\":\\\"MEQCIJqeOaU6f8zo9gfc5W8Rs/8fVD7wOC8Q13MXsliwBCHnAiB5ihfgbVAxH03dfc29NQ0dZPpUdgIB/KtCi9ZB3/BXiQ==.2DQVUCUe8B8HgfrcWmJRvKaM1NhyhhretxfGL7h1jK7zDvnVvBA2teAXf4GKRR1VGeYeoAwtmRqvJmpQ9eRcU4T9\\\"}],\\\"Amount\\\":1E-08,\\\"SignatureInput\\\":\\\"ZPkOLTc1FTKn\\\"}\",\"UnlockTime\":null,\"Signature\":\"MEQCIDKWGJA6hULeMdyk1vAmzDD8pgx278nJI33n3VaUhj8fAiAAgDnGa80wWlPLj65H8UWbJ6kB2FPMEmJYbVfwLMAQmA==.2DQVUCUe8B8HgfrcWmJRvKaM1NhyhhretxfGL7h1jK7zDvnVvBA2teAXf4GKRR1VGeYeoAwtmRqvJmpQ9eRcU4T9\",\"Height\":4108774,\"TransactionType\":18,\"TransactionRating\":1,\"TransactionStatus\":null}";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorIsTestNet;

        public HistoricalTransactionExceptionsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"histex_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorIsTestNet = Globals.IsTestNet;
            Globals.CustomPath = _tempRoot;
            Globals.IsTestNet = false;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.IsTestNet = _priorIsTestNet;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Load(string json) => JsonConvert.DeserializeObject<Transaction>(json)!;

        [Fact]
        public void TheFixturesAreTheMinedContent()
        {
            foreach (var json in new[] { SelfTransferJson, V1MultiJson })
            {
                var tx = Load(json);
                Assert.Equal(tx.Hash, tx.GetHash());
            }
        }

        [Fact]
        public async Task ListedTransaction_IsAcceptedInItsOwnBlock()
        {
            var tx = Load(SelfTransferJson);
            Assert.True(HistoricalTransactionExceptions.IsAccepted(tx, 3_202_099));
            Assert.True((await TransactionValidatorService.VerifyTX(tx, true, true, false, null, false, 3_202_099)).Item1);

            var multi = Load(V1MultiJson);
            Assert.True(HistoricalTransactionExceptions.IsAccepted(multi, 4_108_774));
            Assert.True((await TransactionValidatorService.VerifyTX(multi, true, true, false, null, false, 4_108_774)).Item1);
        }

        [Fact]
        public async Task ListedTransaction_IsRefusedAnywhereElse()
        {
            var tx = Load(SelfTransferJson);
            // Another block height (a replay or a re-proposal).
            Assert.False(HistoricalTransactionExceptions.IsAccepted(tx, 3_202_100));
            Assert.False((await TransactionValidatorService.VerifyTX(tx, true, true, false, null, false, 3_202_100)).Item1);
            // Admission and proposal: no block height.
            Assert.False(HistoricalTransactionExceptions.IsAccepted(tx, null));
            Assert.False((await TransactionValidatorService.VerifyTX(tx)).Item1);
            // Testnet.
            Globals.IsTestNet = true;
            Assert.False(HistoricalTransactionExceptions.IsAccepted(tx, 3_202_099));
        }

        [Fact]
        public void ListedHash_WithOtherContent_IsNotAccepted()
        {
            var tx = Load(SelfTransferJson);
            tx.Data = tx.Data.Replace("250000.0", "9250000.0");   // same carried Hash, different content
            Assert.False(HistoricalTransactionExceptions.IsAccepted(tx, 3_202_099));
            var multi = Load(V1MultiJson);
            multi.ToAddress = "RCgVe39M8XdFPCvuCajo6ND9gn1sVXgFFf";
            Assert.False(HistoricalTransactionExceptions.IsAccepted(multi, 4_108_774));
        }

        [Fact]
        public void TheReplayScan_ReportsNothingForAListedTransactionInItsBlock_AndStillFlagsItElsewhere()
        {
            var tx = Load(SelfTransferJson);
            Assert.Empty(AuditReplayScanService.CheckTransaction(tx, 3_202_099));
            Assert.NotEmpty(AuditReplayScanService.CheckTransaction(tx, 3_202_100));
        }

        [Fact]
        public void TheListHoldsTheScannedTransactions()
        {
            // 35 rule hits on 32 transactions in the read-only scan of mainnet to block 6,891,066.
            Assert.Equal(32, HistoricalTransactionExceptions.MainnetCount);
        }
    }
}
