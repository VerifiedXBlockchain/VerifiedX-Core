using LiteDB;
using VerifiedXCore.Privacy;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A transparent account key has one shielded identity whatever the spelling its hex is stored in: HD accounts store
    /// a sign byte ("00" + 64 hex) and a leading zero can be dropped. The wallet also keeps a row for the identity earlier
    /// builds derived from a non-32-byte spelling, so notes paid to it stay visible and spendable.
    /// </summary>
    [Collection("PrivacySequential")]
    public class ShieldedAccountKeyIdentityTests : IDisposable
    {
        private const string Key64 = "0c28fca386c7a227600b2fe50b7cae11ec86d3bf1fbe471be89827e19d72aa1d";
        private const string LeadingZeroKey64 = "000b2fe50b7cae11ec86d3bf1fbe471be89827e19d72aa1d0c28fca386c7a227";

        private readonly string _dbPath;
        private readonly LiteDatabase _db;

        public ShieldedAccountKeyIdentityTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "vfx_shielded_identity_" + Guid.NewGuid() + ".db");
            _db = new LiteDatabase(new ConnectionString { Filename = _dbPath, Connection = ConnectionType.Direct });
            PrivacyDbContext.EnsurePrivacyIndexes(_db);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        [Fact]
        public void HdSpellingOfAKey_DerivesTheSameIdentityAsThePlainKey()
        {
            var plain = ShieldedHdDerivation.DeriveFromPrivateKey(Key64);
            var hd = ShieldedHdDerivation.DeriveFromPrivateKey("00" + Key64);

            Assert.Equal(plain.ZfxAddress, hd.ZfxAddress);
            Assert.Equal(plain.SpendingKey32, hd.SpendingKey32);
            Assert.Equal(plain.ViewingKey32, hd.ViewingKey32);
        }

        [Fact]
        public void DroppedLeadingZeros_DeriveTheSameIdentityAsThe32ByteKey()
        {
            var full = ShieldedHdDerivation.DeriveFromPrivateKey(LeadingZeroKey64).ZfxAddress;

            Assert.Equal(full, ShieldedHdDerivation.DeriveFromPrivateKey(LeadingZeroKey64.Substring(2)).ZfxAddress);  // 31 bytes
            Assert.Equal(full, ShieldedHdDerivation.DeriveFromPrivateKey(LeadingZeroKey64.Substring(3)).ZfxAddress);  // odd length
            Assert.Equal(full, ShieldedHdDerivation.DeriveFromPrivateKey("00" + LeadingZeroKey64).ZfxAddress);       // sign byte
        }

        [Fact]
        public void A32ByteKey_KeepsTheIdentityEarlierBuildsDerived()
        {
            Assert.False(ShieldedHdDerivation.HasLegacyIdentity(Key64));
            Assert.Equal(ShieldedHdDerivation.DeriveLegacyFromPrivateKey(Key64).ZfxAddress, ShieldedHdDerivation.DeriveFromPrivateKey(Key64).ZfxAddress);
        }

        [Fact]
        public void AnHdSpelling_HasADistinctLegacyIdentity()
        {
            Assert.True(ShieldedHdDerivation.HasLegacyIdentity("00" + Key64));
            Assert.NotEqual(ShieldedHdDerivation.DeriveLegacyFromPrivateKey("00" + Key64).ZfxAddress, ShieldedHdDerivation.DeriveFromPrivateKey("00" + Key64).ZfxAddress);
        }

        [Fact]
        public void NormalizeAccountKey_PadsAndStripsToThirtyTwoBytes()
        {
            var key = Convert.FromHexString(Key64);
            Assert.Equal(key, ShieldedHdDerivation.NormalizeAccountKey(key));
            Assert.Equal(key, ShieldedHdDerivation.NormalizeAccountKey(Convert.FromHexString("0000" + Key64)));
            Assert.Equal(Convert.FromHexString(LeadingZeroKey64), ShieldedHdDerivation.NormalizeAccountKey(Convert.FromHexString(LeadingZeroKey64.Substring(2))));
            // Not a scalar: left for the rehash fallback.
            var wide = Convert.FromHexString("01" + Key64);
            Assert.Equal(wide, ShieldedHdDerivation.NormalizeAccountKey(wide));
        }

        [Fact]
        public void UpsertFromAccountKey_HdSpelling_AlsoKeepsASpendableRowForTheLegacyIdentity()
        {
            var material = ShieldedWalletService.UpsertFromAccountKey("00" + Key64, "xSource", "test-password", _db);

            Assert.Equal(ShieldedHdDerivation.DeriveFromPrivateKey(Key64).ZfxAddress, material.ZfxAddress);
            var rows = ShieldedWalletService.GetAll(_db);
            Assert.Equal(2, rows.Count);
            var legacy = ShieldedWalletService.FindByZfxAddress(ShieldedHdDerivation.DeriveLegacyFromPrivateKey("00" + Key64).ZfxAddress, _db);
            Assert.NotNull(legacy);
            Assert.Equal("xSource", legacy!.TransparentSourceAddress);
            Assert.False(legacy.IsViewOnly);
            Assert.True(PrivacyApiHelper.TryGetKeyMaterial(legacy, "test-password", out var keys, out var error), error);
            Assert.Equal(ShieldedHdDerivation.DeriveLegacyFromPrivateKey("00" + Key64).SpendingKey32, keys!.SpendingKey32);
            Assert.NotNull(ShieldedWalletService.FindByZfxAddress(material.ZfxAddress, _db));
        }

        [Fact]
        public void UpsertFromAccountKey_PlainKey_CreatesOneRow()
        {
            ShieldedWalletService.UpsertFromAccountKey(Key64, "xSource", "test-password", _db);
            ShieldedWalletService.UpsertFromAccountKey(Key64, "xSource", "test-password", _db);

            Assert.Single(ShieldedWalletService.GetAll(_db));
        }
    }
}
