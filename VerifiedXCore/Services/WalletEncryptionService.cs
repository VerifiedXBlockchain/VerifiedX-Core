using VerifiedXCore.Data;
using VerifiedXCore.Models;
using System.Security.Cryptography;
using System.Text;

namespace VerifiedXCore.Services
{
    public class WalletEncryptionService
    {
        public static async Task<Keystore?> EncryptWallet(Account account, bool updateAccount = false)
        {
			var accounts = AccountData.GetAccounts();
			if(Globals.EncryptPassword.Length == 0)
            {
				return null;	
            }

			//Pulling password from secure string.
			var password = Globals.EncryptPassword.ToUnsecureString();

			//Generating a random key to encrypt private key with
			var key = new byte[32]; 
			RandomNumberGenerator.Create().GetBytes(key);
			var encryptionString = Convert.ToBase64String(key);

			//Encrypting private key with random 32 byte
			byte[] encrypted = EncryptKey(account.GetKey, key);

			//Encrypting random 32 byte with clients supplied password. This key will be stored and is encrypted.
			//VX-14: KDF-based wrap (PasswordKeyWrap v1), not the zero-padded password used as the AES key.
			var keyEncrypted = PasswordKeyWrap.Wrap(encryptionString, password);

			Keystore keystore = new Keystore
			{
				Address = account.Address,
				PrivateKey = Convert.ToBase64String(encrypted), 
				PublicKey = account.PublicKey,
				Key = keyEncrypted,
				IsUsed = false
			};

			if(updateAccount == true)
            {
				account.PrivateKey = Convert.ToBase64String(encrypted);
				accounts.UpdateSafe(account);
				keystore.IsUsed = true;

            }

			password = "0";

			return keystore;
		}

		/// <summary>
		/// VX-14: re-wraps every legacy (zero-padded password) keystore record under the KDF-based format once the
		/// wallet password is in memory. Records the password does not open, or whose data key does not decrypt the
		/// stored private key, are left untouched. Returns the number of records re-wrapped.
		/// </summary>
		public static int RewrapLegacyKeystoresIfUnlocked()
		{
			if (!Globals.IsWalletEncrypted || Globals.EncryptPassword == null || Globals.EncryptPassword.Length == 0)
				return 0;
			int n = 0;
			try
			{
				var keystores = Keystore.GetKeystore();
				if (keystores == null)
					return 0;
				var password = Globals.EncryptPassword.ToUnsecureString();
				foreach (var ks in keystores.FindAll().ToList())
				{
					if (TryRewrapLegacy(ks, password))
						n++;
				}
				if (n > 0)
					Utilities.LogUtility.Log($"VX-14: re-wrapped {n} legacy keystore record(s) under the KDF-based format.", "WalletEncryptionService.RewrapLegacyKeystoresIfUnlocked()");
			}
			catch (Exception ex)
			{
				Utilities.ErrorLogUtility.LogError($"Re-wrapping legacy keystores failed: {ex.Message}", "WalletEncryptionService.RewrapLegacyKeystoresIfUnlocked()");
			}
			return n;
		}

		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _passwordChecks = new();

		/// <summary>Test hook: forget verified passwords (a test process opens many wallets).</summary>
		internal static void ResetPasswordChecks() => _passwordChecks.Clear();

