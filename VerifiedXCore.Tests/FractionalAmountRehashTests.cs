using System;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.FractionalAmountRehashHeight a non-whole carried Amount must be the amount that was hashed. VerifyTX's
    /// fallback re-hashed it as 0, so a transaction signed over an Amount of "0" could carry any fraction.
    /// </summary>
    public class FractionalAmountRehashTests : IDisposable
    {
        private const long Rule = 5_000;
        private readonly long _priorRule = Globals.FractionalAmountRehashHeight;

        public FractionalAmountRehashTests() => Globals.FractionalAmountRehashHeight = Rule;
        public void Dispose() => Globals.FractionalAmountRehashHeight = _priorRule;

        private static Transaction Built(decimal amount)
        {
            var tx = new Transaction { Timestamp = 1_790_000_000, FromAddress = "xSender", ToAddress = "xRecipient", Amount = amount, Fee = 0.00001M, Nonce = 3, TransactionType = TransactionType.TX };
            tx.Build();
            return tx;
        }

        [Fact]
        public void FromTheRule_AFractionCarriedOnAHashOverZero_DoesNotMatch_AndBelowItStillDoes()
        {
            var raised = Built(0M); // hashed (and so signed) over "0"
            raised.Amount = 5.5M;

            Assert.False(TransactionValidatorService.StandardHashMatches(raised, Rule));
            Assert.True(TransactionValidatorService.StandardHashMatches(raised, Rule - 1)); // kept for replay below the height
        }

        [Fact]
        public void FromTheRule_TheSpellingsVerifyTxAlwaysAccepted_StillMatch()
        {
            Assert.True(TransactionValidatorService.StandardHashMatches(Built(5.5M), Rule)); // a fraction, as hashed

            var whole = Built(5.0M); // hashed as "5.0"
            whole.Amount = 5M;       // carried as 5 once it has crossed the wire
            Assert.True(TransactionValidatorService.StandardHashMatches(whole, Rule));

            Assert.True(TransactionValidatorService.StandardHashMatches(Built(0M), Rule));
        }
    }
}
