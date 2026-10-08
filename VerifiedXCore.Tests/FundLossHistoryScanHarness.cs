using System;
using System.Globalization;
using System.IO;
using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Runs <see cref="FundLossHistoryScanService"/> over a real block database opened READ-ONLY.
    /// Inert unless FUNDLOSS_SCAN_DB names the database folder (the one holding rsrvblkdata.db);
    /// FUNDLOSS_SCAN_OUT names the folder for the report and progress file (defaults to the DB folder).
    /// Only the block database is opened; wallet and key databases are never touched.
    /// </summary>
    public class FundLossHistoryScanHarness
    {
        [Fact]
        public void Run()
        {
            var dbDir = Environment.GetEnvironmentVariable("FUNDLOSS_SCAN_DB");
            if (string.IsNullOrWhiteSpace(dbDir)) return;
            var outDir = Environment.GetEnvironmentVariable("FUNDLOSS_SCAN_OUT");
            if (string.IsNullOrWhiteSpace(outDir)) outDir = dbDir;
            Directory.CreateDirectory(outDir);

            var dbPath = Path.Combine(dbDir, DbContext.RSRV_DB_NAME);
            Assert.True(File.Exists(dbPath), $"Block database not found: {dbPath}");

            var startedUtc = DateTime.UtcNow;
            var stamp = startedUtc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var progressPath = Path.Combine(outDir, $"fundloss-scan-progress-{stamp}.txt");
            var reportPath = Path.Combine(outDir, $"fundloss-scan-{stamp}.txt");
            void Progress(string msg) => File.AppendAllText(progressPath, $"{DateTime.UtcNow:O} {msg}{Environment.NewLine}");

            var mapper = new BsonMapper(null, LegacyTypeNameBinder.Instance);
            mapper.RegisterType<DateTime>(
                value => value.ToString("o", CultureInfo.InvariantCulture),
                bson => DateTime.ParseExact(bson, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            mapper.RegisterType<DateTimeOffset>(
                value => value.ToString("o", CultureInfo.InvariantCulture),
                bson => DateTimeOffset.ParseExact(bson, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

            using var db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var blocks = db.GetCollection<Block>(DbContext.RSRV_BLOCKS);
            Progress($"opened {dbPath} read-only, {blocks.Count()} blocks");

            var result = FundLossHistoryScanService.Scan(blocks.Query().OrderBy(b => b.Height).ToEnumerable(), Progress);
            var network = dbDir.Contains("TestNet", StringComparison.OrdinalIgnoreCase) ? "testnet" : "mainnet";
            FundLossHistoryScanService.WriteReport(result, reportPath, network, startedUtc);
            Progress($"report written: {reportPath}");
        }
    }
}
