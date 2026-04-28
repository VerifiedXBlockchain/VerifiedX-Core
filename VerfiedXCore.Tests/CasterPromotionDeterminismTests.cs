using ReserveBlockCore.Models;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// CONSENSUS-V2 (Fix #1): Deterministic candidate ordering tie-breaks by
    /// address using ordinal comparison.
    /// </summary>
    public class CasterPromotionDeterminismTests
    {
        [Fact]
        public void DeterministicOrdering_WithTiedBalances_PicksSameHeadAcrossShuffles()
        {
            var candidates = new List<(NetworkValidator Validator, decimal Balance)>
            {
                (new NetworkValidator { Address = "xZZ" }, 5000m),
                (new NetworkValidator { Address = "xAA" }, 5000m),
                (new NetworkValidator { Address = "xMM" }, 5000m),
                (new NetworkValidator { Address = "xAB" }, 5000m),
            };

            var expectedHead = candidates
                .OrderByDescending(x => x.Balance)
                .ThenBy(x => x.Validator.Address ?? "", StringComparer.Ordinal)
                .First()
                .Validator
                .Address;

            for (int i = 0; i < 100; i++)
            {
                var shuffled = candidates
                    .OrderBy(_ => Guid.NewGuid())
                    .ToList();

                var head = shuffled
                    .OrderByDescending(x => x.Balance)
                    .ThenBy(x => x.Validator.Address ?? "", StringComparer.Ordinal)
                    .First()
                    .Validator
                    .Address;

                Assert.Equal(expectedHead, head);
            }
        }
    }
}
