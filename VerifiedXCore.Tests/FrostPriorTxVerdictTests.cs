using System.Reflection;
using VerifiedXCore.Bitcoin.FROST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Funds-safety regression: Electrum's unspent list hides outputs spent by an UNCONFIRMED
    /// transaction, so a merely-broadcast prior transaction looked "conflicted away" and a second,
    /// disjoint transaction for the same withdrawal could be signed (double payout once both
    /// confirmed). A prior tx known to any server but unconfirmed is alive and must refuse; only a
    /// txid unknown to every server proceeds to the outpoint check.
    /// </summary>
    public class FrostPriorTxVerdictTests
    {
        private static string Classify(int? confirmations)
        {
            var m = typeof(FrostStartup).GetMethod("ClassifyPriorTx", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return m!.Invoke(null, new object?[] { confirmations })!.ToString()!;
        }

        [Fact]
        public void Confirmed_WhenAtLeastOneConfirmation()
        {
            Assert.Equal("Confirmed", Classify(1));
            Assert.Equal("Confirmed", Classify(6));
        }

        [Fact]
        public void InMempool_WhenKnownButUnconfirmed()
        {
            Assert.Equal("InMempool", Classify(0));
        }

        [Fact]
        public void Unknown_WhenNoServerKnowsIt()
        {
            Assert.Equal("Unknown", Classify(null));
            Assert.Equal("Unknown", Classify(-1));
        }
    }
}
