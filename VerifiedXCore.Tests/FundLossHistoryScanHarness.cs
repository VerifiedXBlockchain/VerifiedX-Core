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
        /// Unshields mined in history whose outer recipient or amount differ from their payload's transparent output and
        /// amount (fifth review, round 18): the outer fields are not in an unshield's hash, and the rule binding them
        /// (PrivateTxSupplyRules.StructureError) starts at the fund-loss height, so whoever serves blocks to a syncing node
        /// can change them below it. If history holds none, the binding can be required at every height. Inert unless
        /// UNSHIELD_SCAN_DB names the database folder; the block database is opened read-only, in place.
        /// </summary>
        /// <summary>
        /// Round 26: every transaction in history that the path-audit rules' plain-ASCII checks would refuse (addresses,
        /// ADNR and BTC ADNR names and Bitcoin addresses, DecShop ids and URLs, vote topic ids; vBTC Data keys and values).
        /// Such text goes through platform-dependent casing and collation; two items differing only by the case of a
        /// non-ASCII letter would replay differently by platform. Inert unless ASCII_SCAN_DB names the database folder;
        /// the block database is opened read-only, in place.
        /// </summary>
        [Fact]
        public void PlainAsciiScan()
        {
            var dbDir = Environment.GetEnvironmentVariable("ASCII_SCAN_DB");
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
            var db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var blocks = db.GetCollection<Block>(DbContext.RSRV_BLOCKS);
            var network = dbDir.Contains("TestNet", StringComparison.OrdinalIgnoreCase) ? "testnet" : "mainnet";
            var lines = new System.Collections.Generic.List<string>();
            var byType = new System.Collections.Generic.Dictionary<string, int>();
            long scanned = 0, lastHeight = -1, txs = 0, refused = 0;
            string? stopped = null;
            try
            {
                foreach (var block in blocks.Query().Where(b => b.Height >= 0).OrderBy(b => b.Height).ToEnumerable())
                {
                    lastHeight = block.Height;
                    scanned++;
                    foreach (var tx in block.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (tx == null) continue;
                        txs++;
                        var why = LedgerIntegrityRules.IdentifiersArePlainAscii(tx, long.MaxValue) ?? LedgerIntegrityRules.VbtcDataKeysAreDistinct(tx, long.MaxValue);
                        if (why == null) continue;
                        refused++;
                        var key = tx.TransactionType.ToString();
                        byType[key] = byType.TryGetValue(key, out var c) ? c + 1 : 1;
                        var data = tx.Data ?? "";
                        lines.Add($"{block.Height} {tx.Hash} {tx.TransactionType} from {tx.FromAddress} to {tx.ToAddress}: {why} Data: {(data.Length > 300 ? data.Substring(0, 300) + "..." : data)}");
                    }
                }
            }
            catch (Exception ex) { stopped = ex.ToString(); }
            if (stopped != null) lines.Insert(0, $"STOPPED after height {lastHeight}: {stopped}");
            lines.Insert(0, $"{network}: {scanned} blocks to height {lastHeight}, {txs} transactions, {refused} the rules would refuse ({string.Join(", ", byType.Select(kv => kv.Key + " " + kv.Value))})");
            File.WriteAllLines(Path.Combine(outDir, $"ascii-scan-{network}.txt"), lines);
        }

        [Fact]
        public void UnshieldBindingScan()
        {
            var dbDir = Environment.GetEnvironmentVariable("UNSHIELD_SCAN_DB");
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
            var db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var blocks = db.GetCollection<Block>(DbContext.RSRV_BLOCKS);
            var network = dbDir.Contains("TestNet", StringComparison.OrdinalIgnoreCase) ? "testnet" : "mainnet";
            var lines = new System.Collections.Generic.List<string>();
            long scanned = 0, lastHeight = -1, unshields = 0, unreadable = 0, mismatched = 0, noOutput = 0;
            string? stopped = null;
            try
            {
                foreach (var block in blocks.Query().Where(b => b.Height >= 0).OrderBy(b => b.Height).ToEnumerable())
                {
                    lastHeight = block.Height;
                    scanned++;
                    foreach (var tx in block.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (tx == null || (tx.TransactionType != TransactionType.VFX_UNSHIELD && tx.TransactionType != TransactionType.VBTC_V2_UNSHIELD)) continue;
                        unshields++;
                        if (!VerifiedXCore.Privacy.PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out var err) || payload == null)
                        { unreadable++; lines.Add($"UNREADABLE {block.Height} {tx.Hash} {tx.TransactionType}: {err}"); continue; }
                        var outputMatches = !string.IsNullOrWhiteSpace(payload.TransparentOutput) && string.Equals(payload.TransparentOutput, tx.ToAddress, StringComparison.Ordinal);
                        var amountMatches = tx.TransactionType != TransactionType.VFX_UNSHIELD || (payload.TransparentAmount != null && payload.TransparentAmount.Value == tx.Amount);
                        if (string.IsNullOrWhiteSpace(payload.TransparentOutput)) noOutput++;
                        if (!outputMatches || !amountMatches)
                        {
                            mismatched++;
                            lines.Add($"MISMATCH {block.Height} {tx.Hash} {tx.TransactionType}: to {tx.ToAddress} / payload {payload.TransparentOutput}; amount {tx.Amount} / payload {payload.TransparentAmount}");
                        }
                    }
                }
            }
            catch (Exception ex) { stopped = ex.ToString(); }
            if (stopped != null) lines.Insert(0, $"STOPPED after height {lastHeight}: {stopped}");
            lines.Insert(0, $"{network}: {scanned} blocks to height {lastHeight}, {unshields} unshields, {mismatched} with outer fields unlike the payload ({noOutput} without a transparent output), {unreadable} unreadable");
            File.WriteAllLines(Path.Combine(outDir, $"unshield-scan-{network}.txt"), lines);
        }

        /// <summary>
        /// Delayed owner changes of one contract made by reserve accounts (fourth review, round 8): reserve rows falling due
        /// together are now settled in (ConfirmTimestamp, Height, Hash) order, where nodes used their own random row ids.
        /// Before the path-audit gate a delayed Transfer() or TokenContractOwnerChange() is applied without a "still stands"
        /// check, so two of one contract falling due between the same two blocks would now resolve one way on every
        /// rebuilt node. Lists every contract with more than one such transaction, with heights and unlock times, and
        /// marks pairs whose unlock times fall between the same two blocks. Inert unless RESERVE_ORDER_SCAN_DB names the
        /// database folder; the block database is opened read-only, in place.
        /// </summary>
        [Fact]
        public void ReserveOwnerChangeOrderScan()
        {
            var dbDir = Environment.GetEnvironmentVariable("RESERVE_ORDER_SCAN_DB");
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
            var db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var blocks = db.GetCollection<Block>(DbContext.RSRV_BLOCKS);
            var network = dbDir.Contains("TestNet", StringComparison.OrdinalIgnoreCase) ? "testnet" : "mainnet";
            var changes = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(long Height, string Hash, string Function, long Unlock)>>(StringComparer.Ordinal);
            var blockTimes = new System.Collections.Generic.List<(long Height, long Time)>();
            long scanned = 0, lastHeight = -1, reserveTxs = 0, longestUidHeight = -1;
            var longestUid = "";
            string? stopped = null;
            try
            {
                foreach (var block in blocks.Query().OrderBy(b => b.Height).ToEnumerable())
                {
                    lastHeight = block.Height;
                    scanned++;
                    blockTimes.Add((block.Height, block.Timestamp));
                    foreach (var tx in block.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        var created = tx?.Data == null ? null : LedgerIntegrityRules.CreatedContractUid(tx);
                        if (created != null && created.Length > longestUid.Length) { longestUid = created; longestUidHeight = block.Height; }
                        if (tx?.FromAddress == null || !tx.FromAddress.StartsWith("xRBX", StringComparison.Ordinal) || tx.TransactionType == TransactionType.RESERVE) continue;
                        reserveTxs++;
                        var (function, uid) = LedgerIntegrityRules.DispatchedCall(tx.Data);
                        if ((function != "Transfer()" && function != "TokenContractOwnerChange()") || uid == null) continue;
                        if (!changes.TryGetValue(uid, out var list)) changes[uid] = list = new();
                        list.Add((block.Height, tx.Hash, function, tx.UnlockTime ?? 0));
                    }
                }
            }
            catch (Exception ex) { stopped = ex.ToString(); }
            // The block a row falls due in: the first block whose timestamp is above its unlock time.
            long DueAt(long unlock)
            {
                int lo = 0, hi = blockTimes.Count - 1, found = -1;
                while (lo <= hi) { var mid = (lo + hi) / 2; if (blockTimes[mid].Time > unlock) { found = mid; hi = mid - 1; } else lo = mid + 1; }
                return found < 0 ? -1 : blockTimes[found].Height;
            }
            var lines = new System.Collections.Generic.List<string>();
            long contractsWithMany = 0, sameBatch = 0;
            foreach (var (uid, list) in changes.Where(kv => kv.Value.Count > 1))
            {
                contractsWithMany++;
                var due = list.Select(c => (c, Due: DueAt(c.Unlock))).ToList();
                foreach (var group in due.GroupBy(d => d.Due).Where(g => g.Count() > 1))
                {
                    sameBatch++;
                    lines.Add($"SAME BATCH {uid} due at {group.Key}: " + string.Join("; ", group.Select(d => $"{d.c.Function} {d.c.Hash} mined {d.c.Height} unlock {d.c.Unlock}")));
                }
                lines.Add($"{uid}: " + string.Join("; ", due.Select(d => $"{d.c.Function} mined {d.c.Height} due {d.Due}")));
            }
            if (stopped != null) lines.Insert(0, $"STOPPED after height {lastHeight}: {stopped}");
            lines.Insert(0, $"{network}: {scanned} blocks to height {lastHeight}, {reserveTxs} reserve transactions, {changes.Count} contracts with a delayed owner change, {contractsWithMany} with more than one, {sameBatch} pairs falling due together");
            // The longest contract UID created in history: LiteDB refuses an index key over 1,023 bytes, and contract state is
            // now indexed by its UID (round 7); such a contract would be missing on a node with the index (round 9).
            lines.Insert(1, $"longest created contract UID: {longestUid.Length} characters ({longestUid}) at block {longestUidHeight}");
            File.WriteAllLines(Path.Combine(outDir, $"reserve-order-scan-{network}.txt"), lines);
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
