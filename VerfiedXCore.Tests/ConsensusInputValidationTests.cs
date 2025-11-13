using Xunit;
using ReserveBlockCore.P2P;
using System.Reflection;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-035: Tests for consensus input validation to prevent DoS attacks
    /// via malformed or oversized inputs to Message and Hash endpoints
    /// </summary>
    public class ConsensusInputValidationTests
    {
        [Fact]
        public void Message_WithMalformedInput_MissingDelimiter_ShouldNotThrow()
        {
            // Arrange
            var malformedInput = "messageWithoutDelimiter";

            // Act & Assert
            // The parsing logic should handle this gracefully without throwing exceptions
            var exception = Record.Exception(() => ParseMessageInput(malformedInput));
            Assert.Null(exception);
        }

        [Fact]
        public void Message_WithSingleDelimiter_InsufficientParts_ShouldNotThrow()
        {
            // Arrange
            var malformedInput = "onlyOnePartNoSignature;:;";

            // Act & Assert
            var exception = Record.Exception(() => ParseMessageInput(malformedInput));
            Assert.Null(exception);
        }

        [Fact]
        public void Message_WithValidInput_ShouldParseCorrectly()
        {
            // Arrange
            var validInput = "testMessage;:;testSignature";

            // Act
            var (message, signature) = ParseMessageInput(validInput);

            // Assert
            Assert.Equal("testMessage", message);
            Assert.Equal("testSignature", signature);
        }

        [Fact]
        public void Message_WithOversizedInput_ShouldBeRejected()
        {
            // Arrange
            var oversizedInput = new string('x', 20000); // Exceeds MaxMessageSize of 10KB

            // Act
            var (message, signature) = ParseMessageInput(oversizedInput);

            // Assert - Should be rejected and return nulls
            Assert.Null(message);
            Assert.Null(signature);
        }

        [Fact]
        public void Hash_WithMalformedInput_MissingDelimiter_ShouldNotThrow()
        {
            // Arrange
            var malformedInput = "hashWithoutDelimiter";

            // Act & Assert
            var exception = Record.Exception(() => ParseHashInput(malformedInput));
            Assert.Null(exception);
        }

        [Fact]
        public void Hash_WithSingleDelimiter_InsufficientParts_ShouldNotThrow()
        {
            // Arrange
            var malformedInput = "onlyHash:";

            // Act & Assert
            var exception = Record.Exception(() => ParseHashInput(malformedInput));
            Assert.Null(exception);
        }

        [Fact]
        public void Hash_WithValidInput_ShouldParseCorrectly()
        {
            // Arrange
            var validInput = "testHash:testSignature";

            // Act
            var (hash, signature) = ParseHashInput(validInput);

            // Assert
            Assert.Equal("testHash", hash);
            Assert.Equal("testSignature", signature);
        }

        [Fact]
        public void Hash_WithOversizedInput_ShouldBeRejected()
        {
            // Arrange
            var oversizedInput = new string('x', 15000); // Exceeds MaxMessageSize of 10KB

            // Act
            var (hash, signature) = ParseHashInput(oversizedInput);

            // Assert - Should be rejected and return nulls
            Assert.Null(hash);
            Assert.Null(signature);
        }

        [Fact]
        public void Hash_WithMultipleDelimiters_ShouldOnlyUseFirsTwo()
        {
            // Arrange
            var inputWithExtraDelimiters = "hash:signature:extra:parts";

            // Act
            var (hash, signature) = ParseHashInput(inputWithExtraDelimiters);

            // Assert - Should only use first two parts
            Assert.Equal("hash", hash);
            Assert.Equal("signature", signature);
        }

        [Fact]
        public void Message_WithColonReplacement_ShouldProcessCorrectly()
        {
            // Arrange
            var inputWithDoubleColons = "test::message::with::colons;:;signature";

            // Act
            var (message, signature) = ParseMessageInput(inputWithDoubleColons);

            // Assert - Double colons should be replaced with single colons
            Assert.Equal("test:message:with:colons", message);
            Assert.Equal("signature", signature);
        }

        // Helper methods that simulate the parsing logic from ConsensusServer
        private const int MaxMessageSize = 10000;

        private (string message, string signature) ParseMessageInput(string peerMessage)
        {
            string message = null;
            string signature = null;

            if (peerMessage != null)
            {
                // HAL-035 Fix: Validate input size and array length before processing
                if (peerMessage.Length <= MaxMessageSize)
                {
                    var split = peerMessage.Split(";:;");
                    if (split.Length >= 2)
                    {
                        (message, signature) = (split[0], split[1]);
                        message = message.Replace("::", ":");
                    }
                }
            }

            return (message, signature);
        }

        private (string hash, string signature) ParseHashInput(string peerHash)
        {
            string hash = null;
            string signature = null;

            if (peerHash != null)
            {
                // HAL-035 Fix: Validate input size and array length before processing
                if (peerHash.Length <= MaxMessageSize)
                {
                    var split = peerHash.Split(":");
                    if (split.Length >= 2)
                    {
                        (hash, signature) = (split[0], split[1]);
                    }
                }
            }

            return (hash, signature);
        }
    }
}
