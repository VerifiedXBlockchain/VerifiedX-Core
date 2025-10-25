using Xunit;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Models;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-034: Tests for consensus cache hard caps to prevent unbounded growth
    /// </summary>
    public class ConsensusCacheLimitsTests
    {
        private const int MaxCacheEntries = 100;

        [Fact]
        public void Messages_Cache_Should_Not_Exceed_Hard_Cap()
        {
            // Arrange
            ClearConsensusCaches();
            var testNode = CreateTestNode("TestAddress1");

            // Act: Add more than MaxCacheEntries to Messages cache
            for (int i = 0; i < MaxCacheEntries + 50; i++)
            {
                var key = (Height: (long)i, MethodCode: 0);
                var innerDict = new ConcurrentDictionary<string, (string Message, string Signature)>();
                innerDict.TryAdd($"addr_{i}", ($"message_{i}", $"sig_{i}"));
                ConsensusServer.Messages.TryAdd(key, innerDict);
            }

            // Trigger cache enforcement
            ConsensusServer.UpdateNode(testNode, 200, 0, false);

            // Assert: Cache should be at or below the hard cap
            Assert.True(ConsensusServer.Messages.Count <= MaxCacheEntries,
                $"Messages cache exceeded hard cap: {ConsensusServer.Messages.Count} > {MaxCacheEntries}");
        }

        [Fact]
        public void Hashes_Cache_Should_Not_Exceed_Hard_Cap()
        {
            // Arrange
            ClearConsensusCaches();
            var testNode = CreateTestNode("TestAddress2");

            // Act: Add more than MaxCacheEntries to Hashes cache
            for (int i = 0; i < MaxCacheEntries + 50; i++)
            {
                var key = (Height: (long)i, MethodCode: 0);
                var innerDict = new ConcurrentDictionary<string, (string Hash, string Signature)>();
                innerDict.TryAdd($"addr_{i}", ($"hash_{i}", $"sig_{i}"));
                ConsensusServer.Hashes.TryAdd(key, innerDict);
            }

            // Trigger cache enforcement
            ConsensusServer.UpdateNode(testNode, 200, 0, true);

            // Assert: Cache should be at or below the hard cap
            Assert.True(ConsensusServer.Hashes.Count <= MaxCacheEntries,
                $"Hashes cache exceeded hard cap: {ConsensusServer.Hashes.Count} > {MaxCacheEntries}");
        }

        [Fact]
        public void Cache_Should_Remove_Oldest_Entries_First()
        {
            // Arrange
            ClearConsensusCaches();
            var testNode = CreateTestNode("TestAddress3");

            // Act: Add entries with varying heights
            for (int i = 0; i < MaxCacheEntries + 20; i++)
            {
                var key = (Height: (long)i, MethodCode: 0);
                var innerDict = new ConcurrentDictionary<string, (string Message, string Signature)>();
                innerDict.TryAdd($"addr_{i}", ($"message_{i}", $"sig_{i}"));
                ConsensusServer.Messages.TryAdd(key, innerDict);
            }

            // Trigger cache enforcement
            ConsensusServer.UpdateNode(testNode, 200, 0, false);

            // Assert: Oldest entries should be removed (lowest heights)
            var remainingKeys = ConsensusServer.Messages.Keys.ToList();
            var minHeight = remainingKeys.Min(k => k.Height);
            
            // The minimum height should be > 0 (oldest entries removed)
            Assert.True(minHeight > 0, 
                $"Oldest entries were not removed. Min height: {minHeight}");
        }

        [Fact]
        public void Empty_Inner_Dictionaries_Should_Be_Removed()
        {
            // Arrange
            ClearConsensusCaches();
            var testNode = CreateTestNode("TestAddress4");

            // Act: Add entries, some with empty inner dictionaries
            for (int i = 0; i < 10; i++)
            {
                var key = (Height: (long)i, MethodCode: 0);
                var innerDict = new ConcurrentDictionary<string, (string Message, string Signature)>();
                
                // Only add content to even-numbered entries
                if (i % 2 == 0)
                {
                    innerDict.TryAdd($"addr_{i}", ($"message_{i}", $"sig_{i}"));
                }
                
                ConsensusServer.Messages.TryAdd(key, innerDict);
            }

            // Trigger cache enforcement
            ConsensusServer.UpdateNode(testNode, 200, 0, false);

            // Assert: Empty dictionaries should be removed
            var emptyCount = ConsensusServer.Messages.Count(x => x.Value.IsEmpty);
            Assert.True(emptyCount == 0, 
                $"Found {emptyCount} empty inner dictionaries that should have been removed");
        }

        [Fact]
        public void Cache_Enforcement_Should_Handle_Concurrent_Access()
        {
            // Arrange
            ClearConsensusCaches();
            var nodes = Enumerable.Range(0, 10).Select(i => CreateTestNode($"TestAddress_{i}")).ToList();

            // Act: Simulate concurrent updates from multiple nodes
            System.Threading.Tasks.Parallel.For(0, 150, i =>
            {
                var key = (Height: (long)i, MethodCode: i % 3);
                var innerDict = new ConcurrentDictionary<string, (string Message, string Signature)>();
                innerDict.TryAdd($"addr_{i}", ($"message_{i}", $"sig_{i}"));
                ConsensusServer.Messages.TryAdd(key, innerDict);
            });

            // Trigger enforcement from multiple nodes
            var nodeIndex = 0;
            System.Threading.Tasks.Parallel.ForEach(nodes, node =>
            {
                var index = System.Threading.Interlocked.Increment(ref nodeIndex) - 1;
                ConsensusServer.UpdateNode(node, 200 + index, 0, false);
            });

            // Assert: Cache should still respect the hard cap
            Assert.True(ConsensusServer.Messages.Count <= MaxCacheEntries,
                $"Messages cache exceeded hard cap under concurrent access: {ConsensusServer.Messages.Count} > {MaxCacheEntries}");
        }

        // Helper methods
        private void ClearConsensusCaches()
        {
            ConsensusServer.Messages.Clear();
            ConsensusServer.Hashes.Clear();
        }

        private NodeInfo CreateTestNode(string address)
        {
            return new NodeInfo
            {
                Address = address,
                NodeHeight = 0,
                MethodCode = 0,
                IsFinalized = false,
                LastMethodCodeTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
    }
}
