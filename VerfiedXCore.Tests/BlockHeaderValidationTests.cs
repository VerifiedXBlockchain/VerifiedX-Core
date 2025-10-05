using Xunit;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Unit tests for HAL-20 block header validation functionality
    /// Tests basic validation logic for null checks, version, timestamp, and duplicate detection
    /// </summary>
    public class BlockHeaderValidationTests
    {
        [Fact]
        public void ValidateBlockHeaders_WithNullBlock_ReturnsInvalid()
        {
            // Act
            var result = InputValidationHelper.ValidateBlockHeaders(null, "127.0.0.1");

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Block is null", result.ErrorMessage);
        }

        [Fact] 
        public void ValidateBlockHeaders_WithInvalidVersion_ReturnsInvalid()
        {
            // Arrange
            var block = new Block
            {
                Height = 0,
                Version = 999, // Invalid version (should be 4 for height 0)
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Hash = "test_hash",
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };

            // Act
            var result = InputValidationHelper.ValidateBlockHeaders(block, "127.0.0.1");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("Invalid block version", result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockHeaders_WithFutureTimestamp_ReturnsInvalid()
        {
            // Arrange  
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var block = new Block
            {
                Height = 0,
                Version = 4,
                Timestamp = currentTime + InputValidationHelper.MAX_TIMESTAMP_DRIFT_SECONDS + 100, // Far future
                Hash = "test_hash",
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };

            // Act
            var result = InputValidationHelper.ValidateBlockHeaders(block, "127.0.0.1");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("timestamp drift", result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockHeaders_WithOldTimestamp_ReturnsInvalid()
        {
            // Arrange
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var block = new Block
            {
                Height = 0,
                Version = 4,
                Timestamp = currentTime - InputValidationHelper.MAX_TIMESTAMP_DRIFT_SECONDS - 100, // Far past
                Hash = "test_hash",
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };

            // Act
            var result = InputValidationHelper.ValidateBlockHeaders(block, "127.0.0.1");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("timestamp drift", result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockHeaders_WithValidGenesisBlock_ReturnsValid()
        {
            // Arrange
            var block = new Block
            {
                Height = 0,
                Version = 4,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Hash = "valid_hash",
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };

            // Act
            var result = InputValidationHelper.ValidateBlockHeaders(block, "127.0.0.1");

            // Assert
            Assert.True(result.IsValid);
            Assert.False(result.IsDuplicate);
            Assert.Empty(result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockHeaders_WithDuplicateHash_ReturnsInvalidAndMarksAsDuplicate()
        {
            // Arrange
            var hash = "test_duplicate_hash";
            var block1 = new Block
            {
                Height = 0,
                Version = 4,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Hash = hash,
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };
            
            var block2 = new Block
            {
                Height = 0,
                Version = 4,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Hash = hash, // Same hash
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };

            // Act
            var result1 = InputValidationHelper.ValidateBlockHeaders(block1, "127.0.0.1");
            var result2 = InputValidationHelper.ValidateBlockHeaders(block2, "127.0.0.1");

            // Assert
            Assert.True(result1.IsValid); // First should be valid
            Assert.False(result2.IsValid); // Second should be invalid
            Assert.True(result2.IsDuplicate); // Should be marked as duplicate
            Assert.Contains("already processed recently", result2.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockHeaders_WithDifferentHashes_BothValid()
        {
            // Arrange
            var block1 = new Block
            {
                Height = 0,
                Version = 4,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Hash = "hash_1",
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };
            
            var block2 = new Block
            {
                Height = 0,
                Version = 4,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Hash = "hash_2", // Different hash
                PrevHash = "Genesis Block",
                ChainRefId = "RBX",
                Size = 1000
            };

            // Act
            var result1 = InputValidationHelper.ValidateBlockHeaders(block1, "127.0.0.1");
            var result2 = InputValidationHelper.ValidateBlockHeaders(block2, "127.0.0.1");

            // Assert
            Assert.True(result1.IsValid);
            Assert.True(result2.IsValid);
            Assert.False(result1.IsDuplicate);
            Assert.False(result2.IsDuplicate);
        }
    }
}
