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
    /// From Globals.FeeBelowOneVfxHeight a fee is below 1 VFX: Amount and Fee are joined in the hash with no separator, and
    /// moving trailing amount digits into the fee's whole part kept the hash and signature while burning the balance.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FeeBelowOneVfxTests : IDisposable
    {
        private const long Rule = 5_000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.FeeBelowOneVfxHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly string _recipient = NewKey().Address;

        public FeeBelowOneVfxTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fee_below_one_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.FeeBelowOneVfxHeight = Rule;
            Globals.LastBlock = new Block { Height = Rule - 1 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 10_000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.FeeBelowOneVfxHeight = _priorRule;
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

        private Transaction Honest(decimal amount)
        {
            var tx = new Transaction { Timestamp = TimeUtil.GetTime(), FromAddress = _sender.Address, ToAddress = _recipient, Amount = amount, Fee = 0, Nonce = 0, TransactionType = TransactionType.TX };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        /// <summary>The same preimage with the amount's last <paramref name="digits"/> characters moved to the fee's front.</summary>
        private static Transaction Resplit(Transaction honest, int digits)
        {
            var amount = honest.Amount.ToString();
            var moved = new Transaction
            {
                Timestamp = honest.Timestamp, FromAddress = honest.FromAddress, ToAddress = honest.ToAddress, Nonce = honest.Nonce,
                TransactionType = honest.TransactionType, Hash = honest.Hash, Signature = honest.Signature,
                Amount = decimal.Parse(amount[..^digits], System.Globalization.CultureInfo.InvariantCulture),
                Fee = decimal.Parse(amount[^digits..] + honest.Fee, System.Globalization.CultureInfo.InvariantCulture),
            };
            Assert.Equal(honest.GetHashPreimage(), moved.GetHashPreimage()); // same hash, same signature
            return moved;
        }

        [Fact]
        public async Task FromTheRule_AmountDigitsMovedIntoTheFee_AreRefused_AndBelowItTheyStillPass()
        {
            var honest = Honest(1.2345M);
            var burn = Resplit(honest, 3); // Amount 1.2, Fee 345.0000…

            var (atRule, why) = await TransactionValidatorService.VerifyTX(burn, false, true, false, null, false, Rule);
            Assert.False(atRule);
            Assert.Contains("below 1 VFX", why);

            var (below, _) = await TransactionValidatorService.VerifyTX(burn, false, true, false, null, false, Rule - 1);
            Assert.True(below); // the hole, kept for replay below the height
        }

        [Fact]
        public async Task FromTheRule_AnHonestTransfer_StillPasses()
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(Honest(1.2345M), false, true, false, null, false, Rule);
            Assert.True(ok, why);
            Assert.Null(LedgerIntegrityRules.FeeBelowOneVfx(new Transaction { TransactionType = TransactionType.TX, Fee = 0.99999999M }, Rule));
            Assert.NotNull(LedgerIntegrityRules.FeeBelowOneVfx(new Transaction { TransactionType = TransactionType.TX, Fee = 1.0M }, Rule));
        }
    }
}
