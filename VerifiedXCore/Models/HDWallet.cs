using VerifiedXCore.Extensions;
using VerifiedXCore.Data;
using VerifiedXCore.BIP39;
using VerifiedXCore.BIP32;
using VerifiedXCore.Utilities;
using System.Text;

namespace VerifiedXCore.Models
{
    public class HDWallet
    {
        public int Id { get; set; }
        public string WalletSeed { get; set; }
        public int Nonce { get; set; }
        public string Path { get; set; }

        public class HDWalletData
        {
            public static LiteDB.ILiteCollection<HDWallet> GetHDWalletData()
            {
                var hdwallet = DbContext.DB_HD_Wallet.GetCollection<HDWallet>(DbContext.RSRV_HD_WALLET);
                return hdwallet;
            }

            /// <summary>NEW-01 (follow-up): HD and wallet encryption are mutually exclusive (encrypting an HD wallet was
            /// already refused); the seed and every derived key are stored unencrypted, so none is made in an encrypted wallet.</summary>
            public const string EncryptedWalletRefusal = "An HD wallet cannot be used in an encrypted wallet: its seed and derived keys would be stored unencrypted.";

            public static (bool,string) CreateHDWallet(int amount, BIP39Wordlist wordList, string password = "")
            {
                if (Globals.IsWalletEncrypted)
                    return (false, EncryptedWalletRefusal);
                var hd = GetHDWalletData();
                var hdwExist = GetHDWallet();
                if(hdwExist != null)
                {
                    return (false, "HD wallet exist");
                }

                int strength = 256;

                if(amount == 12)
                {
                    strength = 128;
                }

                Mnemonic mnemonic = new Mnemonic();
                var myMnemonic = mnemonic.GenerateMnemonic(strength, wordList);
                var myMnemonicSeed = mnemonic.MnemonicToSeedHex(myMnemonic, password);

                HDWallet hdw = new HDWallet { 
                    Nonce = 0,
                    Path = "m/0'/0'",
                    WalletSeed = myMnemonicSeed
                };

                hd.InsertSafe(hdw);

                return (true,myMnemonic);
            }

            /// <summary>The one spelling a phrase's seed is derived from: NFKD, words split on any whitespace,
            /// lowercased and joined by single spaces. A generated phrase is already in this form.</summary>
            public static string CanonicalMnemonic(string? mnemonicStr) =>
                string.Join(" ", (mnemonicStr ?? "").Normalize(NormalizationForm.FormKD)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.ToLowerInvariant()));

            public static async Task<string> RestoreHDWallet(string mnemonicStr, string password = "")
            {
                if (Globals.IsWalletEncrypted)
                    return EncryptedWalletRefusal;
                var hd = GetHDWalletData();
                var hdwExist = GetHDWallet();
                if (hdwExist != null)
                {
                    return "HD Wallet Already Exist";
                }

                Mnemonic mnemonic = new Mnemonic();
                var typedMnemonic = mnemonicStr ?? "";
                try { mnemonicStr = CanonicalMnemonic(typedMnemonic); }
                catch (ArgumentException) { mnemonicStr = ""; } // text that cannot be normalized is not a phrase
                var validateMnemonic = mnemonic.ValidateMnemonic(mnemonicStr, BIP39Wordlist.English);
                if(validateMnemonic == false)
                {
                    return "Invalid Mnemonic Entered... Please Try again.";
                }

                var myMnemonicSeed = mnemonic.MnemonicToSeedHex(mnemonicStr, password);

                // Restore used to accept extra spaces and derive the seed from the phrase as typed, a different wallet.
                // When this input would have restored such a wallet, its used addresses are imported too.
                var typedSeed = typedMnemonic.Normalize(NormalizationForm.FormKD) != mnemonicStr
                    && mnemonic.ValidateMnemonic(typedMnemonic, BIP39Wordlist.English)
                        ? mnemonic.MnemonicToSeedHex(typedMnemonic, password)
                        : null;

                HDWallet hdw = new HDWallet
                {
                    Nonce = 0,
                    Path = "m/0'/0'",
                    WalletSeed = myMnemonicSeed
                };

                hd.InsertSafe(hdw);

                Globals.HDWallet = true;

                // Gap-limit scan: previously used addresses are not recoverable from the seed alone,
                // so probe derivation indices against chain state and restore every active one.
                // Stops after GapLimit consecutive addresses with no on-chain activity.
                const int GapLimit = 10;
                int restoredCount = 0;
                int typedRestoredCount = 0;
                try
                {
                    var highestActiveIndex = await RestoreUsedAddresses(myMnemonicSeed, () => restoredCount++);

                    // Continue new-address derivation after the last used index.
                    hdw.Nonce = highestActiveIndex + 1;
                    hd.UpdateSafe(hdw);

                    if (typedSeed != null)
                        await RestoreUsedAddresses(typedSeed, () => typedRestoredCount++);
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"HD address scan failed after restoring {restoredCount + typedRestoredCount} address(es). Error: {ex}", "HDWallet.RestoreHDWallet()");
                    return $"Mnemonic Restored... Address scan failed after restoring {restoredCount + typedRestoredCount} address(es). Remaining addresses can be re-derived one at a time with the new address command.";
                }

