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

        /// <summary>
        /// Runs the production privacy-store rebuild (PrivacyDbRebuildService) over a real block database opened
        /// read-only into a scratch DB_Privacy, and writes the resulting pool rows next to the scan report, so the
        /// figure every node will hold after the one-time startup rebuild can be compared with the scan's
        /// reconstruction. Inert unless FUNDLOSS_REBUILD_DB names the database folder.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task RebuildIntoScratch()
        {
            var dbDir = Environment.GetEnvironmentVariable("FUNDLOSS_REBUILD_DB");
            if (string.IsNullOrWhiteSpace(dbDir)) return;
            var outDir = Environment.GetEnvironmentVariable("FUNDLOSS_SCAN_OUT");
            if (string.IsNullOrWhiteSpace(outDir)) outDir = dbDir;
            Directory.CreateDirectory(outDir);
            var dbPath = Path.Combine(dbDir, DbContext.RSRV_DB_NAME);
            Assert.True(File.Exists(dbPath), $"Block database not found: {dbPath}");

            var mapper = new BsonMapper(null, LegacyTypeNameBinder.Instance);
            mapper.RegisterType<DateTime>(
                value => value.ToString("o", CultureInfo.InvariantCulture),
                bson => DateTime.ParseExact(bson, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            mapper.RegisterType<DateTimeOffset>(
                value => value.ToString("o", CultureInfo.InvariantCulture),
                bson => DateTimeOffset.ParseExact(bson, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            using var blocksDb = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var blocks = blocksDb.GetCollection<Block>(DbContext.RSRV_BLOCKS);

            var progressPath = Path.Combine(outDir, "fundloss-rebuild-progress.txt");
            void Progress(string msg) => File.AppendAllText(progressPath, $"{DateTime.UtcNow:O} {msg}{Environment.NewLine}");
            var kept = VerifiedXCore.Privacy.PrivacyDbRebuildService.CollectBlocksWithPrivateTransactions(blocks.Query().OrderBy(b => b.Height).ToEnumerable(), default, Progress);

            var scratchPath = Path.Combine(outDir, $"scratch-privacy-{Guid.NewGuid():N}.db");
            using (var scratch = new LiteDatabase(new ConnectionString { Filename = scratchPath, Connection = ConnectionType.Direct }, new BsonMapper(null, LegacyTypeNameBinder.Instance)))
            {
                VerifiedXCore.Privacy.PrivacyDbContext.EnsurePrivacyIndexes(scratch);
                var (ok, message) = await VerifiedXCore.Privacy.PrivacyDbRebuildService.TryReplayPrivateBlocksAsync(kept, scratch);
                var lines = new System.Collections.Generic.List<string> { $"ok={ok} {message}", $"blocks with private txs: {string.Join(",", kept.Select(b => b.Height))}" };
                foreach (var row in scratch.GetCollection<VerifiedXCore.Models.Privacy.ShieldedPoolState>(VerifiedXCore.Privacy.PrivacyDbContext.PRIV_POOL_STATE).FindAll())
                    lines.Add($"asset={row.AssetType} supply={row.TotalShieldedSupply} commitments={row.TotalCommitments} root={row.CurrentMerkleRoot} lastUpdateHeight={row.LastUpdateHeight}");
                lines.Add($"nullifiers={scratch.GetCollection(VerifiedXCore.Privacy.PrivacyDbContext.PRIV_NULLIFIERS).Count()} commitments={scratch.GetCollection(VerifiedXCore.Privacy.PrivacyDbContext.PRIV_COMMITMENTS).Count()}");
                File.WriteAllLines(Path.Combine(outDir, "fundloss-rebuild-result.txt"), lines);
                Assert.True(ok, message);
            }
            try { File.Delete(scratchPath); } catch { }
        }

        /// <summary>
        /// Reads the recorded shielded pool rows of a node's DB_Privacy read-only (FUNDLOSS_POOL_PROBE_DB names the
        /// database folder) and writes them next to the scan report, so the recorded counter can be compared with the
        /// scan's reconstruction and Globals.ShieldedSupplyCorrections.
        /// </summary>
        [Fact]
        public void ProbePoolState()
        {
            var dbDir = Environment.GetEnvironmentVariable("FUNDLOSS_POOL_PROBE_DB");
            if (string.IsNullOrWhiteSpace(dbDir)) return;
            var outDir = Environment.GetEnvironmentVariable("FUNDLOSS_SCAN_OUT");
            if (string.IsNullOrWhiteSpace(outDir)) outDir = dbDir;
            Directory.CreateDirectory(outDir);
            var dbPath = Path.Combine(dbDir, DbContext.RSRV_DB_PRIVACY);
            Assert.True(File.Exists(dbPath), $"Privacy database not found: {dbPath}");

            var mapper = new BsonMapper(null, LegacyTypeNameBinder.Instance);
            using var db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var lines = new System.Collections.Generic.List<string>();
            foreach (var row in db.GetCollection<VerifiedXCore.Models.Privacy.ShieldedPoolState>(VerifiedXCore.Privacy.PrivacyDbContext.PRIV_POOL_STATE).FindAll())
                lines.Add($"asset={row.AssetType} supply={row.TotalShieldedSupply} commitments={row.TotalCommitments} root={row.CurrentMerkleRoot} lastUpdateHeight={row.LastUpdateHeight}");
            lines.Add($"nullifiers={db.GetCollection(VerifiedXCore.Privacy.PrivacyDbContext.PRIV_NULLIFIERS).Count()} commitments={db.GetCollection(VerifiedXCore.Privacy.PrivacyDbContext.PRIV_COMMITMENTS).Count()}");
            File.WriteAllLines(Path.Combine(outDir, "fundloss-pool-probe.txt"), lines);
        }
    }
}
