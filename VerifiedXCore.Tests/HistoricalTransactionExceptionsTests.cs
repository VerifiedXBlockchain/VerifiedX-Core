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

        // Testnet block 64,607: the bridge exit refused by the pre-audit committee-caster submitter rule (b61d49f5).
        private const string TestnetBridgeExitJson = "{\"Hash\":\"b9c2b93e8f59837a40555010d8c25f698b83996696932d9a4201a43e76e72765\",\"ToAddress\":\"xNKJ6Xxskkw3dn8nQRbqR7Kfi8dbSFYYjN\",\"FromAddress\":\"xNKJ6Xxskkw3dn8nQRbqR7Kfi8dbSFYYjN\",\"Amount\":0.0,\"Nonce\":130,\"Fee\":0.00,\"Timestamp\":1778548059,\"Data\":\"{\\\"Function\\\":\\\"VBTCBridgeExitToBTC()\\\",\\\"ContractUID\\\":\\\"376abde5f1b24678a75404c8332ecc64:1778217665\\\",\\\"LockId\\\":\\\"c8340ca3c93543d0aa884ae0034e1b85\\\",\\\"Amount\\\":0.0001,\\\"AmountSats\\\":10000,\\\"TotalAmount\\\":0.0001,\\\"TotalAmountSats\\\":10000,\\\"BtcDestination\\\":\\\"tb1pj46xty7nwv5zw5s4805a0m2sxpf20w7zm860cqy3dvs8220tpj6q2wd0zf\\\",\\\"BaseBurnTxHash\\\":\\\"0x276693fb8febbdab587107f6b59df592848e72f66eb515210bf8ac7bafaed35a\\\",\\\"Allocations\\\":[{\\\"LockId\\\":\\\"c8340ca3c93543d0aa884ae0034e1b85\\\",\\\"UnlockAmount\\\":0.0001,\\\"SmartContractUID\\\":\\\"376abde5f1b24678a75404c8332ecc64:1778217665\\\"}],\\\"BtcWithdrawals\\\":null,\\\"CasterConsensusVotes\\\":[{\\\"CasterAddress\\\":\\\"xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj\\\",\\\"BaseBurnTxHash\\\":\\\"0x276693fb8febbdab587107f6b59df592848e72f66eb515210bf8ac7bafaed35a\\\",\\\"BurnType\\\":\\\"BTC_EXIT\\\",\\\"Timestamp\\\":1778548044,\\\"Signature\\\":\\\"MEUCIQDtad4b1h8PxsQxzN6rNN46EqotX77dBrOq0LtCf/mFXAIgHX8u9M3kCARE9nynJtD+sZ7Kx+QsEVPfMsBpqSMMFks=.44KhTuHBiGm3rhfXn9SaRyQBuBzZtpEZe2RSNuJY9Fb3UPcLLeVkzGXYESKtGgWNoYbAuBuVC2F1Yq3dAnUi2ZsH\\\"},{\\\"CasterAddress\\\":\\\"xNKJ6Xxskkw3dn8nQRbqR7Kfi8dbSFYYjN\\\",\\\"BaseBurnTxHash\\\":\\\"0x276693fb8febbdab587107f6b59df592848e72f66eb515210bf8ac7bafaed35a\\\",\\\"BurnType\\\":\\\"BTC_EXIT\\\",\\\"Timestamp\\\":1778548051,\\\"Signature\\\":\\\"MEQCIEfX06X0eOIBJvdjLHMaUxLPVxKkvDDYO4LoVvUav1yxAiAsGuXyODUiPL981kA0Z8dzS4rZqSffoOjEiyaUy80pEA==.JfcRKcvLvvpy3yhW7yfCCMdhWcqD2AnfiYKUguZ41TqW82khvAyoqhHX1aDtbmfFyFqYYRvecfgyu4NPbb37euT\\\"},{\\\"CasterAddress\\\":\\\"xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC\\\",\\\"BaseBurnTxHash\\\":\\\"0x276693fb8febbdab587107f6b59df592848e72f66eb515210bf8ac7bafaed35a\\\",\\\"BurnType\\\":\\\"BTC_EXIT\\\",\\\"Timestamp\\\":1778548054,\\\"Signature\\\":\\\"MEUCIQD0a2EmVSsc0ggqMPC5uLpbycq9x9ljR3VB4TQjVO837QIgWDLgYGzctWFDgdLc9sqArS+unjEvpuofE/4xbxph7Fg=.5msmMuPGKwpvtYofSdr6zk1AWG5Rz1vmf5z5DTVb6PYXibwovzkKM7Eg2BTQ5KDeUKwSyEAzHq9KjTFc1hyhfiFg\\\"},{\\\"CasterAddress\\\":\\\"xCkUC4rrh2AnfNf78D5Ps83pMywk5vrwpi\\\",\\\"BaseBurnTxHash\\\":\\\"0x276693fb8febbdab587107f6b59df592848e72f66eb515210bf8ac7bafaed35a\\\",\\\"BurnType\\\":\\\"BTC_EXIT\\\",\\\"Timestamp\\\":1778548054,\\\"Signature\\\":\\\"MEUCIDsiXO8D27eQ9+Kr9cZlLV6jieLGmYHivwhUMIUJHxPqAiEAtBcxhwVAKrBrIzY4ZaN05eIPXzqD3DpTKNwrKKzlLwo=.3Lc4BV7dKm3M2W3d7sPRDf4AYFw9AGmoyKmZTy89ipAMjpgkQ2txTrCkxag7jQFXbWuQnna2ifDR8ouu1jzDrZ1c\\\"}]}\",\"UnlockTime\":null,\"Signature\":\"MEYCIQCnPZcdWRWU6C1FNQ4evqOTyUBnMti3tPRz4Z5ctsifZAIhAKatH7MUvV7rbE5dIZI6hd7wy2W9t+a9xP1WYFL3IlOz.JfcRKcvLvvpy3yhW7yfCCMdhWcqD2AnfiYKUguZ41TqW82khvAyoqhHX1aDtbmfFyFqYYRvecfgyu4NPbb37euT\",\"Height\":64607,\"TransactionType\":40,\"TransactionRating\":1,\"TransactionStatus\":null}";

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
            foreach (var json in new[] { SelfTransferJson, V1MultiJson, TestnetBridgeExitJson })
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
            // Testnet: mainnet entries are not honoured there.
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
            // 35 rule hits on 32 transactions in the read-only scan of mainnet to block 6,891,066, plus 2 found by the full
            // replay (a same-block overspend at 5,655,096 and the last V1 withdrawal request at 5,662,203).
            Assert.Equal(34, HistoricalTransactionExceptions.MainnetCount);
            // Testnet: the only bridge exit and its completion (blocks 64,607 and 64,609).
            Assert.Equal(2, HistoricalTransactionExceptions.TestnetCount);
        }

        [Fact]
        public async Task TestnetBridgeExit_IsAcceptedInItsOwnTestnetBlockOnly()
        {
            var tx = Load(TestnetBridgeExitJson);
            Globals.IsTestNet = true;
            Assert.True(HistoricalTransactionExceptions.IsAccepted(tx, 64_607));
            Assert.True((await TransactionValidatorService.VerifyTX(tx, true, true, false, null, false, 64_607)).Item1);
            Assert.False(HistoricalTransactionExceptions.IsAccepted(tx, 64_608));
            Assert.False(HistoricalTransactionExceptions.IsAccepted(tx, null));
            tx.Data = tx.Data.Replace("\"AmountSats\":10000", "\"AmountSats\":90000");
            Assert.False(HistoricalTransactionExceptions.IsAccepted(tx, 64_607));
            Globals.IsTestNet = false;
            Assert.False(HistoricalTransactionExceptions.IsAccepted(Load(TestnetBridgeExitJson), 64_607));   // not a mainnet entry
        }
    }
}
