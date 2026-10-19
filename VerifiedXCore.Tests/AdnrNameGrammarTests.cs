using System.Collections.Generic;
using System.Linq;
using LiteDB;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From AdnrNameGrammarHeight an AdnrCreate() name is lowercase letters, digits and '-', 1 to ADNRLimit characters,
    /// with no suffix; below it any free name passes as before. A producer orders name and shop registrations by when it
    /// first saw them, not by the sender's Timestamp.
    /// </summary>
    [Collection("DbContextSequential")]
    public class AdnrNameGrammarTests : System.IDisposable
    {
        private const long Gate = 4_000;
        private readonly long _prior = Globals.AdnrNameGrammarHeight;

        public AdnrNameGrammarTests() => Globals.AdnrNameGrammarHeight = Gate;

        public void Dispose() => Globals.AdnrNameGrammarHeight = _prior;

        [Theory]
        [InlineData("alice")]
        [InlineData("a")]
        [InlineData("my-shop-2")]
        public void AWalletShapedName_Passes(string name) => Assert.Null(LedgerIntegrityRules.AdnrNameError(name, Gate));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Alice")]
        [InlineData("alice.vfx")]
        [InlineData("<b>x</b>")]
        [InlineData("al ice")]
        public void AnythingElse_IsRefusedFromTheHeight(string? name)
        {
            Assert.NotNull(LedgerIntegrityRules.AdnrNameError(name, Gate));
            Assert.Null(LedgerIntegrityRules.AdnrNameError(name, Gate - 1));
        }

        [Fact]
        public void TheLengthLimit_IsTheWalletsLimit()
        {
            Assert.Null(LedgerIntegrityRules.AdnrNameError(new string('a', Globals.ADNRLimit), Gate));
            Assert.NotNull(LedgerIntegrityRules.AdnrNameError(new string('a', Globals.ADNRLimit + 1), Gate));
        }

        [Fact]
        public void Registrations_AreOrderedByArrival_OthersKeepTheirPlace()
        {
            var first = new ObjectId(1, 1, 1, 1);
            var second = new ObjectId(2, 1, 1, 2);
            Transaction Reg(string hash, ObjectId id, long ts) => new() { Hash = hash, Id = id, Timestamp = ts, TransactionType = TransactionType.ADNR };
            var other = new Transaction { Hash = "pay", Id = new ObjectId(3, 1, 1, 3), Timestamp = 5, TransactionType = TransactionType.TX };
            // The later registration is backdated, so the Timestamp sort put it first.
            var sorted = new List<Transaction> { Reg("backdated", second, 10), other, Reg("earlier", first, 20) };

            var ordered = TransactionData.RegistrationsInArrivalOrder(sorted);
            Assert.Equal(new[] { "earlier", "pay", "backdated" }, ordered.Select(t => t.Hash));
        }
    }
}
