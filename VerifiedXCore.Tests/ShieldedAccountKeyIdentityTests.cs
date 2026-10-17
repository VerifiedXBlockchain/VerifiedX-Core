using LiteDB;
using VerifiedXCore.Models.Privacy;
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
            Assert.Equal(ShieldedHdDerivation.DeriveFromPrivateKey(HighBitKey64).ZfxAddress, ShieldedHdDerivation.DeriveFromPrivateKey("00" + HighBitKey64).ZfxAddress);
        }

        [Fact]
        public void DroppedLeadingZeros_DeriveTheSameIdentityAsThe32ByteKey()
        {
            var full = ShieldedHdDerivation.DeriveFromPrivateKey(LeadingZeroKey64).ZfxAddress;

            Assert.Equal(full, ShieldedHdDerivation.DeriveFromPrivateKey(LeadingZeroKey64.Substring(2)).ZfxAddress);  // 31 bytes
            Assert.Equal(full, ShieldedHdDerivation.DeriveFromPrivateKey(LeadingZeroKey64.Substring(3)).ZfxAddress);  // odd length
            Assert.Equal(full, ShieldedHdDerivation.DeriveFromPrivateKey("00" + LeadingZeroKey64).ZfxAddress);       // sign byte
        }

        // Identities the build before this rule derived from each stored spelling (captured from that build).
        private const string OldZfxKey64 = "zfx_QjyvXzTvFRfWUZVyzGJwCcyjGQBf55UfBpb2wn3osURUpTzVHZqSo7k61zZsuSJejTkQQ5KzV2AHAc8tyWu7uhEtJZ9apt1Q2";
        private const string OldZfxHdKey64 = "zfx_QjyvB6ShNFb2J4gmq9EvJvaSYLNKwTcH6ZpKk2A2zwsuZgk8mqQcwQj5pqRxQf4MyL2M1QjQDVhGWoYE1etMHPuoCEZnknrRX";
        private const string HighBitKey64 = "8c28fca386c7a227600b2fe50b7cae11ec86d3bf1fbe471be89827e19d72aa1d";
        private const string OldZfxHighBitKey64 = "zfx_Qjyvmiqtb6ingLaaRrokVzvFASCCh35itWaFmk2fAgpFz9fEHthXdcKTVqGeNAihdHyj72uvcRM6XjLREnePKSmtventGEFfA";
        private const string OldZfxSignByteHighBitKey = "zfx_QjyvCb9tnkN4hGCEmvXLoCuZfPux6NjucTy7Ex7F6awV8pc9FhVQ7rdovZKJmWBn9TAbWHB7cCscR6amLY8pFSbrjncRdk1CR";
        private const string OldZfxLeadingZeroKey64 = "zfx_QjyvUiaWFQSXkCTN5ou4Dr764wkn5TU8PspJPxrUQweisP4TaCC1KhKpyiDLgHSYV3Hw3mRYvyc7SZHm9Em3nb48umLGHWL4S";
        private const string OldZfxLeadingZeroDropped = "zfx_Qjyvk1ksRMfV9YzBB5arMYDSn5ancEPmS86c9diPny7atP4Zy4Ce8UE14FRQRtrANGFjc1vt5mvAkbt7HPhCEKyqjcDjEiCAP";

        // Real BIP32 children of seed 00..3f at m/44'/889'/0'/0'/i', and the identity the build before this rule derived
        // from what RestoreHDAccount stores for them ("00" + the child key hex).
        private const string Bip32Seed = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f";
        public static readonly TheoryData<string, string, string> HdChildren = new()
        {
            // The child's serialisation keeps a sign byte: "00" + 31 bytes with the top bit set.
            { "m/44'/889'/0'/0'/537'", "00eabca703fa1db0c164e5a7a527230d9fa5b1d13a51cb53cc809611f9b22fdf", "zfx_QjyvLrQNVuqw8c3kXUm1D4SdiXG6dDa3qM1FkR855BY9YBbRWf94C7SjnBBAFgZ4iWBsujy3j3imfX7GxJyD7rr55Q93vaLy3" },
            { "m/44'/889'/0'/0'/1359'", "00a4c2d9557e11a214de90e409f19a82f458f9a5d0daa4bdd8f1e4655658192b", "zfx_Qjyvj4cz1cWh7PsSE5h3TDdXS7TXJqBf7jhEsmnv3FY8kv92TiziaGdi5NR1PmxKu3BkAp1YfKRCN9k1cPKx2pvngWqMUMVWG" },
            { "m/44'/889'/0'/0'/1'", "0cd821464d177f941851e545945982c14a3b0c712fe93355f0816517c0a6d6c0", "zfx_QjyvuijWqXGLiD4AE3VJspAWqjjxX5LHpeU9kLATqYZyiBmWubip2Mvvy7kKtAzn1zRTfEfh6nFGtpB6JrXcV3Z2fo4bhmrZm" },
        };

        [Theory]
        [MemberData(nameof(HdChildren))]
        public void HdAccountKey_RestoredAsAPlainAccount_StillFindsTheHdIdentity(string path, string childHex, string oldHdZfx)
        {
            var child = new VerifiedXCore.BIP32.BIP32().DerivePath(path, Bip32Seed).Key;
            Assert.Equal(childHex, Convert.ToHexString(child).ToLowerInvariant());

            // What a restore of the exported key stores: the scalar's BigInteger hex.
            var restored = System.Numerics.BigInteger.Parse("00" + childHex, System.Globalization.NumberStyles.AllowHexSpecifier).ToString("x");
            Assert.Contains(oldHdZfx, Legacy(restored));
            Assert.Contains(oldHdZfx, Legacy(childHex));
            Assert.Contains(oldHdZfx, Legacy("00" + childHex));

            ShieldedWalletService.UpsertFromAccountKey(restored, "xSource", "test-password", _db);
            var row = ShieldedWalletService.FindByZfxAddress(oldHdZfx, _db);
            Assert.NotNull(row);
            Assert.True(PrivacyApiHelper.TryGetKeyMaterial(row!, "test-password", out _, out var error), error);
        }

        private static List<string> Legacy(string keyHex) =>
            ShieldedHdDerivation.DeriveLegacyIdentities(keyHex).Select(m => m.ZfxAddress).OrderBy(z => z, StringComparer.Ordinal).ToList();

        [Fact]
        public void A32ByteKey_KeepsTheIdentityEarlierBuildsDerived()
        {
            Assert.Equal(OldZfxKey64, ShieldedHdDerivation.DeriveFromPrivateKey(Key64).ZfxAddress);
            Assert.Equal(OldZfxHighBitKey64, ShieldedHdDerivation.DeriveFromPrivateKey(HighBitKey64).ZfxAddress);
            Assert.Equal(OldZfxLeadingZeroKey64, ShieldedHdDerivation.DeriveFromPrivateKey(LeadingZeroKey64).ZfxAddress);
        }

        [Fact]
        public void LegacyIdentities_AreTheOnesEarlierBuildsDerivedFromEachStoredSpelling()
        {
            // Top bit clear: an account stores 64 hex, an HD account "00" + 64 hex.
            Assert.Equal(new[] { OldZfxHdKey64 }, Legacy(Key64));
            Assert.Equal(new[] { OldZfxHdKey64 }, Legacy("00" + Key64));
            // Top bit set: both store "00" + 64 hex.
            Assert.Equal(new[] { OldZfxSignByteHighBitKey }, Legacy(HighBitKey64));
            Assert.Equal(new[] { OldZfxSignByteHighBitKey }, Legacy("00" + HighBitKey64));
            // A leading zero byte: an account stores 62 hex, an HD account "00" + 62 hex (the 32-byte key itself).
            Assert.Equal(new[] { OldZfxLeadingZeroDropped }, Legacy(LeadingZeroKey64.Substring(2)));
            Assert.Equal(new[] { OldZfxLeadingZeroDropped }, Legacy(LeadingZeroKey64));
        }

        [Fact]
        public void LegacyIdentities_NeverIncludeTheCurrentOne()
        {
            foreach (var key in new[] { Key64, "00" + Key64, HighBitKey64, LeadingZeroKey64, LeadingZeroKey64.Substring(3) })
                Assert.DoesNotContain(ShieldedHdDerivation.DeriveFromPrivateKey(key).ZfxAddress, Legacy(key));
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

            Assert.Equal(OldZfxKey64, material.ZfxAddress);
            Assert.Equal(2, ShieldedWalletService.GetAll(_db).Count);
            var legacy = ShieldedWalletService.FindByZfxAddress(OldZfxHdKey64, _db);
            Assert.NotNull(legacy);
            Assert.Equal("xSource", legacy!.TransparentSourceAddress);
            Assert.False(legacy.IsViewOnly);
            Assert.True(PrivacyApiHelper.TryGetKeyMaterial(legacy, "test-password", out var keys, out var error), error);
            Assert.Equal(ShieldedHdDerivation.DeriveLegacyIdentities("00" + Key64).Single().SpendingKey32, keys!.SpendingKey32);
            Assert.NotNull(ShieldedWalletService.FindByZfxAddress(material.ZfxAddress, _db));
        }

        [Fact]
        public void UpsertFromAccountKey_HdKeyImportedAsAPlainAccount_StillKeepsTheHdIdentity()
        {
            // Exported from an HD account ("00" + 64 hex) and restored elsewhere, where it is stored as 64 hex.
            ShieldedWalletService.UpsertFromAccountKey(Key64, "xSource", "test-password", _db);

            Assert.NotNull(ShieldedWalletService.FindByZfxAddress(OldZfxHdKey64, _db));
            Assert.NotNull(ShieldedWalletService.FindByZfxAddress(OldZfxKey64, _db));
        }

        [Fact]
        public void UpsertFromAccountKey_IsIdempotent()
        {
            ShieldedWalletService.UpsertFromAccountKey(Key64, "xSource", "test-password", _db);
            ShieldedWalletService.UpsertFromAccountKey("00" + Key64, "xSource", "test-password", _db);

            Assert.Equal(2, ShieldedWalletService.GetAll(_db).Count);
        }

        [Fact]
        public void UpsertFromAccountKey_KeepsTheNotesAndScanStateOfAnExistingLegacyRow()
        {
            var existing = ShieldedWalletService.CreateFromKeyMaterial(ShieldedHdDerivation.DeriveLegacyIdentities(Key64).Single(), "xSource", "test-password");
            existing.LastScannedBlock = 7_300_000;
            existing.UnspentCommitments = new List<UnspentCommitment> { new UnspentCommitment { Commitment = "c1", AssetType = "VFX", Amount = 4.1M, TreePosition = 3 } };
            existing.ShieldedBalances = new Dictionary<string, decimal> { ["VFX"] = 4.1M };
            ShieldedWalletService.Upsert(existing, _db);

            ShieldedWalletService.UpsertFromAccountKey(Key64, "xSource", "test-password", _db);

            var row = ShieldedWalletService.FindByZfxAddress(OldZfxHdKey64, _db)!;
            Assert.Equal(7_300_000, row.LastScannedBlock);
            Assert.Equal(4.1M, Assert.Single(row.UnspentCommitments!).Amount);
            Assert.Equal(4.1M, row.ShieldedBalances!["VFX"]);
        }
    }
}
