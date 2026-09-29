// TEMPORARY, NOT COMMITTED: dumps the listed transactions (height:hash,...) from a chain copy.
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempDumpTx
    {
        [Fact]
        public void Dump()
        {
            var root = Environment.GetEnvironmentVariable("SCAN_ROOT");
            var list = Environment.GetEnvironmentVariable("SCAN_TXS");
            var report = Environment.GetEnvironmentVariable("SCAN_REPORT");
            if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(report))
            {
                Globals.IsTestNet = Environment.GetEnvironmentVariable("SCAN_NET") == "testnet";
                if (Globals.IsTestNet) Globals.AddressPrefix = 0x89;
                Globals.CustomPath = root;
                DbContext.Initialize();
                var tally = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var line in File.ReadLines(report).Where(l => l.StartsWith("height=") && l.Contains("NEW-04")))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"height=(\d+) tx=([0-9a-f]+)");
                    var hh = long.Parse(m.Groups[1].Value);
                    var t = BlockchainData.GetBlocks().Query().Where(b => b.Height == hh).First().Transactions.First(x => x.Hash == m.Groups[2].Value);
                    var d = Newtonsoft.Json.Linq.JObject.Parse(t.Data);
                    var key = $"txTo={t.ToAddress} fn={d["Function"]} dataToEqualsTxTo={(string?)d["ToAddress"] == t.ToAddress} dataFromEqualsSigner={(string?)d["FromAddress"] == t.FromAddress} amountPositive={((decimal?)d["Amount"] ?? 0) > 0}";
                    tally[key] = tally.TryGetValue(key, out var c) ? c + 1 : 1;
                }
                using var tw = new StreamWriter(Path.Combine(root, "new04-tally.txt"));
                foreach (var kv in tally.OrderByDescending(k => k.Value)) tw.WriteLine($"{kv.Value}  {kv.Key}");
                try { DbContext.CloseDB(); } catch { }
                return;
            }
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(list)) return;
            Globals.IsTestNet = Environment.GetEnvironmentVariable("SCAN_NET") == "testnet";
            Globals.CustomPath = root;
            DbContext.Initialize();
            using var w = new StreamWriter(Path.Combine(root, "dump.txt"));
            foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split(':');
                var h = long.Parse(parts[0]);
                var block = BlockchainData.GetBlocks().Query().Where(b => b.Height == h).First();
                var tx = block.Transactions.FirstOrDefault(t => t.Hash == parts[1]);
                w.WriteLine($"=== height={h} blockTxs={block.Transactions.Count}");
                w.WriteLine(JsonConvert.SerializeObject(tx, Formatting.Indented));
            }
            try { DbContext.CloseDB(); } catch { }
        }
    }
}