                if (typedRestoredCount > 0)
                    return $"Mnemonic Restored... {restoredCount} previously used address(es) restored. {typedRestoredCount} more address(es) restored from the different wallet an earlier restore made from this phrase as typed with its extra spaces; move their funds to this wallet.";

                return $"Mnemonic Restored... {restoredCount} previously used address(es) restored.";

                async Task<int> RestoreUsedAddresses(string seed, Action restored)
                {
                    BIP32.BIP32 bip32 = new BIP32.BIP32();
                    var scStateTrei = SmartContractStateTrei.GetSCST();
                    int index = 0;
                    int consecutiveUnused = 0;
                    int highestActiveIndex = -1;

                    while (consecutiveUnused < GapLimit)
                    {
                        var derivePath = bip32.DerivePath($"{hdw.Path}/{index}'", seed);
                        var key = derivePath.Key.ToStringHex();
                        var address = AccountData.GetAddressFromHDKey(key);

                        bool active =
                            StateData.GetSpecificAccountStateTrei(address) != null ||
                            Adnr.GetAdnr(address) != null ||
                            scStateTrei.Exists(x => x.OwnerAddress == address || (x.MinterAddress == address && x.MinterManaged == true));

                        if (active)
                        {
                            await AccountData.RestoreHDAccount(key);
                            highestActiveIndex = index;
                            consecutiveUnused = 0;
                            restored();
                        }
                        else
                        {
                            consecutiveUnused++;
                        }
                        index++;
                    }

                    return highestActiveIndex;
                }
            }

            public static HDWallet? GetHDWallet()
            {
                var hd = GetHDWalletData();
                if (hd != null)
                {
                    var hdw = hd.FindAll().FirstOrDefault();
                    if (hdw != null)
                    {
                        return hdw;
                    }
                }
                return null;
            }

            public static async Task<Account?> GenerateAddress()
            {
                if (Globals.IsWalletEncrypted)
                    return null; // NEW-01 (follow-up): see EncryptedWalletRefusal
                var hd = GetHDWalletData();
                var hdw = GetHDWallet();

                if(hdw != null)
                {
                    var seed = hdw.WalletSeed;
                    var path = hdw.Path;
                    var nonce = hdw.Nonce;
                    var expectedPath = $"{path}/{nonce}'";
                    BIP32.BIP32 bip32 = new BIP32.BIP32();
                    var derivePath = bip32.DerivePath(expectedPath, seed);
                    var key = derivePath.Key.ToStringHex();
                    var account = await AccountData.RestoreHDAccount(key);

                    IncrementNonce(hdw);

                    return account;
                }
                else
                {
                    //no hd wallet
                    return null;
                }
            }

            public static void IncrementNonce(HDWallet hdw)
            {
                var hd = GetHDWalletData();
                hdw.Nonce += 1;
                hd.UpdateSafe(hdw);
            }
        }

    }
}
