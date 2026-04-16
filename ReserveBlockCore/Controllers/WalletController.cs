using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.BrowserWalletServices;
using ReserveBlockCore.Bitcoin.Models;

namespace ReserveBlockCore.Controllers
{
    [Route("wallet")]
    [ApiController]
    public class WalletController : ControllerBase
    {
        // ── HTML page ─────────────────────────────────────────────────────────────
        [HttpGet("")]
        public ContentResult Index() => Content(BrowserUIService.GetHtml(), "text/html; charset=utf-8");

        // ═══════════════════════════════════════════════════════════════════════════
        //  VFX Endpoints
        // ═══════════════════════════════════════════════════════════════════════════

        [HttpGet("api/accounts")]
        public IActionResult GetAccounts()
        {
            try { return Ok(WalletVfxService.GetAccounts()); }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpGet("api/txs/{address}")]
        public IActionResult GetTransactions(string address)
        {
            try { return Ok(WalletVfxService.GetTransactions(address)); }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpGet("api/nfts/{address}")]
        public IActionResult GetNFTs(string address)
        {
            try { return Ok(WalletVfxService.GetNFTs(address)); }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpPost("api/send/vfx")]
        public async Task<IActionResult> SendVFX([FromBody] SendRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.From) || string.IsNullOrWhiteSpace(req.To) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "From, To, and Amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                var (success, message) = await WalletVfxService.SendVFX(req.From, req.To, amount);
                return Ok(new { success, message });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Bitcoin / vBTC Endpoints
        // ═══════════════════════════════════════════════════════════════════════════

        [HttpGet("api/btc")]
        public IActionResult GetBitcoinAccounts()
        {
            try { return Ok(WalletVbtcService.GetBitcoinAccounts()); }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        /// <summary>ETH + vBTC.b on Base for each BTC account that has a linked EVM address (Nethereum read; respects Globals.IsTestNet defaults).</summary>
        [HttpGet("api/btc/base-balances")]
        public async Task<IActionResult> GetBitcoinBaseBalances()
        {
            try { return Ok(await WalletBtcBaseService.GetLinkedBaseBalancesAsync()); }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpPost("api/btc/link-evm")]
        public IActionResult LinkBtcEvm([FromBody] BtcLinkEvmRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.BtcAddress))
                    return BadRequest(new { success = false, message = "btcAddress is required." });

                var ok = BitcoinAccount.SetLinkedEvmAddress(req.BtcAddress, req.EvmAddress);
                if (!ok)
                    return BadRequest(new { success = false, message = "Invalid BTC address or EVM address format (expect 0x + 40 hex)." });

                return Ok(new { success = true, message = "Linked EVM address updated." });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/vbtc/{address}")]
        public IActionResult GetVBTC(string address)
        {
            try { return Ok(WalletVbtcService.GetVBTCContracts(address)); }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpPost("api/vbtc/withdraw/request")]
        public async Task<IActionResult> VBTCWithdrawRequest([FromBody] VBTCWDRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.OwnerAddress) ||
                    string.IsNullOrWhiteSpace(req.BTCAddress) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "scUID, ownerAddress, btcAddress, and amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                int feeRate = 10;
                if (!string.IsNullOrWhiteSpace(req.FeeRate) && int.TryParse(req.FeeRate, out int fr) && fr > 0)
                    feeRate = fr;

