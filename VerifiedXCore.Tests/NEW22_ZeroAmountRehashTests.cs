using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
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
    /// NEW-22 (found by the sixth independent review; HIGH, pre-existing): VerifyTX's hash fallback re-hashed with Amount 0
    /// when the carried Amount did not match, so a transaction SIGNED with Amount "0" verified with any Amount (reviewer
    /// PoC R6_ZeroAmountSignedTx_AnyAmountAccepted: 0 -> 999, attacker +999).
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW22_ZeroAmountRehashTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly (PrivateKey Key, string Pub, string Address) _victim = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _attacker = NewKey();

        public NEW22_ZeroAmountRehashTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new22_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            foreach (var k in new[] { _victim, _attacker })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 2000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private Transaction SignedZero()
        {
            var tx = new Transaction { Timestamp = TimeUtil.GetTime(), FromAddress = _victim.Address, ToAddress = _attacker.Address, Amount = 0M, Fee = 0.00001M, Nonce = 0, TransactionType = TransactionType.TX };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _victim.Key, _victim.Pub);
            return tx;
        }

        [Fact]
        public async Task NEW22_PoC_ZeroAmountSignedTransactionWithAnotherAmount_Refused()
        {
            var mutated = SignedZero();
            mutated.Amount = 999M;
            Assert.False((await TransactionValidatorService.VerifyTX(mutated, false, true, false, null, false, 1002)).Item1);
            Assert.False((await TransactionValidatorService.VerifyTX(mutated)).Item1);
        }

        [Fact]
        public async Task NEW22_Control_ZeroAmountSignedTransactionAsSigned_Accepted()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(SignedZero());
            Assert.True(ok, message);
        }
    }
}
