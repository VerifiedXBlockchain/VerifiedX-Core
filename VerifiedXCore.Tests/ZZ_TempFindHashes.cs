// TEMPORARY, NOT COMMITTED: heights of given transaction hashes in a chain copy.
using System;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempFindHashes
    {
        [Fact]
        public void Find()
        {
            var root = Environment.GetEnvironmentVariable("SCAN_ROOT");
            var list = Environment.GetEnvironmentVariable("SCAN_HASHES");
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(list)) return;
            Globals.IsTestNet = Environment.GetEnvironmentVariable("SCAN_NET") == "testnet";
            Globals.CustomPath = root;
            DbContext.Initialize();
            var wanted = list.Split(',').ToHashSet();
            var blocks = BlockchainData.GetBlocks();
            using var w = new StreamWriter(Path.Combine(root, "found-hashes.txt"));
            long n = 0, tip = 0;
            foreach (var b in blocks.FindAll()) // one sequential pass (range queries rescanned the collection)
            {
                n++; if (b.Height > tip) tip = b.Height;
                if (n % 250000 == 0) { w.WriteLine($"progress blocks={n} lastHeight={b.Height}"); w.Flush(); }
                if (b.Transactions == null) continue;
                foreach (var t in b.Transactions)
                    if (wanted.Contains(t.Hash))
                    {
                        w.WriteLine($"{t.Hash} height={b.Height} type={t.TransactionType} from={t.FromAddress} to={t.ToAddress} amount={t.Amount} recomputedHashMatches={t.GetHash() == t.Hash}");
                        w.WriteLine("json " + Newtonsoft.Json.JsonConvert.SerializeObject(t));
                        w.Flush();
                    }
            }
            w.WriteLine($"done tip={tip}");
            try { DbContext.CloseDB(); } catch { }
        }
    }
}
