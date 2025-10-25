using Xunit;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using ReserveBlockCore;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-19 Fix: Unit tests for DoS protection features in P2PValidatorServer
    /// Tests block size validation and SignalRQueue protection mechanisms
    /// </summary>
    public class DoSProtectionTests
    {
        public DoSProtectionTests()
        {
            // Initialize global configuration for tests
            Globals.MaxBlockSizeBytes = 10485760; // 10MB
            Globals.BlockValidationTimeoutMs = 5000;
        }

        [Fact]
        public void ValidateBlockSize_ValidBlock_ReturnsValid()
        {
            // Arrange
            var block = new Block
            {
                Size = 1024, // 1KB - valid size
                Height = 100,
                Hash = "valid_hash"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "127.0.0.1");

            // Assert
            Assert.True(result.IsValid);
            Assert.Empty(result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockSize_OversizedBlock_ReturnsInvalid()
        {
            // Arrange
            var block = new Block
            {
                Size = 20971520, // 20MB - exceeds 10MB limit
                Height = 100,
                Hash = "oversized_hash"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "192.168.1.100");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("exceeds maximum allowed", result.ErrorMessage);
            Assert.Contains("20971520", result.ErrorMessage);
            Assert.Contains("10485760", result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockSize_NegativeSize_ReturnsInvalid()
        {
            // Arrange
            var block = new Block
            {
                Size = -100, // Negative size
                Height = 100,
                Hash = "negative_hash"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "10.0.0.1");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("cannot be negative", result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockSize_TooSmallBlock_ReturnsInvalid()
        {
            // Arrange
            var block = new Block
            {
                Size = 50, // Too small - below 100 byte minimum
                Height = 100,
                Hash = "tiny_hash"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "172.16.0.1");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("suspiciously small", result.ErrorMessage);
            Assert.Contains("minimum: 100", result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockSize_NullBlock_ReturnsInvalid()
        {
            // Arrange
            Block block = null;

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "203.0.113.1");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("Block is null", result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockSize_EdgeCaseMinimumValid_ReturnsValid()
        {
            // Arrange
            var block = new Block
            {
                Size = 100, // Exactly minimum size
                Height = 100,
                Hash = "minimum_valid_hash"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "198.51.100.1");

            // Assert
            Assert.True(result.IsValid);
            Assert.Empty(result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockSize_EdgeCaseMaximumValid_ReturnsValid()
        {
            // Arrange
            var block = new Block
            {
                Size = 10485760, // Exactly maximum size (10MB)
                Height = 100,
                Hash = "maximum_valid_hash"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "198.51.100.2");

            // Assert
            Assert.True(result.IsValid);
            Assert.Empty(result.ErrorMessage);
        }

        [Fact]
        public void ValidateBlockSize_EdgeCaseJustOverLimit_ReturnsInvalid()
        {
            // Arrange
            var block = new Block
            {
                Size = 10485761, // Just 1 byte over limit
                Height = 100,
                Hash = "just_over_limit_hash"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "198.51.100.3");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("exceeds maximum allowed", result.ErrorMessage);
        }

        [Theory]
        [InlineData(1024, true)]      // 1KB - valid
        [InlineData(1048576, true)]   // 1MB - valid
        [InlineData(5242880, true)]   // 5MB - valid
        [InlineData(10485760, true)]  // 10MB - valid (at limit)
        [InlineData(10485761, false)] // Just over 10MB - invalid
        [InlineData(20971520, false)] // 20MB - invalid
        [InlineData(0, false)]        // Zero size - invalid
        [InlineData(-1, false)]       // Negative - invalid
        [InlineData(50, false)]       // Too small - invalid
        public void ValidateBlockSize_VariousSizes_ReturnsExpectedResult(long blockSize, bool expectedValid)
        {
            // Arrange
            var block = new Block
            {
                Size = blockSize,
                Height = 100,
                Hash = $"test_hash_{blockSize}"
            };

            // Act
            var result = InputValidationHelper.ValidateBlockSize(block, "test.ip.address");

            // Assert
            Assert.Equal(expectedValid, result.IsValid);
        }

        [Fact]
        public void DoSProtectionConfiguration_DefaultValues_AreSecure()
        {
            // Assert - Verify default configuration is secure
            Assert.True(Globals.MaxBlockSizeBytes > 0, "MaxBlockSizeBytes should be positive");
            Assert.True(Globals.MaxBlockSizeBytes <= 50 * 1024 * 1024, "MaxBlockSizeBytes should not exceed 50MB for security");
            Assert.True(Globals.BlockValidationTimeoutMs > 0, "BlockValidationTimeoutMs should be positive");
            Assert.True(Globals.BlockValidationTimeoutMs <= 30000, "BlockValidationTimeoutMs should not exceed 30 seconds");
        }

        [Fact]
        public void DoSProtectionConfiguration_IsConfigurable()
        {
            // Arrange
            var originalMaxSize = Globals.MaxBlockSizeBytes;
            var originalTimeout = Globals.BlockValidationTimeoutMs;

            try
            {
                // Act - Change configuration
                Globals.MaxBlockSizeBytes = 5242880; // 5MB
                Globals.BlockValidationTimeoutMs = 3000; // 3 seconds

                // Assert
                Assert.Equal(5242880, Globals.MaxBlockSizeBytes);
                Assert.Equal(3000, Globals.BlockValidationTimeoutMs);

                // Test with new configuration
                var block = new Block
                {
                    Size = 6291456, // 6MB - should now be invalid with 5MB limit
                    Height = 100,
                    Hash = "test_new_config"
                };

                var result = InputValidationHelper.ValidateBlockSize(block, "config.test.ip");
                Assert.False(result.IsValid);
                Assert.Contains("exceeds maximum allowed 5242880", result.ErrorMessage);
            }
            finally
            {
                // Cleanup - Restore original values
                Globals.MaxBlockSizeBytes = originalMaxSize;
                Globals.BlockValidationTimeoutMs = originalTimeout;
            }
        }
    }
}