		/// <summary>
		/// VX-13 (follow-up): true when <paramref name="password"/> is this wallet's encryption password — it opens a
		/// keystore record and that record's data key opens its stored private key. Anything that seals keys under the
		/// in-memory password must check this first: the password can be set before it is verified (encpass= at startup,
		/// the unlock routes' set-then-verify window), and sealing under a typo would lock the keys for good.
		/// </summary>
		public static bool IsWalletPassword(string? password)
		{
			if (string.IsNullOrEmpty(password)) return false;
			var id = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("vfx-wallet-pw-check|" + password)));
			if (_passwordChecks.TryGetValue(id, out var known)) return known;
			var ok = false;
			try
			{
				var ks = Keystore.GetKeystore()?.FindAll().FirstOrDefault(k => !string.IsNullOrEmpty(k.Key) && !string.IsNullOrEmpty(k.PrivateKey));
				if (ks != null && PasswordKeyWrap.TryUnwrap(ks.Key, password, out var dataKey, out _))
				{
					var keyHex = DecryptKey(Convert.FromBase64String(ks.PrivateKey), Convert.FromBase64String(dataKey));
					ok = !string.IsNullOrEmpty(keyHex) && System.Numerics.BigInteger.TryParse(keyHex, System.Globalization.NumberStyles.AllowHexSpecifier, null, out _);
				}
			}
			catch { ok = false; }
			if (ok) _passwordChecks[id] = true; // only cache successes (the keystore can appear later)
			return ok;
		}

		/// <summary>
		/// NEW-01 (follow-up): encrypts an imported account for an encrypted wallet and saves its keystore record. Returns
		/// false (nothing stored) when the wallet password is not in memory or is not the wallet's. The import path used to
		/// insert the plaintext key first and then call EncryptWallet, which returned null with no password (the plaintext
		/// key stayed on disk) and, with a password, returned a keystore record the caller never saved (the wallet could
		/// no longer decrypt the imported key).
		/// </summary>
		public static async Task<bool> EncryptImportedAccount(Account account)
		{
			if (Globals.EncryptPassword == null || Globals.EncryptPassword.Length == 0)
				return false;
			if (!IsWalletPassword(Globals.EncryptPassword.ToUnsecureString()))
				return false;
			var ks = await EncryptWallet(account, false);
			if (ks == null)
				return false;
			ks.IsUsed = true;
			Keystore.SaveKeystore(ks);
			account.PrivateKey = ks.PrivateKey;
			return true;
		}

		/// <summary>VX-14: re-wraps one legacy keystore record (in memory and in the database). False if not legacy or not opened.</summary>
		public static bool TryRewrapLegacy(Keystore ks, string password)
		{
			if (ks == null || !PasswordKeyWrap.IsLegacy(ks.Key))
				return false;
			if (!PasswordKeyWrap.TryUnwrap(ks.Key, password, out var dataKey, out var wasLegacy) || !wasLegacy)
				return false;
			try
			{
				// The data key must actually open the stored private key before the record is rewritten.
				var keyHex = DecryptKey(Convert.FromBase64String(ks.PrivateKey), Convert.FromBase64String(dataKey));
				if (string.IsNullOrEmpty(keyHex) || !System.Numerics.BigInteger.TryParse(keyHex, System.Globalization.NumberStyles.AllowHexSpecifier, null, out _))
					return false;
			}
			catch
			{
				return false;
			}
			var oldKey = ks.Key;
			var newKey = PasswordKeyWrap.Wrap(dataKey, password);
			var db = Keystore.GetKeystore();
			// Field-level update, conditional on the record still holding the legacy value.
			var updated = db?.UpdateManySafe(x => new Keystore { Key = newKey }, x => x.Address == ks.Address && x.Key == oldKey) ?? 0;
			if (updated > 0)
				ks.Key = newKey;
			return updated > 0;
		}

        public static void DecryptWallet(string passphrase)
        {
            
        }

        public static void LockWallet()
        {
            //Removes key in memory.
        }

		static byte[] EncryptKey(string plainText, byte[] Key)
		{
			byte[] encrypted;
			byte[] IV;

			using (Aes aesAlg = Aes.Create())
			{
				aesAlg.Key = Key;

				aesAlg.GenerateIV();
				IV = aesAlg.IV;

				aesAlg.Mode = CipherMode.CBC;

				var encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

				// Create the streams used for encryption. 
				using (var msEncrypt = new MemoryStream())
				{
					using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
					{
						using (var swEncrypt = new StreamWriter(csEncrypt))
						{
							//Write all data to the stream.
							swEncrypt.Write(plainText);
						}
						encrypted = msEncrypt.ToArray();
					}
				}
			}

			var combinedIvCt = new byte[IV.Length + encrypted.Length];
			Array.Copy(IV, 0, combinedIvCt, 0, IV.Length);
			Array.Copy(encrypted, 0, combinedIvCt, IV.Length, encrypted.Length);

			// Return the encrypted bytes from the memory stream. 
			return combinedIvCt;

		}

		public static string DecryptKey(byte[] cipherTextCombined, byte[] Key)
		{

			// Declare the string used to hold 
			// the decrypted text. 
			string plaintext = null;

			// Create an Aes object 
			// with the specified key and IV. 
			using (Aes aesAlg = Aes.Create())
			{
				aesAlg.Key = Key;

				byte[] IV = new byte[aesAlg.BlockSize / 8];
				byte[] cipherText = new byte[cipherTextCombined.Length - IV.Length];

				Array.Copy(cipherTextCombined, IV, IV.Length);
				Array.Copy(cipherTextCombined, IV.Length, cipherText, 0, cipherText.Length);

				aesAlg.IV = IV;

				aesAlg.Mode = CipherMode.CBC;

				// Create a decrytor to perform the stream transform.
				ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

				// Create the streams used for decryption. 
				using (var msDecrypt = new MemoryStream(cipherText))
				{
					using (var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
					{
						using (var srDecrypt = new StreamReader(csDecrypt))
						{

							// Read the decrypted bytes from the decrypting stream
							// and place them in a string.
							plaintext = srDecrypt.ReadToEnd();
						}
					}
				}

			}

			return plaintext;

		}
	}
}
