using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
namespace ReserveBlockCore.Controllers
{
    [Route("explorer")]
    [ApiController]
    public class ExplorerController : ControllerBase
    {
        // ── HTML page ────────────────────────────────────────────────────────────
        [HttpGet("")]
        public ContentResult Index()
        {
            return Content(GetHtml(), "text/html; charset=utf-8");
        }

        // ── Stats ────────────────────────────────────────────────────────────────
        [HttpGet("api/stats")]
        public IActionResult GetStats()
        {
            var height = -1L;
            var peerCount = 0;
            var mempoolCount = 0;
            decimal supply = 0;

            try { height = Globals.LastBlock?.Height ?? -1; } catch { }
            try { peerCount = Globals.Nodes?.Count ?? 0; } catch { }
            try { mempoolCount = TransactionData.GetPool()?.Count() ?? 0; } catch { }
            try { supply = AccountStateTrei.GetNetworkTotal(); } catch { }

            return Ok(new
            {
                height,
                networkSupply = supply,
                peers = peerCount,
                mempool = mempoolCount,
                isTestNet = Globals.IsTestNet
            });
        }

        // ── Block list (last 100, no full tx bodies) ─────────────────────────────
        [HttpGet("api/blocks")]
        public IActionResult GetBlocks()
        {
            try
            {
                var lastBlock = Globals.LastBlock;
                if (lastBlock == null || lastBlock.Height < 0)
                    return Ok(Array.Empty<object>());

                var startHeight = Math.Max(0, lastBlock.Height - 99);
                var blocks = DbContext.DB.GetCollection<Block>(DbContext.RSRV_BLOCKS);

                var result = blocks.Query()
                    .Where(b => b.Height >= startHeight)
                    .OrderByDescending(b => b.Height)
                    .Limit(100)
                    .ToList()
                    .Select(b => new
                    {
                        b.Height,
                        b.Hash,
                        b.Timestamp,
                        b.Validator,
                        b.NumOfTx,
                        b.TotalReward,
                        b.TotalAmount,
                        b.Size,
                        b.BCraftTime,
                        b.Version
                    });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Block by height (with full transactions) ─────────────────────────────
        [HttpGet("api/block/{height:long}")]
        public IActionResult GetBlock(long height)
        {
            try
            {
                var block = BlockchainData.GetBlockByHeight(height);
                if (block == null) return NotFound(new { error = "Block not found" });
                return Ok(block);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Block by hash ────────────────────────────────────────────────────────
        [HttpGet("api/block/hash/{hash}")]
        public IActionResult GetBlockByHash(string hash)
        {
            try
            {
                var block = BlockchainData.GetBlockByHash(hash);
                if (block == null) return NotFound(new { error = "Block not found" });
                return Ok(block);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Transaction by hash ──────────────────────────────────────────────────
        [HttpGet("api/tx/{hash}")]
        public IActionResult GetTransaction(string hash)
        {
            try
            {
                // Search local wallet transactions first
                var tx = TransactionData.GetTxByHash(hash);
                if (tx != null) return Ok(tx);

                // Fall back: search recent blocks
                var lastBlock = Globals.LastBlock;
                if (lastBlock != null)
                {
                    var blocks = DbContext.DB.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                    var startHeight = Math.Max(0, lastBlock.Height - 500);
                    var recentBlocks = blocks.Query()
                        .Where(b => b.Height >= startHeight)
                        .OrderByDescending(b => b.Height)
                        .ToList();

                    foreach (var block in recentBlocks)
                    {
                        var found = block.Transactions?.FirstOrDefault(t => t.Hash == hash);
                        if (found != null) return Ok(found);
                    }
                }

                return NotFound(new { error = "Transaction not found" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Address info ─────────────────────────────────────────────────────────
        [HttpGet("api/address/{address}")]
        public IActionResult GetAddress(string address)
        {
            try
            {
                var aTrei = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
                var state = aTrei.FindOne(x => x.Key == address);

                var balance = state?.Balance ?? 0m;
                var lockedBalance = state?.LockedBalance ?? 0m;
                var nonce = state?.Nonce ?? 0;

                // Get transactions involving this address from wallet DB
                var txs = TransactionData.GetAll().Query()
                    .Where(t => t.FromAddress == address || t.ToAddress == address)
                    .OrderByDescending(t => t.Height)
                    .Limit(50)
                    .ToList();

                return Ok(new
                {
                    address,
                    balance,
                    lockedBalance,
                    nonce,
                    transactions = txs
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Mempool ──────────────────────────────────────────────────────────────
        [HttpGet("api/mempool")]
        public IActionResult GetMempoolTransactions()
        {
            try
            {
                var pool = TransactionData.GetMempool();
                if (pool == null || pool.Count == 0)
                    return Ok(Array.Empty<object>());

                var result = pool.Select(tx =>
                {
                    object? privacyInfo = null;
                    if (IsPrivacyTxType(tx.TransactionType) && !string.IsNullOrWhiteSpace(tx.Data))
                    {
                        privacyInfo = ParsePrivacyPayloadSummary(tx.Data);
                    }

                    return new
                    {
                        tx.Hash,
                        tx.FromAddress,
                        tx.ToAddress,
                        tx.Amount,
                        tx.Fee,
                        tx.Nonce,
                        tx.Timestamp,
                        TransactionType = (int)tx.TransactionType,
                        tx.Data,
                        privacyInfo
                    };
                }).ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Universal search ─────────────────────────────────────────────────────
        [HttpGet("api/search")]
        public IActionResult Search([FromQuery] string q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return BadRequest(new { error = "Query required" });

            q = q.Trim();

            // Try block height (numeric)
            if (long.TryParse(q, out long height))
            {
                var block = BlockchainData.GetBlockByHeight(height);
                if (block != null)
                    return Ok(new { type = "block", data = block });
            }

            // Try block hash (longer hex-like strings)
            if (q.Length >= 36)
            {
                var block = BlockchainData.GetBlockByHash(q);
                if (block != null)
                    return Ok(new { type = "block", data = block });

                // Try transaction hash
                var tx = TransactionData.GetTxByHash(q);
                if (tx != null)
                    return Ok(new { type = "tx", data = tx });

                // Try scanning recent blocks for tx
                var lastBlock = Globals.LastBlock;
                if (lastBlock != null)
                {
                    var blocks = DbContext.DB.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                    var startHeight = Math.Max(0, lastBlock.Height - 500);
                    var recentBlocks = blocks.Query()
                        .Where(b => b.Height >= startHeight)
                        .OrderByDescending(b => b.Height)
                        .ToList();

                    foreach (var b in recentBlocks)
                    {
                        var found = b.Transactions?.FirstOrDefault(t => t.Hash == q);
                        if (found != null)
                            return Ok(new { type = "tx", data = found });
                    }
                }
            }

            // Try as address
            var aTrei = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
            var state = aTrei.FindOne(x => x.Key == q);
            if (state != null)
            {
                var txs = TransactionData.GetAll().Query()
                    .Where(t => t.FromAddress == q || t.ToAddress == q)
                    .OrderByDescending(t => t.Height)
                    .Limit(50)
                    .ToList();

                return Ok(new
                {
                    type = "address",
                    data = new
                    {
                        address = q,
                        balance = state.Balance,
                        lockedBalance = state.LockedBalance,
                        nonce = state.Nonce,
                        transactions = txs
                    }
                });
            }

            // Address might exist with 0 balance (not in state trie)
            // Still check if it has any transactions
            var addrTxs = TransactionData.GetAll().Query()
                .Where(t => t.FromAddress == q || t.ToAddress == q)
                .OrderByDescending(t => t.Height)
                .Limit(50)
                .ToList();

            if (addrTxs.Any())
            {
                return Ok(new
                {
                    type = "address",
                    data = new
                    {
                        address = q,
                        balance = 0m,
                        lockedBalance = 0m,
                        nonce = 0L,
                        transactions = addrTxs
                    }
                });
            }

            return NotFound(new { error = "Not found", type = "none" });
        }

        // ── SSE stream ───────────────────────────────────────────────────────────
        [HttpGet("api/stream")]
        public async Task Stream(CancellationToken cancellationToken)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["X-Accel-Buffering"] = "no";

            long lastHeight = Globals.LastBlock?.Height ?? -1;
            int pingCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var currentBlock = Globals.LastBlock;
                    if (currentBlock != null && currentBlock.Height != lastHeight && currentBlock.Height >= 0)
                    {
                        lastHeight = currentBlock.Height;
                        var payload = JsonConvert.SerializeObject(new
                        {
                            height = currentBlock.Height,
                            hash = currentBlock.Hash,
                            timestamp = currentBlock.Timestamp,
                            validator = currentBlock.Validator,
                            numOfTx = currentBlock.NumOfTx,
                            totalReward = currentBlock.TotalReward,
                            totalAmount = currentBlock.TotalAmount,
                            size = currentBlock.Size,
                            bCraftTime = currentBlock.BCraftTime,
                            version = currentBlock.Version
                        });
                        await Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                        await Response.Body.FlushAsync(cancellationToken);
                    }
                    else if (pingCount++ % 10 == 0)
                    {
                        // Keep-alive ping every ~30s
                        await Response.WriteAsync("data: {\"type\":\"ping\"}\n\n", cancellationToken);
                        await Response.Body.FlushAsync(cancellationToken);
                    }

                    await Task.Delay(3000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }

        // ── Privacy helpers ──────────────────────────────────────────────────────
        private static bool IsPrivacyTxType(TransactionType txType)
        {
            return txType == TransactionType.VFX_SHIELD
                || txType == TransactionType.VFX_UNSHIELD
                || txType == TransactionType.VFX_PRIVATE_TRANSFER
                || txType == TransactionType.VBTC_V2_SHIELD
                || txType == TransactionType.VBTC_V2_UNSHIELD
                || txType == TransactionType.VBTC_V2_PRIVATE_TRANSFER;
        }

        private static object? ParsePrivacyPayloadSummary(string data)
        {
            try
            {
                var payload = JsonConvert.DeserializeObject<PrivateTxPayload>(data);
                if (payload == null) return null;
                return new
                {
                    version = payload.Version,
                    kind = payload.Kind ?? "unknown",
                    asset = payload.Asset,
                    outputCount = payload.Outs?.Count ?? 0,
                    nullifierCount = payload.NullsB64?.Count ?? 0,
                    hasMerkleRoot = !string.IsNullOrWhiteSpace(payload.MerkleRootB64),
                    hasProof = !string.IsNullOrWhiteSpace(payload.ProofB64),
                    hasFeeProof = !string.IsNullOrWhiteSpace(payload.FeeProofB64),
                    transparentAmount = payload.TransparentAmount,
                    transparentInput = payload.TransparentInput,
                    transparentOutput = payload.TransparentOutput,
                    fee = payload.Fee,
                    vbtcContractUid = payload.VbtcContractUid,
                    vbtcTransparentAmount = payload.VbtcTransparentAmount
                };
            }
            catch
            {
                return null;
            }
        }

        // ── Embedded HTML ────────────────────────────────────────────────────────
        private static string GetHtml() => @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
<title>VFX Block Explorer</title>
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
:root{
  --bg:#0d1117;--surface:#161b22;--surface2:#21262d;
  --border:#30363d;--border2:#21262d;
  --text:#e6edf3;--muted:#8b949e;
  --accent:#58a6ff;--accent-dark:#1f6feb;
  --green:#3fb950;--orange:#e3b341;--red:#f85149;
  --purple:#bc8cff;--cyan:#39d353;
}
body{background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',system-ui,sans-serif;font-size:14px;line-height:1.5;min-height:100vh}
/* Header */
.hdr{background:var(--surface);border-bottom:1px solid var(--border);padding:0 24px;height:60px;display:flex;align-items:center;gap:16px;position:sticky;top:0;z-index:100}
.logo{display:flex;align-items:center;gap:10px;cursor:pointer;flex-shrink:0}
.logo-icon{width:34px;height:34px;background:linear-gradient(135deg,#58a6ff,#bc8cff);border-radius:9px;display:flex;align-items:center;justify-content:center;font-weight:800;font-size:17px;color:#fff;flex-shrink:0}
.logo-text{font-size:17px;font-weight:700;color:var(--text);white-space:nowrap}
.logo-text span{color:var(--accent)}
.srch{flex:1;max-width:620px;display:flex;gap:8px}
.srch-inp{flex:1;background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:8px 14px;color:var(--text);font-size:13px;outline:none;transition:border-color .2s}
.srch-inp:focus{border-color:var(--accent)}
.srch-inp::placeholder{color:var(--muted)}
.srch-btn{background:var(--accent-dark);color:#fff;border:none;border-radius:8px;padding:8px 18px;cursor:pointer;font-size:13px;font-weight:500;transition:background .2s;white-space:nowrap}
.srch-btn:hover{background:var(--accent)}
.net-badge{background:rgba(88,166,255,.13);border:1px solid rgba(88,166,255,.3);color:var(--accent);padding:4px 10px;border-radius:20px;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.5px;flex-shrink:0}
/* Main */
.main{max-width:1400px;margin:0 auto;padding:24px}
/* Stats */
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px;margin-bottom:28px}
.stat-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:18px 20px;display:flex;flex-direction:column;gap:5px}
.stat-card.clk{cursor:pointer;transition:border-color .2s}.stat-card.clk:hover{border-color:var(--accent)}
.stat-lbl{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.8px;color:var(--muted)}
.stat-val{font-size:24px;font-weight:700;color:var(--text);font-variant-numeric:tabular-nums}
.stat-val.acc{color:var(--accent)}.stat-val.grn{color:var(--green)}.stat-val.org{color:var(--orange)}
/* Tab bar */
.tab-bar{display:flex;gap:4px;margin-bottom:14px}
.tab-btn{background:var(--surface2);border:1px solid var(--border);color:var(--muted);padding:8px 18px;border-radius:8px 8px 0 0;cursor:pointer;font-size:13px;font-weight:600;transition:all .2s}
.tab-btn:hover{color:var(--text);border-color:var(--accent)}
.tab-btn.active{background:var(--surface);color:var(--accent);border-bottom-color:var(--surface)}
/* Section header */
.sec-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:14px}
.sec-ttl{font-size:17px;font-weight:700;color:var(--text)}
.live{display:flex;align-items:center;gap:6px;font-size:12px;color:var(--green)}
.live-dot{width:8px;height:8px;background:var(--green);border-radius:50%;animation:pulse 2s infinite}
@keyframes pulse{0%,100%{opacity:1;transform:scale(1)}50%{opacity:.5;transform:scale(.8)}}
/* Table */
.tbl-wrap{background:var(--surface);border:1px solid var(--border);border-radius:12px;overflow:hidden}
.dtbl{width:100%;border-collapse:collapse}
.dtbl th{background:var(--surface2);padding:11px 16px;text-align:left;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted);border-bottom:1px solid var(--border)}
.dtbl td{padding:11px 16px;border-bottom:1px solid var(--border2);vertical-align:middle}
.dtbl tr:last-child td{border-bottom:none}
.dtbl tbody tr{transition:background .12s;cursor:pointer}
.dtbl tbody tr:hover{background:var(--surface2)}
@keyframes newBlk{0%{background:rgba(63,185,80,.18)}100%{background:transparent}}
.new-blk{animation:newBlk 2.5s ease-out forwards}
.ht-badge{background:rgba(88,166,255,.1);border:1px solid rgba(88,166,255,.2);color:var(--accent);padding:3px 9px;border-radius:6px;font-size:13px;font-weight:700;font-variant-numeric:tabular-nums}
code,.hash-t,.addr-t{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:12px;color:var(--text)}
.hash-lnk{color:var(--accent);cursor:pointer}.hash-lnk:hover{text-decoration:underline}
.txcnt{color:var(--muted);font-variant-numeric:tabular-nums}.txcnt.has{color:var(--orange);font-weight:700}
.rwd{color:var(--green);font-weight:500;font-variant-numeric:tabular-nums}
.muted{color:var(--muted)}
.ld-row{text-align:center;padding:40px !important;color:var(--muted)}
.spin{display:inline-block;width:18px;height:18px;border:2px solid var(--border);border-top-color:var(--accent);border-radius:50%;animation:spin .7s linear infinite;margin-right:8px;vertical-align:middle}
@keyframes spin{to{transform:rotate(360deg)}}
/* Detail panel */
.detail{display:none;background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:24px}
.detail.on{display:block}
.det-hdr{display:flex;align-items:center;gap:14px;margin-bottom:22px;padding-bottom:16px;border-bottom:1px solid var(--border)}
.det-hdr h2{font-size:19px;font-weight:700}
.back-btn{background:var(--surface2);border:1px solid var(--border);color:var(--text);padding:7px 14px;border-radius:8px;cursor:pointer;font-size:13px;transition:background .2s;flex-shrink:0}
.back-btn:hover{background:var(--border)}
/* Detail grid */
.det-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:10px;margin-bottom:22px}
.det-item{background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:11px 13px;display:flex;flex-direction:column;gap:4px}
.det-item.fw{grid-column:1/-1}
.det-item label{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted)}
.det-item code,.det-item span{font-size:13px;word-break:break-all}
/* Balances */
.bal-cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:10px;margin:12px 0 22px}
.bal-card{background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:14px 16px;display:flex;flex-direction:column;gap:7px}
.bal-card label{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted)}
.big-bal{font-size:20px;font-weight:700;color:var(--green);font-variant-numeric:tabular-nums}
.big-bal.lk{color:var(--orange)}.big-bal.nm{color:var(--text)}
.addr-hero{background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:13px 15px;margin-bottom:10px;word-break:break-all}
/* Badges */
.type-badge{display:inline-block;padding:2px 8px;border-radius:4px;font-size:11px;font-weight:700;text-transform:uppercase;background:rgba(88,166,255,.1);color:var(--accent);border:1px solid rgba(88,166,255,.2)}
.type-badge.t0{background:rgba(63,185,80,.1);color:var(--green);border-color:rgba(63,185,80,.2)}
.type-badge.t1{background:rgba(188,140,255,.1);color:var(--purple);border-color:rgba(188,140,255,.2)}
.type-badge.tpriv{background:rgba(188,140,255,.15);color:var(--purple);border-color:rgba(188,140,255,.3)}
.st-badge{display:inline-block;padding:2px 8px;border-radius:4px;font-size:11px;font-weight:700}
.st-ok{background:rgba(63,185,80,.13);color:var(--green)}
.st-pend{background:rgba(227,179,65,.13);color:var(--orange)}
.st-fail{background:rgba(248,81,73,.13);color:var(--red)}
pre.data-pre{background:var(--bg);border:1px solid var(--border);border-radius:6px;padding:10px;font-size:11px;overflow-x:auto;white-space:pre-wrap;word-break:break-all;color:var(--muted)}
.clk{cursor:pointer;color:var(--accent)}.clk:hover{text-decoration:underline}
.no-data{text-align:center;padding:60px 20px;color:var(--muted)}
.no-data-icon{font-size:42px;margin-bottom:14px}
.sub-ttl{margin:20px 0 12px;font-size:15px;font-weight:700;color:var(--text)}
.tx-wrap{overflow-x:auto}
/* Privacy detail section */
.priv-section{background:var(--bg);border:1px solid rgba(188,140,255,.3);border-radius:8px;padding:16px;margin-top:14px}
.priv-section h4{color:var(--purple);font-size:13px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;margin-bottom:10px}
.priv-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:8px;margin-bottom:10px}
.priv-item{display:flex;flex-direction:column;gap:2px}
.priv-item label{font-size:10px;font-weight:700;text-transform:uppercase;color:var(--muted)}
.priv-item span{font-size:12px;color:var(--text);word-break:break-all}
.priv-item span.yes{color:var(--green)}.priv-item span.no{color:var(--muted)}
.priv-outs-tbl{width:100%;border-collapse:collapse;margin-top:6px}
.priv-outs-tbl th{font-size:10px;text-transform:uppercase;color:var(--muted);text-align:left;padding:4px 8px;border-bottom:1px solid var(--border)}
.priv-outs-tbl td{font-size:11px;padding:4px 8px;border-bottom:1px solid var(--border2);font-family:'SF Mono',Consolas,monospace;word-break:break-all}
/* Mempool section */
.mp-refresh{font-size:11px;color:var(--muted);margin-left:12px}
@media(max-width:768px){.hdr{padding:0 14px}.main{padding:14px}.logo-text{display:none}.stats{grid-template-columns:1fr 1fr}}
@media(max-width:480px){.stats{grid-template-columns:1fr}}
</style>
</head>
<body>
<header class='hdr'>
  <div class='logo' onclick='goHome()'>
    <div class='logo-icon'>V</div>
    <div class='logo-text'>VFX <span>Explorer</span></div>
  </div>
  <div class='srch'>
    <input id='si' class='srch-inp' type='text' placeholder='Search block height, hash, tx hash, or address...'>
    <button class='srch-btn' onclick='doSearch()'>Search</button>
  </div>
  <div id='nb' class='net-badge'>Network</div>
</header>
<main class='main'>
  <div id='hv'>
    <div class='stats'>
      <div class='stat-card'><div class='stat-lbl'>Block Height</div><div class='stat-val acc' id='s-ht'>--</div></div>
      <div class='stat-card'><div class='stat-lbl'>Network Supply</div><div class='stat-val grn' id='s-sp'>--</div></div>
      <div class='stat-card'><div class='stat-lbl'>Connected Peers</div><div class='stat-val' id='s-pr'>--</div></div>
      <div class='stat-card clk' onclick='showMempool()'><div class='stat-lbl'>Mempool TXs</div><div class='stat-val org' id='s-mp'>--</div></div>
    </div>
    <div class='tab-bar'>
      <div class='tab-btn active' id='tab-blocks' onclick='switchTab(""blocks"")'>Latest Blocks</div>
      <div class='tab-btn' id='tab-mempool' onclick='switchTab(""mempool"")'>Mempool</div>
    </div>
    <div id='blocks-section'>
      <div class='sec-hdr'>
        <h2 class='sec-ttl'>Latest Blocks</h2>
        <div class='live' id='li'><div class='live-dot'></div><span>Live</span></div>
      </div>
      <div class='tbl-wrap'>
        <table class='dtbl'>
          <thead><tr><th>Height</th><th>Hash</th><th>Age</th><th>Validator</th><th>TXs</th><th>Size</th><th>Reward</th></tr></thead>
          <tbody id='bt'><tr><td class='ld-row' colspan='7'><span class='spin'></span>Loading blocks...</td></tr></tbody>
        </table>
      </div>
    </div>
    <div id='mempool-section' style='display:none'>
      <div class='sec-hdr'>
        <h2 class='sec-ttl'>Mempool Transactions<span class='mp-refresh' id='mp-refresh'></span></h2>
      </div>
      <div class='tbl-wrap'>
        <table class='dtbl'>
          <thead><tr><th>Hash</th><th>Type</th><th>From</th><th>To</th><th>Amount</th><th>Fee</th><th>Age</th></tr></thead>
          <tbody id='mpt'><tr><td class='ld-row' colspan='7'><span class='spin'></span>Loading mempool...</td></tr></tbody>
        </table>
      </div>
    </div>
  </div>
  <div id='dv' class='detail'><div id='dc'></div></div>
</main>
<script>
(function(){
var blocks=[],sse=null,mpTimer=null,currentTab='blocks';

document.addEventListener('DOMContentLoaded',function(){
  loadStats();loadBlocks();connectSSE();
  el('si').addEventListener('keydown',function(e){if(e.key==='Enter')doSearch();});
});

function loadStats(){
  fetch('/explorer/api/stats').then(function(r){return r.json();}).then(function(d){
    set('s-ht',num(d.height));
    set('s-sp',d.networkSupply!=null?(+d.networkSupply).toFixed(2)+' VFX':'--');
    set('s-pr',d.peers!=null?d.peers:'--');
    set('s-mp',d.mempool!=null?d.mempool:'--');
    set('nb',d.isTestNet?'Testnet':'Mainnet');
  }).catch(function(){});
  setTimeout(loadStats,10000);
}

function loadBlocks(){
  fetch('/explorer/api/blocks').then(function(r){return r.json();}).then(function(d){
    blocks=d;renderList(d);
    if(d&&d.length>0&&d[0].height>=0)set('s-ht',num(d[0].height));
  }).catch(function(){
    el('bt').innerHTML='<tr><td class=""ld-row"" colspan=""7"">Failed to load blocks.</td></tr>';
  });
}

function renderList(data){
  if(!data||!data.length){
    el('bt').innerHTML='<tr><td class=""ld-row"" colspan=""7"">No blocks found.</td></tr>';return;
  }
  el('bt').innerHTML=data.map(blkRow).join('');
}

function blkRow(b){
  var hs=b.hash?b.hash.substring(0,14)+'...':'N/A';
  var vs=b.validator&&b.validator.length>24?b.validator.substring(0,24)+'...':b.validator||'N/A';
  var tc=b.numOfTx>0?'txcnt has':'txcnt';
  return '<tr class=""block-row"" onclick=""viewBlock('+b.height+')"">'+
    '<td><span class=""ht-badge"">'+b.height+'</span></td>'+
    '<td><code class=""hash-lnk"" title=""'+esc(b.hash)+'"">' +hs+'</code></td>'+
    '<td class=""muted"">'+ago(b.timestamp)+'</td>'+
    '<td><code class=""addr-t"" title=""'+esc(b.validator)+'"">' +vs+'</code></td>'+
    '<td><span class=""'+tc+'"">'+b.numOfTx+'</span></td>'+
    '<td class=""muted"">'+sz(b.size)+'</td>'+
    '<td class=""rwd"">'+b.totalReward+' VFX</td>'+
    '</tr>';
}

function connectSSE(){
  sse=new EventSource('/explorer/api/stream');
  sse.onmessage=function(e){
    try{
      var b=JSON.parse(e.data);
      if(b.type==='ping')return;
      blocks.unshift(b);if(blocks.length>100)blocks.pop();
      prependRow(b);
      set('s-ht',num(b.height));
    }catch(ex){}
  };
  sse.onerror=function(){
    sse.close();
    el('li').innerHTML='<div class=""live-dot"" style=""background:var(--red)""></div><span style=""color:var(--red)"">Reconnecting...</span>';
    setTimeout(function(){
      el('li').innerHTML='<div class=""live-dot""></div><span>Live</span>';
      connectSSE();
    },5000);
  };
}

function prependRow(b){
  var tbody=el('bt');
  if(tbody.children.length===1&&tbody.children[0].querySelector('.spin'))tbody.innerHTML='';
  var tr=document.createElement('tr');
  tr.className='block-row new-blk';
  tr.onclick=function(){viewBlock(b.height);};
  tr.innerHTML=blkRowInner(b);
  tbody.insertBefore(tr,tbody.firstChild);
  while(tbody.children.length>100)tbody.removeChild(tbody.lastChild);
}

function blkRowInner(b){
  var hs=b.hash?b.hash.substring(0,14)+'...':'N/A';
  var vs=b.validator&&b.validator.length>24?b.validator.substring(0,24)+'...':b.validator||'N/A';
  var tc=b.numOfTx>0?'txcnt has':'txcnt';
  return '<td><span class=""ht-badge"">'+b.height+'</span></td>'+
    '<td><code class=""hash-lnk"" title=""'+esc(b.hash)+'"">' +hs+'</code></td>'+
    '<td class=""muted"">'+ago(b.timestamp)+'</td>'+
    '<td><code class=""addr-t"" title=""'+esc(b.validator||'')+'"">' +vs+'</code></td>'+
    '<td><span class=""'+tc+'"">'+b.numOfTx+'</span></td>'+
    '<td class=""muted"">'+sz(b.size)+'</td>'+
    '<td class=""rwd"">'+b.totalReward+' VFX</td>';
}

/* ── Tab switching ── */
window.switchTab=function(tab){
  currentTab=tab;
  el('tab-blocks').className=tab==='blocks'?'tab-btn active':'tab-btn';
  el('tab-mempool').className=tab==='mempool'?'tab-btn active':'tab-btn';
  el('blocks-section').style.display=tab==='blocks'?'':'none';
  el('mempool-section').style.display=tab==='mempool'?'':'none';
  if(tab==='mempool'){loadMempool();startMpRefresh();}else{stopMpRefresh();}
};

window.showMempool=function(){switchTab('mempool');};

function startMpRefresh(){stopMpRefresh();mpTimer=setInterval(loadMempool,5000);}
function stopMpRefresh(){if(mpTimer){clearInterval(mpTimer);mpTimer=null;}}

function loadMempool(){
  fetch('/explorer/api/mempool').then(function(r){return r.json();}).then(function(data){
    set('s-mp',data.length);
    set('mp-refresh','Updated '+new Date().toLocaleTimeString());
    renderMempool(data);
  }).catch(function(){
    el('mpt').innerHTML='<tr><td class=""ld-row"" colspan=""7"">Failed to load mempool.</td></tr>';
  });
}

function renderMempool(data){
  if(!data||!data.length){
    el('mpt').innerHTML='<tr><td class=""ld-row"" colspan=""7"">Mempool is empty.</td></tr>';return;
  }
  el('mpt').innerHTML=data.map(mpRow).join('');
}

function mpRow(tx){
  var hs=tx.hash?tx.hash.substring(0,10)+'...':'N/A';
  var fs=shn(tx.fromAddress,20);var ts2=shn(tx.toAddress,20);
  var tt=ttype(tx.transactionType);
  var tc=tCls(tx.transactionType);
  var pi=tx.privacyInfo;
  var extra='';
  if(pi){extra=' title=""'+esc(pi.kind)+' | '+esc(pi.asset)+' | outs:'+pi.outputCount+' nulls:'+pi.nullifierCount+'""';}
  return '<tr class=""block-row"" onclick=""vt(\''+esc(tx.hash)+'\')"">' +
    '<td><code class=""hash-lnk"" title=""'+esc(tx.hash)+'"">' +hs+'</code></td>' +
    '<td><span class=""type-badge'+tc+'""'+extra+'>' +tt+'</span></td>' +
    '<td><code class=""addr-t clk"" title=""'+esc(tx.fromAddress||'')+'""  onclick=""event.stopPropagation();sa(\''+esc(tx.fromAddress||'')+'\')"">' +fs+'</code></td>' +
    '<td><code class=""addr-t clk"" title=""'+esc(tx.toAddress||'')+'""  onclick=""event.stopPropagation();sa(\''+esc(tx.toAddress||'')+'\')"">' +ts2+'</code></td>' +
    '<td class=""rwd"">'+tx.amount+'</td>' +
    '<td class=""muted"">'+tx.fee+'</td>' +
    '<td class=""muted"">'+ago(tx.timestamp)+'</td>' +
    '</tr>';
}

window.viewBlock=function(h){
  showDet('<div style=""text-align:center;padding:48px""><span class=""spin""></span> Loading block #'+h+'...</div>');
  fetch('/explorer/api/block/'+h).then(function(r){return r.json();}).then(renderBlkDet)
    .catch(function(){showDet('<div class=""no-data""><div class=""no-data-icon"">&#9888;</div><div>Failed to load block.</div></div>');});
};

function renderBlkDet(b){
  var txr=(b.transactions||[]).map(txRow).join('');
  var h=
    '<div class=""det-hdr""><button class=""back-btn"" onclick=""goHome()"">&#8592; Back</button><h2>Block #'+b.height+'</h2></div>' +
    '<div class=""det-grid"">' +
    di('Hash','<code>'+esc(b.hash||'N/A')+'</code>',true) +
    di('Height',b.height) +
    di('Timestamp',b.timestamp?new Date(b.timestamp*1000).toLocaleString():'N/A') +
    di('Validator','<code class=""clk"" onclick=""sa(\''+esc(b.validator||'')+'\')"">' +esc(b.validator||'N/A')+'</code>',true) +
    di('Previous Hash','<code>'+esc(b.prevHash||'N/A')+'</code>',true) +
    di('Merkle Root','<code>'+esc(b.merkleRoot||'N/A')+'</code>',true) +
    di('State Root','<code>'+esc(b.stateRoot||'N/A')+'</code>',true) +
    di('Validator Signature','<code>'+esc(b.validatorSignature||'N/A')+'</code>',true) +
    di('Total Reward',b.totalReward+' VFX') +
    di('Total Amount',b.totalAmount+' VFX') +
    di('Transactions',b.numOfTx) +
    di('Size',sz(b.size)) +
    di('Craft Time',b.bCraftTime+' ms') +
    di('Version',b.version) +
    di('Total Validators',b.totalValidators) +
    '</div>';
  if(txr){
    h+='<div class=""sub-ttl"">Transactions ('+(b.transactions||[]).length+')</div>' +
      '<div class=""tx-wrap""><table class=""dtbl"">' +
      '<thead><tr><th>#</th><th>Hash</th><th>Type</th><th>From</th><th>To</th><th>Amount</th><th>Fee</th><th>Status</th></tr></thead>' +
      '<tbody>'+txr+'</tbody></table></div>';
  }else{
    h+='<p class=""muted"" style=""margin-top:16px"">No transactions in this block</p>';
  }
  showDet(h);
}

function txRow(tx,i){
  var hs=tx.hash?tx.hash.substring(0,10)+'...':'N/A';
  var fs=shn(tx.fromAddress,20);var ts2=shn(tx.toAddress,20);
  var sc=stCls(tx.transactionStatus);var st=stNm(tx.transactionStatus);
  var tt=ttype(tx.transactionType);
  return '<tr class=""block-row"" onclick=""vt(\''+esc(tx.hash)+'\')"">' +
    '<td class=""muted"">'+(i+1)+'</td>' +
    '<td><code class=""hash-lnk"" title=""'+esc(tx.hash)+'"">' +hs+'</code></td>' +
    '<td><span class=""type-badge'+tCls(tx.transactionType)+'"">' +tt+'</span></td>' +
    '<td><code class=""addr-t clk"" title=""'+esc(tx.fromAddress||'')+'""  onclick=""event.stopPropagation();sa(\''+esc(tx.fromAddress||'')+'\')"">' +fs+'</code></td>' +
    '<td><code class=""addr-t clk"" title=""'+esc(tx.toAddress||'')+'""  onclick=""event.stopPropagation();sa(\''+esc(tx.toAddress||'')+'\')"">' +ts2+'</code></td>' +
    '<td class=""rwd"">'+tx.amount+'</td>' +
    '<td class=""muted"">'+tx.fee+'</td>' +
    '<td><span class=""st-badge '+sc+'"">' +st+'</span></td>' +
    '</tr>';
}

window.vt=function(hash){
  showDet('<div style=""text-align:center;padding:48px""><span class=""spin""></span> Loading transaction...</div>');
  fetch('/explorer/api/tx/'+encodeURIComponent(hash)).then(function(r){return r.json();}).then(renderTxDet)
    .catch(function(){showDet('<div class=""no-data""><div class=""no-data-icon"">&#9888;</div><div>Transaction not found.</div></div>');});
};

function renderTxDet(tx){
  var sc=stCls(tx.transactionStatus);var st=stNm(tx.transactionStatus);
  var isPriv=isPrivTx(tx.transactionType);
  var h=
    '<div class=""det-hdr""><button class=""back-btn"" onclick=""goHome()"">&#8592; Back</button><h2>Transaction</h2></div>' +
    '<div class=""det-grid"">' +
    di('Hash','<code>'+esc(tx.hash||'N/A')+'</code>',true) +
    di('Type','<span class=""type-badge'+tCls(tx.transactionType)+'"">' +ttype(tx.transactionType)+'</span>') +
    di('Status','<span class=""st-badge '+sc+'"">' +st+'</span>') +
    di('From','<code class=""clk"" onclick=""sa(\''+esc(tx.fromAddress||'')+'\')"">' +esc(tx.fromAddress||'N/A')+'</code>',true) +
    di('To','<code class=""clk"" onclick=""sa(\''+esc(tx.toAddress||'')+'\')"">' +esc(tx.toAddress||'N/A')+'</code>',true) +
    di('Amount',tx.amount+' VFX') +
    di('Fee',tx.fee+' VFX') +
    di('Block','<span class=""clk"" onclick=""viewBlock('+tx.height+')"">#'+tx.height+'</span>') +
    di('Nonce',tx.nonce) +
    di('Timestamp',tx.timestamp?new Date(tx.timestamp*1000).toLocaleString():'N/A') +
    (tx.unlockTime?di('Unlock Time',new Date(tx.unlockTime*1000).toLocaleString()):'') +
    '</div>';
  if(isPriv&&tx.data){
    h+=renderPrivacyPayload(tx.data);
  }else if(tx.data){
    h+='<div class=""sub-ttl"">Transaction Data</div><pre class=""data-pre"">'+esc(tx.data)+'</pre>';
  }
  showDet(h);
}

function renderPrivacyPayload(dataStr){
  try{
    var p=JSON.parse(dataStr);
    if(!p||!p.v)return '<div class=""sub-ttl"">Transaction Data</div><pre class=""data-pre"">'+esc(dataStr)+'</pre>';
    var s='<div class=""priv-section""><h4>&#128274; Privacy Payload</h4>';
    s+='<div class=""priv-grid"">';
    s+=pi('Version',p.v);
    s+=pi('Kind',p.kind||'N/A');
    if(p.sub_type)s+=pi('Sub Type',p.sub_type);
    s+=pi('Asset',p.asset||'N/A');
    s+=pi('Outputs',p.outs?p.outs.length:0);
    s+=pi('Nullifiers',p.nulls?p.nulls.length:0);
    s+=pi('Merkle Root',p.merkle_root?shn(p.merkle_root,24):'N/A');
    s+=pi('Proof','<span class=""'+(p.proof_b64?'yes':'no')+'"">'+(p.proof_b64?'Present':'None')+'</span>');
    s+=pi('Fee Proof','<span class=""'+(p.fee_proof_b64?'yes':'no')+'"">'+(p.fee_proof_b64?'Present':'None')+'</span>');
    if(p.transparent_amount!=null)s+=pi('Transparent Amt',p.transparent_amount+' VFX');
    if(p.transparent_input)s+=pi('Transparent In',shn(p.transparent_input,24));
    if(p.transparent_output)s+=pi('Transparent Out',shn(p.transparent_output,24));
    if(p.fee!=null)s+=pi('Payload Fee',p.fee+' VFX');
    if(p.vbtc_uid)s+=pi('vBTC Contract',shn(p.vbtc_uid,24));
    if(p.vbtc_amt!=null)s+=pi('vBTC Amount',p.vbtc_amt);
    if(p.fee_input_nullifier_b64)s+=pi('Fee Nullifier',shn(p.fee_input_nullifier_b64,24));
    if(p.fee_output_commitment_b64)s+=pi('Fee Out Commit',shn(p.fee_output_commitment_b64,24));
    if(p.fee_tree_merkle_root)s+=pi('Fee Merkle Root',shn(p.fee_tree_merkle_root,24));
    if(p.fee_input_spent_tree_position!=null)s+=pi('Fee Spent Pos',p.fee_input_spent_tree_position);
    if(p.fee_out_note_hash)s+=pi('Fee Note Hash',shn(p.fee_out_note_hash,24));
    s+='</div>';
    if(p.outs&&p.outs.length>0){
      s+='<div style=""margin-top:10px;font-size:12px;font-weight:700;color:var(--purple)"">Shielded Outputs</div>';
      s+='<table class=""priv-outs-tbl""><thead><tr><th>Idx</th><th>Commitment</th><th>Note Hash</th><th>Enc. Note</th></tr></thead><tbody>';
      for(var i=0;i<p.outs.length;i++){
        var o=p.outs[i];
        s+='<tr><td>'+o.i+'</td><td>'+shn(o.c||'',20)+'</td><td>'+shn(o.nh||'N/A',20)+'</td><td>'+(o.note?shn(o.note,16):'N/A')+'</td></tr>';
      }
      s+='</tbody></table>';
    }
    if(p.nulls&&p.nulls.length>0){
      s+='<div style=""margin-top:10px;font-size:12px;font-weight:700;color:var(--purple)"">Nullifiers</div>';
      s+='<div style=""display:flex;flex-wrap:wrap;gap:6px;margin-top:4px"">';
      for(var j=0;j<p.nulls.length;j++){
        var pos=p.spent_tree_positions&&p.spent_tree_positions[j]!=null?' (pos:'+p.spent_tree_positions[j]+')':'';
        s+='<code style=""background:var(--surface2);padding:2px 6px;border-radius:4px;font-size:11px"">'+shn(p.nulls[j],20)+pos+'</code>';
      }
      s+='</div>';
    }
    s+='</div>';
    return s;
  }catch(ex){
    return '<div class=""sub-ttl"">Transaction Data (Raw)</div><pre class=""data-pre"">'+esc(dataStr)+'</pre>';
  }
}

function pi(lbl,val){return '<div class=""priv-item""><label>'+lbl+'</label><span>'+val+'</span></div>';}

function isPrivTx(t){return t>=31&&t<=36;}

window.sa=function(addr){
  el('si').value=addr;doSearch();
};

function renderAddrDet(data){
  var txr=(data.transactions||[]).map(txRow).join('');
  var h=
    '<div class=""det-hdr""><button class=""back-btn"" onclick=""goHome()"">&#8592; Back</button><h2>Address</h2></div>' +
    '<div class=""addr-hero""><code>'+esc(data.address||'')+'</code></div>' +
    '<div class=""bal-cards"">' +
    '<div class=""bal-card""><label>Balance</label><span class=""big-bal"">'+fmtBal(data.balance)+' VFX</span></div>' +
    '<div class=""bal-card""><label>Locked Balance</label><span class=""big-bal lk"">'+fmtBal(data.lockedBalance)+' VFX</span></div>' +
    '<div class=""bal-card""><label>Nonce</label><span class=""big-bal nm"">'+(data.nonce||0)+'</span></div>' +
    '</div>';
  if(txr){
    h+='<div class=""sub-ttl"">Transactions ('+(data.transactions||[]).length+')</div>' +
      '<div class=""tx-wrap""><table class=""dtbl"">' +
      '<thead><tr><th>#</th><th>Hash</th><th>Type</th><th>From</th><th>To</th><th>Amount</th><th>Fee</th><th>Status</th></tr></thead>' +
      '<tbody>'+txr+'</tbody></table></div>';
  }else{
    h+='<p class=""muted"" style=""margin-top:16px"">No transactions found for this address</p>';
  }
  showDet(h);
}

window.doSearch=function(){
  var q=el('si').value.trim();if(!q)return;
  showDet('<div style=""text-align:center;padding:48px""><span class=""spin""></span> Searching...</div>');
  fetch('/explorer/api/search?q='+encodeURIComponent(q))
    .then(function(r){if(!r.ok)throw new Error('not found');return r.json();})
    .then(function(res){
      if(res.type==='block')renderBlkDet(res.data);
      else if(res.type==='address')renderAddrDet(res.data);
      else if(res.type==='tx')renderTxDet(res.data);
      else showDet('<div class=""no-data""><div class=""no-data-icon"">&#128269;</div><div>No results found for: <strong>'+esc(q)+'</strong></div></div>');
    })
    .catch(function(){
      showDet('<div class=""no-data""><div class=""no-data-icon"">&#128269;</div><div>No results found for: <strong>'+esc(q)+'</strong></div></div>');
    });
};

function showDet(html){
  el('hv').style.display='none';
  el('dc').innerHTML=html;
  el('dv').classList.add('on');
  window.scrollTo(0,0);
}

window.goHome=function(){
  el('hv').style.display='';
  el('dv').classList.remove('on');
  el('si').value='';
};

/* --- Helpers --- */
function el(id){return document.getElementById(id);}
function set(id,v){var e=el(id);if(e)e.textContent=v;}
function num(n){return n!=null&&n>=0?n.toLocaleString():'--';}
function esc(s){return s?String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;'):'';} 
function shn(s,max){return s?(s.length>max?s.substring(0,max)+'...':s):'N/A';}
function fmtBal(n){return n!=null?(+n).toFixed(8):'0.00000000';}

function di(lbl,val,fw){
  return '<div class=""det-item'+(fw?' fw':'')+'""><label>'+lbl+'</label><span>'+val+'</span></div>';
}

function ago(ts){
  var d=Math.floor(Date.now()/1000)-ts;
  if(d<5)return 'just now';
  if(d<60)return d+'s ago';
  if(d<3600)return Math.floor(d/60)+'m ago';
  if(d<86400)return Math.floor(d/3600)+'h ago';
  return Math.floor(d/86400)+'d ago';
}

function sz(b){
  if(!b)return '0 B';
  if(b<1024)return b+' B';
  if(b<1048576)return (b/1024).toFixed(1)+' KB';
  return (b/1048576).toFixed(2)+' MB';
}

function stCls(s){
  var m={'1':'st-ok','0':'st-pend','2':'st-fail','3':'st-pend','4':'st-pend','5':'st-ok','6':'st-fail','7':'st-fail'};
  return m[String(s)]||'st-pend';
}
function stNm(s){
  var m=['Pending','Success','Failed','Reserved','CalledBack','Recovered','ReplacedByFee','Invalid'];
  return m[s]!==undefined?m[s]:'Unknown';
}
function ttype(t){
  var n=['TX','NODE','NFT_MINT','NFT_TX','NFT_BURN','NFT_SALE','ADNR','DSTR','VOTE_TOPIC','VOTE','RESERVE','SC_MINT','SC_TX','SC_BURN','FTKN_MINT','FTKN_TX','FTKN_BURN','TKNZ_MINT','TKNZ_TX','TKNZ_BURN','TKNZ_WD_ARB','TKNZ_WD_OWNER','VBTC2_VAL_REG','VBTC2_VAL_HB','VBTC2_VAL_EXIT','VBTC2_CREATE','VBTC2_TX','VBTC2_WD_REQ','VBTC2_WD_COMP','VBTC2_WD_CANCEL','VBTC2_WD_VOTE','VFX_SHIELD','VFX_UNSHIELD','VFX_PRIV_XFER','VBTC2_SHIELD','VBTC2_UNSHIELD','VBTC2_PRIV_XFER'];
  return n[t]!==undefined?n[t]:'TYPE_'+t;
}
function tCls(t){
  if(t===0)return ' t0';
  if(t===1)return ' t1';
  if(t>=31&&t<=36)return ' tpriv';
  return '';
}
})();
</script>
</body>
</html>";
    }
}