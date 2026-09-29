// TEMPORARY, NOT COMMITTED: compares the account state a replay produced with the owner's node state (both opened
// read-only, never disposed). Chain state only (rsrvastatetrei.db, rsrvscstatetrei.db); no wallet files.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    public class ZZ_TempStateCompare
    {
        [Fact]
        public void Compare()
        {
            var a = Environment.GetEnvironmentVariable("CMP_SRC");     // owner's DBs folder
            var b = Environment.GetEnvironmentVariable("CMP_REPLAY");  // replay's database folder
            var outFile = Environment.GetEnvironmentVariable("CMP_OUT");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || string.IsNullOrEmpty(outFile)) return;
            LiteDatabase Open(string dir, string file) => new LiteDatabase(new ConnectionString { Filename = Path.Combine(dir, file), Connection = ConnectionType.Direct, ReadOnly = true });
            using var w = new StreamWriter(outFile);

            var srcAll = Open(a, "rsrvastatetrei.db").GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI).FindAll().ToList();
            var repAll = Open(b, "rsrvastatetrei.db").GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI).FindAll().ToList();
            w.WriteLine($"account rows: node={srcAll.Count} replay={repAll.Count}; duplicate keys: node={srcAll.Count - srcAll.Select(x => x.Key).Distinct().Count()} replay={repAll.Count - repAll.Select(x => x.Key).Distinct().Count()}");
            var srcAcct = srcAll.GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var repAcct = repAll.GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            w.WriteLine($"accounts: node={srcAcct.Count} replay={repAcct.Count}");
            int diffs = 0;
            foreach (var key in srcAcct.Keys.Union(repAcct.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                srcAcct.TryGetValue(key, out var x); repAcct.TryGetValue(key, out var y);
                string Tok(AccountStateTrei? s) => s?.TokenAccounts == null ? "" : string.Join(",", s.TokenAccounts.OrderBy(t => t.SmartContractUID).Select(t => $"{t.SmartContractUID}={t.Balance}/{t.LockedBalance}"));
                var sx = x == null ? "missing" : $"bal={x.Balance} locked={x.LockedBalance} nonce={x.Nonce} tok=[{Tok(x)}]";
                var sy = y == null ? "missing" : $"bal={y.Balance} locked={y.LockedBalance} nonce={y.Nonce} tok=[{Tok(y)}]";
                if (x == null || y == null || x.Balance != y.Balance || x.LockedBalance != y.LockedBalance || x.Nonce != y.Nonce || Tok(x) != Tok(y))
                {
                    diffs++;
                    if (diffs <= 200) w.WriteLine($"DIFF {key}\n   node:   {sx}\n   replay: {sy}");
                }
            }
            w.WriteLine($"account differences: {diffs}");

            var srcSc = Open(a, "rsrvscstatetrei.db").GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).FindAll().GroupBy(x => x.SmartContractUID, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var repSc = Open(b, "rsrvscstatetrei.db").GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).FindAll().GroupBy(x => x.SmartContractUID, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            w.WriteLine($"contracts: node={srcSc.Count} replay={repSc.Count}");
            int scDiffs = 0;
            foreach (var uid in srcSc.Keys.Union(repSc.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                srcSc.TryGetValue(uid, out var x); repSc.TryGetValue(uid, out var y);
                string S(SmartContractStateTrei? s) => s == null ? "missing" : $"owner={s.OwnerAddress} minter={s.MinterAddress} locked={s.IsLocked} next={s.NextOwner} supply={s.TokenDetails?.CurrentSupply} dataLen={s.ContractData?.Length}";
                if (S(x) != S(y)) { scDiffs++; if (scDiffs <= 100) w.WriteLine($"SCDIFF {uid}\n   node:   {S(x)}\n   replay: {S(y)}"); }
            }
            w.WriteLine($"contract differences: {scDiffs}");
        }
    }
}
