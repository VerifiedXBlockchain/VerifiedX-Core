// TEMPORARY, NOT COMMITTED: runs AuditReplayScanService over the owner's DBs folder READ-ONLY.
// Opens only rsrvblkdata.db and rsrvscstatetrei.db from SCAN_SRC with LiteDB ReadOnly=true (read-only file access);
// every other database is a fresh empty one in a scratch folder. Never touches wallet databases.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempReadOnlyScan
    {
        [Fact]
        public void Scan()
        {
            var src = Environment.GetEnvironmentVariable("SCAN_SRC");     // e.g. ...\VerifiedXCore\DBs\Databases\
            var outFile = Environment.GetEnvironmentVariable("SCAN_OUT"); // report path in the scratchpad
            var scratch = Environment.GetEnvironmentVariable("SCAN_SCRATCH");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(outFile) || string.IsNullOrEmpty(scratch)) return;
            Globals.IsTestNet = Environment.GetEnvironmentVariable("SCAN_NET") == "testnet";
            if (Globals.IsTestNet) Globals.AddressPrefix = 0x89;

            string[] files = { "rsrvblkdata.db", "rsrvscstatetrei.db" };
            string Stamp() => string.Join("; ", files.SelectMany(f => new[] { f, f.Replace(".db", "-log.db") })
                .Select(f => Path.Combine(src, f)).Where(File.Exists)
                .Select(f => { var i = new FileInfo(f); return $"{i.Name} {i.Length} {i.LastWriteTimeUtc:o}"; }));
            var before = Stamp();

            Directory.CreateDirectory(scratch);
            Globals.CustomPath = scratch;
            DbContext.Initialize(); // empty scratch databases for everything

            var mapper = (BsonMapper)typeof(DbContext).GetMethod("CreateDbMapper", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            DbContext.DB.Dispose();
            DbContext.DB = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvblkdata.db"), Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            DbContext.DB_SmartContractStateTrei.Dispose();
            DbContext.DB_SmartContractStateTrei = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvscstatetrei.db"), Connection = ConnectionType.Direct, ReadOnly = true });

            using var w = new StreamWriter(outFile) { AutoFlush = true };
            w.WriteLine($"source {src} (read-only) net={(Globals.IsTestNet ? "testnet" : "mainnet")} started {DateTime.UtcNow:o}");
            w.WriteLine("before: " + before);
            try
            {
                var top = BlockchainData.GetBlocks().Query().OrderByDescending(b => b.Height).Limit(1).FirstOrDefault();
                Globals.LastBlock = top ?? new Block { Height = -1 };
                if (long.TryParse(Environment.GetEnvironmentVariable("SCAN_MAX"), out var max) && max < Globals.LastBlock.Height)
                    Globals.LastBlock = new Block { Height = max };
                w.WriteLine($"tip {Globals.LastBlock.Height}");
                var (blocks, txs, hits) = AuditReplayScanService.ScanChain(p => w.WriteLine("progress " + p));
                w.WriteLine($"done blocks={blocks} txs={txs} hits={hits.Count} finished {DateTime.UtcNow:o}");
                foreach (var g in hits.GroupBy(h => h.Rule).OrderBy(g => g.Key))
                    w.WriteLine($"rule {g.Key}: {g.Count()}");
                foreach (var h in hits)
                    w.WriteLine($"hit height={h.Height} tx={h.TxHash} type={h.Type} rule={h.Rule} :: {h.Reason}");
            }
            catch (Exception ex) { w.WriteLine("ERROR " + ex); }
            finally
            {
                w.WriteLine("after:  " + Stamp());
            }
        }
    }
}
