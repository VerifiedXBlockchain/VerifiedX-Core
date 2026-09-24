using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-11 (HIGH): "Private key import derives the wrong address for approximately half of all valid keys".
    ///
    /// Audit PoC: keys whose first hex digit is 8–f, imported via privkey=, produced an address other than the
    /// one the key controls (BigInteger.Parse(hex, AllowHexSpecifier) reads them as negative). Control: a key
    /// starting with 3 imported correctly.
    ///
    /// Owner-approved design under test: correct import by default; frozen legacy derivation
    /// (RestoreAccountLegacy); legacy address with on-chain history restored alongside; explicit legacy flag;
    /// canonical 64-digit export.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX11_PrivateKeyImportTests : IDisposable
    {
        // A high-first-digit key (the class the audit showed mis-importing) and a low one (the audit control).
        private const string HighKey = "c1a5e0f1b2c3d4e5f60718293a4b5c6d7e8f90112233445566778899aabbccdd";
        private const string LowKey = "31a5e0f1b2c3d4e5f60718293a4b5c6d7e8f90112233445566778899aabbccdd";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VX11_PrivateKeyImportTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx11_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>The address a standard tool derives: bytes → scalar with no sign semantics.</summary>
        private static string StandardAddress(string hex)
        {
            var pk = PrivateKey.fromString(HexByteUtility.HexToByte(hex), "secp256k1");
            return AccountData.GetHumanAddress("04" + AccountData.ByteToHex(pk.publicKey().toString()));
        }

        private static BigInteger N => KeyParsing.CurveOrder;

        // ── Audit PoC and control ──────────────────────────────────────────────────────────

        [Fact]
        public async Task VX11_AuditPoC_HighDigitKey_ImportsToTheAddressTheKeyControls()
        {
            var account = await AccountData.RestoreAccount(HighKey, skipSave: true);
            Assert.Equal(StandardAddress(HighKey), account.Address);
        }

        [Fact]
        public async Task VX11_Control_LowDigitKey_Unchanged()
        {
            var account = await AccountData.RestoreAccount(LowKey, skipSave: true);
            Assert.Equal(StandardAddress(LowKey), account.Address);
            Assert.Null(KeyParsing.LegacyAddressIfDifferent(LowKey)); // legacy == canonical for 0-7
        }

        // ── Frozen legacy derivation ───────────────────────────────────────────────────────

        [Fact]
        public async Task VX11_LegacyDerivation_IsPinned_ToTheNegativeInterpretation()
        {
            // The pre-fix parse yields d - 2^256, which the curve maths reduces mod n.
            var d = BigInteger.Parse("0" + HighKey, NumberStyles.AllowHexSpecifier);
            var legacyScalar = ((d - BigInteger.Pow(2, 256)) % N + N) % N;
            var expectedLegacy = KeyParsing.DeriveAddress(legacyScalar);

            var legacy = await AccountData.RestoreAccountLegacy(HighKey, skipSave: true);
            Assert.Equal(expectedLegacy, legacy.Address);
            Assert.NotEqual(StandardAddress(HighKey), legacy.Address);
            Assert.Equal(expectedLegacy, KeyParsing.LegacyAddressIfDifferent(HighKey));
        }

        [Fact]
        public async Task VX11_ExplicitLegacyFlag_ForcesTheLegacyAddress()
        {
            var forced = await AccountData.RestoreAccount(HighKey, skipSave: true, legacy: true);
            Assert.Equal(KeyParsing.LegacyAddressIfDifferent(HighKey), forced.Address);
        }

        [Fact]
        public async Task VX11_LegacyAddressWithOnChainHistory_IsRestoredAlongside()
        {
            var legacyAddress = KeyParsing.LegacyAddressIfDifferent(HighKey)!;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = legacyAddress, Balance = 5M, Nonce = 2 });

            var account = await AccountData.RestoreAccount(HighKey, skipSave: true);

            Assert.Equal(StandardAddress(HighKey), account.Address);
            Assert.Equal(legacyAddress, account.AlsoRestoredLegacyAddress);
        }

        [Fact]
        public async Task VX11_LegacyAddressWithoutHistory_IsNotRestored()
        {
            var account = await AccountData.RestoreAccount(HighKey, skipSave: true);
            Assert.Null(account.AlsoRestoredLegacyAddress);
        }

        // ── Existing users are unaffected ──────────────────────────────────────────────────

        [Fact]
        public void VX11_WalletGeneratedKeys_ParseIdentically_UnderBothInterpretations()
        {
            for (int i = 0; i < 64; i++)
            {
                var account = AccountData.CreateNewAccount(skipSave: true);
                var stored = account.PrivateKey; // "0…" prefixed when the top digit is 8-f
                Assert.True(KeyParsing.TryParseExternalPrivateKeyHex(stored, out var canonical, out var err), err);
                Assert.Equal(KeyParsing.ParseLegacy(stored), canonical);
                Assert.Equal(account.Address, KeyParsing.DeriveAddress(canonical));
            }
        }

        [Fact]
        public async Task VX11_WalletShown65CharKey_IsAccepted()
        {
            var shown = "0" + HighKey; // how the wallet stores/shows a high-digit key
            var account = await AccountData.RestoreAccount(shown, skipSave: true);
            Assert.Equal(StandardAddress(HighKey), account.Address);
        }

        // ── Canonical export ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX11_CanonicalExport_OfALegacyAccount_ReimportsToThatAccount()
        {
            var legacy = await AccountData.RestoreAccountLegacy(HighKey, skipSave: true);
            var exported = KeyParsing.CanonicalKeyHexFromStored(legacy.PrivateKey);

            Assert.Equal(64, exported.Length);
            var reimported = await AccountData.RestoreAccount(exported, skipSave: true);
            Assert.Equal(legacy.Address, reimported.Address);
            Assert.Equal(legacy.Address, StandardAddress(exported)); // and any standard tool agrees
        }

        [Fact]
        public void VX11_CanonicalExport_OfANativeKey_IsTheKey()
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            var exported = KeyParsing.CanonicalKeyHexFromStored(account.PrivateKey);
            Assert.Equal(64, exported.Length);
            Assert.Equal(account.Address, StandardAddress(exported));
        }

        // ── Range validation ───────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("0")]
        [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141")] // n
        [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364142")] // n+1
        [InlineData("1FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")] // 65 significant digits
        [InlineData("not-hex")]
        [InlineData("")]
        public void VX11_InvalidKeys_AreRejected(string hex) =>
            Assert.False(KeyParsing.TryParseExternalPrivateKeyHex(hex, out _, out _));

        [Fact]
        public void VX11_ReserveSingleKeyChoice_PrefersLegacyOnlyWhenOnlyItHasHistory()
        {
            var legacyAddress = KeyParsing.LegacyAddressIfDifferent(HighKey)!;
            Assert.True(KeyParsing.TryParseImportedKey(HighKey, false, a => false, out var s1, out var used1, out _));
            Assert.False(used1);
            Assert.Equal(StandardAddress(HighKey), KeyParsing.DeriveAddress(s1));

            Assert.True(KeyParsing.TryParseImportedKey(HighKey, false, a => a == legacyAddress, out var s2, out var used2, out _));
            Assert.True(used2);
            Assert.Equal(legacyAddress, KeyParsing.DeriveAddress(s2));
        }
    }
}
