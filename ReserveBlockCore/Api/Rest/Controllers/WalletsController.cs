using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.BIP39;
using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Globalization;
using System.Numerics;
using System.Security;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class WalletsController : RestBaseController
    {
        /// <summary>
        /// Health check — no auth required
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok("Online");
        }

        /// <summary>
        /// Wallet status including sync, peers, version
        /// </summary>
        [HttpGet("info")]
        public async Task<IActionResult> GetInfo()
        {
            var peersConnected = await P2PClient.ArePeersConnected();
            var peerCount = peersConnected ? Globals.Nodes.Count : 0;

            var info = new
            {
                BlockHeight = Globals.LastBlock.Height,
                PeerCount = peerCount,
                BlocksDownloading = Globals.BlocksDownloadSlim.CurrentCount == 0,
                IsResyncing = Globals.IsResyncing,
                IsChainSynced = Globals.IsChainSynced,
                ChainCorrupted = Globals.DatabaseCorruptionDetected,
                DuplicateValIP = Globals.DuplicateAdjIP,
                DuplicateValAddress = Globals.DuplicateAdjAddr,
                UpToDate = Globals.UpToDate,
                BlockVersion = BlockVersionUtility.GetBlockVersion(Globals.LastBlock.Height),
                TimeInSync = Globals.TimeInSync,
                TimeSyncError = Globals.TimeSyncError
            };

            return Ok(info);
        }

        /// <summary>
        /// CLI version string
        /// </summary>
        [HttpGet("version")]
        public IActionResult GetVersion()
        {
            return Ok(Globals.CLIVersion);
        }

        /// <summary>
        /// Check encryption state
        /// </summary>
        [HttpGet("encryption-status")]
        public IActionResult GetEncryptionStatus()
        {
            var result = new
            {
                IsEncrypted = Globals.IsWalletEncrypted,
                HasPassword = Globals.IsWalletEncrypted && Globals.EncryptPassword.Length != 0
            };

            return Ok(result);
        }

        /// <summary>
        /// Encrypt the wallet with a password
        /// </summary>
        [HttpPost("encrypt")]
        public async Task<IActionResult> Encrypt([FromBody] WalletPasswordRequest request)
        {
            if (Globals.HDWallet == true)
                return Fail("HD_WALLET", "HD wallet cannot be encrypted at this time.");

            if (Globals.IsWalletEncrypted)
                return Fail("ALREADY_ENCRYPTED", "Wallet is already encrypted.");

            Globals.EncryptPassword = request.Password.ToSecureString();
            await Keystore.GenerateKeystoreAddresses();
            Globals.IsWalletEncrypted = true;

            return Ok("Wallet encrypted.");
        }

        /// <summary>
        /// Unlock an encrypted wallet
        /// </summary>
        [HttpPost("unlock")]
        public async Task<IActionResult> Unlock([FromBody] WalletPasswordRequest request)
        {
            if (!Globals.IsWalletEncrypted)
                return Fail("NOT_ENCRYPTED", "Wallet is not encrypted.");

            Globals.EncryptPassword = request.Password.ToSecureString();
            var accounts = AccountData.GetAccounts();
            if (accounts == null)
                return Fail("NO_ACCOUNTS", "No accounts in wallet.", 404);

            var account = accounts.Query().Where(x => x.Address != null).FirstOrDefault();
            if (account == null)
                return Fail("NO_ACCOUNTS", "No accounts in wallet.", 404);

            await Task.Delay(200);
            var privKey = account.GetKey;
            BigInteger b1 = BigInteger.Parse(privKey, NumberStyles.AllowHexSpecifier);
            PrivateKey privateKey = new PrivateKey("secp256k1", b1);

            var randString = RandomStringUtility.GetRandomString(8);
            var signature = SignatureService.CreateSignature(randString, privateKey, account.PublicKey);
            var sigVerify = SignatureService.VerifySignature(account.Address, randString, signature);

            if (!sigVerify)
            {
                Globals.EncryptPassword.Dispose();
                Globals.EncryptPassword = new SecureString();
                return Fail("WRONG_PASSWORD", "Password was incorrect.", 401);
            }

            Globals.GUIPasswordNeeded = false;
            return Ok($"Password has been stored for {Globals.PasswordClearTime} minutes.");
        }

        /// <summary>
        /// Lock the wallet (clear encryption password from memory)
        /// </summary>
        [HttpPost("lock")]
        public IActionResult Lock()
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress) && Globals.AdjudicateAccount == null)
            {
                Globals.EncryptPassword.Dispose();
                Globals.EncryptPassword = new SecureString();
                return Ok("Wallet is locked.");
            }

            return Fail("LOCK_FAILED", "Cannot lock wallet while validating.", 409);
        }

        /// <summary>
        /// Create an HD wallet
        /// </summary>
        [HttpPost("hd")]
        public IActionResult CreateHd([FromBody] CreateHdWalletRequest request)
        {
            var mnemonic = HDWallet.HDWalletData.CreateHDWallet(request.Strength, BIP39Wordlist.English);

            Globals.HDWallet = mnemonic.Item1;

            var result = new
            {
                Success = mnemonic.Item1,
                Mnemonic = mnemonic.Item2
            };

            return Created(result);
        }
    }
}
