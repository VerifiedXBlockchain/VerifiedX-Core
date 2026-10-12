using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NBitcoin;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Data;
using Xunit;
using Xunit.Abstractions;
using BtcTx = NBitcoin.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 18: size (docs/PLAN-vBTC-Bitcoin-Attestation.md, test 11 of section 11). What a
    /// validator does for every Bitcoin block, and what every node does to judge and apply the report, measured against
    /// a state with very many vaults and coins.
    ///
    /// The numbers are a measurement, not a rule, so the test runs only when asked: set VFX_BTC_SCALE to the number of
    /// vaults (1000000 for the plan's figure; there are five coins per vault). Without it the test returns at once.
    /// The bounds it asserts are loose on purpose: they catch a pass that has stopped using its indexes, not a slow disk.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest18_ScaleTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private readonly ITestOutputHelper _out;
        private readonly Btc _btc = new(Checkpoint - 11);

        public BtcAttest18_ScaleTests(ITestOutputHelper output) : base(realMainnetRules: false)
        {
            _out = output;
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
        }

        private static long Tip => Globals.LastBlock.Height;
        private static string Hex(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        private static string ScriptOfVault(int i) => "5120" + Hex("vault-script-" + i);   // a Taproot output script
        private static string UidOfVault(int i) => $"{Hex("vault-" + i)[..32]}:1";
        private static string OutpointOfCoin(int i) => Hex("vault-coin-" + i) + ":0";

        private T Timed<T>(string what, Func<T> work)
        {
            var clock = Stopwatch.StartNew();
            var result = work();
            _out.WriteLine($"{what}: {clock.Elapsed.TotalMilliseconds:N0} ms");
            return result;
        }

        [Fact]
        public async Task TheBlockPass_AgainstVeryManyVaultsAndCoins()
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("VFX_BTC_SCALE"), out var vaults) || vaults <= 0)
                return; // a measurement, run on request
            var coinCount = vaults * 5;
            await Start();
            var db = DbContext.DB_BtcAttest;
            var stamp = H1 + 1;

            // ── The state: every vault's script, five coins each, and its total ─────────────────────────────
            // Written in batches with a checkpoint after each: one transaction for millions of rows makes the
            // database's log file enormous. (The node itself writes a block's worth at a time.)
            void Load<T>(string what, string collection, int count, Func<int, T> row)
            {
                var clock = Stopwatch.StartNew();
                var table = db.GetCollection<T>(collection);
                const int batch = 50_000;
                for (var from = 0; from < count; from += batch)
                {
                    table.InsertBulk(Enumerable.Range(from, Math.Min(batch, count - from)).Select(row), batch);
                    db.Checkpoint();
                }
                _out.WriteLine($"{count:N0} {what} written: {clock.Elapsed.TotalSeconds:N0} s");
            }
            Load("vault scripts", BtcAttestStore.Scripts, vaults, i => new VaultDepositScriptRow
            {
                ScriptHex = ScriptOfVault(i), SmartContractUID = UidOfVault(i), DepositAddress = "bc1p" + i, CreatedAtVfxHeight = stamp,
                WatchFromBitcoinHeight = Checkpoint + 1, LastModifiedHeight = stamp,
            });
            Load("coins", BtcAttestStore.Coins, coinCount, i => new VaultCoinRow
            {
                Outpoint = OutpointOfCoin(i), SmartContractUID = UidOfVault(i % vaults), ValueSats = 100_000 + i % 1_000, Status = VaultCoinStatus.Unspent,
                CreatedInHeight = Checkpoint, FreeOf = UidOfVault(i % vaults), LastModifiedHeight = stamp,   // below the block reported here
            });
            Load("totals", BtcAttestStore.Totals, vaults, i => new VaultDepositTotalRow { SmartContractUID = UidOfVault(i), UnspentSats = 500_000 + 5 * (i % 1_000), LastModifiedHeight = stamp });
            db.Checkpoint();
            var file = new FileInfo(Path.Combine(Globals.CustomPath!, "Databases", "rsrvbtcattest.db"));
            if (!file.Exists) file = Directory.GetFiles(Globals.CustomPath!, "rsrvbtcattest.db", SearchOption.AllDirectories).Select(f => new FileInfo(f)).First();
            _out.WriteLine($"attestation database: {file.Length / (1024.0 * 1024.0):N0} MB on disk");

            // ── A full Bitcoin block: 4,500 transactions of two inputs and two outputs. 50 spend a vault coin, 50 pay
            //    a vault; the other 4,400 have nothing to do with any vault (and every one of their inputs and outputs
            //    still has to be looked up to know that). ───────────────────────────────────────────────────
            var random = new Random(7);
            NBitcoin.Script Plain() => new Key().PubKey.WitHash.ScriptPubKey;
            OutPoint Nowhere() { var b = new byte[32]; random.NextBytes(b); return new OutPoint(new uint256(b), (uint)random.Next(3)); }
            var txs = new List<BtcTx>();
            for (var i = 0; i < 4_500; i++)
            {
                var tx = Network.Main.CreateTransaction();
                var vault = random.Next(vaults);
                if (i % 90 == 0)
                {
                    var coin = OutpointOfCoin(vault);     // the first coin of a vault
                    tx.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(coin[..64]), 0)));
                }
                else tx.Inputs.Add(new TxIn(Nowhere()));
                tx.Inputs.Add(new TxIn(Nowhere()));
                tx.Outputs.Add(Money.Satoshis(50_000), i % 90 == 45 ? NBitcoin.Script.FromHex(ScriptOfVault(vault)) : Plain());
                tx.Outputs.Add(Money.Satoshis(40_000), Plain());
                txs.Add(tx);
            }
            var at = _btc.Mine(txs.ToArray());
            _btc.MineEmpty(BtcAttestRules.Depth);
            var block = _btc.Block(at);
            _out.WriteLine($"block: {block.Transactions.Count:N0} transactions, {block.WithOptions(TransactionOptions.None).ToBytes().Length / 1024.0:N0} KB without witnesses");
            var headers = _btc.HeaderHexes(BtcAttestStore.GetMeta()!.TipHeight + 1, at + BtcAttestRules.Depth);

            // ── What each validator does for a Bitcoin block ────────────────────────────────────────────────
            var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
            var scan = Stopwatch.StartNew();
            var parts = BtcBlockScanner.BuildForward(block, at, headers, 0);
            scan.Stop();
            _out.WriteLine($"scanner pass (build the report): {scan.Elapsed.TotalMilliseconds:N0} ms, {parts.Count} part(s), {parts.Sum(p => p.Events.Count)} event(s), {parts.Sum(p => p.ToCanonicalJson().Length) / 1024.0:N0} KB of report");
            Assert.Equal(100, parts.Sum(p => p.Events.Count));

            // ── What every node does with the report ────────────────────────────────────────────────────────
            var judge = new Stopwatch();
            var apply = new Stopwatch();
            foreach (var part in parts)
            {
                var tx = Signed(part);
                judge.Start();
                var (ok, why) = await Verify(tx, Tip + 1);
                judge.Stop();
                Assert.True(ok, why);
                apply.Start();
                await Apply(Tip + 1, tx);
                apply.Stop();
            }
            _out.WriteLine($"judge the report (VerifyTX, block validation): {judge.Elapsed.TotalMilliseconds:N0} ms");
            _out.WriteLine($"apply the report (UpdateTreis): {apply.Elapsed.TotalMilliseconds:N0} ms");
            Assert.Equal(at, BtcAttestStore.GetMeta()!.EventsThroughHeight);

            // ── What runs all the time ──────────────────────────────────────────────────────────────────────
            var one = UidOfVault(vaults / 2);
            Timed("one vault's attested deposit (the owner's balance term)", () => BtcAttestBalance.DepositBtc(one));
            var destination = BitcoinAddress.Create("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", Network.Main).ScriptPubKey.ToHex();
            var built = Timed("the canonical withdrawal transaction for one vault", () => BtcCanonicalWithdrawal.Build(one, destination, 150_000, 10, out _));
            Assert.NotNull(built);
            Timed("is a vault watched (script row by vault)", () => BtcAttestBalance.IsAttestedVault(one));
            Timed("did the layer write above a height (rollback guard)", () => BtcAttestStore.ChangedAbove(Tip - 1));
            Timed("coins touched above a Bitcoin height (rewind)", () => BtcAttestStore.CoinsTouchedAbove(at - 1).Count);
            var planner = new BtcReportPlanner();
            var view = new ChainView(_btc);
            Timed("the planner, first pass after start (sweeps back over reported blocks)", () => planner.Plan(view, Tip).Reports.Count);
            Timed("the planner, next pass (nothing new)", () => planner.Plan(view, Tip).Reports.Count);

            var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
            _out.WriteLine($"managed memory held after the pass: {memoryAfter / (1024.0 * 1024.0):N0} MB (before it: {memoryBefore / (1024.0 * 1024.0):N0} MB); process working set {Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0):N0} MB");

            // Loose bounds: a pass that scans tables instead of using their indexes takes minutes at this size.
            Assert.True(scan.Elapsed < TimeSpan.FromSeconds(60), $"the scanner pass took {scan.Elapsed}");
            Assert.True(judge.Elapsed + apply.Elapsed < TimeSpan.FromSeconds(60), $"judging and applying took {judge.Elapsed + apply.Elapsed}");
        }

        private sealed class ChainView : VerifiedXCore.Bitcoin.Attestation.Net.IBtcChainView
        {
            private readonly Btc _chain;
            public ChainView(Btc chain) { _chain = chain; }
            public long TipHeight => _chain.Tip;
            public BtcHeader? HeaderAt(long height) => height >= Checkpoint - 11 && height <= TipHeight ? _chain.Header(height) : null;
            public NBitcoin.Block? BlockAt(long height) => height >= Checkpoint - 11 && height <= TipHeight ? _chain.Block(height) : null;
        }
    }
}
