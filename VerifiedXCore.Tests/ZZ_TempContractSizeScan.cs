// TEMPORARY, NOT COMMITTED: largest decompressed smart-contract body on a COPY of a node's chain (VX-20 bound check).
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempContractSizeScan
    {
        [Fact]
        public void Scan()
        {
            var root = Environment.GetEnvironmentVariable("SCAN_ROOT");
            if (string.IsNullOrEmpty(root)) return;
            Globals.IsTestNet = Environment.GetEnvironmentVariable("SCAN_NET") == "testnet";
            Globals.CustomPath = root;
            DbContext.Initialize();
            var blocks = BlockchainData.GetBlocks();
            var tip = blocks.Max(b => b.Height);
            long bodies = 0, maxSize = 0, maxHeight = -1, undecodable = 0; string maxTx = "";
            for (long start = 0; start <= tip; start += 1000)
            {
                var end = Math.Min(tip, start + 999);
                foreach (var block in blocks.Query().Where(b => b.Height >= start && b.Height <= end).ToList())
                {
                    foreach (var tx in block.Transactions ?? Enumerable.Empty<Transaction>())
                    {
                        if (string.IsNullOrEmpty(tx.Data) || !tx.Data.Contains("Data")) continue;
                        var (_, _, body, _) = SmartContractDeployBinding.ReadPayload(tx.Data);
                        if (string.IsNullOrEmpty(body)) continue;
                        try
                        {
                            var bytes = Convert.FromBase64String(body);
                            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                            var buf = new byte[81920]; long total = 0; int n;
                            while ((n = gz.Read(buf, 0, buf.Length)) > 0) total += n;
                            bodies++;
                            if (total > maxSize) { maxSize = total; maxHeight = block.Height; maxTx = tx.Hash; }
                        }
                        catch { undecodable++; }
                    }
                }
            }
            File.WriteAllText(Path.Combine(root, "contract-size-scan.txt"),
                $"tip={tip} bodies={bodies} undecodable={undecodable} maxDecompressedBytes={maxSize} atHeight={maxHeight} tx={maxTx}{Environment.NewLine}");
            try { DbContext.CloseDB(); } catch { }
        }
    }
}
