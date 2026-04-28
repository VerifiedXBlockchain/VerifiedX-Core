using System.Collections.Concurrent;
using System.Reflection;
using ReserveBlockCore;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// CONSENSUS-V2 (Fix #2): Atomic add helper must enforce MaxCasters and uniqueness
    /// under concurrent callers.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class CasterPoolOverflowTests
    {
        [Fact]
        public void AddBlockCasterIfRoomAndUnique_ConcurrentCalls_NeverExceedsCap()
        {
            var original = Globals.BlockCasters;
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();

                var method = typeof(CasterDiscoveryService)
                    .GetMethod("AddBlockCasterIfRoomAndUnique", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var candidates = Enumerable.Range(0, 16)
                    .Select(i => new Peers
                    {
                        ValidatorAddress = $"xADDR{i:D2}",
                        PeerIP = $"10.0.0.{i + 1}",
                        IsValidator = true,
                        ValidatorPublicKey = $"PK{i:D2}",
                    })
                    .ToList();

                Parallel.ForEach(candidates, candidate =>
                {
                    _ = (bool)method!.Invoke(null, new object[] { candidate })!;
                });

                var finalCount = Globals.BlockCasters
                    .Select(x => x.ValidatorAddress)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                Assert.Equal(CasterDiscoveryService.MaxCasters, finalCount);
            }
            finally
            {
                Globals.BlockCasters = original;
            }
        }
    }
}
