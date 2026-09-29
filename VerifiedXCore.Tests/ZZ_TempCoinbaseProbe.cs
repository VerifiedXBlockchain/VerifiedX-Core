// TEMPORARY, NOT COMMITTED: prints coinbase transactions at given heights from the owner's DBs (read-only, never disposed).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    public class ZZ_TempCoinbaseProbe
    {
        [Fact]
        public void Probe()
        {
            var src = Environment.GetEnvironmentVariable("PROBE_SRC");
            var outFile = Environment.GetEnvironmentVariable("PROBE_OUT");
            var heights = Environment.GetEnvironmentVariable("PROBE_HEIGHTS");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(outFile) || string.IsNullOrEmpty(heights)) return;
            if (heights == "v1count")
            {
                // NEW-28: classify every contract in the chain state (read-only, never disposed).
                var scdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvscstatetrei.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                using var w1 = new StreamWriter(outFile);
                int total = 0, v1 = 0, v2 = 0, both = 0, v1WithLedger = 0;
                foreach (var sc in scdb.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).FindAll())
                {
                    total++;
                    var isV1 = VerifiedXCore.Bitcoin.Services.VBTCService.IsVbtcV1Contract(sc);
                    var isV2 = VerifiedXCore.Bitcoin.Services.VBTCService.IsVbtcV2Contract(sc);
                    if (isV1) { v1++; if (sc.SCStateTreiTokenizationTXes?.Count > 0) v1WithLedger++; w1.WriteLine($"V1 {sc.SmartContractUID} owner={sc.OwnerAddress} ledgerRows={sc.SCStateTreiTokenizationTXes?.Count ?? 0}"); }
                    if (isV2) { v2++; w1.WriteLine($"V2 {sc.SmartContractUID} owner={sc.OwnerAddress}"); }
                    if (isV1 && isV2) both++;
                }
                w1.WriteLine($"contracts={total} v1={v1} v1WithLedgerRows={v1WithLedger} v2={v2} classifiedBoth={both}");
                return;
            }
            if (heights == "twdb")
            {
                // chain-derived tokenized-withdrawal records (read-only, never disposed)
                var twdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvtokenizedwithdrawals.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                using var w0 = new StreamWriter(outFile);
                foreach (var d in twdb.GetCollection(DbContext.RSRV_TOKENIZED_WITHDRAWALS).FindAll())
                    w0.WriteLine($"_id={d["_id"]} req={d["RequestorAddress"]} sc={d["SmartContractUID"]} uid={d["OriginalUniqueId"]} amt={d["Amount"]} done={d["IsCompleted"]} type={d["WithdrawalRequestType"]} btc={d["TransactionHash"]}");
                return;
            }
            var mapper = (BsonMapper)typeof(DbContext).GetMethod("CreateDbMapper", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            var db = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvblkdata.db"), Connection = ConnectionType.Direct, ReadOnly = true }, mapper);
            var col = db.GetCollection<Block>(DbContext.RSRV_BLOCKS);
            var raw = db.GetCollection(DbContext.RSRV_BLOCKS);
            using var w = new StreamWriter(outFile);
            if (heights == "twscan")
            {
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (t.TransactionType != TransactionType.TKNZ_WD_ARB && t.TransactionType != TransactionType.TKNZ_WD_OWNER) continue;
                        try
                        {
                            var j = Newtonsoft.Json.Linq.JObject.Parse(t.Data ?? "{}");
                            var tw = j["TokenizedWithdrawal"];
                            w.WriteLine(t.TransactionType == TransactionType.TKNZ_WD_ARB
                                ? $"h={b.Height} ARB from={t.FromAddress} req={tw?["RequestorAddress"]} sc={j["ContractUID"]} uid={tw?["OriginalUniqueId"]} amt={tw?["Amount"]} tx={t.Hash}"
                                : $"h={b.Height} OWNER from={t.FromAddress} sc={j["ContractUID"]} uid={j["UniqueId"]} btc={j["TransactionHash"]} tx={t.Hash}");
                        }
                        catch (Exception ex) { w.WriteLine($"h={b.Height} {t.TransactionType} unparsable {ex.Message}"); }
                    }
                w.WriteLine("done");
                return;
            }
            if (heights.StartsWith("range:"))
            {
                var r = heights.Split(':'); long lo = long.Parse(r[1]), hi = long.Parse(r[2]);
                foreach (var b in col.Query().Where(x => x.Height >= lo && x.Height <= hi).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                        if (t.FromAddress != "Coinbase_BlkRwd" && t.FromAddress != "Coinbase_TrxFees")
                            w.WriteLine($"h={b.Height} type={t.TransactionType} from={t.FromAddress} to={t.ToAddress} amt={t.Amount} data={(t.Data ?? "").Substring(0, Math.Min(120, (t.Data ?? "").Length))}");
                return;
            }
            if (heights.StartsWith("ownercheck:"))
            {
                // Owner balance of a vBTC V2 contract from the node's own state and withdrawal tables (read-only, never disposed):
                // the pre-fix formula and the fixed one (this build's NegativeNonOwnerPositions), plus every holder position.
                var uid = heights.Substring(11);
                var scdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvscstatetrei.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                var sc = scdb.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).FindOne(x => x.SmartContractUID == uid);
                var rows = sc.SCStateTreiTokenizationTXes ?? new System.Collections.Generic.List<SmartContractStateTreiTokenizationTX>();
                w.WriteLine($"owner={sc.OwnerAddress} rows={rows.Count}");
                foreach (var r in rows) w.WriteLine($"  row from={r.FromAddress} to={r.ToAddress} amt={r.Amount}");
                var wdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvvbtcwithdrawalrequests.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                decimal completedAll = 0M;
                foreach (var d in wdb.GetCollection(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS).Find(LiteDB.Query.EQ("SmartContractUID", uid)))
                {
                    w.WriteLine($"  withdrawal requestor={d["RequestorAddress"].AsString} amt={d["Amount"].AsDecimal} status={d["Status"]} completed={d["IsCompleted"]}");
                    if (d["IsCompleted"].AsBoolean && d["Status"].AsString == "Completed") completedAll += d["Amount"].AsDecimal;
                }
                var ownerRows = rows.Where(r => r.FromAddress == sc.OwnerAddress || r.ToAddress == sc.OwnerAddress).Sum(r => r.Amount);
                var negative = VerifiedXCore.Bitcoin.Services.VBTCService.NegativeNonOwnerPositions(sc, sc.OwnerAddress);
                w.WriteLine($"owner rows={ownerRows} completedAddBack={completedAll} negativeOtherPositions={negative}");
                w.WriteLine($"owner ledger component: BEFORE fix={ownerRows + completedAll}  AFTER fix={ownerRows + completedAll + negative}");
                foreach (var a in rows.SelectMany(r => new[] { r.FromAddress, r.ToAddress }).Where(a => a != "+" && a != "-" && a != sc.OwnerAddress).Distinct())
                    w.WriteLine($"  holder {a} position={rows.Where(r => r.FromAddress == a || r.ToAddress == a).Sum(r => r.Amount)}");
                return;
            }
            if (heights == "exowner")
            {
                // vBTC V2 vaults whose owner-at-the-time withdrew and whose ownership later changed (read-only).
                var net = Environment.GetEnvironmentVariable("PROBE_NET") ?? "testnet";
                Globals.IsTestNet = net == "testnet"; if (Globals.IsTestNet) Globals.AddressPrefix = 0x89;
                var owner = new System.Collections.Generic.Dictionary<string, string>();
                var events = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();
                var ownerWithdrawals = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(long H, string Who, decimal Amt)>>();
                var transfersAfter = new System.Collections.Generic.HashSet<string>();
                long maxH = 0;
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                {
                    maxH = b.Height;
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (string.IsNullOrEmpty(t.Data)) continue;
                        if (t.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE)
                        {
                            var pl = VerifiedXCore.Services.SmartContractDeployBinding.ReadPayload(t.Data);
                            if (pl.ContractUID != null) owner[pl.ContractUID] = t.FromAddress;
                            continue;
                        }
                        Newtonsoft.Json.Linq.JToken root; try { root = Newtonsoft.Json.Linq.JToken.Parse(t.Data); } catch { continue; }
                        var o = (root is Newtonsoft.Json.Linq.JArray a && a.Count > 0 ? a[0] : root) as Newtonsoft.Json.Linq.JObject;
                        if (o == null) continue;
                        var fn = (string?)o["Function"] ?? ""; var uid = (string?)o["ContractUID"];
                        if (fn == "Transfer()" && uid != null && owner.ContainsKey(uid))
                        {
                            if (ownerWithdrawals.ContainsKey(uid)) transfersAfter.Add(uid);
                            owner[uid] = t.ToAddress;
                        }
                        if (t.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_REQUEST)
                        {
                            var list = new System.Collections.Generic.List<(string Uid, decimal Amt)>();
                            if (uid != null) list.Add((uid, (decimal?)o["Amount"] ?? 0M));
                            if (o["Inputs"] is Newtonsoft.Json.Linq.JArray ins) foreach (var i in ins) list.Add(((string?)i["SCUID"] ?? "", (decimal?)i["Amount"] ?? 0M));
                            foreach (var (u, amt) in list)
                                if (owner.TryGetValue(u, out var ow) && ow == t.FromAddress)
                                {
                                    if (!ownerWithdrawals.TryGetValue(u, out var l)) ownerWithdrawals[u] = l = new();
                                    l.Add((b.Height, t.FromAddress, amt));
                                }
                        }
                    }
                }
                w.WriteLine($"{net}: tip {maxH}, vaults {owner.Count}, vaults with owner withdrawals {ownerWithdrawals.Count}, of which ownership changed afterwards {transfersAfter.Count}");
                foreach (var u in transfersAfter)
                    w.WriteLine($"  {u} currentOwner={owner[u]} ownerEraWithdrawals: " + string.Join("; ", ownerWithdrawals[u].Select(x => $"h={x.H} by={x.Who} amt={x.Amt}")));
                return;
            }
            if (heights.StartsWith("contract:"))
            {
                // A vBTC contract's history (testnet copy, read-only): state record, ledger rows, withdrawal rows, and every
                // block transaction that names it.
                var uid = heights.Substring(9);
                Globals.IsTestNet = Environment.GetEnvironmentVariable("PROBE_NET") != "mainnet"; if (Globals.IsTestNet) Globals.AddressPrefix = 0x89;
                var scdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvscstatetrei.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                var sc = scdb.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).FindOne(x => x.SmartContractUID == uid);
                w.WriteLine($"state: owner={sc?.OwnerAddress} minter={sc?.MinterAddress} rows={sc?.SCStateTreiTokenizationTXes?.Count}");
                foreach (var r in sc?.SCStateTreiTokenizationTXes ?? new System.Collections.Generic.List<SmartContractStateTreiTokenizationTX>())
                    w.WriteLine($"  row from={r.FromAddress} to={r.ToAddress} amt={r.Amount}");
                var wdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvvbtcwithdrawalrequests.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                foreach (var d in wdb.GetCollection(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS).Find(LiteDB.Query.EQ("SmartContractUID", uid)))
                    w.WriteLine($"  withdrawal requestor={d["RequestorAddress"]} amt={d["Amount"]} status={d["Status"]} completed={d["IsCompleted"]} reqHeight={d["RequestBlockHeight"]} tx={d["TransactionHash"]}");
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                        if (t.Data != null && t.Data.Contains(uid))
                        {
                            string fn = "";
                            try { var j = Newtonsoft.Json.Linq.JToken.Parse(t.Data); var o = j is Newtonsoft.Json.Linq.JArray a ? a[0] : j; fn = (string?)o["Function"] ?? ""; } catch { }
                            var d = t.Data.Length > 220 ? t.Data.Substring(0, 220) : t.Data;
                            if (fn == "Mint()" || fn == "Transfer()" || fn == "Update()") d = "(body)";
                            w.WriteLine($"  h={b.Height} {t.TransactionType} {fn} from={t.FromAddress} to={t.ToAddress} amt={t.Amount} data={d}");
                        }
                return;
            }
            if (heights.StartsWith("basefor:"))
            {
                // Base address of an account, derived from the public key in one of its signed transactions (testnet).
                var who = heights.Substring(8);
                Globals.IsTestNet = true; Globals.AddressPrefix = 0x89;
                foreach (var b in col.Query().OrderByDescending(x => x.Height).ToEnumerable())
                {
                    var t = b.Transactions?.FirstOrDefault(x => x.FromAddress == who && x.Signature != null && x.Signature.Contains('.'));
                    if (t == null) continue;
                    var pubHex = VerifiedXCore.Utilities.HexByteUtility.ByteToHex(VerifiedXCore.Utilities.Base58Utility.Base58Decode(t.Signature.Split('.', 2)[1]));
                    if (pubHex.Length / 2 == 63) pubHex = "00" + pubHex;
                    var pk = VerifiedXCore.EllipticCurve.PublicKey.fromString(VerifiedXCore.Utilities.HexByteUtility.HexToByte(pubHex));
                    var full = "04" + VerifiedXCore.Utilities.HexByteUtility.ByteToHex(pk.toString());
                    var derivedVfx = VerifiedXCore.Data.AccountData.GetHumanAddress(full);
                    var baseAddr = Nethereum.Util.AddressUtil.Current.ConvertToChecksumAddress(VerifiedXCore.Bitcoin.Services.ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(full));
                    w.WriteLine($"tx h={b.Height} {t.Hash} vfxFromKey={derivedVfx} matches={derivedVfx == who} base={baseAddr}");
                    return;
                }
                w.WriteLine("no signed tx found");
                return;
            }
            if (heights.StartsWith("reservedb:"))
            {
                // reserve transaction rows in a (scratch replay) database folder, read-only; rows touching the given addresses
                var addrs = heights.Substring(10).Split(',');
                var rdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvreserve.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                var rows = rdb.GetCollection<VerifiedXCore.Models.ReserveTransactions>(DbContext.RSRV_RESERVE_TRANSACTIONS).FindAll().ToList();
                w.WriteLine("status counts: " + string.Join(" ", rows.GroupBy(r => r.ReserveTransactionStatus).Select(g => $"{g.Key}={g.Count()}")));
                foreach (var r in rows.Where(r => addrs.Contains(r.ToAddress) || addrs.Contains(r.FromAddress)).OrderBy(r => r.Height))
                    w.WriteLine($"h={r.Height} tx={r.Hash} {r.FromAddress}->{r.ToAddress} amt={r.Amount} confirmTs={r.ConfirmTimestamp} unlock={r.UnlockTime} status={r.ReserveTransactionStatus} type={r.TransactionType}");
                return;
            }
            if (heights.StartsWith("addrs:"))
            {
                // every transaction sending to or from the given addresses (data mentioning them too)
                var addrs = heights.Substring(6).Split(',');
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                        foreach (var a in addrs)
                            if (t.FromAddress == a || t.ToAddress == a || (t.Data != null && t.Data.Contains(a)))
                                w.WriteLine($"{a} h={b.Height} tx={t.Hash} type={t.TransactionType} from={t.FromAddress} to={t.ToAddress} amt={t.Amount} fee={t.Fee} unlock={t.UnlockTime} data={(t.Data ?? "").Substring(0, Math.Min(160, (t.Data ?? "").Length))}");
                w.WriteLine("done");
                return;
            }
            if (heights == "trlexport")
            {
                // Unique decoded contract sources (carried in blocks + stored) into PROBE_EXPORT_DIR, with an index.
                var net = Environment.GetEnvironmentVariable("PROBE_NET") ?? "mainnet";
                var dir = Environment.GetEnvironmentVariable("PROBE_EXPORT_DIR")!;
                Directory.CreateDirectory(dir);
                var seen = new System.Collections.Generic.HashSet<string>();
                using var idx = new StreamWriter(Path.Combine(dir, $"index-{net}.tsv"));
                idx.WriteLine("sha256\tnet\tsource\theight\ttx_or_uid\tfunction");
                void Export(string where, long height, string id, string fn, string body)
                {
                    string text;
                    try { text = System.Text.Encoding.Unicode.GetString(VerifiedXCore.Utilities.SmartContractUtility.Decompress(Convert.FromBase64String(body))); }
                    catch { return; }
                    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                    idx.WriteLine($"{sha}\t{net}\t{where}\t{height}\t{id}\t{fn}");
                    if (seen.Add(sha)) File.WriteAllText(Path.Combine(dir, sha.Substring(0, 16) + ".trlm"), text, new System.Text.UTF8Encoding(false));
                }
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (string.IsNullOrEmpty(t.Data) || !t.Data.Contains("\"Data\"")) continue;
                        Newtonsoft.Json.Linq.JToken root;
                        try { root = Newtonsoft.Json.Linq.JToken.Parse(t.Data); } catch { continue; }
                        var items = root is Newtonsoft.Json.Linq.JArray a ? a.ToList() : new System.Collections.Generic.List<Newtonsoft.Json.Linq.JToken> { root };
                        foreach (var it in items)
                            if (it is Newtonsoft.Json.Linq.JObject o && o["Data"] is Newtonsoft.Json.Linq.JValue v && v.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                Export("block", b.Height, t.Hash, (string?)o["Function"] ?? "?", (string)v!);
                    }
                var scdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvscstatetrei.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                foreach (var sc in scdb.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).FindAll())
                    if (!string.IsNullOrEmpty(sc.ContractData)) Export("stored", -1, sc.SmartContractUID, "", sc.ContractData);
                w.WriteLine($"{net}: unique sources={seen.Count}");
                return;
            }
            if (heights == "trlscan")
            {
                // Every contract body carried in a block, and every stored body: does it still parse with the loop/recursion
                // ban on, and does it use rand/input/dateProc/createSig, do/while/for, and how deep does it nest?
                var net = Environment.GetEnvironmentVariable("PROBE_NET") == "testnet";
                Globals.IsTestNet = net; if (net) Globals.AddressPrefix = 0x89;
                var tally = new System.Collections.Generic.Dictionary<string, int>();
                void T(string k, int n = 1) { tally.TryGetValue(k, out var c); tally[k] = c + n; }
                int maxDepth = 0; string maxDepthAt = "";
                void Check(string where, string body)
                {
                    string text;
                    try { text = System.Text.Encoding.Unicode.GetString(VerifiedXCore.Utilities.SmartContractUtility.Decompress(Convert.FromBase64String(body))); }
                    catch { T("undecodable"); return; }
                    T("bodies");
                    var tokens = global::Trillium.Syntax.SyntaxTree.ParseTokens(text).ToList();
                    int depth = 0, localMax = 0;
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        var k = tokens[i].Kind;
                        if (k == global::Trillium.Syntax.SyntaxKind.OpenParenthesisToken || k == global::Trillium.Syntax.SyntaxKind.OpenBraceToken) { depth++; localMax = Math.Max(localMax, depth); }
                        if (k == global::Trillium.Syntax.SyntaxKind.CloseParenthesisToken || k == global::Trillium.Syntax.SyntaxKind.CloseBraceToken) depth--;
                        if (k == global::Trillium.Syntax.SyntaxKind.DoKeyword) T("kw:do");
                        if (k == global::Trillium.Syntax.SyntaxKind.WhileKeyword) T("kw:while");
                        if (k == global::Trillium.Syntax.SyntaxKind.ForKeyword) T("kw:for");
                        if (k == global::Trillium.Syntax.SyntaxKind.IdentifierToken && i + 1 < tokens.Count && tokens[i + 1].Kind == global::Trillium.Syntax.SyntaxKind.OpenParenthesisToken)
                        {
                            var name = tokens[i].Text;
                            if (name == "rand" || name == "input" || name == "dateProc" || name == "createSig" || name == "send" || name == "print")
                            { T("call:" + name); if (name != "send" && name != "print" && name != "dateProc") w.WriteLine($"{name}() in {where}"); }
                        }
                    }
                    if (localMax > maxDepth) { maxDepth = localMax; maxDepthAt = where; }
                    var off = global::Trillium.Syntax.SyntaxTree.Parse(text, false).Diagnostics;
                    var on = global::Trillium.Syntax.SyntaxTree.Parse(text, true).Diagnostics;
                    if (off.Any()) T("parse-errors-already");
                    if (on.Length > off.Length)
                    {
                        T("REFUSED-BY-BAN");
                        w.WriteLine($"BAN {where}: " + string.Join(" | ", on.Select(d => d.Message).Except(off.Select(d => d.Message))));
                    }
                }
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (string.IsNullOrEmpty(t.Data) || !t.Data.Contains("\"Data\"")) continue;
                        Newtonsoft.Json.Linq.JToken root;
                        try { root = Newtonsoft.Json.Linq.JToken.Parse(t.Data); } catch { continue; }
                        var items = root is Newtonsoft.Json.Linq.JArray a ? a.ToList() : new System.Collections.Generic.List<Newtonsoft.Json.Linq.JToken> { root };
                        foreach (var it in items)
                        {
                            if (it is not Newtonsoft.Json.Linq.JObject o) continue;
                            var d = o["Data"];
                            if (d == null || d.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                            T("fn:" + ((string?)o["Function"] ?? "?"));
                            Check($"h={b.Height} tx={t.Hash} fn={(string?)o["Function"]}", (string)d!);
                        }
                    }
                var scdb = new LiteDatabase(new ConnectionString { Filename = Path.Combine(src, "rsrvscstatetrei.db"), Connection = ConnectionType.Direct, ReadOnly = true });
                foreach (var sc in scdb.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).FindAll())
                    if (!string.IsNullOrEmpty(sc.ContractData)) { T("stored"); Check($"stored {sc.SmartContractUID}", sc.ContractData); }
                w.WriteLine($"maxNestingDepth={maxDepth} at {maxDepthAt}");
                w.WriteLine("tally: " + string.Join(" | ", tally.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                return;
            }
            if (heights == "bridgescan")
            {
                // testnet: every bridge unlock/exit transaction, its sender, and whether the sender is a seed caster
                Globals.IsTestNet = true; Globals.AddressPrefix = 0x89;
                var seeds = VerifiedXCore.Bitcoin.Services.BridgeCasterConsensus.SeedCommitteeForNetwork(true);
                w.WriteLine("seeds: " + string.Join(",", seeds));
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                        if ((int)t.TransactionType >= 37 && (int)t.TransactionType <= 42)
                            w.WriteLine($"h={b.Height} type={t.TransactionType} from={t.FromAddress} seed={seeds.Contains(t.FromAddress)} ts={t.Timestamp} hash={t.Hash} recomputes={t.GetHash() == t.Hash}");
                var last = col.Query().OrderByDescending(x => x.Height).FirstOrDefault();
                w.WriteLine($"tip={last?.Height} tipTime={last?.Timestamp}");
                return;
            }
            if (heights.StartsWith("json:"))
            {
                foreach (var item in heights.Substring(5).Split(','))
                {
                    var parts = item.Split(':'); var hh = long.Parse(parts[0]);
                    var b = col.Query().Where(x => x.Height == hh).FirstOrDefault();
                    var t = b?.Transactions?.FirstOrDefault(x => x.Hash == parts[1]);
                    w.WriteLine(t == null ? $"{item}: not found" : JsonConvert.SerializeObject(t));
                }
                return;
            }
            if (heights.StartsWith("tx:"))
            {
                // tx:height:hash,height:hash - print the transactions (Data truncated) and whether their content recomputes.
                foreach (var item in heights.Substring(3).Split(','))
                {
                    var parts = item.Split(':'); var hh = long.Parse(parts[0]);
                    var b = col.Query().Where(x => x.Height == hh).FirstOrDefault();
                    var t = b?.Transactions?.FirstOrDefault(x => x.Hash == parts[1]);
                    if (t == null) { w.WriteLine($"{item}: not found"); continue; }
                    var d = t.Data ?? "";
                    w.WriteLine($"== {hh} {t.Hash} type={t.TransactionType} from={t.FromAddress} to={t.ToAddress} amount={t.Amount} fee={t.Fee} recomputes={t.GetHash() == t.Hash}");
                    w.WriteLine("   data: " + (d.Length > 1500 ? d.Substring(0, 1500) + "...(" + d.Length + ")" : d));
                }
                return;
            }
            if (heights == "vaultchanges")
            {
                // Every Update()/Transfer()/Evolve()/Devolve()/ChangeEvolveStateSpecific() on a vBTC V2 vault (created with a
                // TokenizationV2 body): did the body it carried equal the vault's current body?
                string? Decode(string? b)
                {
                    if (string.IsNullOrEmpty(b) || b.Length < 40) return null;
                    try { return System.Text.Encoding.Unicode.GetString(VerifiedXCore.Extensions.GenericExtensions.ToDecompress(Convert.FromBase64String(b), 64 * 1024 * 1024)); } catch { return null; }
                }
                var current = new System.Collections.Generic.Dictionary<string, string?>(StringComparer.Ordinal); // vault UID -> current body (raw)
                var tally = new System.Collections.Generic.Dictionary<string, int>();
                void Count(string k) => tally[k] = tally.TryGetValue(k, out var c) ? c + 1 : 1;
                int shown = 0;
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (string.IsNullOrEmpty(t.Data) || !t.Data.Contains("Function")) continue;
                        string? fn = null, uid = null, body = null;
                        try { var pl = VerifiedXCore.Services.SmartContractDeployBinding.ReadPayload(t.Data); fn = pl.Function; uid = pl.ContractUID; body = pl.Data; } catch { }
                        if (fn == null || uid == null) continue;
                        if (fn == "Mint()")
                        {
                            var text = Decode(body);
                            if (text != null && text.Contains("GetFrostGroupPublicKey")) { current[uid] = body; Count("vault created"); }
                            continue;
                        }
                        if (!current.ContainsKey(uid)) continue;
                        if (fn != "Update()" && fn != "Transfer()" && fn != "Evolve()" && fn != "Devolve()" && fn != "ChangeEvolveStateSpecific()") continue;
                        var same = string.Equals(body, current[uid], StringComparison.Ordinal);
                        Count($"{fn} {(same ? "same body" : (string.IsNullOrEmpty(body) ? "EMPTY body" : "CHANGED body"))}");
                        if (!same && shown++ < 10)
                        {
                            var oldT = Decode(current[uid]); var newT = Decode(body);
                            string Field(string? src, string key) { if (src == null) return "?"; var i = src.IndexOf(key, StringComparison.Ordinal); return i < 0 ? "-" : src.Substring(i, Math.Min(120, src.Length - i)).Replace((char)10, (char)32).Replace((char)13, (char)32); }
                            w.WriteLine($"{fn} h={b.Height} tx={t.Hash} uid={uid} from={t.FromAddress} to={t.ToAddress} oldLen={current[uid]?.Length} newLen={body?.Length}");
                            w.WriteLine($"   old DepositAddress: {Field(oldT, "DepositAddress")}");
                            w.WriteLine($"   new DepositAddress: {Field(newT, "DepositAddress")}");
                        }
                        if (fn != "Transfer()" || !string.IsNullOrEmpty(body)) current[uid] = body; // apply writes the carried body
                    }
                w.WriteLine("tally: " + string.Join(" | ", tally.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                return;
            }
            if (heights == "bodies")
            {
                // Every contract body carried by a transaction: does its source call rand / input / dateProc?
                long bodies = 0, undecodable = 0; var found = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                    foreach (var t in b.Transactions ?? new System.Collections.Generic.List<Transaction>())
                    {
                        if (string.IsNullOrEmpty(t.Data) || !t.Data.Contains("Data")) continue;
                        string? body = null; string? fn = null;
                        try { var pl = VerifiedXCore.Services.SmartContractDeployBinding.ReadPayload(t.Data); body = pl.Data; fn = pl.Function; } catch { }
                        if (fn != null) found["fn:" + fn] = found.TryGetValue("fn:" + fn, out var fc) ? fc + 1 : 1;
                        if (string.IsNullOrEmpty(body) || body.Length < 40) continue;
                        string text;
                        try { text = System.Text.Encoding.Unicode.GetString(VerifiedXCore.Extensions.GenericExtensions.ToDecompress(Convert.FromBase64String(body), 64 * 1024 * 1024)); }
                        catch { undecodable++; continue; }
                        bodies++;
                        foreach (var tok in new[] { "rand(", "input(", "dateProc" })
                            if (text.Contains(tok))
                            {
                                found[tok] = found.TryGetValue(tok, out var c) ? c + 1 : 1;
                                if (found[tok] <= 5) w.WriteLine($"{tok} at h={b.Height} tx={t.Hash} type={t.TransactionType}");
                            }
                    }
                w.WriteLine($"bodies={bodies} undecodable={undecodable} " + string.Join(" ", found.Select(kv => $"{kv.Key}={kv.Value}")));
                return;
            }
            if (heights == "survey")
            {
                // Every coinbase: runs of heights whose content does not recompute to the stored hash, and any coinbase
                // with a non-zero Fee or Nonce, an UnlockTime, Data, a non-TX type, or a Coinbase_TrxFees sender.
                long runStart = -1, prev = -1, mism = 0, total = 0, blocks = 0; var odd = 0;
                foreach (var b in col.Query().OrderBy(x => x.Height).ToEnumerable())
                {
                    blocks++;
                    foreach (var t in (b.Transactions ?? new System.Collections.Generic.List<Transaction>()).Where(t => t.FromAddress == "Coinbase_TrxFees" || t.FromAddress == "Coinbase_BlkRwd"))
                    {
                        total++;
                        if (t.Fee != 0 || t.Nonce != 0 || t.UnlockTime != null || !string.IsNullOrEmpty(t.Data) || t.TransactionType != TransactionType.TX || t.FromAddress == "Coinbase_TrxFees")
                            if (odd++ < 50) w.WriteLine($"odd h={b.Height} from={t.FromAddress} fee={t.Fee} nonce={t.Nonce} unlock={t.UnlockTime} type={t.TransactionType} data={(t.Data ?? "").Length}");
                        if (t.GetHash() != t.Hash)
                        {
                            mism++;
                            if (runStart < 0) runStart = b.Height;
                            else if (b.Height != prev && b.Height != prev + 1) { w.WriteLine($"mismatch run {runStart}..{prev}"); runStart = b.Height; }
                            prev = b.Height;
                        }
                    }
                }
                if (runStart >= 0) w.WriteLine($"mismatch run {runStart}..{prev}");
                w.WriteLine($"survey done blocks={blocks} coinbases={total} mismatched={mism} odd={odd}");
                return;
            }
            foreach (var h in heights.Split(',').Select(long.Parse))
            {
                var b = col.Query().Where(x => x.Height == h).FirstOrDefault();
                if (b == null) { w.WriteLine($"{h}: missing"); continue; }
                w.WriteLine($"== block {h} version={b.Version} validator={b.Validator} txs={b.Transactions.Count}");
                foreach (var t in b.Transactions.Where(t => t.FromAddress == "Coinbase_TrxFees" || t.FromAddress == "Coinbase_BlkRwd"))
                {
                    w.WriteLine($"  stored hash {t.Hash}");
                    w.WriteLine($"  GetHash()   {t.GetHash()}");
                    w.WriteLine($"  preimage    {t.GetHashPreimage()}");
                    w.WriteLine($"  json        {JsonConvert.SerializeObject(t)}");
                }
                var rawBlock = raw.Query().Where("$.Height = @0", h).FirstOrDefault();
                if (rawBlock != null)
                    foreach (var tv in rawBlock["Transactions"].AsArray.Where(x => x["FromAddress"].AsString.StartsWith("Coinbase")))
                        w.WriteLine($"  bson        Amount={tv["Amount"]} ({tv["Amount"].Type}) Fee={tv["Fee"]} ({tv["Fee"].Type}) Timestamp={tv["Timestamp"]} Nonce={tv["Nonce"]} Height={tv["Height"]}");
            }
        }
    }
}
