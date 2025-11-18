using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-066 Fix: Unit tests for deterministic fork-choice mechanism
    /// Tests that competing blocks at same height are resolved deterministically
    /// </summary>
    public class ForkChoiceTests
    {
        [Fact]
        public void SelectCanonicalBlock_WithSingleBlock_ReturnsThatBlock()
        {
            // Arrange
            var block = CreateTestBlock(1000, "abc123");
            var competingBlocks = new List<(Block block, string IPAddress)>
            {
                (block, "192.168.1.1")
            };

            // Act
            var result = BlockDownloadService.SelectCanonicalBlock(competingBlocks);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(block.Hash, result.Hash);
            Assert.Equal(block.Height, result.Height);
        }

        [Fact]
        public void SelectCanonicalBlock_WithNullList_ReturnsNull()
        {
            // Act
            var result = BlockDownloadService.SelectCanonicalBlock(null);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void SelectCanonicalBlock_WithEmptyList_ReturnsNull()
        {
            // Arrange
            var competingBlocks = new List<(Block block, string IPAddress)>();

            // Act
            var result = BlockDownloadService.SelectCanonicalBlock(competingBlocks);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void SelectCanonicalBlock_WithTwoBlocks_SelectsLowestHash()
        {
            // Arrange: Create blocks with specific hashes to control order
            var blockA = CreateTestBlock(1000, "aaa000");
            var blockB = CreateTestBlock(1000, "bbb111");
            
            var competingBlocks = new List<(Block block, string IPAddress)>
            {
                (blockB, "192.168.1.2"), // Higher hash added first
                (blockA, "192.168.1.1")  // Lower hash added second
            };

            // Act
            var result = BlockDownloadService.SelectCanonicalBlock(competingBlocks);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("aaa000", result.Hash);
        }

        [Fact]
        public void SelectCanonicalBlock_WithMultipleBlocks_SelectsLowestHashDeterministically()
        {
            // Arrange: Create multiple blocks at same height with different hashes
            var block1 = CreateTestBlock(1000, "fff999");
            var block2 = CreateTestBlock(1000, "aaa111");
            var block3 = CreateTestBlock(1000, "ccc333");
            var block4 = CreateTestBlock(1000, "bbb222");
            
            var competingBlocks = new List<(Block block, string IPAddress)>
            {
                (block1, "192.168.1.1"),
                (block2, "192.168.1.2"),
                (block3, "192.168.1.3"),
                (block4, "192.168.1.4")
            };

            // Act
            var result = BlockDownloadService.SelectCanonicalBlock(competingBlocks);

            // Assert: Should always select "aaa111" regardless of insertion order
            Assert.NotNull(result);
            Assert.Equal("aaa111", result.Hash);
        }

        [Fact]
        public void SelectCanonicalBlock_IsDeterministic_AcrossDifferentOrders()
        {
            // Arrange: Create same blocks in different orders
            var blockA = CreateTestBlock(1000, "hash_a");
            var blockB = CreateTestBlock(1000, "hash_b");
            var blockC = CreateTestBlock(1000, "hash_c");

            var order1 = new List<(Block, string)>
            {
                (blockA, "ip1"), (blockB, "ip2"), (blockC, "ip3")
            };

            var order2 = new List<(Block, string)>
            {
                (blockC, "ip3"), (blockA, "ip1"), (blockB, "ip2")
            };

            var order3 = new List<(Block, string)>
            {
                (blockB, "ip2"), (blockC, "ip3"), (blockA, "ip1")
            };

            // Act
            var result1 = BlockDownloadService.SelectCanonicalBlock(order1);
            var result2 = BlockDownloadService.SelectCanonicalBlock(order2);
            var result3 = BlockDownloadService.SelectCanonicalBlock(order3);

            // Assert: All should select the same block
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.NotNull(result3);
            Assert.Equal(result1.Hash, result2.Hash);
            Assert.Equal(result2.Hash, result3.Hash);
        }

        [Fact]
        public async Task BlockCollisionResolve_WithDifferentHeights_ReturnsNull()
        {
            // Arrange
            var block1 = CreateTestBlock(1000, "hash1");
            var block2 = CreateTestBlock(1001, "hash2");

            // Act
            var result = await BlockDownloadService.BlockCollisionResolve(block1, block2);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task BlockCollisionResolve_WithSameBlock_ReturnsThatBlock()
        {
            // Arrange
            var block = CreateTestBlock(1000, "samehash");

            // Act
            var result = await BlockDownloadService.BlockCollisionResolve(block, block);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("samehash", result.Hash);
        }

        [Fact]
        public async Task BlockCollisionResolve_WithTwoBlocks_SelectsLowestHash()
        {
            // Arrange
            var block1 = CreateTestBlock(1000, "zzz999");
            var block2 = CreateTestBlock(1000, "aaa111");

            // Act
            var result = await BlockDownloadService.BlockCollisionResolve(block1, block2);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("aaa111", result.Hash);
        }

        [Fact]
        public async Task BlockCollisionResolve_IsDeterministic_RegardlessOfOrder()
        {
            // Arrange
            var blockA = CreateTestBlock(1000, "hash_x");
            var blockB = CreateTestBlock(1000, "hash_y");

            // Act: Call in both orders
            var result1 = await BlockDownloadService.BlockCollisionResolve(blockA, blockB);
            var result2 = await BlockDownloadService.BlockCollisionResolve(blockB, blockA);

            // Assert: Both should return the same winner
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.Equal(result1.Hash, result2.Hash);
        }

        [Fact]
        public void SelectCanonicalBlock_WithIdenticalHashes_ReturnsFirstMatch()
        {
            // Arrange: Blocks with identical hashes (should not happen in practice)
            var block1 = CreateTestBlock(1000, "samehash");
            var block2 = CreateTestBlock(1000, "samehash");
            
            var competingBlocks = new List<(Block block, string IPAddress)>
            {
                (block1, "192.168.1.1"),
                (block2, "192.168.1.2")
            };

            // Act
            var result = BlockDownloadService.SelectCanonicalBlock(competingBlocks);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("samehash", result.Hash);
        }

        /// <summary>
        /// Helper method to create test blocks with specific properties
        /// </summary>
        private Block CreateTestBlock(long height, string hash)
        {
            return new Block
            {
                Height = height,
                Hash = hash,
                Timestamp = 1000000 + height,
                Validator = "TestValidator",
                ChainRefId = "TestChain",
                Transactions = new List<Transaction>()
            };
        }
    }
}
