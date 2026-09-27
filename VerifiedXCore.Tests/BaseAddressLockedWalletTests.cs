using System;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Tester report (Mac, encrypted + locked wallet): "Transfer to Base" preflight failed with FormatException in
    /// HexByteUtility.HexToByte from ValidatorEthKeyService.DeriveBaseAddressFromAccount - with the wallet locked,
    /// Account.GetKey returns the encrypted key record, which was parsed as hex. The Base address depends only on the public
    /// key, which the account stores in the clear; it is now derived from that. It must be the same address, cased the
    /// same way (EIP-55), as the private-key derivation every existing bridge used.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseAddressLockedWalletTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorEncrypted;

        public BaseAddressLockedWalletTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"baseaddr_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorEncrypted = Globals.IsWalletEncrypted;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            Globals.IsWalletEncrypted = _priorEncrypted;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>The derivation every existing bridge used: from the account's private key.</summary>
        private static string ThroughPrivateKey(Account a)
        {
            var hex = a.GetKey;
            if (hex.Length % 2 != 0) hex = "0" + hex;
            return ValidatorEthKeyService.DeriveBaseAddress(HexByteUtility.HexToByte(hex));
        }

        [Fact]
        public void TheSameAddress_AsThePrivateKeyDerivation_ForGeneratedKeys()
        {
            int shortKeys = 0;
            for (int i = 0; i < 300; i++)
            {
                var a = AccountData.CreateNewAccount(skipSave: true);
                AccountData.GetAccounts().Insert(a);
                if (a.GetKey.Length < 64) shortKeys++;       // leading zero byte: stored as 63 or fewer hex digits
                Assert.Equal(ThroughPrivateKey(a), ValidatorEthKeyService.DeriveBaseAddressFromAccount(a.Address));
            }
            Assert.True(shortKeys > 0);
        }

        [Fact]
        public async System.Threading.Tasks.Task TheSameAddress_AsThePrivateKeyDerivation_ForImportedKeys()
        {
            // Imported keys can have a high first byte (wallet-generated ones do not) - VX-11's import path.
            var rng = new Random(7);
            for (int i = 0; i < 20; i++)
            {
                var bytes = new byte[32];
                rng.NextBytes(bytes);
                bytes[0] = (byte)(0x80 | bytes[0]);
                var a = await AccountData.RestoreAccount(Convert.ToHexString(bytes).ToLowerInvariant());
                Assert.NotNull(a);
                var stored = AccountData.GetSingleAccount(a!.Address)!;
                Assert.Equal(ThroughPrivateKey(stored), ValidatorEthKeyService.DeriveBaseAddressFromAccount(stored.Address));
            }
        }

        [Fact]
        public void LockedEncryptedWallet_StillDerivesTheAddress()
        {
            var a = AccountData.CreateNewAccount(skipSave: true);
            var expected = ThroughPrivateKey(a);
            // Encrypted and locked: the stored key is the encrypted record (base64), and there is no password in memory.
            a.PrivateKey = Convert.ToBase64String(Enumerable.Range(0, 48).Select(i => (byte)(i * 7)).ToArray());
            AccountData.GetAccounts().Insert(a);
            Globals.IsWalletEncrypted = true;
            Assert.Equal(0, Globals.EncryptPassword.Length);
            Assert.False(a.GetKey.All(Uri.IsHexDigit));                                         // what the old code parsed as hex
            Assert.Equal(expected, ValidatorEthKeyService.DeriveBaseAddressFromAccount(a.Address));
        }

        [Fact]
        public void UnknownAccount_IsEmpty() =>
            Assert.Equal(string.Empty, ValidatorEthKeyService.DeriveBaseAddressFromAccount("RNotInThisWallet00000000000000000"));
    }
}
