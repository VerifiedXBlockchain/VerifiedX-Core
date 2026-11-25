using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A key backup is encrypted with a fresh random nonce every time and bound to its contract's UID, so two backups
    /// under one UID never share a nonce: with the nonce derived from the key and the UID alone, two encryptions under
    /// one UID gave away each other's plaintext and the key that authenticates them. Blobs of the earlier form are still
    /// read, and a recovered backup whose group key is not the chain's for its contract is not restored.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostBackupEncryptionTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _address;

        public FrostBackupEncryptionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"backup_encryption_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _address = AccountData.GetHumanAddress(pub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = _address, PublicKey = pub, PrivateKey = "0" + key.secret.ToString("x") });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static FrostValidatorKeyStore Key(string group) => new()
        {
            KeyPackage = "{\"signing_share\":\"" + new string('1', 64) + "\"}", PubkeyPackage = "{\"verifying_key\":\"" + group + "\"}", GroupPublicKey = group, ParticipantOrderJson = "[\"a\",\"b\"]",
        };

        private static byte[] Raw(string blob) => Convert.FromBase64String(blob.Substring(FrostKeyBackupService.RandomNonceBlobPrefix.Length));

        [Fact]
        public void Two_backups_under_one_contract_never_share_a_nonce()
        {
            var (first, _) = FrostKeyBackupService.EncryptKeyPackage(_address, "sc-1", Key("02" + new string('a', 64)));
            var (second, _) = FrostKeyBackupService.EncryptKeyPackage(_address, "sc-1", Key("03" + new string('b', 64)));
            Assert.StartsWith(FrostKeyBackupService.RandomNonceBlobPrefix, first);
            Assert.StartsWith(FrostKeyBackupService.RandomNonceBlobPrefix, second);
            Assert.NotEqual(Raw(first!).Take(12), Raw(second!).Take(12));

            // Even the same key backed up twice is written under two nonces.
            var (again, _) = FrostKeyBackupService.EncryptKeyPackage(_address, "sc-1", Key("02" + new string('a', 64)));
            Assert.NotEqual(Raw(first!).Take(12), Raw(again!).Take(12));

            Assert.Equal("02" + new string('a', 64), FrostKeyBackupService.DecryptKeyPackage(_address, "sc-1", first!)!.GroupPublicKey);
            Assert.Equal("03" + new string('b', 64), FrostKeyBackupService.DecryptKeyPackage(_address, "sc-1", second!)!.GroupPublicKey);
        }

        [Fact]
        public void A_blob_decrypts_under_its_own_contract_only()
        {
            var (blob, _) = FrostKeyBackupService.EncryptKeyPackage(_address, "sc-1", Key("02" + new string('a', 64)));
            Assert.Null(FrostKeyBackupService.DecryptKeyPackage(_address, "sc-2", blob!));
        }

        [Fact]
        public void A_blob_of_the_earlier_form_is_still_read()
        {
            // Written as the earlier release wrote it: nonce from HMAC(key, UID), tag then ciphertext, no marker.
            // The node's own derivations, as the earlier release used them to write.
            var service = typeof(FrostKeyBackupService);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var encryptionKey = (byte[])service.GetMethod("DeriveEncryptionKey", flags)!.Invoke(null, new object[] { _address })!;
            var nonce = (byte[])service.GetMethod("DeriveNonce", flags)!.Invoke(null, new object[] { encryptionKey, "sc-old" })!;
            var store = Key("02" + new string('c', 64));
            var plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { store.KeyPackage, store.PubkeyPackage, store.GroupPublicKey, store.ParticipantOrderJson }));
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            VerifiedXCore.Privacy.CrossPlatformAesGcm.Encrypt(encryptionKey, nonce, plaintext, ciphertext, tag, null);
            var legacy = Convert.ToBase64String(tag.Concat(ciphertext).ToArray());

            Assert.Equal(store.GroupPublicKey, FrostKeyBackupService.DecryptKeyPackage(_address, "sc-old", legacy)!.GroupPublicKey);
        }

        [Fact]
        public void A_backup_for_an_id_with_no_key_on_chain_is_not_judged_by_one()
        {
            Assert.Null(FrostKeyBackupService.GroupKeyRefusal("made-up-session", "02" + new string('a', 64)));
        }
    }
}
