using VerifiedXCore;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From TokenMintRulesHeight a TokenMint() is refused on a paused contract, with more decimals than the token's
    /// DecimalPlaces, or when it would overflow the supply or the holder's balance; below it the mint is judged as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TokenMintRulesTests : System.IDisposable
    {
        private const long Gate = 6_000;
        private readonly long _prior = Globals.TokenMintRulesHeight;

        public TokenMintRulesTests() => Globals.TokenMintRulesHeight = Gate;

        public void Dispose() => Globals.TokenMintRulesHeight = _prior;

        private static TokenDetails Token(int places = 2, bool paused = false, decimal supply = 100M) =>
            new() { DecimalPlaces = places, IsPaused = paused, CurrentSupply = supply, TokenMintable = true };

        [Fact]
        public void AMintWithinTheTokensPrecision_Passes()
        {
            Assert.Null(LedgerIntegrityRules.TokenMintRefusal(Token(), 10.25M, 5M, Gate));
            Assert.Null(LedgerIntegrityRules.TokenMintRefusal(Token(0), 10M, 5M, Gate));
        }

        [Fact]
        public void MoreDecimalsThanTheToken_IsRefusedFromTheHeight()
        {
            Assert.NotNull(LedgerIntegrityRules.TokenMintRefusal(Token(2), 10.255M, 5M, Gate));
            Assert.NotNull(LedgerIntegrityRules.TokenMintRefusal(Token(0), 1.5M, 5M, Gate));
            Assert.Null(LedgerIntegrityRules.TokenMintRefusal(Token(2), 10.255M, 5M, Gate - 1));
        }

        [Fact]
        public void APausedContract_IsRefusedFromTheHeight()
        {
            Assert.NotNull(LedgerIntegrityRules.TokenMintRefusal(Token(paused: true), 10M, 5M, Gate));
            Assert.Null(LedgerIntegrityRules.TokenMintRefusal(Token(paused: true), 10M, 5M, Gate - 1));
        }

        [Fact]
        public void AMintPastTheLargestAmount_IsRefused()
        {
            Assert.NotNull(LedgerIntegrityRules.TokenMintRefusal(Token(0, supply: decimal.MaxValue - 1M), 2M, 0M, Gate));
            Assert.NotNull(LedgerIntegrityRules.TokenMintRefusal(Token(0, supply: 0M), 2M, decimal.MaxValue - 1M, Gate));
        }
    }
}
