namespace ReserveBlockCore.BrowserWalletServices
{
    /// <summary>
    /// Assembles the wallet browser UI from composable HTML chunks.
    /// Each section is independently editable without touching the full page.
    /// </summary>
    public static class BrowserUIService
    {
        public static string GetHtml() =>
            BuildHead() +
            BuildHeader() +
            BuildTabBar() +
            "<main class='main'>" +
            BuildOverviewPanel() +
            BuildNftsPanel() +
            BuildVbtcPanel() +
            BuildBtcPanel() +
            BuildHistoryPanel() +
            BuildPrivacyPanel() +
            "</main>" +
            BuildSendVfxModal() +
            BuildNftTransferModal() +
            BuildTokenTransferModal() +
            BuildVbtcWithdrawModal() +
            BuildVbtcCompleteModal() +
            BuildVbtcSendModal() +
            BuildBridgeToBaseModal() +
            BuildCreateZfxModal() +
            BuildShieldModal() +
            BuildUnshieldModal() +
            BuildPrivateTransferModal() +
            BuildSendBtcModal() +
            BuildShieldVbtcModal() +
            BuildUnshieldVbtcModal() +
            BuildPrivateTransferVbtcModal() +
            BuildScript() +
            BuildFooter();

        // ── Document Head ────────────────────────────────────────────────────
        private static string BuildHead() => @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1.0'>
<title>VFX Wallet</title>
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
:root{
  --bg:#0d1117;--surface:#161b22;--surface2:#21262d;
  --border:#30363d;--border2:#21262d;
  --text:#e6edf3;--muted:#8b949e;
  --accent:#58a6ff;--accent-dark:#1f6feb;
  --green:#3fb950;--orange:#e3b341;--red:#f85149;--purple:#bc8cff;
}
body{background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',system-ui,sans-serif;font-size:14px;line-height:1.5;min-height:100vh}
/* Header */
.hdr{background:var(--surface);border-bottom:1px solid var(--border);padding:0 24px;height:60px;display:flex;align-items:center;gap:14px;position:sticky;top:0;z-index:200}
.logo{display:flex;align-items:center;gap:10px;flex-shrink:0;cursor:pointer;text-decoration:none}
.logo-icon{width:34px;height:34px;background:linear-gradient(135deg,#3fb950,#58a6ff);border-radius:9px;display:flex;align-items:center;justify-content:center;font-weight:800;font-size:17px;color:#fff;flex-shrink:0}
.logo-text{font-size:17px;font-weight:700;color:var(--text);white-space:nowrap}
.logo-text span{color:var(--green)}
.addr-wrap{flex:1;display:flex;align-items:center;gap:10px;max-width:860px}
.addr-grp{display:flex;align-items:center;gap:6px;flex:1;min-width:0}
.addr-lbl{font-size:10px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted);flex-shrink:0}
.addr-sel{flex:1;background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:8px 12px;color:var(--text);font-size:13px;font-family:'SF Mono','Fira Code',Consolas,monospace;outline:none;cursor:pointer;appearance:none;-webkit-appearance:none;background-image:url(""data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='12' fill='%238b949e' viewBox='0 0 16 16'%3E%3Cpath d='M7.247 11.14L2.451 5.658C1.885 5.013 2.345 4 3.204 4h9.592a1 1 0 0 1 .753 1.659l-4.796 5.48a1 1 0 0 1-1.506 0z'/%3E%3C/svg%3E"");background-repeat:no-repeat;background-position:right 10px center;padding-right:30px}
.addr-sel:focus{border-color:var(--accent)}
.icon-btn{background:var(--surface2);border:1px solid var(--border);color:var(--muted);width:34px;height:34px;border-radius:8px;cursor:pointer;font-size:15px;display:flex;align-items:center;justify-content:center;transition:all .2s;flex-shrink:0}
.icon-btn:hover{color:var(--text);border-color:var(--accent)}
.net-badge{background:rgba(63,185,80,.13);border:1px solid rgba(63,185,80,.3);color:var(--green);padding:4px 10px;border-radius:20px;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.5px;flex-shrink:0;margin-left:auto}
/* Tab bar */
.tab-bar{background:var(--surface);border-bottom:1px solid var(--border);padding:0 24px;display:flex;gap:2px;overflow-x:auto}
.tab-bar::-webkit-scrollbar{height:3px}.tab-bar::-webkit-scrollbar-thumb{background:var(--border)}
.tab-btn{padding:12px 18px;background:none;border:none;color:var(--muted);font-size:13px;font-weight:500;cursor:pointer;border-bottom:2px solid transparent;white-space:nowrap;transition:color .2s}
.tab-btn:hover{color:var(--text)}
.tab-btn.on{color:var(--accent);border-bottom-color:var(--accent)}
/* Main */
.main{max-width:1200px;margin:0 auto;padding:24px}
/* Panels */
.panel{display:none}.panel.on{display:block}
/* Balance hero */
.bal-hero{background:linear-gradient(135deg,#161b22 0%,#1a2233 100%);border:1px solid var(--border);border-radius:14px;padding:28px 32px;margin-bottom:20px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:16px}
.bal-main{}
.bal-lbl{font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.8px;color:var(--muted);margin-bottom:8px}
.bal-num{font-size:42px;font-weight:800;color:var(--text);font-variant-numeric:tabular-nums;letter-spacing:-1px}
.bal-num span{font-size:20px;color:var(--muted);font-weight:500;margin-left:6px}
.bal-locked{font-size:13px;color:var(--orange);margin-top:6px}
.bal-addr{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:11px;color:var(--muted);margin-top:8px;word-break:break-all;max-width:360px}
.send-btn{background:linear-gradient(135deg,var(--accent-dark),var(--accent));color:#fff;border:none;border-radius:10px;padding:12px 28px;font-size:14px;font-weight:600;cursor:pointer;transition:opacity .2s;white-space:nowrap}
.send-btn:hover{opacity:.85}
/* Stats row */
.stat-row{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:12px;margin-bottom:24px}
.stat-card{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:16px 18px}
.stat-lbl{font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.7px;color:var(--muted);margin-bottom:5px}
.stat-val{font-size:20px;font-weight:700;font-variant-numeric:tabular-nums}
.stat-val.acc{color:var(--accent)}.stat-val.grn{color:var(--green)}.stat-val.org{color:var(--orange)}
/* Section headers */
.sec-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:12px}
.sec-ttl{font-size:15px;font-weight:700;color:var(--text)}
/* Table */
.tbl-wrap{background:var(--surface);border:1px solid var(--border);border-radius:12px;overflow:hidden;margin-bottom:24px}
.dtbl{width:100%;border-collapse:collapse}
.dtbl th{background:var(--surface2);padding:10px 16px;text-align:left;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted);border-bottom:1px solid var(--border)}
.dtbl td{padding:11px 16px;border-bottom:1px solid var(--border2);vertical-align:middle}
.dtbl tr:last-child td{border-bottom:none}
.dtbl tbody tr{transition:background .12s}
.dtbl tbody tr.clk{cursor:pointer}.dtbl tbody tr.clk:hover{background:var(--surface2)}
/* Token rows */
.tok-icon{width:28px;height:28px;background:linear-gradient(135deg,var(--accent-dark),var(--purple));border-radius:6px;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;color:#fff}
/* NFT grid */
.nft-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:16px;margin-bottom:24px}
.nft-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:20px;display:flex;flex-direction:column;gap:10px;transition:border-color .2s}
.nft-card:hover{border-color:var(--accent)}
.nft-icon{width:48px;height:48px;background:linear-gradient(135deg,var(--accent-dark),var(--purple));border-radius:10px;display:flex;align-items:center;justify-content:center;font-size:22px;margin-bottom:4px}
.nft-name{font-size:15px;font-weight:700;color:var(--text)}
.nft-uid{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:11px;color:var(--muted);word-break:break-all}
.nft-actions{display:flex;gap:8px;margin-top:4px}
.act-btn{flex:1;padding:8px;border-radius:7px;font-size:12px;font-weight:600;cursor:pointer;border:1px solid;text-align:center;transition:all .2s}
.act-btn.prim{background:rgba(88,166,255,.1);border-color:rgba(88,166,255,.3);color:var(--accent)}.act-btn.prim:hover{background:rgba(88,166,255,.2)}
.act-btn.sec{background:rgba(63,185,80,.1);border-color:rgba(63,185,80,.3);color:var(--green)}.act-btn.sec:hover{background:rgba(63,185,80,.2)}
/* Badges */
.badge{display:inline-block;padding:2px 8px;border-radius:4px;font-size:11px;font-weight:700;text-transform:uppercase}
.badge-nft{background:rgba(188,140,255,.1);color:var(--purple);border:1px solid rgba(188,140,255,.2)}
.badge-tok{background:rgba(88,166,255,.1);color:var(--accent);border:1px solid rgba(88,166,255,.2)}
.badge-ok{background:rgba(63,185,80,.13);color:var(--green)}
.badge-pend{background:rgba(227,179,65,.13);color:var(--orange)}
.badge-fail{background:rgba(248,81,73,.13);color:var(--red)}
code,.mono{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:12px}
.muted{color:var(--muted)}
.acc{color:var(--accent)}.grn{color:var(--green)}.org{color:var(--orange)}.red{color:var(--red)}
/* Empty state */
.empty{text-align:center;padding:60px 20px;color:var(--muted)}
.empty-icon{font-size:40px;margin-bottom:12px}
/* Loading */
.spin{display:inline-block;width:16px;height:16px;border:2px solid var(--border);border-top-color:var(--accent);border-radius:50%;animation:spin .7s linear infinite;vertical-align:middle;margin-right:6px}
@keyframes spin{to{transform:rotate(360deg)}}
.ld{text-align:center;padding:48px;color:var(--muted)}
/* vBTC cards */
.vbtc-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px;margin-bottom:24px}
.vbtc-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:20px;display:flex;flex-direction:column;gap:10px}
.vbtc-bal{font-size:28px;font-weight:800;color:var(--orange);font-variant-numeric:tabular-nums}
.vbtc-bal span{font-size:14px;color:var(--muted);font-weight:500;margin-left:4px}
.vbtc-row{display:flex;justify-content:space-between;align-items:center;font-size:12px}
.vbtc-row .k{color:var(--muted)}.vbtc-row .v{font-family:'SF Mono','Fira Code',Consolas,monospace;font-size:11px;word-break:break-all;text-align:right;max-width:200px}
/* BTC cards */
.btc-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px;margin-bottom:24px}
.btc-card{background:var(--surface);border:1px solid var(--border);border-radius:12px;padding:20px;display:flex;flex-direction:column;gap:10px}
.btc-bal{font-size:26px;font-weight:800;color:var(--orange);font-variant-numeric:tabular-nums}
.btc-bal span{font-size:13px;color:var(--muted);font-weight:500;margin-left:4px}
/* TX history */
.dir-in{color:var(--green)}.dir-out{color:var(--red)}
/* Modal */
.overlay{display:none;position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:300;align-items:center;justify-content:center;backdrop-filter:blur(2px)}
.overlay.on{display:flex}
.modal{background:var(--surface);border:1px solid var(--border);border-radius:14px;padding:28px;width:100%;max-width:480px;max-height:90vh;overflow-y:auto}
.modal-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:22px;padding-bottom:16px;border-bottom:1px solid var(--border)}
.modal-ttl{font-size:18px;font-weight:700}
.modal-close{background:none;border:none;color:var(--muted);font-size:22px;cursor:pointer;line-height:1;padding:2px 6px;border-radius:6px}
.modal-close:hover{color:var(--text);background:var(--surface2)}
.form-grp{display:flex;flex-direction:column;gap:6px;margin-bottom:16px}
.form-grp label{font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted)}
.form-inp{background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:10px 14px;color:var(--text);font-size:13px;outline:none;width:100%;transition:border-color .2s}
.form-inp:focus{border-color:var(--accent)}
.form-inp::placeholder{color:var(--muted)}
.form-inp:read-only{color:var(--muted);cursor:default}
.modal-foot{display:flex;gap:10px;margin-top:8px}
.btn-prim{flex:1;background:var(--accent-dark);color:#fff;border:none;border-radius:8px;padding:11px;font-size:14px;font-weight:600;cursor:pointer;transition:background .2s}
.btn-prim:hover{background:var(--accent)}
.btn-prim:disabled{opacity:.5;cursor:not-allowed}
.btn-sec{background:var(--surface2);color:var(--text);border:1px solid var(--border);border-radius:8px;padding:11px 18px;font-size:14px;cursor:pointer;transition:background .2s}
.btn-sec:hover{background:var(--border)}
.msg{padding:12px 14px;border-radius:8px;font-size:13px;margin-top:12px;display:none}
.msg.on{display:block}
.msg.ok{background:rgba(63,185,80,.1);border:1px solid rgba(63,185,80,.2);color:var(--green)}
.msg.err{background:rgba(248,81,73,.1);border:1px solid rgba(248,81,73,.2);color:var(--red)}
.tx-wrap{overflow-x:auto}
@media(max-width:768px){.hdr{padding:0 14px;flex-wrap:wrap;height:auto;padding-top:12px;padding-bottom:12px}.main{padding:14px}.bal-hero{padding:20px}.bal-num{font-size:32px}.logo-text{display:none}.net-badge{display:none}}
</style>
</head>
<body>";

        // ── Header ───────────────────────────────────────────────────────────
        private static string BuildHeader() => @"
<header class='hdr'>
  <a class='logo' href='/explorer'>
    <div class='logo-icon'>W</div>
    <div class='logo-text'>VFX <span>Wallet</span></div>
  </a>
  <div class='addr-wrap'>
    <div class='addr-grp'>
      <span class='addr-lbl'>VFX</span>
      <select id='addr-sel' class='addr-sel' onchange='onAddrChange()'><option value=''>Loading...</option></select>
    </div>
    <div class='addr-grp'>
      <span class='addr-lbl'>BTC</span>
      <select id='btc-sel' class='addr-sel' onchange='onBtcChange()'><option value=''>Loading...</option></select>
    </div>
    <button class='icon-btn' onclick='refreshAll()' title='Refresh'>&#8635;</button>
  </div>
  <div id='nb' class='net-badge'>Network</div>
</header>";

        // ── Tab Bar ──────────────────────────────────────────────────────────
        private static string BuildTabBar() => @"
<nav class='tab-bar'>
  <button class='tab-btn on' onclick='switchTab(""overview"",this)'>Overview</button>
  <button class='tab-btn' onclick='switchTab(""nfts"",this)'>NFTs</button>
  <button class='tab-btn' onclick='switchTab(""vbtc"",this)'>vBTC</button>
  <button class='tab-btn' onclick='switchTab(""btc"",this)'>Bitcoin</button>
  <button class='tab-btn' onclick='switchTab(""history"",this)'>History</button>
  <button class='tab-btn' onclick='switchTab(""privacy"",this)'>&#128274; Privacy</button>
</nav>";

        // ── Overview Panel ───────────────────────────────────────────────────
        private static string BuildOverviewPanel() => @"
  <!-- Overview -->
  <div id='p-overview' class='panel on'>
    <div id='bal-hero' class='bal-hero'>
      <div class='bal-main'>
        <div class='bal-lbl'>Available Balance</div>
        <div class='bal-num' id='bal-num'>-- <span>VFX</span></div>
        <div class='bal-locked' id='bal-locked'></div>
        <div class='bal-addr' id='bal-addr'></div>
      </div>
      <button class='send-btn' onclick='openSendVFX()'>&#8594; Send VFX</button>
    </div>
    <div id='tok-section'>
      <div class='sec-hdr'><span class='sec-ttl'>Token Balances</span></div>
      <div id='tok-content'><div class='ld'><span class='spin'></span>Loading tokens...</div></div>
    </div>
  </div>";

        // ── NFTs Panel ───────────────────────────────────────────────────────
        private static string BuildNftsPanel() => @"
  <!-- NFTs -->
  <div id='p-nfts' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>NFTs & Smart Contracts</span></div>
    <div id='nft-content'><div class='ld'><span class='spin'></span>Loading NFTs...</div></div>
  </div>";

        // ── vBTC Panel ───────────────────────────────────────────────────────
        private static string BuildVbtcPanel() => @"
  <!-- vBTC -->
  <div id='p-vbtc' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>vBTC Contracts</span></div>
    <div id='vbtc-content'><div class='ld'><span class='spin'></span>Loading vBTC...</div></div>
    <div id='bridge-history-section' style='display:none'>
      <div class='sec-hdr' style='margin-top:24px'><span class='sec-ttl'>&#127881; Bridge History</span></div>
      <div id='bridge-hist-content'></div>
    </div>
    <div id='burn-instructions' style='display:none;margin-top:24px;padding:20px;background:var(--surface);border:1px solid var(--border);border-radius:12px'>
      <div style='font-size:15px;font-weight:700;margin-bottom:10px'>&#128293; How to Burn vBTC.b (Exit from Base)</div>
      <div style='font-size:13px;color:var(--muted);line-height:1.6'>
        To convert vBTC.b back to vBTC on VerifiedX, burn the tokens on Base using an EVM wallet (MetaMask, etc.):<br><br>
        <strong>1.</strong> Open MetaMask connected to Base Sepolia (or Base Mainnet).<br>
        <strong>2.</strong> Call <code>burnForExit(amount)</code> on the vBTC.b contract via Basescan Write Contract tab.<br>
        <strong>3.</strong> The relay node detects the burn event and unlocks your vBTC on VerifiedX.
      </div>
    </div>
  </div>";

        // ── Bitcoin Panel ────────────────────────────────────────────────────
        private static string BuildBtcPanel() => @"
  <!-- Bitcoin -->
  <div id='p-btc' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>Bitcoin Accounts</span></div>
    <div id='btc-content'><div class='ld'><span class='spin'></span>Loading BTC accounts...</div></div>
  </div>";

        // ── History Panel ────────────────────────────────────────────────────
        private static string BuildHistoryPanel() => @"
  <!-- History -->
  <div id='p-history' class='panel'>
    <div class='sec-hdr'><span class='sec-ttl'>Transaction History</span></div>
    <div id='hist-content'><div class='ld'><span class='spin'></span>Loading transactions...</div></div>
  </div>";

        // ── Privacy Panel ────────────────────────────────────────────────────
        private static string BuildPrivacyPanel() => @"
  <!-- Privacy -->
  <div id='p-privacy' class='panel'>
    <div id='priv-hero' class='bal-hero' style='background:linear-gradient(135deg,#161b22 0%,#1a1833 100%)'>
      <div class='bal-main'>
        <div class='bal-lbl'>&#128274; Shielded VFX Balance</div>
        <div class='bal-num' id='priv-bal-num'>-- <span>VFX</span></div>
        <div id='priv-notes' class='muted' style='font-size:12px;margin-top:4px'></div>
        <div id='priv-zfx-addr' class='bal-addr'></div>
      </div>
      <div style='display:flex;flex-direction:column;gap:8px'>
        <button class='send-btn' onclick='openCreateZfx()' style='background:linear-gradient(135deg,#6e40c9,#bc8cff)'>+ Create Shielded Address</button>
        <button class='send-btn' onclick='openShield()'>&#8595; Shield VFX</button>
      </div>
    </div>
    <div id='priv-zfx-sel-wrap' style='margin-bottom:16px;display:none'>
      <div class='form-grp'>
        <label>Shielded Address</label>
        <select id='priv-zfx-sel' class='addr-sel' onchange='onZfxChange()' style='max-width:600px'><option value=''>No shielded addresses</option></select>
      </div>
    </div>
    <div id='priv-actions' style='display:none;margin-bottom:20px'>
      <div class='nft-actions'>
        <button class='act-btn prim' onclick='openUnshield()'>&#8593; Unshield</button>
        <button class='act-btn prim' onclick='openPrivTransfer()'>&#8596; Private Transfer</button>
        <button class='act-btn sec' onclick='doScanZfx()'>&#128269; Scan for Notes</button>
      </div>
    </div>
    <hr style='border:none;border-top:1px solid var(--border);margin:24px 0'>
    <div id='vbtc-priv-hero' class='bal-hero' style='background:linear-gradient(135deg,#161b22 0%,#1a2219 100%)'>
      <div class='bal-main'>
        <div class='bal-lbl'>&#8383; Shielded vBTC Balance</div>
        <div class='bal-num' id='vbtc-priv-bal-num'>-- <span>vBTC</span></div>
        <div id='vbtc-priv-notes' class='muted' style='font-size:12px;margin-top:4px'>Select a vBTC contract below</div>
      </div>
      <div style='display:flex;flex-direction:column;gap:8px'>
        <button class='send-btn' onclick='openShieldVbtc()' style='background:linear-gradient(135deg,#e3b341,#f0883e)'>&#8595; Shield vBTC</button>
      </div>
    </div>
    <div id='vbtc-priv-sel-wrap' style='margin-bottom:16px;display:none'>
      <div class='form-grp'>
        <label>vBTC Contract</label>
        <select id='vbtc-priv-sc-sel' class='addr-sel' onchange='onVbtcPrivScChange()' style='max-width:600px'><option value=''>No vBTC contracts</option></select>
      </div>
    </div>
    <div id='vbtc-priv-actions' style='display:none;margin-bottom:20px'>
      <div class='nft-actions'>
        <button class='act-btn prim' onclick='openUnshieldVbtc()'>&#8593; Unshield vBTC</button>
        <button class='act-btn prim' onclick='openPrivTransferVbtc()'>&#8596; Private Transfer vBTC</button>
        <button class='act-btn sec' onclick='doScanVbtc()'>&#128269; Scan vBTC Notes</button>
      </div>
    </div>
    <div id='vbtc-priv-pool-content'></div>
    <hr style='border:none;border-top:1px solid var(--border);margin:24px 0'>
    <div class='sec-hdr'><span class='sec-ttl'>System Status</span></div>
    <div id='priv-status-content'><div class='ld'><span class='spin'></span>Loading privacy status...</div></div>
  </div>";

        // ── Send VFX Modal ───────────────────────────────────────────────────
        private static string BuildSendVfxModal() => @"
<!-- Send VFX Modal -->
<div class='overlay' id='send-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>Send VFX</div>
      <button class='modal-close' onclick='closeSend()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From</label>
      <input class='form-inp' id='s-from' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Address</label>
      <input class='form-inp' id='s-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='s-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='msg' id='s-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeSend()'>Cancel</button>
      <button class='btn-prim' id='s-btn' onclick='doSendVFX()'>Send</button>
    </div>
  </div>
</div>";

        // ── Transfer NFT Modal ───────────────────────────────────────────────
        private static string BuildNftTransferModal() => @"
<!-- Transfer NFT Modal -->
<div class='overlay' id='nft-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>Transfer NFT</div>
      <button class='modal-close' onclick='closeNFT()'>&#215;</button>
    </div>
    <input type='hidden' id='nft-scuid'>
    <div class='form-grp'>
      <label>NFT</label>
      <input class='form-inp' id='nft-name-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Address</label>
      <input class='form-inp' id='nft-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='msg' id='nft-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeNFT()'>Cancel</button>
      <button class='btn-prim' id='nft-btn' onclick='doTransferNFT()'>Transfer</button>
    </div>
  </div>
</div>";

        // ── Transfer Token Modal ─────────────────────────────────────────────
        private static string BuildTokenTransferModal() => @"
<!-- Transfer Token Modal -->
<div class='overlay' id='tok-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>Send Token</div>
      <button class='modal-close' onclick='closeTok()'>&#215;</button>
    </div>
    <input type='hidden' id='tok-scuid'>
    <input type='hidden' id='tok-from-addr'>
    <div class='form-grp'>
      <label>Token</label>
      <input class='form-inp' id='tok-name-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Address</label>
      <input class='form-inp' id='tok-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='form-grp'>
      <label>Amount</label>
      <input class='form-inp' id='tok-amount' type='text' placeholder='0.00'>
    </div>
    <div class='msg' id='tok-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeTok()'>Cancel</button>
      <button class='btn-prim' id='tok-btn' onclick='doTransferToken()'>Send</button>
    </div>
  </div>
</div>";

        // ── vBTC Withdrawal Modal ────────────────────────────────────────────
        private static string BuildVbtcWithdrawModal() => @"
<!-- vBTC Withdrawal Modal -->
<div class='overlay' id='wd-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; vBTC Withdrawal</div>
      <button class='modal-close' onclick='closeWD()'>&#215;</button>
    </div>
    <input type='hidden' id='wd-scuid'>
    <input type='hidden' id='wd-owner'>
    <div class='form-grp'>
      <label>Contract</label>
      <input class='form-inp' id='wd-contract-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Destination BTC Address</label>
      <input class='form-inp' id='wd-btcaddr' type='text' placeholder='Enter Bitcoin address to receive BTC...'>
    </div>
    <div class='form-grp'>
      <label>Amount (BTC)</label>
      <input class='form-inp' id='wd-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Fee Rate (sat/vbyte)</label>
      <input class='form-inp' id='wd-fee' type='text' placeholder='10' value='10'>
    </div>
    <div class='msg' id='wd-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeWD()'>Cancel</button>
      <button class='btn-prim' id='wd-btn' onclick='doWDRequest()'>Request Withdrawal</button>
    </div>
  </div>
</div>";

        // ── vBTC Complete Withdrawal Modal ────────────────────────────────────
        private static string BuildVbtcCompleteModal() => @"
<!-- vBTC Complete Withdrawal Modal -->
<div class='overlay' id='wdc-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; Complete Withdrawal</div>
      <button class='modal-close' onclick='closeWDC()'>&#215;</button>
    </div>
    <input type='hidden' id='wdc-scuid'>
    <input type='hidden' id='wdc-hash'>
    <div class='form-grp'>
      <label>Contract</label>
      <input class='form-inp' id='wdc-contract-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Pending Amount</label>
      <input class='form-inp' id='wdc-amount-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Destination</label>
      <input class='form-inp' id='wdc-dest-disp' type='text' readonly>
    </div>
    <div style='padding:12px;background:rgba(227,179,65,.1);border:1px solid rgba(227,179,65,.2);border-radius:8px;font-size:13px;color:var(--orange);margin-bottom:12px'>
      This will coordinate a FROST MPC signing ceremony with validators and broadcast the Bitcoin transaction.
    </div>
    <div class='msg' id='wdc-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeWDC()'>Cancel</button>
      <button class='btn-prim' id='wdc-btn' onclick='doWDComplete()'>Complete Withdrawal</button>
    </div>
  </div>
</div>";

        // ── Send vBTC Modal ──────────────────────────────────────────────────
        private static string BuildVbtcSendModal() => @"
<!-- Send vBTC Modal -->
<div class='overlay' id='vbtc-tx-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; Send vBTC</div>
      <button class='modal-close' onclick='closeVBTCTx()'>&#215;</button>
    </div>
    <input type='hidden' id='vbtc-tx-scuid'>
    <input type='hidden' id='vbtc-tx-from'>
    <div class='form-grp'>
      <label>Contract</label>
      <input class='form-inp' id='vbtc-tx-contract-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>From</label>
      <input class='form-inp' id='vbtc-tx-from-disp' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To (VFX Address)</label>
      <input class='form-inp' id='vbtc-tx-to' type='text' placeholder='Enter destination VFX address...'>
    </div>
    <div class='form-grp'>
      <label>Amount (vBTC)</label>
      <input class='form-inp' id='vbtc-tx-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='msg' id='vbtc-tx-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeVBTCTx()'>Cancel</button>
      <button class='btn-prim' id='vbtc-tx-btn' onclick='doVBTCTransfer()'>Send vBTC</button>
    </div>
  </div>
</div>";

        // ── Bridge to Base Modal ────────────────────────────────────────────
        private static string BuildBridgeToBaseModal() => @"
<!-- Bridge to Base Modal -->
<div class='overlay' id='bridge-overlay'>
  <div class='modal' style='max-width:520px'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#127881; Bridge vBTC to Base</div>
      <button class='modal-close' onclick='closeBridge()'>&#215;</button>
    </div>
    <input type='hidden' id='br-scuid'>
    <input type='hidden' id='br-owner'>
    <input type='hidden' id='br-derived-addr-val'>
    <div id='br-loading' style='text-align:center;padding:24px;display:none'><span class='spin'></span> Loading bridge info...</div>
    <div id='br-body'>
      <div class='form-grp'>
        <label>vBTC Contract</label>
        <input class='form-inp' id='br-contract-disp' type='text' readonly>
      </div>
      <div class='form-grp'>
        <label>Available vBTC to Bridge</label>
        <input class='form-inp' id='br-bal-disp' type='text' readonly>
      </div>
      <!-- Derived Base address section -->
      <div id='br-derived-section' style='background:var(--surface2);border:1px solid var(--border);border-radius:10px;padding:14px 16px;margin-bottom:16px'>
        <div style='font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:var(--muted);margin-bottom:8px'>Your Derived Base Address</div>
        <div id='br-derived-addr' style='font-family:monospace;font-size:12px;color:var(--accent);word-break:break-all;margin-bottom:8px'>Loading...</div>
        <div style='display:flex;gap:12px;flex-wrap:wrap'>
          <div id='br-eth-bal-wrap' style='font-size:12px'><span class='muted'>ETH:</span> <span id='br-eth-bal' class='grn'>--</span></div>
          <div id='br-vbtcb-bal-wrap' style='font-size:12px'><span class='muted'>vBTC.b:</span> <span id='br-vbtcb-bal' class='org'>--</span></div>
          <div id='br-network-wrap' style='font-size:11px;color:var(--muted)'>(<span id='br-network-name'>Base</span>)</div>
        </div>
      </div>
      <div class='form-grp'>
        <label>Amount (vBTC)</label>
        <input class='form-inp' id='br-amount' type='text' placeholder='0.00000000'>
      </div>
      <!-- Destination toggle -->
      <div class='form-grp'>
        <label>Destination Base Address</label>
        <div style='display:flex;gap:8px;margin-bottom:6px'>
          <label style='font-size:12px;color:var(--text);cursor:pointer;display:flex;align-items:center;gap:4px;text-transform:none;letter-spacing:0;font-weight:500'>
            <input type='radio' name='br-dest-mode' value='derived' checked onchange='brDestMode(this.value)'> Use my derived address
          </label>
          <label style='font-size:12px;color:var(--text);cursor:pointer;display:flex;align-items:center;gap:4px;text-transform:none;letter-spacing:0;font-weight:500'>
            <input type='radio' name='br-dest-mode' value='custom' onchange='brDestMode(this.value)'> Send to a different address
          </label>
        </div>
        <input class='form-inp' id='br-evm' type='text' placeholder='0x...' readonly style='color:var(--accent)'>
      </div>
      <div style='padding:12px;background:rgba(88,166,255,.08);border:1px solid rgba(88,166,255,.15);border-radius:8px;font-size:12px;color:var(--muted);margin-bottom:12px;line-height:1.5'>
        <strong style='color:var(--accent)'>How it works:</strong> This locks your vBTC on VerifiedX. Validators automatically attest the lock and a caster submits <code>mintWithProof</code> on Base &mdash; <strong>you do not pay Base gas</strong>. The vBTC.b ERC-20 tokens appear at the destination address.<br><br>
        <strong style='color:var(--orange)'>Note:</strong> To later transfer or burn vBTC.b on Base, the destination address will need ETH for gas.
      </div>
      <div class='msg' id='br-msg'></div>
      <div class='modal-foot'>
        <button class='btn-sec' onclick='closeBridge()'>Cancel</button>
        <button class='btn-prim' id='br-btn' onclick='doBridgeToBase()'>Bridge to Base</button>
      </div>
    </div>
  </div>
</div>";

        // ── Create Shielded Address Modal ────────────────────────────────────
        private static string BuildCreateZfxModal() => @"
<!-- Create Shielded Address Modal -->
<div class='overlay' id='czfx-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#128274; Create Shielded Address</div>
      <button class='modal-close' onclick='closeCZfx()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>Transparent VFX Address</label>
      <select id='czfx-addr' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>Password (min 8 chars, protects spending key)</label>
      <input class='form-inp' id='czfx-pwd' type='password' placeholder='Enter password...'>
    </div>
    <div style='padding:12px;background:rgba(188,140,255,.1);border:1px solid rgba(188,140,255,.2);border-radius:8px;font-size:13px;color:var(--purple);margin-bottom:12px'>
      This derives a <code>zfx_</code> shielded address from your account. The password encrypts your spending key at rest.
    </div>
    <div class='msg' id='czfx-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeCZfx()'>Cancel</button>
      <button class='btn-prim' id='czfx-btn' onclick='doCreateZfx()'>Create</button>
    </div>
  </div>
</div>";

        // ── Shield VFX Modal ─────────────────────────────────────────────────
        private static string BuildShieldModal() => @"
<!-- Shield VFX Modal -->
<div class='overlay' id='shield-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8595; Shield VFX (T&#8594;Z)</div>
      <button class='modal-close' onclick='closeShield()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Transparent Address</label>
      <select id='sh-from' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>To Shielded Address (zfx_)</label>
      <input class='form-inp' id='sh-zfx' type='text' placeholder='zfx_...'>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='sh-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='msg' id='sh-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeShield()'>Cancel</button>
      <button class='btn-prim' id='sh-btn' onclick='doShield()'>Shield</button>
    </div>
  </div>
</div>";

        // ── Unshield VFX Modal ───────────────────────────────────────────────
        private static string BuildUnshieldModal() => @"
<!-- Unshield VFX Modal -->
<div class='overlay' id='unshield-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8593; Unshield VFX (Z&#8594;T)</div>
      <button class='modal-close' onclick='closeUnshield()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Shielded Address</label>
      <input class='form-inp' id='ush-zfx' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Transparent Address</label>
      <select id='ush-to' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='ush-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Spending Password</label>
      <input class='form-inp' id='ush-pwd' type='password' placeholder='Password used when creating shielded address'>
    </div>
    <div class='msg' id='ush-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeUnshield()'>Cancel</button>
      <button class='btn-prim' id='ush-btn' onclick='doUnshield()'>Unshield</button>
    </div>
  </div>
</div>";

        // ── Private Transfer Modal ───────────────────────────────────────────
        private static string BuildPrivateTransferModal() => @"
<!-- Private Transfer Modal -->
<div class='overlay' id='ptx-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8596; Private Transfer (Z&#8594;Z)</div>
      <button class='modal-close' onclick='closePTx()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Shielded Address</label>
      <input class='form-inp' id='ptx-from' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Shielded Address (zfx_)</label>
      <input class='form-inp' id='ptx-to' type='text' placeholder='zfx_...'>
    </div>
    <div class='form-grp'>
      <label>Amount (VFX)</label>
      <input class='form-inp' id='ptx-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Spending Password</label>
      <input class='form-inp' id='ptx-pwd' type='password' placeholder='Password used when creating shielded address'>
    </div>
    <div class='msg' id='ptx-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closePTx()'>Cancel</button>
      <button class='btn-prim' id='ptx-btn' onclick='doPrivTransfer()'>Send</button>
    </div>
  </div>
</div>";

        // ── Send BTC Modal ───────────────────────────────────────────────────
        private static string BuildSendBtcModal() => @"
<!-- Send BTC Modal -->
<div class='overlay' id='btc-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8383; Send Bitcoin</div>
      <button class='modal-close' onclick='closeBtcSend()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From (BTC Address)</label>
      <input class='form-inp' id='btc-s-from' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To (BTC Address)</label>
      <input class='form-inp' id='btc-s-to' type='text' placeholder='Enter destination Bitcoin address...'>
    </div>
    <div class='form-grp'>
      <label>Amount (BTC)</label>
      <input class='form-inp' id='btc-s-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Fee Rate (sat/vbyte)</label>
      <input class='form-inp' id='btc-s-fee' type='text' placeholder='10' value='10'>
    </div>
    <div class='msg' id='btc-s-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeBtcSend()'>Cancel</button>
      <button class='btn-prim' id='btc-s-btn' onclick='doSendBTC()'>Send BTC</button>
    </div>
  </div>
</div>";

        // ── Shield vBTC Modal ────────────────────────────────────────────────
        private static string BuildShieldVbtcModal() => @"
<!-- Shield vBTC Modal -->
<div class='overlay' id='shvbtc-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8595; Shield vBTC (T&#8594;Z)</div>
      <button class='modal-close' onclick='closeShieldVbtc()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Transparent Address</label>
      <select id='shvbtc-from' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>To Shielded Address (zfx_)</label>
      <input class='form-inp' id='shvbtc-zfx' type='text' placeholder='zfx_...'>
    </div>
    <div class='form-grp'>
      <label>vBTC Contract</label>
      <select id='shvbtc-sc' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>Amount (vBTC)</label>
      <input class='form-inp' id='shvbtc-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='msg' id='shvbtc-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeShieldVbtc()'>Cancel</button>
      <button class='btn-prim' id='shvbtc-btn' onclick='doShieldVbtc()'>Shield vBTC</button>
    </div>
  </div>
</div>";

        // ── Unshield vBTC Modal ──────────────────────────────────────────────
        private static string BuildUnshieldVbtcModal() => @"
<!-- Unshield vBTC Modal -->
<div class='overlay' id='ushvbtc-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8593; Unshield vBTC (Z&#8594;T)</div>
      <button class='modal-close' onclick='closeUnshieldVbtc()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Shielded Address</label>
      <input class='form-inp' id='ushvbtc-zfx' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Transparent Address</label>
      <select id='ushvbtc-to' class='form-inp' style='cursor:pointer'></select>
    </div>
    <div class='form-grp'>
      <label>vBTC Contract</label>
      <input class='form-inp' id='ushvbtc-sc' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Amount (vBTC)</label>
      <input class='form-inp' id='ushvbtc-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Spending Password</label>
      <input class='form-inp' id='ushvbtc-pwd' type='password' placeholder='Password used when creating shielded address'>
    </div>
    <div class='msg' id='ushvbtc-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closeUnshieldVbtc()'>Cancel</button>
      <button class='btn-prim' id='ushvbtc-btn' onclick='doUnshieldVbtc()'>Unshield vBTC</button>
    </div>
  </div>
</div>";

        // ── Private Transfer vBTC Modal ──────────────────────────────────────
        private static string BuildPrivateTransferVbtcModal() => @"
<!-- Private Transfer vBTC Modal -->
<div class='overlay' id='ptxvbtc-overlay'>
  <div class='modal'>
    <div class='modal-hdr'>
      <div class='modal-ttl'>&#8596; Private Transfer vBTC (Z&#8594;Z)</div>
      <button class='modal-close' onclick='closePTxVbtc()'>&#215;</button>
    </div>
    <div class='form-grp'>
      <label>From Shielded Address</label>
      <input class='form-inp' id='ptxvbtc-from' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>To Shielded Address (zfx_)</label>
      <input class='form-inp' id='ptxvbtc-to' type='text' placeholder='zfx_...'>
    </div>
    <div class='form-grp'>
      <label>vBTC Contract</label>
      <input class='form-inp' id='ptxvbtc-sc' type='text' readonly>
    </div>
    <div class='form-grp'>
      <label>Amount (vBTC)</label>
      <input class='form-inp' id='ptxvbtc-amount' type='text' placeholder='0.00000000'>
    </div>
    <div class='form-grp'>
      <label>Spending Password</label>
      <input class='form-inp' id='ptxvbtc-pwd' type='password' placeholder='Password used when creating shielded address'>
    </div>
    <div class='msg' id='ptxvbtc-msg'></div>
    <div class='modal-foot'>
      <button class='btn-sec' onclick='closePTxVbtc()'>Cancel</button>
      <button class='btn-prim' id='ptxvbtc-btn' onclick='doPrivTransferVbtc()'>Send vBTC</button>
    </div>
  </div>
</div>";

        // ── JavaScript ───────────────────────────────────────────────────────
        private static string BuildScript() => @"
<script>
(function(){
var accounts=[],btcAccounts=[],selAddr=null,selBtcAddr=null,activeTab='overview',tabLoaded={};

document.addEventListener('DOMContentLoaded',function(){
  loadAccounts();
  loadBTC();
  setNB();
});

function setNB(){
  fetch('/explorer/api/stats').then(function(r){return r.json();}).then(function(d){
    el('nb').textContent=d.isTestNet?'Testnet':'Mainnet';
  }).catch(function(){});
}

/* ---- Accounts ---- */
function loadAccounts(){
  fetch('/wallet/api/accounts').then(function(r){return r.json();}).then(function(data){
    accounts=data;
    var sel=el('addr-sel');
    sel.innerHTML='';
    if(!data||!data.length){
      sel.innerHTML='<option value="""">No accounts found</option>';
      return;
    }
    data.forEach(function(a){
      var opt=document.createElement('option');
      opt.value=a.address;
      opt.textContent=a.address.substring(0,20)+'... | '+fmtBal(a.balance)+' VFX'+(a.adnr?' | '+a.adnr:'');
      sel.appendChild(opt);
    });
    selAddr=data[0].address;
    renderOverview(data[0]);
    tabLoaded={overview:true};
  }).catch(function(){
    el('addr-sel').innerHTML='<option value="""">Error loading accounts</option>';
  });
}

window.onAddrChange=function(){
  selAddr=el('addr-sel').value;
  tabLoaded={};
  var acc=accounts.find(function(a){return a.address===selAddr;});
  if(acc)renderOverview(acc);
  if(activeTab!=='overview')loadTab(activeTab);
};

window.refreshAll=function(){
  tabLoaded={};
  loadAccounts();
  loadBTC();
};

/* ---- Tab switching ---- */
window.switchTab=function(tab,btn){
  activeTab=tab;
  document.querySelectorAll('.tab-btn').forEach(function(b){b.classList.remove('on');});
  if(btn)btn.classList.add('on');
  document.querySelectorAll('.panel').forEach(function(p){p.classList.remove('on');});
  el('p-'+tab).classList.add('on');
  if(!tabLoaded[tab])loadTab(tab);
};

function loadTab(tab){
  tabLoaded[tab]=true;
  if(tab==='nfts')loadNFTs();
  else if(tab==='vbtc')loadVBTC();
  else if(tab==='btc')loadBTC();
  else if(tab==='history')loadHistory();
  else if(tab==='privacy')loadPrivacy();
}

/* ---- Overview ---- */
function renderOverview(acc){
  el('bal-num').innerHTML=fmtBal(acc.balance)+'<span>VFX</span>';
  el('bal-addr').textContent=acc.address+(acc.adnr?' ('+acc.adnr+')':'');
  if(acc.lockedBalance&&acc.lockedBalance>0){
    el('bal-locked').textContent='Locked: '+fmtBal(acc.lockedBalance)+' VFX';
  }else{
    el('bal-locked').textContent='';
  }
  renderTokens(acc.tokens||[]);
}

function renderTokens(tokens){
  var c=el('tok-content');
  if(!tokens||!tokens.length){
    c.innerHTML='<div class=""empty""><div class=""empty-icon"">&#128296;</div><div>No tokens found for this address</div></div>';
    return;
  }
  var rows=tokens.map(function(t){
    var ticker=t.ticker||t.name||'?';
    var initials=ticker.substring(0,2).toUpperCase();
    return '<tr>'+
      '<td><div style=""display:flex;align-items:center;gap:10px""><div class=""tok-icon"">'+esc(initials)+'</div><div><div style=""font-weight:600"">'+esc(t.name)+'</div><div class=""muted"" style=""font-size:11px"">'+esc(t.ticker)+'</div></div></div></td>'+
      '<td class=""grn"" style=""font-weight:700;font-variant-numeric:tabular-nums"">'+fmtTok(t.balance,t.decimals)+'</td>'+
      '<td class=""muted"" style=""font-variant-numeric:tabular-nums"">'+fmtTok(t.lockedBalance,t.decimals)+'</td>'+
      '<td><button class=""act-btn prim"" onclick=""openSendToken(\''+esc(t.scUID)+'\',\''+esc(t.name)+'\',\''+esc(t.ticker)+'\')"">&rarr; Send</button></td>'+
      '</tr>';
  }).join('');
  c.innerHTML='<div class=""tbl-wrap""><table class=""dtbl""><thead><tr><th>Token</th><th>Balance</th><th>Locked</th><th></th></tr></thead><tbody>'+rows+'</tbody></table></div>';
}

/* ---- NFTs ---- */
function loadNFTs(){
  if(!selAddr)return;
  el('nft-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading NFTs...</div>';
  fetch('/wallet/api/nfts/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(renderNFTs)
    .catch(function(){el('nft-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load NFTs</div></div>';});
}

function renderNFTs(nfts){
  if(!nfts||!nfts.length){
    el('nft-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#128444;</div><div>No NFTs found for this address</div></div>';
    return;
  }
  var cards=nfts.map(function(n){
    var icon=n.isToken?'&#128296;':'&#127760;';
    var badge=n.isToken?'<span class=""badge badge-tok"">Token</span>':'<span class=""badge badge-nft"">NFT</span>';
    var pubBadge=n.isPublished?'<span class=""badge badge-ok"" style=""font-size:10px"">Published</span>':'<span class=""badge badge-pend"" style=""font-size:10px"">Draft</span>';
    var uid=n.scUID?n.scUID.substring(0,20)+'...':'N/A';
    return '<div class=""nft-card"">'+
      '<div class=""nft-icon"">'+icon+'</div>'+
      '<div class=""nft-name"">'+esc(n.name||'Unnamed')+'</div>'+
      '<div style=""display:flex;gap:6px;flex-wrap:wrap"">'+badge+pubBadge+'</div>'+
      '<div class=""nft-uid"" title=""'+esc(n.scUID)+'"">'+uid+'</div>'+
      (n.minterName?'<div class=""muted"" style=""font-size:11px"">By: '+esc(n.minterName)+'</div>':'')+
      '<div class=""nft-actions"">'+
      '<button class=""act-btn prim"" onclick=""openTransferNFT(\''+esc(n.scUID)+'\',\''+esc(n.name||'Unnamed')+'\')"">&rarr; Transfer</button>'+
      '</div>'+
      '</div>';
  }).join('');
  el('nft-content').innerHTML='<div class=""nft-grid"">'+cards+'</div>';
}

/* ---- vBTC ---- */
function loadVBTC(){
  if(!selAddr)return;
  el('vbtc-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading vBTC...</div>';
  fetch('/wallet/api/vbtc/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(renderVBTC)
    .catch(function(){el('vbtc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load vBTC</div></div>';});
}

function renderVBTC(contracts){
  if(!contracts||!contracts.length){
    el('vbtc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#8383;</div><div>No vBTC contracts found for this address</div></div>';
    return;
  }
  var cards=contracts.map(function(c){
    var statusCls=c.withdrawalStatus==='None'?'badge-ok':c.withdrawalStatus==='Pending'?'badge-pend':'badge-tok';
    var canRequest=c.balance>0&&(c.withdrawalStatus==='None'||c.withdrawalStatus==='Completed');
    var canComplete=c.withdrawalStatus==='Requested';
    var btns='<div class=""nft-actions"" style=""margin-top:6px"">';
    if(c.balance>0)btns+='<button class=""act-btn prim"" onclick=""openVBTCTx(\''+esc(c.scUID)+'\','+c.balance+')"">&rarr; Send vBTC</button>';
    if(c.balance>0)btns+='<button class=""act-btn prim"" style=""background:rgba(88,166,255,.15);border-color:rgba(88,166,255,.4)"" onclick=""openBridge(\''+esc(c.scUID)+'\',\''+esc(c.ownerAddress)+'\','+c.balance+')"">&#127881; Bridge to Base</button>';
    if(canRequest)btns+='<button class=""act-btn prim"" onclick=""openWD(\''+esc(c.scUID)+'\',\''+esc(c.ownerAddress)+'\','+c.balance+')"">&darr; Withdraw</button>';
    if(canComplete)btns+='<button class=""act-btn sec"" onclick=""openWDC(\''+esc(c.scUID)+'\','+c.activeWithdrawalAmount+',\''+esc(c.activeWithdrawalDest||'')+'\')"">&check; Complete Withdrawal</button>';
    btns+='</div>';
    return '<div class=""vbtc-card"">'+
      '<div class=""muted"" style=""font-size:11px;font-family:monospace"">'+esc(c.scUID||'')+'</div>'+
      '<div class=""vbtc-bal"">'+fmtBal(c.balance)+'<span>vBTC</span></div>'+
      '<div class=""vbtc-row""><span class=""k"">BTC Deposit</span><span class=""v"">'+esc(c.depositAddress||'N/A')+'</span></div>'+
      '<div class=""vbtc-row""><span class=""k"">Withdrawal Status</span><span class=""badge '+statusCls+'"">'+esc(c.withdrawalStatus)+'</span></div>'+
      (c.activeWithdrawalAmount?'<div class=""vbtc-row""><span class=""k"">Pending Withdrawal</span><span class=""v org"">'+c.activeWithdrawalAmount+' BTC &rarr; '+esc(c.activeWithdrawalDest||'')+'</span></div>':'')+
      '<div class=""vbtc-row""><span class=""k"">Validators</span><span class=""v"">'+c.totalValidators+' (threshold: '+c.requiredThreshold+')</span></div>'+
      '<div class=""vbtc-row""><span class=""k"">Proof Block</span><span class=""v"">#'+c.proofBlockHeight+'</span></div>'+
      btns+
      '</div>';
  }).join('');
  el('vbtc-content').innerHTML='<div class=""vbtc-grid"">'+cards+'</div>';
  loadBridgeHistory();
  startBridgePoll();
  el('burn-instructions').style.display='block';
}

/* ---- Bridge to Base ---- */
window.openBridge=function(scUID,owner,bal){
  el('br-scuid').value=scUID;el('br-owner').value=owner;
  el('br-contract-disp').value=scUID;el('br-bal-disp').value=fmtBal(bal)+' vBTC';
  el('br-amount').value='';el('br-evm').value='';el('br-evm').readOnly=true;
  el('br-evm').style.color='var(--accent)';
  hideMsg('br-msg');
  el('br-derived-addr').textContent='Loading...';
  el('br-derived-addr-val').value='';
  el('br-eth-bal').textContent='--';el('br-vbtcb-bal').textContent='--';
  el('br-network-name').textContent='Base';
  var radios=document.querySelectorAll('input[name=""br-dest-mode""]');
  if(radios.length>0)radios[0].checked=true;
  el('br-loading').style.display='block';el('br-body').style.opacity='0.4';
  el('bridge-overlay').classList.add('on');
  fetch('/wallet/api/vbtc/bridge/preflight/'+encodeURIComponent(owner)+'/'+encodeURIComponent(scUID))
    .then(function(r){return r.json();}).then(function(d){
      el('br-loading').style.display='none';el('br-body').style.opacity='1';
      if(!d.success){showMsg('br-msg',d.message||'Failed to load bridge info.','err');return;}
      el('br-bal-disp').value=fmtBal(d.availableVbtc)+' vBTC';
      if(d.hasDerivedAddress){
        el('br-derived-addr').textContent=d.derivedBaseAddress;
        el('br-derived-addr-val').value=d.derivedBaseAddress;
        el('br-evm').value=d.derivedBaseAddress;
      }else{
        el('br-derived-addr').textContent='Could not derive (account not found)';
        el('br-derived-addr').style.color='var(--red)';
      }
      if(d.ethBalance!=null){el('br-eth-bal').textContent=fmtBal(d.ethBalance)+' ETH';}
      else{el('br-eth-bal').textContent=d.ethError||'N/A';}
      if(d.vbtcBBalance!=null){el('br-vbtcb-bal').textContent=fmtBal(d.vbtcBBalance)+' vBTC.b';}
      else{el('br-vbtcb-bal').textContent=d.vbtcBError||'N/A';}
      el('br-network-name').textContent=d.networkName||'Base';
      if(!d.bridgeConfigured){showMsg('br-msg','Bridge not configured on this node. Set BaseBridgeV2Contract and BaseBridgeRpcUrl in config.txt.','err');}
    }).catch(function(e){
      el('br-loading').style.display='none';el('br-body').style.opacity='1';
      showMsg('br-msg','Failed to load bridge info: '+(e.message||''),'err');
    });
};
window.brDestMode=function(mode){
  var inp=el('br-evm');
  if(mode==='derived'){
    var derived=el('br-derived-addr-val').value;
    inp.value=derived||'';inp.readOnly=true;inp.style.color='var(--accent)';
  }else{
    inp.readOnly=false;inp.style.color='var(--text)';inp.placeholder='0x...';
    if(inp.value===el('br-derived-addr-val').value)inp.value='';
    inp.focus();
  }
};
window.closeBridge=function(){el('bridge-overlay').classList.remove('on');};
window.doBridgeToBase=function(){
  var scUID=el('br-scuid').value,owner=el('br-owner').value,amt=el('br-amount').value.trim(),evm=el('br-evm').value.trim();
  if(!amt||!evm){showMsg('br-msg','Fill amount and EVM destination.','err');return;}
  if(parseFloat(amt)<=0){showMsg('br-msg','Amount must be greater than 0.','err');return;}
  if(!evm.startsWith('0x')||evm.length!==42){showMsg('br-msg','Invalid EVM address. Must be 0x + 40 hex characters.','err');return;}
  var btn=el('br-btn');btn.disabled=true;btn.textContent='Bridging...';
  fetch('/wallet/api/vbtc/bridge/toBase',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ScUID:scUID,OwnerAddress:owner,Amount:amt,EvmDestination:evm})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Bridge to Base';
    if(d.success){showMsg('br-msg','Bridge lock created! Lock ID: '+(d.lockId||'')+'. Validators will attest and casters will mint vBTC.b on Base automatically.','ok');
      setTimeout(function(){closeBridge();tabLoaded.vbtc=false;loadVBTC();},3000);
    }else{showMsg('br-msg',d.message||'Bridge failed.','err');}
  }).catch(function(e){btn.disabled=false;btn.textContent='Bridge to Base';showMsg('br-msg',e.message||'Failed.','err');});
};
function loadBridgeHistory(){
  if(!selAddr)return;
  fetch('/vbtcapi/VBTC/GetBridgeLocksByOwner/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(function(data){
    var locks=data.Locks||data.locks||data;
    if(!locks||!locks.length){el('bridge-history-section').style.display='none';return;}
    el('bridge-history-section').style.display='block';
    var rows=locks.map(function(lk){
      var st=lk.Status||lk.status||'Unknown';
      var sCls=st==='Minted'?'badge-ok':st==='Failed'?'badge-fail':'badge-pend';
      return '<tr><td><code class=""muted"" style=""cursor:pointer;word-break:break-all"" title=""Click to copy"" onclick=""navigator.clipboard.writeText(this.textContent)"">'+(lk.LockId||lk.lockId||'')+'</code></td>'+
        '<td>'+fmtBal(lk.Amount||lk.amount||0)+' vBTC</td>'+
        '<td><code class=""muted"" title=""'+esc(lk.EvmDestination||lk.evmDestination||'')+'"">'+ shn(lk.EvmDestination||lk.evmDestination||'',14)+'</code></td>'+
        '<td><span class=""badge '+sCls+'"">'+esc(st)+'</span></td>'+
        '<td>'+(lk.BaseTxHash||lk.baseTxHash?'<code class=""muted"">'+shn(lk.BaseTxHash||lk.baseTxHash,12)+'</code>':'--')+'</td></tr>';
    }).join('');
    el('bridge-hist-content').innerHTML='<div class=""tbl-wrap""><table class=""dtbl""><thead><tr><th>Lock ID</th><th>Amount</th><th>EVM Dest</th><th>Status</th><th>Base TX</th></tr></thead><tbody>'+rows+'</tbody></table></div>';
  }).catch(function(){el('bridge-history-section').style.display='none';});
}
var _bridgePollTimer=null;
function startBridgePoll(){if(_bridgePollTimer)clearInterval(_bridgePollTimer);_bridgePollTimer=setInterval(function(){loadBridgeHistory();},15000);}
function stopBridgePoll(){if(_bridgePollTimer){clearInterval(_bridgePollTimer);_bridgePollTimer=null;}}

/* ---- Bitcoin ---- */
function loadBTC(){
  el('btc-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading Bitcoin accounts...</div>';
  Promise.all([
    fetch('/wallet/api/btc').then(function(r){return r.json();}),
    fetch('/wallet/api/btc/base-balances').then(function(r){return r.json();}).catch(function(){return[];})
  ]).then(function(pair){
    var accs=pair[0]||[];
    var baseRows=pair[1]||[];
    var baseByBtc={};
    baseRows.forEach(function(b){ if(b&&b.btcAddress) baseByBtc[b.btcAddress]=b; });
    btcAccounts=accs.map(function(a){
      return { address:a.address, adnr:a.adnr, balance:a.balance, isValidating:a.isValidating, linkedEvmAddress:a.linkedEvmAddress, base: baseByBtc[a.address] };
    });
    var sel=el('btc-sel');
    sel.innerHTML='';
    if(!btcAccounts.length){
      sel.innerHTML='<option value="""">No BTC accounts</option>';
    }else{
      btcAccounts.forEach(function(a){
        var opt=document.createElement('option');
        opt.value=a.address;
        opt.textContent=a.address.substring(0,22)+'... | '+fmtBal(a.balance)+' BTC';
        sel.appendChild(opt);
      });
      if(!selBtcAddr)selBtcAddr=btcAccounts[0].address;
    }
    renderBTC(selBtcAddr?btcAccounts.filter(function(a){return a.address===selBtcAddr;}):btcAccounts);
  }).catch(function(){
    el('btc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load Bitcoin accounts</div></div>';
  });
}

window.linkBtcEvm=function(btcAddr){
  var cur='';
  try{
    var acc=(btcAccounts||[]).filter(function(a){return a.address===btcAddr;})[0];
    if(acc&&acc.linkedEvmAddress) cur=acc.linkedEvmAddress;
  }catch(e){}
  var evm=prompt('Paste Base EVM address (0x...) for this Bitcoin account. Leave empty and OK to clear:',cur);
  if(evm===null)return;
  evm=(''+evm).trim();
  fetch('/wallet/api/btc/link-evm',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({btcAddress:btcAddr,evmAddress:evm||null})})
    .then(function(r){return r.json();})
    .then(function(d){
      if(d.success) loadBTC();
      else alert(d.message||'Failed to link');
    }).catch(function(){ alert('Request failed'); });
};

window.onBtcChange=function(){
  selBtcAddr=el('btc-sel').value;
  renderBTC(selBtcAddr?btcAccounts.filter(function(a){return a.address===selBtcAddr;}):btcAccounts);
};

function baseNodeConfigHints(b){
  if(!b)return'';
  var h='';
  if(b.canReadEth===false){
    h+='<div class=""cfg-hint"" style=""font-size:11px;color:#c9a227;margin-top:6px;line-height:1.35""><strong>Node:</strong> set <code style=""font-size:10px"">BASE_BRIDGE_RPC_URL</code> to read native ETH on '+esc(b.network||'Base')+'.</div>';
  }
  if(b.canReadVbtc===false){
    h+='<div class=""cfg-hint"" style=""font-size:11px;color:#c9a227;margin-top:4px;line-height:1.35""><strong>Node:</strong> set <code style=""font-size:10px"">BASE_BRIDGE_CONTRACT</code> (and RPC) to read vBTC.b.</div>';
  }
  return h;
}

function renderBTC(accs){
  if(!accs||!accs.length){
    el('btc-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#8383;</div><div>No Bitcoin accounts found</div></div>';
    return;
  }
  var cards=accs.map(function(a){
    var baseHtml='';
    if(a.base){
      if(a.base.linkedEvmAddress){
        baseHtml='<div class=""muted"" style=""font-size:11px;margin-top:10px"">Base &mdash; '+esc(a.base.network||'')+'</div>'+
          baseNodeConfigHints(a.base)+
          '<div style=""font-family:monospace;font-size:11px;word-break:break-all;color:var(--text);margin-top:6px"">'+esc(a.base.linkedEvmAddress)+'</div>'+
          (a.base.canReadEth!==false&&a.base.ethBalance!=null?'<div class=""vbtc-row""><span class=""k"">ETH</span><span class=""v"">'+fmtBal(a.base.ethBalance)+'</span></div>':'')+
          (a.base.canReadEth!==false&&a.base.ethBalance==null&&a.base.ethMessage?'<div class=""muted"" style=""font-size:11px"">ETH: '+esc(a.base.ethMessage)+'</div>':'')+
          (a.base.canReadVbtc!==false&&a.base.vbtcBBalance!=null?'<div class=""vbtc-row""><span class=""k"">vBTC.b</span><span class=""v"">'+fmtBal(a.base.vbtcBBalance)+'</span></div>':'')+
          (a.base.canReadVbtc!==false&&a.base.vbtcBBalance==null&&a.base.vbtcMessage?'<div class=""muted"" style=""font-size:11px"">vBTC.b: '+esc(a.base.vbtcMessage)+'</div>':'');
      }else{
        baseHtml='<div class=""muted"" style=""font-size:11px;margin-top:10px"">Base &mdash; '+esc(a.base.network||'')+'</div>'+
          baseNodeConfigHints(a.base)+
          '<div class=""muted"" style=""font-size:12px;margin-top:8px"">'+esc(a.base.message||'Link an EVM address to view ETH and vBTC.b')+'</div>';
      }
    }
    return '<div class=""btc-card"">'+
      '<div class=""muted"" style=""font-size:11px"">Bitcoin Address</div>'+
      '<div class=""btc-bal"">'+fmtBal(a.balance)+'<span>BTC</span></div>'+
      '<div style=""font-family:monospace;font-size:12px;word-break:break-all;color:var(--text)"">'+esc(a.address||'N/A')+'</div>'+
      (a.adnr?'<div class=""muted"" style=""font-size:12px"">'+esc(a.adnr)+'</div>':'')+
      baseHtml+
      '<div class=""nft-actions"" style=""margin-top:8px;display:flex;flex-wrap:wrap;gap:6px"">'+
      '<button class=""act-btn prim"" onclick=""openSendBTC(\''+esc(a.address)+'\')"">&rarr; Send BTC</button>'+
      '<button class=""act-btn sec"" onclick=""linkBtcEvm(\''+esc(a.address)+'\')"">Link Base EVM</button>'+
      '</div>'+
      '</div>';
  }).join('');
  el('btc-content').innerHTML='<div class=""btc-grid"">'+cards+'</div>';
}

/* ---- History ---- */
function loadHistory(){
  if(!selAddr)return;
  el('hist-content').innerHTML='<div class=""ld""><span class=""spin""></span>Loading transactions...</div>';
  fetch('/wallet/api/txs/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(renderHistory)
    .catch(function(){el('hist-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#9888;</div><div>Failed to load transactions</div></div>';});
}

function renderHistory(txs){
  if(!txs||!txs.length){
    el('hist-content').innerHTML='<div class=""empty""><div class=""empty-icon"">&#128196;</div><div>No transactions found for this address</div></div>';
    return;
  }
  var rows=txs.map(function(tx){
    var dir=tx.fromAddress===selAddr?'out':'in';
    var dirLbl=dir==='out'?'<span class=""dir-out"">&#8593; Out</span>':'<span class=""dir-in"">&#8595; In</span>';
    var peer=dir==='out'?tx.toAddress:tx.fromAddress;
    var hashShort=tx.hash?tx.hash.substring(0,12)+'...':'N/A';
    var sc=stCls(tx.transactionStatus);var sn=stNm(tx.transactionStatus);
    var tt=ttype(tx.transactionType);
    return '<tr>'+
      '<td><code class=""muted"">'+hashShort+'</code></td>'+
      '<td>'+dirLbl+'</td>'+
      '<td><span class=""badge badge-tok"" style=""font-size:10px"">'+tt+'</span></td>'+
      '<td><code class=""muted"" title=""'+esc(peer||'')+'"">'+shn(peer||'--',18)+'</code></td>'+
      '<td class=""'+(dir==='in'?'grn':'red')+'"" style=""font-weight:600;font-variant-numeric:tabular-nums"">'+(dir==='in'?'+':'-')+tx.amount+' VFX</td>'+
      '<td><span class=""badge '+sc+'"">'+sn+'</span></td>'+
      '<td class=""muted"">'+ago(tx.timestamp)+'</td>'+
      '</tr>';
  }).join('');
  el('hist-content').innerHTML='<div class=""tx-wrap""><div class=""tbl-wrap""><table class=""dtbl""><thead><tr><th>Hash</th><th>Dir</th><th>Type</th><th>Peer</th><th>Amount</th><th>Status</th><th>Age</th></tr></thead><tbody>'+rows+'</tbody></table></div></div>';
}

/* ---- Send VFX ---- */
window.openSendVFX=function(){
  el('s-from').value=selAddr||'';
  el('s-to').value='';
  el('s-amount').value='';
  hideMsg('s-msg');
  el('send-overlay').classList.add('on');
};
window.closeSend=function(){el('send-overlay').classList.remove('on');};

window.doSendVFX=function(){
  var from=el('s-from').value.trim();
  var to=el('s-to').value.trim();
  var amt=el('s-amount').value.trim();
  if(!from||!to||!amt){showMsg('s-msg','Please fill all fields.','err');return;}
  var btn=el('s-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/wallet/api/send/vfx',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({From:from,To:to,Amount:amt})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Send';
    if(d.success){
      showMsg('s-msg','Transaction sent successfully!','ok');
      setTimeout(function(){closeSend();tabLoaded.history=false;if(activeTab==='history')loadHistory();loadAccounts();},2000);
    }else{
      showMsg('s-msg',d.message||'Send failed.','err');
    }
  }).catch(function(){
    btn.disabled=false;btn.textContent='Send';
    showMsg('s-msg','Request failed. Please try again.','err');
  });
};

/* ---- Transfer NFT ---- */
window.openTransferNFT=function(scUID,name){
  el('nft-scuid').value=scUID;
  el('nft-name-disp').value=name+'   ('+scUID.substring(0,16)+'...)';
  el('nft-to').value='';
  hideMsg('nft-msg');
  el('nft-overlay').classList.add('on');
};
window.closeNFT=function(){el('nft-overlay').classList.remove('on');};

window.doTransferNFT=function(){
  var scUID=el('nft-scuid').value;
  var to=el('nft-to').value.trim();
  if(!to){showMsg('nft-msg','Please enter a destination address.','err');return;}
  var btn=el('nft-btn');
  btn.disabled=true;btn.textContent='Transferring...';
  fetch('/scapi/smartcontracts/TransferNFT/'+encodeURIComponent(scUID)+'/'+encodeURIComponent(to))
    .then(function(r){
      if(r.status===403)throw new Error('API must be enabled and unlocked to transfer NFTs. Use /api/v1/UnlockWallet/{password} first.');
      return r.text();
    })
    .then(function(t){
      btn.disabled=false;btn.textContent='Transfer';
      var ok=t.includes('Success')||t.includes('success');
      showMsg('nft-msg',t,ok?'ok':'err');
      if(ok){setTimeout(function(){closeNFT();tabLoaded.nfts=false;loadNFTs();},2000);}
    })
    .catch(function(e){
      btn.disabled=false;btn.textContent='Transfer';
      showMsg('nft-msg',e.message||'Transfer failed.','err');
    });
};

/* ---- Transfer Token ---- */
window.openSendToken=function(scUID,name,ticker){
  el('tok-scuid').value=scUID;
  el('tok-from-addr').value=selAddr||'';
  el('tok-name-disp').value=name+' ('+ticker+')';
  el('tok-to').value='';
  el('tok-amount').value='';
  hideMsg('tok-msg');
  el('tok-overlay').classList.add('on');
};
window.closeTok=function(){el('tok-overlay').classList.remove('on');};

window.doTransferToken=function(){
  var scUID=el('tok-scuid').value;
  var from=el('tok-from-addr').value;
  var to=el('tok-to').value.trim();
  var amt=el('tok-amount').value.trim();
  if(!to||!amt){showMsg('tok-msg','Please fill all fields.','err');return;}
  var btn=el('tok-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/tkapi/tk/TransferToken/'+encodeURIComponent(scUID)+'/'+encodeURIComponent(from)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt))
    .then(function(r){
      if(r.status===403)throw new Error('API must be enabled and unlocked. Use /api/v1/UnlockWallet/{password} first.');
      return r.text();
    })
    .then(function(t){
      btn.disabled=false;btn.textContent='Send';
      var d;try{d=JSON.parse(t);}catch(e){d={Success:t.includes('Success'),Message:t};}
      var ok=d.Success||d.success;
      showMsg('tok-msg',d.Message||d.message||t,ok?'ok':'err');
      if(ok){setTimeout(function(){closeTok();loadAccounts();},2000);}
    })
    .catch(function(e){
      btn.disabled=false;btn.textContent='Send';
      showMsg('tok-msg',e.message||'Transfer failed.','err');
    });
};

/* ---- vBTC Withdrawal Request ---- */
window.openWD=function(scUID,owner,bal){
  el('wd-scuid').value=scUID;
  el('wd-owner').value=owner;
  el('wd-contract-disp').value=scUID;
  el('wd-btcaddr').value='';
  el('wd-amount').value=bal>0?bal.toFixed(8):'';
  el('wd-fee').value='10';
  hideMsg('wd-msg');
  el('wd-overlay').classList.add('on');
};
window.closeWD=function(){el('wd-overlay').classList.remove('on');};

window.doWDRequest=function(){
  var scUID=el('wd-scuid').value;
  var owner=el('wd-owner').value;
  var btcAddr=el('wd-btcaddr').value.trim();
  var amt=el('wd-amount').value.trim();
  var fee=el('wd-fee').value.trim()||'10';
  if(!btcAddr||!amt){showMsg('wd-msg','Please fill BTC address and amount.','err');return;}
  var btn=el('wd-btn');
  btn.disabled=true;btn.textContent='Requesting...';
  fetch('/wallet/api/vbtc/withdraw/request',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ScUID:scUID,OwnerAddress:owner,BTCAddress:btcAddr,Amount:amt,FeeRate:fee})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Request Withdrawal';
    if(d.success){
      showMsg('wd-msg','Withdrawal request submitted! Wait for it to be mined, then click Complete Withdrawal.','ok');
      setTimeout(function(){closeWD();tabLoaded.vbtc=false;loadVBTC();},3000);
    }else{
      showMsg('wd-msg',d.message||'Request failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Request Withdrawal';
    showMsg('wd-msg',e.message||'Request failed.','err');
  });
};

/* ---- vBTC Complete Withdrawal ---- */
window.openWDC=function(scUID,amt,dest){
  el('wdc-scuid').value=scUID;
  el('wdc-contract-disp').value=scUID;
  el('wdc-amount-disp').value=amt?amt.toFixed(8)+' BTC':'Unknown';
  el('wdc-dest-disp').value=dest||'Unknown';
  el('wdc-hash').value='';
  hideMsg('wdc-msg');
  showMsg('wdc-msg','Fetching withdrawal status...','ok');
  fetch('/wallet/api/vbtc/withdraw/status/'+encodeURIComponent(scUID))
    .then(function(r){return r.json();}).then(function(d){
      if(d.success&&d.requestHash){
        el('wdc-hash').value=d.requestHash;
        el('wdc-amount-disp').value=(d.amount||amt||0).toFixed(8)+' BTC';
        el('wdc-dest-disp').value=d.destination||dest||'';
        hideMsg('wdc-msg');
      }else{
        showMsg('wdc-msg','Could not find request hash. The request may still be pending in the mempool.','err');
      }
    }).catch(function(){hideMsg('wdc-msg');});
  el('wdc-overlay').classList.add('on');
};
window.closeWDC=function(){el('wdc-overlay').classList.remove('on');};

window.doWDComplete=function(){
  var scUID=el('wdc-scuid').value;
  var hash=el('wdc-hash').value;
  if(!hash){showMsg('wdc-msg','No withdrawal request hash found. Wait for the request TX to be mined.','err');return;}
  var btn=el('wdc-btn');
  btn.disabled=true;btn.textContent='Completing... (FROST signing)';
  fetch('/wallet/api/vbtc/withdraw/complete',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ScUID:scUID,RequestHash:hash})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Complete Withdrawal';
    if(d.success){
      var msg='Withdrawal completed! BTC TX: '+(d.btcTxHash||'pending');
      if(d.vfxTxHash)msg+=' | VFX TX: '+d.vfxTxHash;
      showMsg('wdc-msg',msg,'ok');
      setTimeout(function(){closeWDC();tabLoaded.vbtc=false;loadVBTC();},4000);
    }else{
      showMsg('wdc-msg',d.message||'Completion failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Complete Withdrawal';
    showMsg('wdc-msg',e.message||'Request failed.','err');
  });
};

/* ---- Send vBTC (transfer to another VFX address) ---- */
window.openVBTCTx=function(scUID,bal){
  el('vbtc-tx-scuid').value=scUID;
  el('vbtc-tx-from').value=selAddr||'';
  el('vbtc-tx-contract-disp').value=scUID;
  el('vbtc-tx-from-disp').value=selAddr||'';
  el('vbtc-tx-to').value='';
  el('vbtc-tx-amount').value='';
  hideMsg('vbtc-tx-msg');
  el('vbtc-tx-overlay').classList.add('on');
};
window.closeVBTCTx=function(){el('vbtc-tx-overlay').classList.remove('on');};

window.doVBTCTransfer=function(){
  var scUID=el('vbtc-tx-scuid').value;
  var from=el('vbtc-tx-from').value;
  var to=el('vbtc-tx-to').value.trim();
  var amt=el('vbtc-tx-amount').value.trim();
  if(!to||!amt){showMsg('vbtc-tx-msg','Please fill To address and Amount.','err');return;}
  var btn=el('vbtc-tx-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/wallet/api/vbtc/transfer',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ScUID:scUID,FromAddress:from,ToAddress:to,Amount:amt})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Send vBTC';
    if(d.success){
      showMsg('vbtc-tx-msg','vBTC sent successfully! TX: '+(d.message||''),'ok');
      setTimeout(function(){closeVBTCTx();tabLoaded.vbtc=false;loadVBTC();},2500);
    }else{
      showMsg('vbtc-tx-msg',d.message||'Transfer failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Send vBTC';
    showMsg('vbtc-tx-msg',e.message||'Request failed.','err');
  });
};

/* ---- Send BTC ---- */
window.openSendBTC=function(addr){
  el('btc-s-from').value=addr||'';
  el('btc-s-to').value='';
  el('btc-s-amount').value='';
  el('btc-s-fee').value='10';
  hideMsg('btc-s-msg');
  el('btc-overlay').classList.add('on');
};
window.closeBtcSend=function(){el('btc-overlay').classList.remove('on');};

window.doSendBTC=function(){
  var from=el('btc-s-from').value.trim();
  var to=el('btc-s-to').value.trim();
  var amt=el('btc-s-amount').value.trim();
  var fee=el('btc-s-fee').value.trim()||'10';
  if(!from||!to||!amt){showMsg('btc-s-msg','Please fill From, To, and Amount.','err');return;}
  var btn=el('btc-s-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/btcapi/BTCV2/SendTransaction/'+encodeURIComponent(from)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt)+'/'+encodeURIComponent(fee))
    .then(function(r){
      if(r.status===403)throw new Error('API must be enabled. Use /api/v1/UnlockWallet/{password} first.');
      return r.json();
    })
    .then(function(d){
      btn.disabled=false;btn.textContent='Send BTC';
      var ok=d.Success||d.success||(d.Result&&(d.Result.includes('Success')||d.Result.includes('txid')));
      var msg=d.Message||d.message||d.Result||d.result||JSON.stringify(d);
      showMsg('btc-s-msg',msg,ok?'ok':'err');
      if(ok){setTimeout(function(){closeBtcSend();tabLoaded.btc=false;if(activeTab==='btc')loadBTC();else loadBTC();},2500);}
    })
    .catch(function(e){
      btn.disabled=false;btn.textContent='Send BTC';
      showMsg('btc-s-msg',e.message||'Request failed.','err');
    });
};

/* ---- Privacy ---- */
var knownZfx=[];var selZfx=null;

function loadPrivacy(){
  loadPlonkAndPool();
  fetch('/wallet/api/privacy/addresses').then(function(r){return r.json();}).then(function(data){
    if(data&&data.length){
      knownZfx=[];
      data.forEach(function(w){
        if(w.zfxAddress&&knownZfx.indexOf(w.zfxAddress)===-1)knownZfx.push(w.zfxAddress);
      });
      updateZfxSel();
      if(!selZfx||knownZfx.indexOf(selZfx)===-1)selZfx=knownZfx[0];
      el('priv-zfx-sel').value=selZfx;
      el('priv-zfx-sel-wrap').style.display='block';
      el('priv-actions').style.display='block';
      loadZfxBalance(selZfx);
      loadVbtcPrivacy();
    }else{
      el('priv-bal-num').innerHTML='-- <span>VFX</span>';
      el('priv-notes').textContent='No shielded address created yet';
      el('priv-zfx-addr').textContent='';
      el('priv-actions').style.display='none';
      el('priv-zfx-sel-wrap').style.display='none';
    }
  }).catch(function(){
    if(knownZfx.length>0){
      loadZfxBalance(selZfx||knownZfx[0]);
      loadVbtcPrivacy();
    }else{
      el('priv-bal-num').innerHTML='-- <span>VFX</span>';
      el('priv-notes').textContent='No shielded address created yet';
      el('priv-zfx-addr').textContent='';
      el('priv-actions').style.display='none';
      el('priv-zfx-sel-wrap').style.display='none';
    }
  });
}

function loadPlonkAndPool(){
  var html='';
  Promise.all([
    fetch('/wallet/api/privacy/plonkStatus').then(function(r){return r.json();}).catch(function(){return null;}),
    fetch('/wallet/api/privacy/poolState').then(function(r){return r.json();}).catch(function(){return null;})
  ]).then(function(results){
    var plonk=results[0];var pool=results[1];
    html+='<div class=""stat-row"">';
    if(plonk&&plonk.success){
      html+='<div class=""stat-card""><div class=""stat-lbl"">Proof Verification</div><div class=""stat-val '+(plonk.proofVerificationImplemented?'grn':'red')+'"">'+( plonk.proofVerificationImplemented?'Available':'Unavailable')+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Proof Proving</div><div class=""stat-val '+(plonk.proofProvingImplemented?'grn':'org')+'"">'+( plonk.proofProvingImplemented?'Available':'Unavailable')+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Enforce PLONK</div><div class=""stat-val '+(plonk.enforcePlonkProofsForZk?'grn':'org')+'"">'+( plonk.enforcePlonkProofsForZk?'Yes':'No')+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Native Caps</div><div class=""stat-val acc"">'+plonk.nativeCapabilities+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Params Mirror</div><div class=""stat-val acc"">'+(plonk.paramsBytesMirrored>0?Math.round(plonk.paramsBytesMirrored/1024)+' KB':'None')+'</div></div>';
    }else{
      html+='<div class=""stat-card""><div class=""stat-lbl"">PLONK Status</div><div class=""stat-val red"">Error</div></div>';
    }
    if(pool&&pool.success){
      html+='<div class=""stat-card""><div class=""stat-lbl"">Pool Asset</div><div class=""stat-val acc"">'+esc(pool.assetType)+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Total Commitments</div><div class=""stat-val grn"">'+pool.totalCommitments+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Shielded Supply</div><div class=""stat-val org"">'+fmtBal(pool.totalShieldedSupply)+' VFX</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Last Update</div><div class=""stat-val acc"">#'+pool.lastUpdateHeight+'</div></div>';
      html+='<div class=""stat-card""><div class=""stat-lbl"">Merkle Root</div><div class=""stat-val muted"" style=""font-size:11px;word-break:break-all"">'+(pool.currentMerkleRoot?shn(pool.currentMerkleRoot,20):'Empty')+'</div></div>';
    }else{
      html+='<div class=""stat-card""><div class=""stat-lbl"">Pool State</div><div class=""stat-val red"">Error</div></div>';
    }
    html+='</div>';
    el('priv-status-content').innerHTML=html;
  });
}

function loadZfxBalance(zfx){
  if(!zfx)return;
  selZfx=zfx;
  el('priv-zfx-addr').textContent=zfx;
  fetch('/wallet/api/privacy/balance/'+encodeURIComponent(zfx))
    .then(function(r){return r.json();})
    .then(function(d){
      if(d.success){
        el('priv-bal-num').innerHTML=fmtBal(d.vfxShieldedBalance)+'<span>VFX</span>';
        el('priv-notes').textContent=d.unspentNotes+' unspent note'+(d.unspentNotes!==1?'s':'');
      }else{
        el('priv-bal-num').innerHTML='-- <span>VFX</span>';
        el('priv-notes').textContent=d.message||'Error';
      }
    }).catch(function(){
      el('priv-bal-num').innerHTML='-- <span>VFX</span>';
      el('priv-notes').textContent='Failed to load balance';
    });
}

function addZfxAddr(zfx){
  if(knownZfx.indexOf(zfx)===-1)knownZfx.push(zfx);
  updateZfxSel();
  selZfx=zfx;
  el('priv-zfx-sel').value=zfx;
  el('priv-zfx-sel-wrap').style.display='block';
  el('priv-actions').style.display='block';
}

function updateZfxSel(){
  var sel=el('priv-zfx-sel');
  sel.innerHTML='';
  knownZfx.forEach(function(z){
    var opt=document.createElement('option');
    opt.value=z;opt.textContent=z;
    sel.appendChild(opt);
  });
}

window.onZfxChange=function(){
  selZfx=el('priv-zfx-sel').value;
  loadZfxBalance(selZfx);
};

function populateAddrSelect(selId){
  var sel=el(selId);sel.innerHTML='';
  accounts.forEach(function(a){
    var opt=document.createElement('option');
    opt.value=a.address;
    opt.textContent=a.address.substring(0,20)+'... | '+fmtBal(a.balance)+' VFX';
    sel.appendChild(opt);
  });
}

/* ---- Create Shielded Address ---- */
window.openCreateZfx=function(){
  populateAddrSelect('czfx-addr');
  el('czfx-pwd').value='';
  hideMsg('czfx-msg');
  el('czfx-overlay').classList.add('on');
};
window.closeCZfx=function(){el('czfx-overlay').classList.remove('on');};

window.doCreateZfx=function(){
  var addr=el('czfx-addr').value;
  var pwd=el('czfx-pwd').value;
  if(!addr){showMsg('czfx-msg','Select a VFX address.','err');return;}
  if(!pwd||pwd.length<8){showMsg('czfx-msg','Password must be at least 8 characters.','err');return;}
  var btn=el('czfx-btn');
  btn.disabled=true;btn.textContent='Creating...';
  fetch('/wallet/api/privacy/createShieldedAddress/'+encodeURIComponent(addr)+'/'+encodeURIComponent(pwd))
    .then(function(r){return r.json();}).then(function(d){
      btn.disabled=false;btn.textContent='Create';
      if(d.success){
        showMsg('czfx-msg','Shielded address created: '+d.zfxAddress,'ok');
        addZfxAddr(d.zfxAddress);
        loadZfxBalance(d.zfxAddress);
        setTimeout(function(){closeCZfx();},2500);
      }else{
        showMsg('czfx-msg',d.message||'Creation failed.','err');
      }
    }).catch(function(e){
      btn.disabled=false;btn.textContent='Create';
      showMsg('czfx-msg',e.message||'Request failed.','err');
    });
};

/* ---- Shield VFX ---- */
window.openShield=function(){
  populateAddrSelect('sh-from');
  el('sh-zfx').value=selZfx||'';
  el('sh-amount').value='';
  hideMsg('sh-msg');
  el('shield-overlay').classList.add('on');
};
window.closeShield=function(){el('shield-overlay').classList.remove('on');};

window.doShield=function(){
  var from=el('sh-from').value;
  var zfx=el('sh-zfx').value.trim();
  var amt=el('sh-amount').value.trim();
  if(!from||!zfx||!amt){showMsg('sh-msg','Please fill all fields.','err');return;}
  if(!zfx.startsWith('zfx_')){showMsg('sh-msg','Shielded address must start with zfx_','err');return;}
  var btn=el('sh-btn');
  btn.disabled=true;btn.textContent='Shielding...';
  fetch('/wallet/api/privacy/shield/'+encodeURIComponent(from)+'/'+encodeURIComponent(zfx)+'/'+encodeURIComponent(amt))
    .then(function(r){return r.json();}).then(function(d){
      btn.disabled=false;btn.textContent='Shield';
      if(d.success){
        showMsg('sh-msg','Shield TX broadcast! Hash: '+(d.hash||''),'ok');
        addZfxAddr(zfx);
        setTimeout(function(){closeShield();loadAccounts();loadZfxBalance(zfx);},2500);
      }else{
        showMsg('sh-msg',d.message||'Shield failed.','err');
      }
    }).catch(function(e){
      btn.disabled=false;btn.textContent='Shield';
      showMsg('sh-msg',e.message||'Request failed.','err');
    });
};

/* ---- Unshield VFX ---- */
window.openUnshield=function(){
  if(!selZfx){showMsg('priv-notes','No shielded address selected.','err');return;}
  el('ush-zfx').value=selZfx;
  populateAddrSelect('ush-to');
  el('ush-amount').value='';
  el('ush-pwd').value='';
  hideMsg('ush-msg');
  el('unshield-overlay').classList.add('on');
};
window.closeUnshield=function(){el('unshield-overlay').classList.remove('on');};

window.doUnshield=function(){
  var zfx=el('ush-zfx').value;
  var to=el('ush-to').value;
  var amt=el('ush-amount').value.trim();
  var pwd=el('ush-pwd').value;
  if(!zfx||!to||!amt){showMsg('ush-msg','Please fill all fields.','err');return;}
  var btn=el('ush-btn');
  btn.disabled=true;btn.textContent='Unshielding...';
  var url='/wallet/api/privacy/unshield/'+encodeURIComponent(zfx)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt);
  if(pwd)url+='?password='+encodeURIComponent(pwd);
  fetch(url).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Unshield';
    if(d.success){
      showMsg('ush-msg','Unshield TX broadcast! Hash: '+(d.hash||''),'ok');
      setTimeout(function(){closeUnshield();loadAccounts();loadZfxBalance(zfx);},2500);
    }else{
      showMsg('ush-msg',d.message||'Unshield failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Unshield';
    showMsg('ush-msg',e.message||'Request failed.','err');
  });
};

/* ---- Private Transfer ---- */
window.openPrivTransfer=function(){
  if(!selZfx){return;}
  el('ptx-from').value=selZfx;
  el('ptx-to').value='';
  el('ptx-amount').value='';
  el('ptx-pwd').value='';
  hideMsg('ptx-msg');
  el('ptx-overlay').classList.add('on');
};
window.closePTx=function(){el('ptx-overlay').classList.remove('on');};

window.doPrivTransfer=function(){
  var from=el('ptx-from').value;
  var to=el('ptx-to').value.trim();
  var amt=el('ptx-amount').value.trim();
  var pwd=el('ptx-pwd').value;
  if(!from||!to||!amt){showMsg('ptx-msg','Please fill all fields.','err');return;}
  if(!to.startsWith('zfx_')){showMsg('ptx-msg','Recipient must be a zfx_ address.','err');return;}
  var btn=el('ptx-btn');
  btn.disabled=true;btn.textContent='Sending...';
  var url='/wallet/api/privacy/transfer/'+encodeURIComponent(from)+'/'+encodeURIComponent(to)+'/'+encodeURIComponent(amt);
  if(pwd)url+='?password='+encodeURIComponent(pwd);
  fetch(url).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Send';
    if(d.success){
      showMsg('ptx-msg','Private transfer broadcast! Hash: '+(d.hash||''),'ok');
      setTimeout(function(){closePTx();loadZfxBalance(from);},2500);
    }else{
      showMsg('ptx-msg',d.message||'Transfer failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Send';
    showMsg('ptx-msg',e.message||'Request failed.','err');
  });
};

/* ---- Scan for Notes ---- */
window.doScanZfx=function(){
  if(!selZfx)return;
  var scanBtn=document.querySelector('#priv-actions .act-btn.sec');
  if(scanBtn){scanBtn.disabled=true;scanBtn.textContent='Scanning...';}
  var url='/wallet/api/privacy/scan/'+encodeURIComponent(selZfx);
  fetch(url).then(function(r){return r.json();}).then(function(d){
    if(scanBtn){scanBtn.disabled=false;scanBtn.innerHTML='&#128269; Scan for Notes';}
    if(d.success){
      var msg='Scanned '+d.blocksScanned+' blocks, '+d.transactionsScanned+' TXs. Found '+d.newNotesFound+' new note'+(d.newNotesFound!==1?'s':'')+ '.';
      el('priv-notes').textContent=msg;
      loadZfxBalance(selZfx);
    }else{
      el('priv-notes').textContent=d.message||'Scan failed.';
    }
  }).catch(function(){
    if(scanBtn){scanBtn.disabled=false;scanBtn.innerHTML='&#128269; Scan for Notes';}
    el('priv-notes').textContent='Scan request failed.';
  });
};

/* ======== vBTC Privacy ======== */
var vbtcPrivContracts=[];var selVbtcPrivSc=null;

function loadVbtcPrivacy(){
  if(!selAddr||!selZfx)return;
  fetch('/wallet/api/vbtc/'+encodeURIComponent(selAddr)).then(function(r){return r.json();}).then(function(contracts){
    vbtcPrivContracts=contracts||[];
    var sel=el('vbtc-priv-sc-sel');
    sel.innerHTML='';
    if(!vbtcPrivContracts.length){
      sel.innerHTML='<option value="">No vBTC contracts</option>';
      el('vbtc-priv-sel-wrap').style.display='none';
      el('vbtc-priv-actions').style.display='none';
      el('vbtc-priv-bal-num').innerHTML='-- <span>vBTC</span>';
      el('vbtc-priv-notes').textContent='No vBTC contracts found for this address';
      return;
    }
    vbtcPrivContracts.forEach(function(c){
      var opt=document.createElement('option');
      opt.value=c.scUID;
      opt.textContent=c.scUID.substring(0,24)+'... | '+fmtBal(c.balance)+' vBTC';
      sel.appendChild(opt);
    });
    selVbtcPrivSc=vbtcPrivContracts[0].scUID;
    el('vbtc-priv-sel-wrap').style.display='block';
    el('vbtc-priv-actions').style.display='block';
    loadVbtcPrivBalance(selZfx,selVbtcPrivSc);
    loadVbtcPoolState(selVbtcPrivSc);
  }).catch(function(){
    el('vbtc-priv-bal-num').innerHTML='-- <span>vBTC</span>';
    el('vbtc-priv-notes').textContent='Failed to load vBTC contracts';
  });
}

window.onVbtcPrivScChange=function(){
  selVbtcPrivSc=el('vbtc-priv-sc-sel').value;
  if(selZfx&&selVbtcPrivSc){
    loadVbtcPrivBalance(selZfx,selVbtcPrivSc);
    loadVbtcPoolState(selVbtcPrivSc);
  }
};

function loadVbtcPrivBalance(zfx,scUID){
  if(!zfx||!scUID)return;
  fetch('/wallet/api/privacy/vbtc/balance/'+encodeURIComponent(zfx)+'/'+encodeURIComponent(scUID))
    .then(function(r){return r.json();})
    .then(function(d){
      if(d.success){
        el('vbtc-priv-bal-num').innerHTML=fmtBal(d.vbtcShieldedBalance)+'<span>vBTC</span>';
        el('vbtc-priv-notes').textContent=d.unspentNotes+' unspent note'+(d.unspentNotes!==1?'s':'')+' ('+d.assetKey+')';
      }else{
        el('vbtc-priv-bal-num').innerHTML='-- <span>vBTC</span>';
        el('vbtc-priv-notes').textContent=d.message||'Error';
      }
    }).catch(function(){
      el('vbtc-priv-bal-num').innerHTML='-- <span>vBTC</span>';
      el('vbtc-priv-notes').textContent='Failed to load vBTC shielded balance';
    });
}

function loadVbtcPoolState(scUID){
  if(!scUID)return;
  fetch('/wallet/api/privacy/vbtc/poolState/'+encodeURIComponent(scUID))
    .then(function(r){return r.json();})
    .then(function(pool){
      if(!pool||!pool.success){el('vbtc-priv-pool-content').innerHTML='';return;}
      var h='<div class=""stat-row"">';
      h+='<div class=""stat-card""><div class=""stat-lbl"">vBTC Pool Asset</div><div class=""stat-val acc"">'+esc(pool.assetType)+'</div></div>';
      h+='<div class=""stat-card""><div class=""stat-lbl"">Total Commitments</div><div class=""stat-val grn"">'+pool.totalCommitments+'</div></div>';
      h+='<div class=""stat-card""><div class=""stat-lbl"">Shielded Supply</div><div class=""stat-val org"">'+fmtBal(pool.totalShieldedSupply)+' vBTC</div></div>';
      h+='<div class=""stat-card""><div class=""stat-lbl"">Last Update</div><div class=""stat-val acc"">#'+pool.lastUpdateHeight+'</div></div>';
      h+='</div>';
      el('vbtc-priv-pool-content').innerHTML=h;
    }).catch(function(){el('vbtc-priv-pool-content').innerHTML='';});
}

function populateVbtcScSelect(selId){
  var sel=el(selId);sel.innerHTML='';
  vbtcPrivContracts.forEach(function(c){
    var opt=document.createElement('option');
    opt.value=c.scUID;
    opt.textContent=c.scUID.substring(0,24)+'... | '+fmtBal(c.balance)+' vBTC';
    sel.appendChild(opt);
  });
}

/* ---- Shield vBTC ---- */
window.openShieldVbtc=function(){
  populateAddrSelect('shvbtc-from');
  el('shvbtc-zfx').value=selZfx||'';
  populateVbtcScSelect('shvbtc-sc');
  el('shvbtc-amount').value='';
  hideMsg('shvbtc-msg');
  el('shvbtc-overlay').classList.add('on');
};
window.closeShieldVbtc=function(){el('shvbtc-overlay').classList.remove('on');};

window.doShieldVbtc=function(){
  var from=el('shvbtc-from').value;
  var zfx=el('shvbtc-zfx').value.trim();
  var scUID=el('shvbtc-sc').value;
  var amt=el('shvbtc-amount').value.trim();
  if(!from||!zfx||!scUID||!amt){showMsg('shvbtc-msg','Please fill all fields.','err');return;}
  if(!zfx.startsWith('zfx_')){showMsg('shvbtc-msg','Shielded address must start with zfx_','err');return;}
  var btn=el('shvbtc-btn');
  btn.disabled=true;btn.textContent='Shielding...';
  fetch('/wallet/api/privacy/vbtc/shield',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({FromAddress:from,ZfxAddress:zfx,ScUID:scUID,Amount:amt})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Shield vBTC';
    if(d.success){
      showMsg('shvbtc-msg','Shield TX broadcast! Hash: '+(d.hash||''),'ok');
      setTimeout(function(){closeShieldVbtc();loadAccounts();if(selZfx&&selVbtcPrivSc)loadVbtcPrivBalance(selZfx,selVbtcPrivSc);},2500);
    }else{
      showMsg('shvbtc-msg',d.message||'Shield failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Shield vBTC';
    showMsg('shvbtc-msg',e.message||'Request failed.','err');
  });
};

/* ---- Unshield vBTC ---- */
window.openUnshieldVbtc=function(){
  if(!selZfx||!selVbtcPrivSc){return;}
  el('ushvbtc-zfx').value=selZfx;
  populateAddrSelect('ushvbtc-to');
  el('ushvbtc-sc').value=selVbtcPrivSc;
  el('ushvbtc-amount').value='';
  el('ushvbtc-pwd').value='';
  hideMsg('ushvbtc-msg');
  el('ushvbtc-overlay').classList.add('on');
};
window.closeUnshieldVbtc=function(){el('ushvbtc-overlay').classList.remove('on');};

window.doUnshieldVbtc=function(){
  var zfx=el('ushvbtc-zfx').value;
  var to=el('ushvbtc-to').value;
  var scUID=el('ushvbtc-sc').value;
  var amt=el('ushvbtc-amount').value.trim();
  var pwd=el('ushvbtc-pwd').value;
  if(!zfx||!to||!scUID||!amt){showMsg('ushvbtc-msg','Please fill all fields.','err');return;}
  var btn=el('ushvbtc-btn');
  btn.disabled=true;btn.textContent='Unshielding...';
  fetch('/wallet/api/privacy/vbtc/unshield',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({ZfxAddress:zfx,ToAddress:to,ScUID:scUID,Amount:amt,Password:pwd||null})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Unshield vBTC';
    if(d.success){
      showMsg('ushvbtc-msg','Unshield TX broadcast! Hash: '+(d.hash||''),'ok');
      setTimeout(function(){closeUnshieldVbtc();loadAccounts();loadVbtcPrivBalance(zfx,scUID);},2500);
    }else{
      showMsg('ushvbtc-msg',d.message||'Unshield failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Unshield vBTC';
    showMsg('ushvbtc-msg',e.message||'Request failed.','err');
  });
};

/* ---- Private Transfer vBTC ---- */
window.openPrivTransferVbtc=function(){
  if(!selZfx||!selVbtcPrivSc){return;}
  el('ptxvbtc-from').value=selZfx;
  el('ptxvbtc-to').value='';
  el('ptxvbtc-sc').value=selVbtcPrivSc;
  el('ptxvbtc-amount').value='';
  el('ptxvbtc-pwd').value='';
  hideMsg('ptxvbtc-msg');
  el('ptxvbtc-overlay').classList.add('on');
};
window.closePTxVbtc=function(){el('ptxvbtc-overlay').classList.remove('on');};

window.doPrivTransferVbtc=function(){
  var from=el('ptxvbtc-from').value;
  var to=el('ptxvbtc-to').value.trim();
  var scUID=el('ptxvbtc-sc').value;
  var amt=el('ptxvbtc-amount').value.trim();
  var pwd=el('ptxvbtc-pwd').value;
  if(!from||!to||!scUID||!amt){showMsg('ptxvbtc-msg','Please fill all fields.','err');return;}
  if(!to.startsWith('zfx_')){showMsg('ptxvbtc-msg','Recipient must be a zfx_ address.','err');return;}
  var btn=el('ptxvbtc-btn');
  btn.disabled=true;btn.textContent='Sending...';
  fetch('/wallet/api/privacy/vbtc/transfer',{
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body:JSON.stringify({FromZfxAddress:from,ToZfxAddress:to,ScUID:scUID,Amount:amt,Password:pwd||null})
  }).then(function(r){return r.json();}).then(function(d){
    btn.disabled=false;btn.textContent='Send vBTC';
    if(d.success){
      showMsg('ptxvbtc-msg','Private transfer broadcast! Hash: '+(d.hash||''),'ok');
      setTimeout(function(){closePTxVbtc();loadVbtcPrivBalance(from,scUID);},2500);
    }else{
      showMsg('ptxvbtc-msg',d.message||'Transfer failed.','err');
    }
  }).catch(function(e){
    btn.disabled=false;btn.textContent='Send vBTC';
    showMsg('ptxvbtc-msg',e.message||'Request failed.','err');
  });
};

/* ---- Scan vBTC Notes ---- */
window.doScanVbtc=function(){
  if(!selZfx||!selVbtcPrivSc)return;
  var scanBtn=document.querySelector('#vbtc-priv-actions .act-btn.sec');
  if(scanBtn){scanBtn.disabled=true;scanBtn.textContent='Scanning...';}
  var url='/wallet/api/privacy/vbtc/scan/'+encodeURIComponent(selZfx)+'/'+encodeURIComponent(selVbtcPrivSc);
  fetch(url).then(function(r){return r.json();}).then(function(d){
    if(scanBtn){scanBtn.disabled=false;scanBtn.innerHTML='&#128269; Scan vBTC Notes';}
    if(d.success){
      var msg='Scanned '+d.blocksScanned+' blocks, '+d.transactionsScanned+' TXs. Found '+d.newNotesFound+' new note'+(d.newNotesFound!==1?'s':'')+'. Balance: '+fmtBal(d.vbtcShieldedBalance)+' vBTC';
      el('vbtc-priv-notes').textContent=msg;
      loadVbtcPrivBalance(selZfx,selVbtcPrivSc);
    }else{
      el('vbtc-priv-notes').textContent=d.message||'Scan failed.';
    }
  }).catch(function(){
    if(scanBtn){scanBtn.disabled=false;scanBtn.innerHTML='&#128269; Scan vBTC Notes';}
    el('vbtc-priv-notes').textContent='Scan request failed.';
  });
};

/* ---- Helpers ---- */
function el(id){return document.getElementById(id);}
function esc(s){return s?String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;'):'';}" + @"
function shn(s,max){return s?(s.length>max?s.substring(0,max)+'...':s):'--';}
function fmtBal(n){return n!=null?(+n).toFixed(8):'0.00000000';}
function fmtTok(n,dec){var d=dec!=null?dec:8;return n!=null?(+n).toFixed(d):'0';}

function showMsg(id,msg,type){
  var e=el(id);e.textContent=msg;e.className='msg '+type+' on';
}
function hideMsg(id){var e=el(id);e.className='msg';}

function ago(ts){
  var d=Math.floor(Date.now()/1000)-ts;
  if(d<5)return 'just now';if(d<60)return d+'s ago';
  if(d<3600)return Math.floor(d/60)+'m ago';if(d<86400)return Math.floor(d/3600)+'h ago';
  return Math.floor(d/86400)+'d ago';
}
function stCls(s){return['badge-pend','badge-ok','badge-fail','badge-pend','badge-pend','badge-ok','badge-fail','badge-fail'][s]||'badge-pend';}
function stNm(s){return['Pending','Success','Failed','Reserved','CalledBack','Recovered','ReplacedByFee','Invalid'][s]||'Unknown';}
function ttype(t){
  var n=['TX','NODE','NFT_MINT','NFT_TX','NFT_BURN','NFT_SALE','ADNR','DSTR','VOTE_TOPIC','VOTE','RESERVE','SC_MINT','SC_TX','SC_BURN','FTKN_MINT','FTKN_TX','FTKN_BURN','TKNZ_MINT','TKNZ_TX','TKNZ_BURN','TKNZ_WD_ARB','TKNZ_WD_OWNER','VBTC2_VAL_REG','VBTC2_VAL_HB','VBTC2_VAL_EXIT','VBTC2_CREATE','VBTC2_TX','VBTC2_WD_REQ','VBTC2_WD_COMP','VBTC2_WD_CANCEL','VBTC2_WD_VOTE'];
  return n[t]!==undefined?n[t]:'TYPE_'+t;
}
})();
</script>";

        // ── Footer ───────────────────────────────────────────────────────────
        private static string BuildFooter() => @"
</body>
</html>";
    }
}