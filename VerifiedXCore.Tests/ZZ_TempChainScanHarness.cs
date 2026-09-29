// TEMPORARY, NOT COMMITTED: runs the committed AuditReplayScanService over a COPY of a node's databases.
using System;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;
using Xunit.Abstractions;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempChainScanHarness
    {
        private readonly ITestOutputHelper _out;
        public ZZ_TempChainScanHarness(ITestOutputHelper o) { _out = o; }

        [Fact]
        public void Scan()
        {
            var root = Environment.GetEnvironmentVariable("SCAN_ROOT");
            var net = Environment.GetEnvironmentVariable("SCAN_NET");
            if (string.IsNullOrEmpty(root)) return;
            Globals.IsTestNet = net == "testnet";
            Globals.AddressPrefix = Globals.IsTestNet ? (byte)0x89 : (byte)0x3C; // signature checks derive addresses with it
            Globals.CustomPath = root;
            DbContext.Initialize();
            var tip = BlockchainData.GetBlocks().Max(b => b.Height);
            Globals.LastBlock = new Block { Height = tip };
            var path = AuditReplayScanService.RunAndWriteReport();
            File.AppendAllText(Path.Combine(root, "scan-result-path.txt"), path + Environment.NewLine);
            try { DbContext.CloseDB(); } catch { }
        }
    }
}
