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
    /// NEW-18 (found by the fifth independent review): Data and UnlockTime are joined in the hash preimage without a
    /// separator, so digits move between them with the hash and signature unchanged (reviewer PoC
    /// R5_ReserveUnlockTime_ShiftsWithoutChangingHash).
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW18_PreimageTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly (PrivateKey Key, string Pub, string Address) _a = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _b = NewKey();

        public NEW18_PreimageTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new18_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            foreach (var k in new[] { _a, _b })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
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

        [Fact]
        public async Task NEW18_PoC_DigitsShiftedFromUnlockTimeIntoData_Refused()
        {
            var tx = new Transaction { Timestamp = TimeUtil.GetTime(), FromAddress = _a.Address, ToAddress = _b.Address, Amount = 1M, Fee = 0.00001M, Nonce = 0,
                TransactionType = TransactionType.TX, Data = null, UnlockTime = TimeUtil.GetReserveTime(1) };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _a.Key, _a.Pub);
            var ut = tx.UnlockTime!.Value.ToString();
            var shifted = new Transaction { Timestamp = tx.Timestamp, FromAddress = tx.FromAddress, ToAddress = tx.ToAddress, Amount = tx.Amount, Fee = tx.Fee, Nonce = tx.Nonce,
                TransactionType = tx.TransactionType, Data = ut.Substring(0, 1), UnlockTime = long.Parse(ut.Substring(1)), Hash = tx.Hash, Signature = tx.Signature };

            Assert.Equal(tx.Hash, shifted.GetHash());                                            // the ambiguity
            Assert.True(SignatureService.VerifySignature(shifted.FromAddress, shifted.GetHash(), shifted.Signature));

            Assert.Null(LedgerIntegrityRules.CanonicalPreimage(tx));                             // honest reading
            var (ok, message) = await TransactionValidatorService.VerifyTX(shifted);
            Assert.False(ok);
            Assert.Contains("ambiguous hash", message);
        }

        // ── NEW-23 (sixth review): Amount | Fee ─────────────────────────────────────────────

        private Transaction SignedWhole(decimal amount)
        {
            var tx = new Transaction { Timestamp = TimeUtil.GetTime(), FromAddress = _a.Address, ToAddress = _b.Address, Amount = amount, Fee = 0, Nonce = 0, TransactionType = TransactionType.TX };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _a.Key, _a.Pub);
            return tx;
        }

        [Fact]
        public async Task NEW23_PoC_FeeDigitsShiftedIntoAWholeNumberAmount_Refused()
        {
            // "10" + "0.0000xxxx" re-splits as Amount "100.0000xxx" + Fee "x": same hash and signature, ten times the amount.
            var tx = SignedWhole(10M);
            var af = tx.Amount.ToString() + tx.Fee.ToString();
            Transaction? variant = null;
            for (int k = tx.Amount.ToString().Length + 1; k < af.Length && variant == null; k++)
            {
                var a = af.Substring(0, k); var f = af.Substring(k);
                if (!decimal.TryParse(a, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ad)) continue;
                if (!decimal.TryParse(f, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var fd)) continue;
                if (ad.ToString() != a || fd.ToString() != f || fd <= 0M) continue;
                var v = new Transaction { Timestamp = tx.Timestamp, FromAddress = tx.FromAddress, ToAddress = tx.ToAddress, Amount = ad, Fee = fd, Nonce = tx.Nonce,
                    TransactionType = tx.TransactionType, Data = tx.Data, Hash = tx.Hash, Signature = tx.Signature };
                if (v.GetHash() == tx.Hash) variant = v;
            }
            Assert.NotNull(variant);
            Assert.True(variant!.Amount > 10M);                                                      // the ambiguity
            var (ok, message) = await TransactionValidatorService.VerifyTX(variant);
            Assert.False(ok);
            Assert.Contains("decimal places", message);
        }

        [Fact]
        public async Task NEW23_Control_WholeNumberAmountAsSigned_Accepted()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(SignedWhole(10M));
            Assert.True(ok, message);
        }
    }
}
