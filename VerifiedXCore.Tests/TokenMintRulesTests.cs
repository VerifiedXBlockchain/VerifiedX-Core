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

        private static VerifiedXCore.Models.Transaction Mint(string uid, string hash) => new()
        {
            Hash = hash, FromAddress = "xOwner", ToAddress = "Token_Base", TransactionType = VerifiedXCore.Models.TransactionType.FTKN_TX,
            Data = Newtonsoft.Json.JsonConvert.SerializeObject(new { Function = "TokenMint()", ContractUID = uid, FromAddress = "xOwner", Amount = 10M }),
        };

        [Fact]
        public void TwoMintsOfOneContractInABlock_AreRefusedFromTheHeight_AndAcceptedBefore()
        {
            var state = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Mint("tok:1", "m1"), state, Gate));
            Assert.StartsWith(LedgerIntegrityRules.TokenMintedTwiceInBlock, LedgerIntegrityRules.RegisterNftStepInBlock(Mint("tok:1", "m2"), state, Gate));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Mint("tok:2", "m3"), state, Gate)); // another contract

            var before = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Mint("tok:1", "m1"), before, Gate - 1));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Mint("tok:1", "m2"), before, Gate - 1));
        }

        [Fact]
        public void TheProducer_LeavesTheSecondMintForALaterBlock()
        {
            var prior = Globals.LastBlock;
            try
            {
                Globals.LastBlock = new VerifiedXCore.Models.Block { Height = Gate - 1 };
                var kept = VerifiedXCore.Data.TransactionData.DropDuplicateContractCreations(new System.Collections.Generic.List<VerifiedXCore.Models.Transaction> { Mint("tok:1", "m1"), Mint("tok:1", "m2") });
                Assert.Equal(new[] { "m1" }, System.Linq.Enumerable.Select(kept, t => t.Hash));
            }
            finally { Globals.LastBlock = prior; }
        }
    }
}
