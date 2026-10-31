using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.BIP39;
using VerifiedXCore.Commands;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// HD wallet restore derives the seed from the phrase's words, not from how they were spaced or capitalised, so a
    /// phrase pasted with stray whitespace restores the same wallet as every other BIP39 wallet. A wallet an earlier
    /// restore made from the phrase as typed (extra spaces hashed into the seed) keeps its used addresses reachable.
    /// </summary>
    [Collection("DbContextSequential")]
    public class HDWalletRestorePhraseSpacingTests : IDisposable
    {
        private const string Phrase = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        // Official BIP39 test vector for Phrase with an empty passphrase.
        private const string PhraseSeed = "5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc19a5ac40b389cd370d086206dec8aa6c43daea6690f20ad3d8d48b2d2ce9e38e4";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly bool _priorHd = Globals.HDWallet;
        private readonly TextReader _priorIn = Console.In;

        public HDWalletRestorePhraseSpacingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"hdrestore_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.IsWalletEncrypted = false;
        }

        public void Dispose()
        {
            Console.SetIn(_priorIn);
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.HDWallet = _priorHd;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string AddressAt(string seedHex, int index)
        {
            var key = new VerifiedXCore.BIP32.BIP32().DerivePath($"m/0'/0'/{index}'", seedHex).Key.ToStringHex();
            return AccountData.GetAddressFromHDKey(key);
        }

        private static void Fund(string address) =>
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = address, Balance = 5M, Nonce = 0 });

        private static async Task<bool> InWallet(string address)
        {
            for (int i = 0; i < 50; i++) // AddToAccount is fire-and-forget
            {
                if (AccountData.GetSingleAccount(address) != null) return true;
                await Task.Delay(20);
            }
            return false;
        }

        [Theory]
        [InlineData(Phrase)]
        [InlineData(Phrase + " ")]
        [InlineData(" " + Phrase)]
        [InlineData("abandon  abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
        [InlineData("abandon\tabandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about\r\n")]
        [InlineData("Abandon ABANDON abandon abandon abandon abandon abandon abandon abandon abandon abandon About")]
        [InlineData("abandon　abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
        public async Task AnySpacingOrCase_RestoresThePhrasesOwnSeed(string typed)
        {
            var result = await HDWallet.HDWalletData.RestoreHDWallet(typed);

            Assert.Contains("Restored", result);
            Assert.Equal(PhraseSeed, HDWallet.HDWalletData.GetHDWallet()!.WalletSeed);
        }

        [Fact]
        public void GeneratedPhrase_IsAlreadyInTheRestoredSpelling()
        {
            var generated = new Mnemonic().GenerateMnemonic(256, BIP39Wordlist.English);
            Assert.Equal(generated, HDWallet.HDWalletData.CanonicalMnemonic(generated));
        }

        [Theory]
        [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")]
        [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")]
        [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon aboutt")]
        [InlineData("   ")]
        public async Task InvalidPhrase_IsStillRefused(string typed)
        {
            var result = await HDWallet.HDWalletData.RestoreHDWallet(typed);

            Assert.DoesNotContain("Restored", result);
            Assert.Null(HDWallet.HDWalletData.GetHDWallet());
        }

        [Fact]
        public async Task TextThatCannotBeNormalized_IsRefusedNotThrown()
        {
            var result = await HDWallet.HDWalletData.RestoreHDWallet("abandon \uD800 about");

            Assert.DoesNotContain("Restored", result);
            Assert.Null(HDWallet.HDWalletData.GetHDWallet());
        }

        [Fact]
        public async Task ExtraSpaceWalletWithFunds_ItsAddressesAreRestoredAlongsideTheRealWallet()
        {
            var typed = Phrase + " ";
            var extraSpaceSeed = new Mnemonic().MnemonicToSeedHex(typed, ""); // what restore derived before
            Assert.NotEqual(PhraseSeed, extraSpaceSeed);
            var real0 = AddressAt(PhraseSeed, 0);
            var extra0 = AddressAt(extraSpaceSeed, 0);
            var extra2 = AddressAt(extraSpaceSeed, 2);
            Fund(real0);
            Fund(extra0);
            Fund(extra2);

            var result = await HDWallet.HDWalletData.RestoreHDWallet(typed);

            Assert.Contains("1 previously used address(es) restored", result);
            Assert.Contains("2 more address(es) restored", result);
            var hdw = HDWallet.HDWalletData.GetHDWallet()!;
            Assert.Equal(PhraseSeed, hdw.WalletSeed);
            Assert.Equal(1, hdw.Nonce); // new addresses continue the real wallet, not the extra-space one
            Assert.True(await InWallet(real0));
            Assert.True(await InWallet(extra0));
            Assert.True(await InWallet(extra2));
        }

        [Fact]
        public async Task CleanPhrase_DoesNotImportAnyOtherWallet()
        {
            var extra0 = AddressAt(new Mnemonic().MnemonicToSeedHex(Phrase + " ", ""), 0);
            Fund(extra0);

            var result = await HDWallet.HDWalletData.RestoreHDWallet(Phrase);

            Assert.Equal("Mnemonic Restored... 0 previously used address(es) restored.", result);
            Assert.False(await InWallet(extra0));
        }

        [Fact]
        public async Task ApiRoute_TrailingSpace_RestoresThePhrasesOwnSeed()
        {
            var output = await new VerifiedXCore.Controllers.V1Controller().GetRestoreHDWallet(Phrase + " ");

            Assert.Contains("Restored", output);
            Assert.Equal(PhraseSeed, HDWallet.HDWalletData.GetHDWallet()!.WalletSeed);
        }

        [Fact]
        public async Task CliRestore_PastedWithStraySpaces_RestoresThePhrasesOwnSeed()
        {
            Console.SetIn(new StringReader("  abandon abandon abandon abandon abandon  abandon abandon abandon abandon abandon abandon about \n"));

            var result = await BaseCommandServices.RestoreHDWallet();

            Assert.Contains("Restored", result);
            Assert.Equal(PhraseSeed, HDWallet.HDWalletData.GetHDWallet()!.WalletSeed);
        }
    }
}
