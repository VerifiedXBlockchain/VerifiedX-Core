using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// BB-2 (serves VX-12, VX-13, VX-14): key material on the account entities is never emitted by either JSON
    /// serializer, while LiteDB still stores and reads the stored key fields.
    /// </summary>
    [Collection("DbContextSequential")]
    public class KeyMaterialSerializationTests : IDisposable
    {
        private static readonly string[] Forbidden = { "PrivateKey", "GetKey", "GetPrivKey", "WifKey", "EncryptedDecryptKey" };
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public KeyMaterialSerializationTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bb2_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private static void AssertNoKeyFields(string json)
        {
            foreach (var f in Forbidden)
                Assert.DoesNotContain($"\"{f}\"", json);
        }

        [Fact]
        public void Account_NeverSerializesKeys()
        {
            var a = AccountData.CreateNewAccount(skipSave: true);
            AssertNoKeyFields(JsonConvert.SerializeObject(a));
            AssertNoKeyFields(JsonConvert.SerializeObject(new[] { a }));
            AssertNoKeyFields(System.Text.Json.JsonSerializer.Serialize(a));
            Assert.DoesNotContain(a.PrivateKey, JsonConvert.SerializeObject(a));
        }

        [Fact]
        public void ReserveAccount_NeverSerializesKeys()
        {
            var r = new ReserveAccount { Address = "xRBXtest", PrivateKey = "cipher", EncryptedDecryptKey = "wrapped", PublicKey = "04ab" };
            AssertNoKeyFields(JsonConvert.SerializeObject(r));
            AssertNoKeyFields(System.Text.Json.JsonSerializer.Serialize(r));
        }

        [Fact]
        public void BitcoinAccount_NeverSerializesKeys()
        {
            var b = new BitcoinAccount { Address = "tb1q", PrivateKey = "108e56ad", WifKey = "cN8tFF2E", PublicKey = "02ab" };
            AssertNoKeyFields(JsonConvert.SerializeObject(b));
            AssertNoKeyFields(System.Text.Json.JsonSerializer.Serialize(b));
        }

        [Fact]
        public void LiteDb_StillStoresTheKeyFields()
        {
            var a = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(a);
            Assert.Equal(a.PrivateKey, AccountData.GetAccounts().FindOne(x => x.Address == a.Address).PrivateKey);

            var b = new BitcoinAccount { Address = "tb1qstore", PrivateKey = "108e56ad", WifKey = "cN8tFF2E", PublicKey = "02ab" };
            var btc = BitcoinAccount.GetBitcoin()!;
            btc.Insert(b);
            var back = btc.FindOne(x => x.Address == "tb1qstore");
            Assert.Equal("108e56ad", back.PrivateKey);
            Assert.Equal("cN8tFF2E", back.WifKey);
        }
    }
}
