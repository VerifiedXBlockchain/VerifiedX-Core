// TEMPORARY, NOT COMMITTED: re-evaluates the transactions listed in a scan report with the CURRENT rules.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempRecheckHits
    {
        [Fact]
        public void Recheck()
        {
            var root = Environment.GetEnvironmentVariable("SCAN_ROOT");
            var report = Environment.GetEnvironmentVariable("SCAN_REPORT");
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(report)) return;
            Globals.IsTestNet = Environment.GetEnvironmentVariable("SCAN_NET") == "testnet";
            Globals.CustomPath = root;
            DbContext.Initialize();
            using var w = new StreamWriter(Path.Combine(root, "recheck.txt"));
            int stillFailing = 0, total = 0;
            foreach (var line in File.ReadLines(report).Where(l => l.StartsWith("height=")))
            {
                var m = Regex.Match(line, @"height=(\d+) tx=([0-9a-f]+)");
                var h = long.Parse(m.Groups[1].Value); var hash = m.Groups[2].Value;
                var tx = BlockchainData.GetBlocks().Query().Where(b => b.Height == h).First().Transactions.First(t => t.Hash == hash);
                var hits = AuditReplayScanService.CheckTransaction(tx, h);
                total++; if (hits.Count > 0) stillFailing++;
                w.WriteLine($"height={h} tx={hash} currentRuleHits={hits.Count} {string.Join(" | ", hits.Select(x => x.Rule + ": " + x.Reason))}");
            }
            w.WriteLine($"TOTAL={total} STILL_FAILING={stillFailing}");
            try { DbContext.CloseDB(); } catch { }
        }
    }
}
