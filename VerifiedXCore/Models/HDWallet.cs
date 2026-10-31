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

            /// <summary>True when earlier versions restored a different wallet from this text: it passed their check
            /// but differs from its rebuilt form in spacing, which their seed included.</summary>
            public static bool HasExtraSpacing(string? typedMnemonic)
            {
                var typed = typedMnemonic ?? "";
                string canonical;
                try { canonical = CanonicalMnemonic(typed); }
                catch (ArgumentException) { return false; }
                return typed.Normalize(NormalizationForm.FormKD) != canonical
                    && new Mnemonic().ValidateMnemonic(typed, BIP39Wordlist.English);
            }

            public const string ExactSpacingHint = " This phrase has extra spaces, which earlier versions restored as a different wallet. If you used that wallet, restore it with exact spacing on a node that has no HD wallet.";

            /// <param name="exactSpacing">Restore the wallet earlier versions made from a phrase with extra spaces
            /// (see <see cref="HasExtraSpacing"/>) as this node's HD wallet, so new addresses continue that wallet.</param>
            public static async Task<string> RestoreHDWallet(string mnemonicStr, string password = "", bool exactSpacing = false)
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

                var realSeed = mnemonic.MnemonicToSeedHex(mnemonicStr, password);

                // Restore used to accept extra spaces and derive the seed from the phrase as typed, a different wallet.
                // When this input would have restored such a wallet, the caller picks which one becomes the HD wallet
                // and the other one's used addresses are imported too.
                var typedSeed = HasExtraSpacing(typedMnemonic) ? mnemonic.MnemonicToSeedHex(typedMnemonic, password) : null;
                var useTypedSeed = exactSpacing && typedSeed != null;
                var myMnemonicSeed = useTypedSeed ? typedSeed! : realSeed;
                var otherSeed = typedSeed == null ? null : useTypedSeed ? realSeed : typedSeed;

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
                int otherRestoredCount = 0;
                bool scanningOther = false;
                var hint = typedSeed != null && !useTypedSeed ? ExactSpacingHint : "";
                try
                {
                    var highestActiveIndex = await RestoreUsedAddresses(myMnemonicSeed, () => restoredCount++);

                    // Continue new-address derivation after the last used index.
                    hdw.Nonce = highestActiveIndex + 1;
                    hd.UpdateSafe(hdw);

                    scanningOther = otherSeed != null;
                    if (otherSeed != null)
                        await RestoreUsedAddresses(otherSeed, () => otherRestoredCount++);
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"HD address scan failed after restoring {restoredCount + otherRestoredCount} address(es). Error: {ex}", "HDWallet.RestoreHDWallet()");
                    var otherHint = useTypedSeed ? " To restore this phrase's real wallet, restore it without exact spacing on a node that has no HD wallet." : hint;
                    if (scanningOther) // new addresses continue this wallet, not the other one
                        return $"Mnemonic Restored... {restoredCount} previously used address(es) restored. Scanning {(useTypedSeed ? "this phrase's real wallet" : "the wallet earlier versions made from this phrase's extra spaces")} failed after restoring {otherRestoredCount} address(es).{otherHint}";
                    return $"Mnemonic Restored... Address scan failed after restoring {restoredCount} address(es). Remaining addresses can be re-derived one at a time with the new address command.{otherHint}";
                }

                if (otherRestoredCount > 0)
                    return useTypedSeed
                        ? $"Mnemonic Restored... {restoredCount} previously used address(es) restored. {otherRestoredCount} more address(es) restored from this phrase's real wallet, the one other BIP39 wallets restore."
                        : $"Mnemonic Restored... {restoredCount} previously used address(es) restored. {otherRestoredCount} more address(es) restored from the different wallet an earlier restore made from this phrase as typed with its extra spaces; move their funds to this wallet.{hint}";

                return $"Mnemonic Restored... {restoredCount} previously used address(es) restored.{hint}";

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
