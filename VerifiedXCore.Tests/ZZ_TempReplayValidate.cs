// TEMPORARY, NOT COMMITTED: offline re-validation. Feeds every block of the owner's chain (read-only, from REPLAY_SRC)
// in height order through the node's real BlockValidatorService.ValidateBlock - the call a syncing node makes - into a
// fresh, empty database in REPLAY_SCRATCH. Stops at the first rejected block and records why. Never opens wallet files
// from the source; the source block database is opened with LiteDB ReadOnly=true (read-only file access).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LiteDB;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    [Collection("DbContextSequential")]
    public class ZZ_TempReplayValidate
    {
        [Fact]
        public async Task Replay()
        {
            var src = Environment.GetEnvironmentVariable("REPLAY_SRC");
            var outFile = Environment.GetEnvironmentVariable("REPLAY_OUT");
            var scratch = Environment.GetEnvironmentVariable("REPLAY_SCRATCH");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(outFile) || string.IsNullOrEmpty(scratch)) return;
            var maxHeight = long.TryParse(Environment.GetEnvironmentVariable("REPLAY_MAX"), out var m) ? m : long.MaxValue;

            // Network settings exactly as Program.Main sets them.
            Globals.IsTestNet = Environment.GetEnvironmentVariable("REPLAY_NET") == "testnet";
            if (Globals.IsTestNet) Globals.AddressPrefix = 0x89;
            Globals.V4Height = Globals.IsTestNet ? 1 : 3_074_181;
            Globals.V2ValHeight = Globals.IsTestNet ? 0 : 3_074_180;
            Globals.SpecialBlockHeight = Globals.IsTestNet ? 2000 : 3_074_185;
            Globals.GenesisValidator = Globals.IsTestNet ? "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" : "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH";
            Globals.TXHeightRule5 = Globals.IsTestNet ? 746313 : Globals.TXHeightRule5;
            Globals.VbtcPrivacyDisableHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2WithdrawalExpiryFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2WithdrawalOwnerAddBackFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.VbtcLegacyTransferBypassFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2TransferMultiHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.BridgeBurnBindingHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.BridgeIntraBlockGuardHeight = Globals.IsTestNet ? 1 : 7_296_200L;
            Globals.WithdrawalEscrowHeight = Globals.IsTestNet ? 1 : 7_296_200L;
            Globals.MaxBlockSizeBytes = 10_485_760;
            if (Environment.GetEnvironmentVariable("REPLAY_BRIDGE_LEGACY") == "1")
            {
                // Harness-only: the pre-audit bridge submitter rule (b61d49f5) needs a caster committee for old heights,
                // which an offline replay (and a fresh sync) does not have; use the legacy bridge path to test the rest.
                Globals.BridgeBurnBindingHeight = long.MaxValue;
                Globals.BridgeIntraBlockGuardHeight = long.MaxValue;
            }
            StartupService.SetBlockchainChainRef();

            Directory.CreateDirectory(scratch);
            Globals.CustomPath = scratch;
            DbContext.Initialize(); // fresh, empty target
            StartupService.SetAdjudicatorAddresses(); // as node startup: historical V3 signer set (defaults) + retired signers
            await Globals.BlocksDownloadSlim.WaitAsync(); // as a syncing node: a block download is in progress (wall-clock checks skipped)
            // REPLAY_RESUME=1: continue on the state an earlier run left in REPLAY_SCRATCH (it holds the state up to its last
            // applied block); otherwise start from genesis on an empty scratch database.
            var resume = Environment.GetEnvironmentVariable("REPLAY_RESUME") == "1";
            Globals.LastBlock = resume ? (BlockchainData.GetLastBlock() ?? new Block { Height = -1 }) : new Block { Height = -1 };
            if (!resume && Globals.LastBlock.Height < 0 && BlockchainData.GetLastBlock() != null)
                throw new InvalidOperationException("REPLAY_SCRATCH already holds a chain; set REPLAY_RESUME=1 or use an empty folder.");
            var resumedFrom = Globals.LastBlock.Height;

            var mapper = (BsonMapper)typeof(DbContext).GetMethod("CreateDbMapper", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            string Stamp() { var i = new FileInfo(Path.Combine(src, "rsrvblkdata.db")); return $"{i.Length} {i.LastWriteTimeUtc:o}"; }
            using var w = new StreamWriter(outFile) { AutoFlush = true };
            w.WriteLine($"replay {(Globals.IsTestNet ? "testnet" : "mainnet")} from {src} (read-only) into {scratch}; bridgeLegacy={Environment.GetEnvironmentVariable("REPLAY_BRIDGE_LEGACY") == "1"}; started {DateTime.UtcNow:o}");
            w.WriteLine("source before: " + Stamp());
            w.WriteLine(resume ? $"resuming after block {resumedFrom}" : "starting from genesis");

            // Not disposed on purpose: LiteDB deletes an EMPTY -log.db file when it closes a database, even read-only.
            var source = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvblkdata.db"), Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            {
                var col = source.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                var sw = Stopwatch.StartNew();
                long applied = 0, skippedDup = 0, forcedTotal = 0;
                var forceBalance = Environment.GetEnvironmentVariable("REPLAY_FORCE_BALANCE") == "1";
                foreach (var block in col.Query().OrderBy(b => b.Height).ToEnumerable())
                {
                    if (block.Height > maxHeight) break;
                    if (block.Height <= resumedFrom) continue;                                   // applied by an earlier run
                    if (block.Height <= Globals.LastBlock.Height) { skippedDup++; continue; } // a second stored block at an applied height
                    if (block.Height != Globals.LastBlock.Height + 1)
                    {
                        w.WriteLine($"GAP: next stored height {block.Height} after {Globals.LastBlock.Height}; stopping");
                        break;
                    }
                    if (block.Height >= Globals.V3Height - 1) Signer.UpdateSigningAddresses(); // signer set for the current height
                    BlockDiagnostics.Clear();
                    bool ok;
                    try { ok = await BlockValidatorService.ValidateBlock(block, false, true, false, false, false, "offline-replay"); }
                    catch (Exception ex) { w.WriteLine($"EXCEPTION at {block.Height}: {ex}"); break; }
                    // REPLAY_FORCE_BALANCE=1 (owner, 26 Sep): a block refused only because a transaction's balance check fails is
                    // applied as the live chain applied it - the refused transaction is put on the operator whitelist (honoured
                    // in block validation only, content-bound) and the real ValidateBlock runs again. Logged as FORCED. Any
                    // other refusal still stops the replay.
                    var forced = new System.Collections.Generic.List<string>();
                    while (!ok && forceBalance && forced.Count < 20)
                    {
                        var last = BlockDiagnostics.RollbackSnapshot().LastOrDefault();
                        var bm = last == null ? null : System.Text.RegularExpressions.Regex.Match(last.Message ?? "", @"^Bad TX: ([0-9a-fA-F]+) \| Reason: (.*)$");
                        if (bm == null || !bm.Success || !(bm.Groups[2].Value.Contains("balance", StringComparison.OrdinalIgnoreCase) || bm.Groups[2].Value.Contains("Insufficient", StringComparison.OrdinalIgnoreCase))) break;
                        var badHash = bm.Groups[1].Value;
                        if (forced.Contains(badHash)) break;
                        forced.Add(badHash);
                        var badTx = block.Transactions?.FirstOrDefault(t => t.Hash == badHash);
                        w.WriteLine($"FORCED height={block.Height} tx={badHash} type={badTx?.TransactionType} from={badTx?.FromAddress} to={badTx?.ToAddress} amount={badTx?.Amount} fee={badTx?.Fee} reason={bm.Groups[2].Value}");
                        forcedTotal++;
                        Globals.BadTxList.Add(badHash);
                        try
                        {
                            BlockDiagnostics.Clear();
                            ok = await BlockValidatorService.ValidateBlock(block, false, true, false, false, false, "offline-replay-forced");
                        }
                        catch (Exception ex) { w.WriteLine($"EXCEPTION at {block.Height} (forced): {ex}"); ok = false; }
                    }
                    foreach (var h in forced) Globals.BadTxList.Remove(h);
                    // A private transaction's stored hash is not Transaction.GetHash(), so the whitelist cannot honour it. If
                    // the block is still refused only on balance, commit it as ValidateBlock does on success (chain + state,
                    // incl. the private ledger in UpdateTreis, + reserve pass). Logged as FORCED-APPLY.
                    if (!ok && forceBalance && forced.Count > 0)
                    {
                        var lastReason = BlockDiagnostics.RollbackSnapshot().LastOrDefault()?.Message ?? "";
                        if (lastReason.Contains("balance", StringComparison.OrdinalIgnoreCase) || lastReason.Contains("Insufficient", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await BlockchainData.AddBlock(block, false);
                                var stateApplied = await StateData.UpdateTreis(block);
                                await ReserveService.Run();
                                ok = Globals.LastBlock.Height == block.Height;
                                w.WriteLine($"FORCED-APPLY height={block.Height} stateApplied={stateApplied} ok={ok} (direct commit; {lastReason})");
                            }
                            catch (Exception ex) { w.WriteLine($"EXCEPTION at {block.Height} (forced apply): {ex}"); ok = false; }
                        }
                    }
                    if (!ok || Globals.LastBlock.Height != block.Height)
                    {
                        w.WriteLine($"REJECTED height={block.Height} hash={block.Hash} validator={block.Validator} txs={block.Transactions?.Count} ok={ok} lastBlock={Globals.LastBlock.Height}");
                        foreach (var r in BlockDiagnostics.RollbackSnapshot().TakeLast(10)) w.WriteLine("  rollback: " + Newtonsoft.Json.JsonConvert.SerializeObject(r));
                        foreach (var t in block.Transactions ?? new System.Collections.Generic.List<Transaction>())
                        {
                            (bool, string) v;
                            try { v = await TransactionValidatorService.VerifyTX(t, true, true, false, null, false, block.Height); } catch (Exception ex) { v = (false, "threw " + ex.Message); }
                            if (!v.Item1) w.WriteLine($"  tx {t.Hash} type={t.TransactionType} from={t.FromAddress} -> {v.Item2}");
                        }
                        var log = Directory.GetFiles(scratch, "*.txt", SearchOption.AllDirectories).Where(f => f.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
                        foreach (var f in log) foreach (var line in ReadTail(f, 25)) w.WriteLine("  errlog: " + line);
                        break;
                    }
                    applied++;
                    if (block.Height % 10_000 == 0)
                        w.WriteLine($"progress height={block.Height} applied={applied} elapsed={sw.Elapsed} rate={applied / Math.Max(1, sw.Elapsed.TotalSeconds):F0}/s");
                }
                w.WriteLine($"finished lastApplied={Globals.LastBlock.Height} applied={applied} duplicatesSkipped={skippedDup} forcedBalanceTxs={forcedTotal} elapsed={sw.Elapsed} at {DateTime.UtcNow:o}");
            }
            w.WriteLine("source after:  " + Stamp());
            try { DbContext.CloseDB(); } catch { }
        }

        private static string[] ReadTail(string path, int n)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var lines = sr.ReadToEnd().Split('\n');
                return lines.Skip(Math.Max(0, lines.Length - n)).ToArray();
            }
            catch { return Array.Empty<string>(); }
        }
    }
}
