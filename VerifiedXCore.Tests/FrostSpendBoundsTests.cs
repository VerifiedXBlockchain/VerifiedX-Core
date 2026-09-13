using VerifiedXCore.Bitcoin.FROST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: validators checked each output of a withdrawal transaction but never
    /// the miner fee (inputs minus outputs). A leader holding a tiny legitimate withdrawal could
    /// build a transaction spending the entire vault with a one-satoshi payout and no change, and
    /// honest validators would sign the whole vault away to miners. The vault's total cost
    /// (destination + fee) must fit inside the authorized amount.
    /// </summary>
    public class FrostSpendBoundsTests
    {
        private const long Btc = 100_000_000L;

        [Fact]
        public void NormalWithdrawal_FeeDeductedFromAmount_Accepted()
        {
            // 0.01 BTC authorized; pays 0.0099 to destination, 0.0001 fee, rest back to vault.
            var (ok, _) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 5 * Btc, destinationSats: 990_000, changeSats: 5 * Btc - 1_000_000, maxSats: 1_000_000);
            Assert.True(ok);
        }

        [Fact]
        public void ExactAuthorizedAmount_NoFee_Accepted()
        {
            var (ok, _) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 1_000_000, destinationSats: 1_000_000, changeSats: 0, maxSats: 1_000_000);
            Assert.True(ok);
        }

        [Fact]
        public void WholeVaultAsFee_WithTinyPayout_Refused()
        {
            // The attack: inputs = whole vault (5 BTC), 1-sat payout, no change -> ~5 BTC fee.
            var (ok, reason) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 5 * Btc, destinationSats: 1, changeSats: 0, maxSats: 1_000_000);
            Assert.False(ok);
            Assert.Contains("fee", reason);
        }

        [Fact]
        public void FeeSlightlyOverAuthorization_Refused()
        {
            // destination 0.009 + fee 0.0011 = 0.0101 > 0.01 authorized
            var (ok, _) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 2 * Btc, destinationSats: 900_000, changeSats: 2 * Btc - 1_010_000, maxSats: 1_000_000);
            Assert.False(ok);
        }

        [Fact]
        public void DestinationOverAuthorization_Refused()
        {
            var (ok, reason) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 2 * Btc, destinationSats: 1_000_001, changeSats: 2 * Btc - 1_000_001, maxSats: 1_000_000);
            Assert.False(ok);
            Assert.Contains("more than the authorized amount", reason);
        }

        [Fact]
        public void OutputsExceedInputs_Refused()
        {
            var (ok, reason) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 1_000, destinationSats: 900, changeSats: 200, maxSats: 1_000_000);
            Assert.False(ok);
            Assert.Contains("exceed inputs", reason);
        }

        [Fact]
        public void ZeroDestination_Refused()
        {
            var (ok, _) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 1_000_000, destinationSats: 0, changeSats: 999_000, maxSats: 1_000_000);
            Assert.False(ok);
        }

        [Fact]
        public void NoAuthorizedAmount_Refused()
        {
            var (ok, _) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 1_000_000, destinationSats: 1, changeSats: 0, maxSats: 0);
            Assert.False(ok);
        }

        [Fact]
        public void ChangeCannotHideFee_VaultCostIsInputsMinusChange()
        {
            // Change is fine as long as (inputs - change) fits the authorization.
            var (ok, _) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 10 * Btc, destinationSats: 500_000, changeSats: 10 * Btc - 1_000_000, maxSats: 1_000_000);
            Assert.True(ok); // vault cost = 1_000_000 exactly

            var (ok2, _) = FrostSigningAuthorization.CheckSpendBounds(
                inputSats: 10 * Btc, destinationSats: 500_000, changeSats: 10 * Btc - 1_000_001, maxSats: 1_000_000);
            Assert.False(ok2); // one sat over
        }
    }
}
