using System;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A transaction a node receives from a peer is rated before it is kept, and one rated F is refused
    /// (P2PServer, P2PValidatorServer, ValidatorNode, BlockcasterNode, NodeDataProcessor all drop it). The rating starts
    /// at F and each transaction type has to be named to get anything else. VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL (42), the
    /// transaction that reverses a bridge exit whose Bitcoin signing failed, was never named: every peer dropped it.
    /// </summary>
    public class VbtcTransactionRatingTests
    {
        [Fact]
        public async Task TheBridgeExitFailureTransaction_IsRatedLikeTheOtherBridgeTransactions()
        {
            var tx = new Transaction { TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL, FromAddress = "a", ToAddress = "a", Hash = "h" };
            Assert.Equal(TransactionRating.A, await TransactionRatingService.GetTransactionRating(tx));
        }

        /// <summary>So that the next vBTC V2 type added to the enum cannot be forgotten the same way.</summary>
        [Fact]
        public async Task EveryVbtcV2TransactionType_HasARating()
        {
            var types = Enum.GetValues<TransactionType>().Where(t => t.ToString().StartsWith("VBTC_V2_", StringComparison.Ordinal)).ToList();
            Assert.True(types.Count >= 20);
            foreach (var type in types)
            {
                var tx = new Transaction { TransactionType = type, FromAddress = "a", ToAddress = "a", Hash = "h" };
                var rating = await TransactionRatingService.GetTransactionRating(tx);
                Assert.True(rating != TransactionRating.F, $"{type} is rated F: peers would drop it");
            }
        }
    }
}
