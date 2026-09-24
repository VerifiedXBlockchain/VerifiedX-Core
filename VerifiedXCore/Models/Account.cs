using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Services;
using VerifiedXCore.Voting;

namespace VerifiedXCore.Models
{
    public class Account
    {
        private string _privateKey;
        public long Id { get; set; }
        /// <summary>
        /// This is where a private key is stored. Do not use this to get the private key. Instead use GetKey.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore][System.Text.Json.Serialization.JsonIgnore] // BB-2: key material is never serialized into API responses
        public string PrivateKey { get; set; }

        /// <summary>
        /// VX-11 (transient, never stored): set by AccountData.RestoreAccount when the legacy address an older
        /// wallet derived from the same key was restored alongside this one.
        /// </summary>
        [LiteDB.BsonIgnore]
        public string? AlsoRestoredLegacyAddress { get; set; }
        public string PublicKey { set; get; }
        public string Address { get; set; }
        public string? ADNR { get; set; }
        public decimal Balance { get; set; }
        public decimal LockedBalance { get; set; }
        public bool IsValidating { get; set; }

        /// <summary>
        /// This will return your private key. It called the GetPrivateKey(PrivateKey, Address) method.
        /// It will return either the private key, or the private key encrypted/decrypted depending if password is present.
        /// </summary>
        /// <returns>
        /// public string PrivateKey
        /// </returns>
        /// <exception cref="PrivateKey"></exception>
        // NEW-01: never persisted. GetKey decrypts when the wallet is unlocked; LiteDB used to write it (in
        // plaintext) into the wallet database on every save made while unlocked.
        [LiteDB.BsonIgnore]
        [Newtonsoft.Json.JsonIgnore][System.Text.Json.Serialization.JsonIgnore]
        public string GetKey{ get { return GetPrivateKey(PrivateKey, Address); } }
        [LiteDB.BsonIgnore]
        [Newtonsoft.Json.JsonIgnore][System.Text.Json.Serialization.JsonIgnore]
        public PrivateKey? GetPrivKey { get { return GetClassPrivateKey(GetKey); } }

        public Account Build()
        {
            var account = new Account();
            account = AccountData.CreateNewAccount();
            return account;
        }

        public async static Task<Account> Restore(string privKey, bool rescanForTx = false, bool legacy = false)
        {
            Account account = await AccountData.RestoreAccount(privKey, rescanForTx, legacy: legacy);
            return account;
        }
        public static async Task AddAdnrToAccount(string address, string name)
        {
            var accounts = AccountData.GetAccounts();
            var account = accounts.FindOne(x => x.Address == address);

            if(account != null)
            {
                account.ADNR = name.ToLower();
                accounts.UpdateSafe(account);
            }
        }
        public static async Task DeleteAdnrFromAccount(string address)
        {
            var accounts = AccountData.GetAccounts();
            var account = accounts.FindOne(x => x.Address == address);

            if (account != null)
            {
                account.ADNR = null;
                accounts.UpdateSafe(account);
            }
        }
        public static async Task TransferAdnrToAccount(string fromAddress, string toAddress)
        {
            var adnrs = Adnr.GetAdnr();
            if(adnrs != null)
            {
                var adnr = adnrs.FindOne(x => x.Address == toAddress); //state trei has alrea
                if (adnr != null)
                {
                    var accounts = AccountData.GetAccounts();
                    var account = accounts.FindOne(x => x.Address == toAddress);

                    if (account != null)
                    {
                        account.ADNR = adnr.Name;
                        accounts.UpdateSafe(account);
                    }
                }
            }
        }

        private PrivateKey? GetClassPrivateKey(string privkey)
        {
            try
            {
                BigInteger b1 = BigInteger.Parse(privkey, NumberStyles.AllowHexSpecifier);//converts hex private key into big int.
                PrivateKey privateKey = new PrivateKey("secp256k1", b1);

                return privateKey;
            }
            catch { }

            return null;
        }

        private string GetPrivateKey(string privkey, string address)
        {
            if (Globals.IsWalletEncrypted == true)
            {
                //decrypt private key for send
                if (Globals.EncryptPassword.Length == 0)
                {
                    return privkey;
                }
                else
                {
                    try
                    {
                        var keystores = Keystore.GetKeystore();
                        if (keystores != null)
                        {
                            var keystore = keystores.FindOne(x => x.Address == address);
                            if (keystore != null)
                            {
                                var password = Globals.EncryptPassword.ToUnsecureString();

                                // VX-14: KDF-based wrap (v1) or the legacy zero-padded-password wrap (v0).
                                if (!PasswordKeyWrap.TryUnwrap(keystore.Key, password, out var keyDecrypted, out var wasLegacy))
                                    return privkey;

                                var encryptedPrivKey = Convert.FromBase64String(privkey);
                                var privKeyDecrypted = WalletEncryptionService.DecryptKey(encryptedPrivKey, Convert.FromBase64String(keyDecrypted));

                                // Legacy record opened with the right password: re-wrap it now.
                                if (wasLegacy)
                                    WalletEncryptionService.TryRewrapLegacy(keystore, password);

                                //clearing values
                                password = "0";
                                encryptedPrivKey = new byte[0];
                                keyDecrypted = "0";
                                return privKeyDecrypted;

                            }
                            else
                            {
                                return privkey;
                            }
                        }
                        else
                        {
                            return privkey;
                        }
                    }
                    catch (Exception ex)
                    {
                        return privkey;
                    }
                }
            }
            else
            {
                return privkey;
            }
        }
    }


}
