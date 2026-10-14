using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
        /// Lists every mined transaction carrying a contract body that LedgerIntegrityRules.ContractBodyAllowed refuses from
        /// Globals.PathAuditRulesHeight (a call to send or createSig): such a contract's later transfers and evolves carry
        /// the same body and would be refused too. Block database opened READ-ONLY in place; inert unless
        /// PROCESSBOUND_SCAN_DB names the database folder. Writes processbound-scan.txt to FUNDLOSS_SCAN_OUT.
        /// </summary>
        [Fact]
        public void ProcessBoundBuiltinsScan()
        {
            var dbDir = Environment.GetEnvironmentVariable("PROCESSBOUND_SCAN_DB");
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
            // Not disposed: a read-only database with a write-ahead log would try to checkpoint it on dispose (and fail).
            var db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var blocks = db.GetCollection<Block>(DbContext.RSRV_BLOCKS);

            var network = dbDir.Contains("TestNet", StringComparison.OrdinalIgnoreCase) ? "testnet" : "mainnet";
            var reportPath = Path.Combine(outDir, $"processbound-scan-{network}.txt");
            var lines = new System.Collections.Generic.List<string>();
            long scanned = 0, withBody = 0, lastHeight = -1;
            string? stopped = null;
            try
            {
            foreach (var block in blocks.Query().OrderBy(b => b.Height).ToEnumerable())
            {
                lastHeight = block.Height;
                if (++scanned % 500_000 == 0)
                    File.WriteAllText(reportPath + ".progress", $"{DateTime.UtcNow:O} height {block.Height}, {withBody} bodies, {lines.Count} hits{Environment.NewLine}");
                foreach (var tx in block.Transactions ?? new System.Collections.Generic.List<Transaction>())
                {
                    if (tx?.Data == null || !tx.Data.Contains("\"Data\"")) continue;
                    withBody++;
                    if (LedgerIntegrityRules.ContractBodyAllowed(tx, 0) != null) continue; // refused before already (NEW-29)
                    var reason = LedgerIntegrityRules.ContractBodyAllowed(tx, long.MaxValue);
                    if (reason != null) lines.Add($"{block.Height} {tx.Hash} {tx.TransactionType} {reason}");
                }
            }
            }
            catch (Exception ex) { stopped = ex.ToString(); }
            if (stopped != null) lines.Insert(0, $"STOPPED after height {lastHeight}: {stopped}");
            lines.Insert(0, $"{network}: {scanned} blocks to height {lastHeight}, {withBody} transactions carrying a Data field, {lines.Count} refused only by the process-bound built-ins rule");
            File.WriteAllLines(reportPath, lines);
        }

        /// <summary>
        /// Lists every mined vault creation (a created contract whose body declares TokenizationV2) and whether its code is
        /// plain (LedgerIntegrityRules.VaultCodeIsPlain): from Globals.PathAuditRulesHeight a vault whose code is not plain
        /// is no longer read as a vault. Block database opened READ-ONLY in place; inert unless VAULTPLAIN_SCAN_DB is set.
        /// Writes vaultplain-scan-{network}.txt to FUNDLOSS_SCAN_OUT.
        /// </summary>
        [Fact]
        public void VaultPlainnessScan()
        {
            var dbDir = Environment.GetEnvironmentVariable("VAULTPLAIN_SCAN_DB");
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
            // Not disposed: a read-only database with a write-ahead log would try to checkpoint it on dispose (and fail).
            var db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var blocks = db.GetCollection<Block>(DbContext.RSRV_BLOCKS);
            var network = dbDir.Contains("TestNet", StringComparison.OrdinalIgnoreCase) ? "testnet" : "mainnet";
            var lines = new System.Collections.Generic.List<string>();
            long scanned = 0, vaults = 0, notPlain = 0, lastHeight = -1;
            string? stopped = null;
            try
            {
                foreach (var block in blocks.Query().OrderBy(b => b.Height).ToEnumerable())
                {
                    lastHeight = block.Height;
                    scanned++;
                    foreach (var tx in block.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (tx?.Data == null || !tx.Data.Contains("\"Data\"")) continue;
                        var uid = LedgerIntegrityRules.CreatedContractUid(tx);
                        if (uid == null) continue;
                        var body = SmartContractDeployBinding.ReadPayload(tx.Data).Data;
                        if (string.IsNullOrEmpty(body)) continue;
                        VerifiedXCore.Models.SmartContracts.SmartContractMain? main;
                        try { main = VerifiedXCore.Models.SmartContracts.SmartContractMain.GenerateSmartContractInMemory(body); }
                        catch { continue; }
                        if (main?.Features?.Any(x => x != null && x.FeatureName == FeatureName.TokenizationV2) != true) continue;
                        vaults++;
                        var timeless = LedgerIntegrityRules.ContractCodeIsTimeless(body);
                        var plain = timeless && LedgerIntegrityRules.VaultCodeIsPlain(main, body);
                        if (!plain)
                        {
                            notPlain++;
                            lines.Add($"{block.Height} {tx.Hash} {uid} features={string.Join(",", main.Features.Where(x => x != null).Select(x => x.FeatureName))} timeless={timeless}");
                        }
                    }
                }
            }
            catch (Exception ex) { stopped = ex.ToString(); }
            if (stopped != null) lines.Insert(0, $"STOPPED after height {lastHeight}: {stopped}");
            lines.Insert(0, $"{network}: {scanned} blocks to height {lastHeight}, {vaults} vault creations, {notPlain} whose code is not plain");
            File.WriteAllLines(Path.Combine(outDir, $"vaultplain-scan-{network}.txt"), lines);
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
