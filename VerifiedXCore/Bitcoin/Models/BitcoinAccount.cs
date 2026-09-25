using NBitcoin;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace VerifiedXCore.Bitcoin.Models
{
    public class BitcoinAccount
    {
        #region Variables

        public long Id { get; set; }
        [Newtonsoft.Json.JsonIgnore][System.Text.Json.Serialization.JsonIgnore] // BB-2 (VX-13): key material is never serialized into API responses
        public string PrivateKey { get; set; }
        [Newtonsoft.Json.JsonIgnore][System.Text.Json.Serialization.JsonIgnore]
        public string WifKey { get; set; }
        public string PublicKey { set; get; }
        public string Address { get; set; }
        /// <summary>VX-13: true when PrivateKey holds a KeystoreCrypto-sealed value (wallet encrypted). Read the key
        /// with BitcoinKeystore.GetPrivateKeyHex / GetWif, never the fields directly.</summary>
        public bool IsEncrypted { get; set; }
        public string? ADNR { get; set; }
        public decimal Balance { get; set; }
        public bool IsValidating { get; set; }

        /// <summary>Optional Base (EVM) address linked to this BTC account for wallet Base ETH / vBTC.b balance display.</summary>
        public string? LinkedEvmAddress { get; set; }

        #endregion

        #region GetBitcoin DB
        public static LiteDB.ILiteCollection<BitcoinAccount>? GetBitcoin()
        {
            try
            {
                var bitcoin = DbContext.DB_Bitcoin.GetCollection<BitcoinAccount>(DbContext.RSRV_BITCOIN);
                return bitcoin;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "BitcoinAccount.GetBitcoin()");
                return null;
            }

        }

        #endregion

        #region Get Bitcoin Address List
        public static List<BitcoinAccount>? GetBitcoinAccounts()
        {
            var bitcoin = GetBitcoin();
            if (bitcoin == null)
            {
                ErrorLogUtility.LogError("GetBitcoin() returned a null value.", "BitcoinAccount.GetBitcoinAccounts()");
            }
            else
            {
                var btcRecs = bitcoin.FindAll().ToList();
                if (btcRecs.Any())
                {
                    return btcRecs;
                }
                else
                {
                    return null;
                }
            }

            return null;

        }
        #endregion

        #region Get Bitcoin Address
        public static BitcoinAccount? GetBitcoinAccount(string address)
        {
            var bitcoin = GetBitcoin();
            if (bitcoin == null)
            {
                ErrorLogUtility.LogError("GetBitcoin() returned a null value.", "BitcoinAccount.GetBitcoinAccount()");
            }
            else
            {
                var btcRec = bitcoin.Query().Where(x => x.Address == address).FirstOrDefault();
                if (btcRec != null)
                {
                    return btcRec;
                }
                else
                {
                    return null;
                }
            }

            return null;

        }
        #endregion

        #region Save Bitcoin Address
        public static bool SaveBitcoinAddress(BitcoinAccount btcAddr)
        {
            var bitcoin = GetBitcoin();
            if (bitcoin == null)
            {
                ErrorLogUtility.LogError("GetBitcoin() returned a null value.", "BitcoinAddress.GetBitcoin()");
            }
            else
            {
                var btcRec = bitcoin.FindOne(x => x.Address == btcAddr.Address);
                if (btcRec != null)
                {
                    return false;
                }
                else
                {
                    // VX-13: an encrypted wallet never stores a plaintext Bitcoin key. Sealed with the wallet
                    // password; refused while the wallet is locked (the key would otherwise be written in the clear).
                    if (Globals.IsWalletEncrypted && !Services.BitcoinKeystore.TrySeal(btcAddr))
                    {
                        ErrorLogUtility.LogError($"Refused to store Bitcoin key for {btcAddr.Address}: wallet is encrypted and locked.", "BitcoinAccount.SaveBitcoinAddress()");
                        return false;
                    }
                    bitcoin.InsertSafe(btcAddr);
                    return true;
                }
            }

            return false;

        }
        #endregion

        #region Create Bitcoin Address
        /// <summary>Null when <paramref name="save"/> is set and the address could not be stored (encrypted wallet locked):
        /// an address whose key was never stored must not be shown, or funds sent to it are lost (fourth review).</summary>
        public static BitcoinAccount? CreateAddress(bool save = true)
        {
            Key privateKey = new Key();

            PubKey publicKey = privateKey.PubKey;

            // Create a Bitcoin address from the public key
            NBitcoin.BitcoinAddress bitcoinAddress = publicKey.GetAddress(Globals.ScriptPubKeyType, Globals.BTCNetwork);
            string privateKeyHex = privateKey.ToHex();

            string wif = privateKey.GetWif(Globals.BTCNetwork).ToString();

            BitcoinAccount btcAddress = new BitcoinAccount {
                Address = bitcoinAddress.ToString(),
                Balance = 0M,
                IsValidating = false,
                PrivateKey = privateKeyHex,
                PublicKey = publicKey.ToString(),
                WifKey = wif, 
            };

            if(save && !SaveBitcoinAddress(btcAddress))
                return null;

            return btcAddress;
        }
        #endregion

        #region Create Bitcoin Address For Arbiter
        public static string CreatePublicKeyForArbiter(string signingPrivateKey, string scUID)
        {
            byte[] hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(signingPrivateKey + scUID));
            // Generate a random private key
            Key privateKey = new Key(hash);

            // Derive the corresponding public key
            PubKey publicKey = privateKey.PubKey;
            return publicKey.ToString();
        }
        #endregion

        #region Create Bitcoin Address For Arbiter
        public static Key CreatePrivateKeyForArbiter(string signingPrivateKey, string scUID)
        {
            //byte[] hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(signingPrivateKey + scUID));
            //// Generate a random private key
            //Key privateKey = new Key(hash);

            //return privateKey;

            // NEW-14: the derivation input (the arbiter's signing PRIVATE key + scUID) used to be written to sclog.txt in
            // plaintext - three times, as text and bytes - on a path any remote caller with a VFX keypair can trigger.
            var inputString = signingPrivateKey + scUID;

            byte[] hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(inputString));
            Key privateKey = new Key(hash);

            // Log the generated public key
            SCLogUtility.Log($"Generated PubKey: {privateKey.PubKey}", "BitcoinAccount");

            return privateKey;
        }
        #endregion

        #region Import Private Key Hex
        /// <summary>False when the key was not stored (already present, or encrypted wallet locked).</summary>
        public static bool ImportPrivateKey(string privateKey, ScriptPubKeyType scriptPubKeyType)
        {
            byte[] privateKeyBytes = privateKey.HexToByteArray();
            Key recreatedKey = new Key(privateKeyBytes);

            PubKey publicKey = recreatedKey.PubKey;

            // Create a Bitcoin address from the public key
            NBitcoin.BitcoinAddress bitcoinAddress = publicKey.GetAddress(scriptPubKeyType, Globals.BTCNetwork);
            string privateKeyHex = recreatedKey.ToHex();

            string wif = recreatedKey.GetWif(Globals.BTCNetwork).ToString();

            BitcoinAccount btcAddress = new BitcoinAccount
            {
                Address = bitcoinAddress.ToString(),
                Balance = 0M, //perform balance check here
                IsValidating = false,
                PrivateKey = privateKeyHex,
                PublicKey = publicKey.ToString(),
                WifKey = wif,
            };

            var stored = SaveBitcoinAddress(btcAddress);
            if (stored)
                _ = AddressSyncService.SyncAddress(btcAddress.Address);
            return stored;
        }

        #endregion

        #region Import Private Key WIF
        public static bool ImportPrivateKeyWIF(string privateKey, ScriptPubKeyType scriptPubKeyType)
        {
            BitcoinSecret bitcoinSecret = new BitcoinSecret(privateKey, Globals.BTCNetwork);
            // Get the private key
            Key recreatedKey = bitcoinSecret.PrivateKey;

            PubKey publicKey = recreatedKey.PubKey;

            // Create a Bitcoin address from the public key
            NBitcoin.BitcoinAddress bitcoinAddress = publicKey.GetAddress(scriptPubKeyType, Globals.BTCNetwork);
            string privateKeyHex = recreatedKey.ToHex();

            string wif = recreatedKey.GetWif(Globals.BTCNetwork).ToString();

            BitcoinAccount btcAddress = new BitcoinAccount
            {
                Address = bitcoinAddress.ToString(),
                Balance = 0M, //perform balance check here
                IsValidating = false,
                PrivateKey = privateKeyHex,
                PublicKey = publicKey.ToString(),
                WifKey = wif,
            };

            var stored = SaveBitcoinAddress(btcAddress);
            if (stored)
                _ = AddressSyncService.SyncAddress(btcAddress.Address);
            return stored;
        }

        #endregion

        #region Linked EVM (Base) address

        public static bool SetLinkedEvmAddress(string btcAddress, string? evmAddress)
        {
            if (!string.IsNullOrEmpty(evmAddress))
            {
                var e = evmAddress.Trim();
                if (!e.StartsWith("0x", StringComparison.Ordinal) || e.Length != 42)
                    return false;
            }

            var accounts = GetBitcoin();
            if (accounts == null) return false;

            var account = accounts.FindOne(x => x.Address == btcAddress);
            if (account == null) return false;

            account.LinkedEvmAddress = string.IsNullOrWhiteSpace(evmAddress) ? null : evmAddress.Trim();
            accounts.UpdateSafe(account);
            return true;
        }

        #endregion

        #region Print Account Info
        public static void PrintAccountInfo(BitcoinAccount account)
        {
            Console.Clear();
            Console.WriteLine("\n\n\nYour Wallet");
            Console.WriteLine("======================");
            Console.WriteLine("\nAddress :\n{0}", account.Address);
            Console.WriteLine("\nPublic Key (Uncompressed):\n{0}", account.PublicKey);
            Console.WriteLine("\nPrivate Key:\n{0}", Services.BitcoinKeystore.GetPrivateKeyHex(account) ?? "(wallet locked)");
            Console.WriteLine("\nWif Key:\n{0}", Services.BitcoinKeystore.GetWif(account) ?? "(wallet locked)");
            Console.WriteLine("\n - - - - - - - - - - - - - - - - - - - - - - ");
            Console.WriteLine("*** Be sure to save private key!                   ***");
            Console.WriteLine("*** Use your private key to restore account!       ***");
        }

        #endregion

        #region Add ADNR Record
        public static async Task AddAdnrToAccount(string address, string name)
        {
            var accounts = GetBitcoin();
            if(accounts != null)
            {
                var account = accounts.FindOne(x => x.Address == address);

                if (account != null)
                {
                    account.ADNR = name.ToLower();
                    accounts.UpdateSafe(account);
                }
            }
        }

        #endregion

        #region Transfer ADNR
        public static async Task TransferAdnrToAccount(string toAddress)
        {
            var adnrs = BitcoinAdnr.GetBitcoinAdnr();
            if (adnrs != null)
            {
                var adnr = adnrs.FindOne(x => x.BTCAddress == toAddress); //state trei has alrea
                if (adnr != null)
                {
                    var accounts = GetBitcoin();
                    var account = accounts.FindOne(x => x.Address == toAddress);

                    if (account != null)
                    {
                        account.ADNR = adnr.Name;
                        accounts.UpdateSafe(account);
                    }
                }
            }
        }

        #endregion

        #region Remove ADNR Record
        public static async Task RemoveAdnrFromAccount(string address)
        {
            var accounts = GetBitcoin();
            if (accounts != null)
            {
                var account = accounts.FindOne(x => x.Address == address);

                if (account != null)
                {
                    account.ADNR = null;
                    accounts.UpdateSafe(account);
                }
            }
        }

        #endregion
    }
}