                var (success, message) = await WalletVbtcService.RequestWithdrawal(req.ScUID, req.OwnerAddress, req.BTCAddress, amount, feeRate);
                return Ok(new { success, message });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPost("api/vbtc/withdraw/complete")]
        public async Task<IActionResult> VBTCWithdrawComplete([FromBody] VBTCWDComplete req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.RequestHash))
                    return BadRequest(new { success = false, message = "scUID and requestHash are required." });

                var result = await WalletVbtcService.CompleteWithdrawal(req.ScUID, req.RequestHash);

                if (result.remoteResult != null)
                    return Ok(new { success = true, message = "Withdrawal completed!", remoteResult = result.remoteResult });

                if (result.success)
                    return Ok(new { success = true, message = "Withdrawal completed!", vfxTxHash = result.vfxTxHash, btcTxHash = result.btcTxHash });
                else
                    return Ok(new { success = false, message = result.message });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/vbtc/withdraw/status/{scUID}")]
        public IActionResult VBTCWithdrawStatus(string scUID)
        {
            try { return Ok(WalletVbtcService.GetWithdrawStatus(scUID)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPost("api/vbtc/transfer")]
        public async Task<IActionResult> VBTCTransfer([FromBody] VBTCTransferRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.FromAddress) ||
                    string.IsNullOrWhiteSpace(req.ToAddress) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "scUID, fromAddress, toAddress, and amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                var (success, message) = await WalletVbtcService.TransferVBTC(req.ScUID, req.FromAddress, req.ToAddress, amount);
                return Ok(new { success, message });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  vBTC Bridge to Base Endpoints
        // ═══════════════════════════════════════════════════════════════════════════

        [HttpPost("api/vbtc/bridge/toBase")]
        public async Task<IActionResult> VBTCBridgeToBase([FromBody] VBTCBridgeToBaseRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.OwnerAddress) ||
                    string.IsNullOrWhiteSpace(req.Amount) || string.IsNullOrWhiteSpace(req.EvmDestination))
                    return BadRequest(new { success = false, message = "scUID, ownerAddress, amount, and evmDestination are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                var result = await WalletVbtcService.BridgeToBase(req.ScUID, req.OwnerAddress, amount, req.EvmDestination);
                return Ok(result);
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/vbtc/bridge/status/{lockId}")]
        public IActionResult VBTCBridgeLockStatus(string lockId)
        {
            try { return Ok(WalletVbtcService.GetBridgeLockStatus(lockId)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPost("api/vbtc/bridge/submitMint/{lockId}")]
        public async Task<IActionResult> VBTCSubmitMint(string lockId)
        {
            try
            {
                var result = await Bitcoin.Services.BaseBridgeMintSubmissionService.ManualSubmitMintWithProof(lockId);
                return Ok(new { success = result.Success, message = result.Message });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/vbtc/bridge/base-balance/{evmAddress}")]
        public async Task<IActionResult> VBTCBaseBalance(string evmAddress)
        {
            try
            {
                var result = await Bitcoin.Services.BaseBridgeService.GetBaseBalance(evmAddress);
                return Ok(new { success = result.Success, evmAddress, balance = result.Balance.ToString(System.Globalization.CultureInfo.InvariantCulture), message = result.Message });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  VFX Privacy / Shielded Endpoints
        // ═══════════════════════════════════════════════════════════════════════════

        [HttpGet("api/privacy/addresses")]
        public IActionResult GetShieldedAddresses()
        {
            try { return Ok(WalletPrivacyVfxService.GetShieldedAddresses()); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/createShieldedAddress/{address}/{password}")]
        public IActionResult CreateShieldedAddress(string address, string password)
        {
            try { return Ok(WalletPrivacyVfxService.CreateShieldedAddress(address, password)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/balance/{zfxAddress}")]
        public IActionResult GetShieldedBalance(string zfxAddress)
        {
            try { return Ok(WalletPrivacyVfxService.GetShieldedBalance(zfxAddress)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/shield/{fromAddress}/{zfxAddress}/{amount}")]
        public async Task<IActionResult> ShieldVFX(string fromAddress, string zfxAddress, decimal amount)
        {
            try { return Ok(await WalletPrivacyVfxService.ShieldVFX(fromAddress, zfxAddress, amount)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/unshield/{zfxAddress}/{toAddress}/{amount}")]
        public async Task<IActionResult> UnshieldVFX(string zfxAddress, string toAddress, decimal amount, [FromQuery] string? password = null)
        {
            try { return Ok(await WalletPrivacyVfxService.UnshieldVFX(zfxAddress, toAddress, amount, password)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/transfer/{fromZfxAddress}/{toZfxAddress}/{amount}")]
        public async Task<IActionResult> PrivateTransferVFX(string fromZfxAddress, string toZfxAddress, decimal amount, [FromQuery] string? password = null)
        {
            try { return Ok(await WalletPrivacyVfxService.PrivateTransferVFX(fromZfxAddress, toZfxAddress, amount, password)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/scan/{zfxAddress}")]
        public IActionResult ScanShieldedVFX(string zfxAddress, [FromQuery] string? password = null, [FromQuery] long? fromBlock = null, [FromQuery] long? toBlock = null)
        {
            try { return Ok(WalletPrivacyVfxService.ScanShieldedVFX(zfxAddress, password, fromBlock, toBlock)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/resync/{zfxAddress}/{fromHeight}")]
        public IActionResult ResyncShieldedWallet(string zfxAddress, long fromHeight, [FromQuery] long? toHeight = null)
        {
            try { return Ok(WalletPrivacyVfxService.ResyncShieldedWallet(zfxAddress, fromHeight, toHeight)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/plonkStatus")]
        public IActionResult GetPlonkStatus()
        {
            try { return Ok(WalletPrivacyVfxService.GetPlonkStatus()); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/poolState")]
        public IActionResult GetShieldedPoolState()
        {
            try { return Ok(WalletPrivacyVfxService.GetShieldedPoolState()); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  vBTC Privacy / Shielded Endpoints
        // ═══════════════════════════════════════════════════════════════════════════

        [HttpGet("api/privacy/vbtc/balance/{zfxAddress}/{scUID}")]
        public IActionResult GetShieldedVbtcBalance(string zfxAddress, string scUID)
        {
            try { return Ok(WalletPrivacyVbtcService.GetShieldedVbtcBalance(zfxAddress, scUID)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPost("api/privacy/vbtc/shield")]
        public async Task<IActionResult> ShieldVBTC([FromBody] VBTCShieldRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.FromAddress) || string.IsNullOrWhiteSpace(req.ZfxAddress) ||
                    string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "fromAddress, zfxAddress, scUID, and amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                return Ok(await WalletPrivacyVbtcService.ShieldVBTC(req.FromAddress, req.ZfxAddress, req.ScUID, amount));
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPost("api/privacy/vbtc/unshield")]
        public async Task<IActionResult> UnshieldVBTC([FromBody] VBTCPrivacyUnshieldRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ZfxAddress) || string.IsNullOrWhiteSpace(req.ToAddress) ||
                    string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "zfxAddress, toAddress, scUID, and amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                return Ok(await WalletPrivacyVbtcService.UnshieldVBTC(req.ZfxAddress, req.ToAddress, req.ScUID, amount, req.Password));
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPost("api/privacy/vbtc/transfer")]
        public async Task<IActionResult> PrivateTransferVBTC([FromBody] VBTCPrivacyTransferRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.FromZfxAddress) || string.IsNullOrWhiteSpace(req.ToZfxAddress) ||
                    string.IsNullOrWhiteSpace(req.ScUID) || string.IsNullOrWhiteSpace(req.Amount))
                    return BadRequest(new { success = false, message = "fromZfxAddress, toZfxAddress, scUID, and amount are required." });

                if (!decimal.TryParse(req.Amount, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
                    return BadRequest(new { success = false, message = "Invalid amount." });

                return Ok(await WalletPrivacyVbtcService.PrivateTransferVBTC(req.FromZfxAddress, req.ToZfxAddress, req.ScUID, amount, req.Password));
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/vbtc/scan/{zfxAddress}/{scUID}")]
        public IActionResult ScanShieldedVBTC(string zfxAddress, string scUID, [FromQuery] string? password = null, [FromQuery] long? fromBlock = null, [FromQuery] long? toBlock = null)
        {
            try { return Ok(WalletPrivacyVbtcService.ScanShieldedVBTC(zfxAddress, scUID, password, fromBlock, toBlock)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("api/privacy/vbtc/poolState/{scUID}")]
        public IActionResult GetVbtcShieldedPoolState(string scUID)
        {
            try { return Ok(WalletPrivacyVbtcService.GetVbtcShieldedPoolState(scUID)); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  Base Address Derivation
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Derives the deterministic Base (EVM) address for a given VFX address.
        /// Uses the same key derivation as validators — Keccak256 of the secp256k1 private key.
        /// </summary>
        [HttpGet("api/base-address/{vfxAddress}")]
        public IActionResult GetBaseAddress(string vfxAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(vfxAddress))
                    return BadRequest(new { success = false, message = "VFX address required." });

                var baseAddress = Bitcoin.Services.ValidatorEthKeyService.DeriveBaseAddressFromAccount(vfxAddress);
                if (string.IsNullOrEmpty(baseAddress))
                    return Ok(new { success = false, message = "Could not derive Base address. Account not found or key unavailable." });

                return Ok(new { success = true, message = "Base address derived.", baseAddress = baseAddress, vfxAddress = vfxAddress });
            }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }
        }
    }
}
