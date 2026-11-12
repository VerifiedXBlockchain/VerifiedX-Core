using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A signature's public-key part is read in time that grows with the square of its length. A key part too long to be a
    /// 64-byte public key is refused before it is read, so an outsider cannot tie up a node with one (on the FROST routes
    /// that verify a requester's signature, or in a transaction). Every signature that verified before still does.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SignatureKeyLengthTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();

        public SignatureKeyLengthTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"sigkey_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1000 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private Transaction Send()
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = NewKey().Address, Amount = 1M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.TX,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        private static string WithKeyPart(string signature, string keyPart) => signature.Split('.')[0] + "." + keyPart;

        [Fact]
        public void AKeyPartTooLongToBeAKey_IsRefusedAtOnce()
        {
            var signature = SignatureService.CreateSignature("hello", _owner.Key, _owner.Pub);
            var watch = Stopwatch.StartNew();
            Assert.False(SignatureService.VerifySignature(_owner.Address, "hello", WithKeyPart(signature, new string('z', 250_000))));
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
            Assert.Throws<FormatException>(() => Base58Utility.Base58Decode(new string('z', 89)));
        }

        [Fact]
        public void EverySignatureThatVerifiedBefore_StillDoes()
        {
            var signature = SignatureService.CreateSignature("hello", _owner.Key, _owner.Pub);
            var keyPart = signature.Split('.')[1];
            Assert.True(keyPart.Length <= 88);
            Assert.True(SignatureService.VerifySignature(_owner.Address, "hello", signature));
            // Leading '1's add nothing to the key, so this verified before and must still.
            Assert.True(SignatureService.VerifySignature(_owner.Address, "hello", WithKeyPart(signature, new string('1', 1_000) + keyPart)));
        }

        [Fact]
        public async Task ATransaction_IsJudgedAsBefore()
        {
            var tx = Send();
            var junk = Send();
            junk.Signature = WithKeyPart(junk.Signature, new string('z', 20_000));
            Assert.Equal((false, "Signature Failed to verify."), await TransactionValidatorService.VerifyTX(junk));

            tx.Signature = WithKeyPart(tx.Signature, new string('1', 100) + tx.Signature.Split('.')[1]);
            Assert.True((await TransactionValidatorService.VerifyTX(tx)).Item1);
        }
    }
}
