using Xunit;
using ReserveBlockCore.Utilities;
using ReserveBlockCore.Models;
using System.Collections.Generic;
using System.Linq;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-15 Security Fix: Comprehensive unit tests for input validation
    /// Tests the InputValidationHelper class and related security mechanisms
    /// </summary>
    public class InputValidationTests
    {
        #region Handshake Header Validation Tests

        [Fact]
        public void ValidateHandshakeHeaders_OversizedAddress_ReturnsFalse()
        {
            // Arrange
            var address = new string('A', InputValidationHelper.MAX_ADDRESS_LENGTH + 1);
            var time = "1696435200";
            var uName = "ValidUser";
            var publicKey = "04abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
            var signature = "ValidSignature123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890";
            var walletVersion = "1.0.0";
            var nonce = "abc123def456";

            // Act
            var result = InputValidationHelper.ValidateHandshakeHeaders(
                address, time, uName, publicKey, signature, walletVersion, nonce);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Address length"));
        }

        [Fact]
        public void ValidateHandshakeHeaders_OversizedPublicKey_ReturnsFalse()
        {
            // Arrange
            var address = "RValidAddress123456789012345678901234567890";
            var time = "1696435200";
            var uName = "ValidUser";
            var publicKey = new string('A', InputValidationHelper.MAX_PUBLIC_KEY_LENGTH + 1);
            var signature = "ValidSignature123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890";
            var walletVersion = "1.0.0";
            var nonce = "abc123def456";

            // Act
            var result = InputValidationHelper.ValidateHandshakeHeaders(
                address, time, uName, publicKey, signature, walletVersion, nonce);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("PublicKey length"));
        }

        [Fact]
        public void ValidateHandshakeHeaders_OversizedUsername_ReturnsFalse()
        {
            // Arrange
            var address = "RValidAddress123456789012345678901234567890";
            var time = "1696435200";
            var uName = new string('U', InputValidationHelper.MAX_USERNAME_LENGTH + 1);
            var publicKey = "04abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
            var signature = "ValidSignature123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890";
            var walletVersion = "1.0.0";
            var nonce = "abc123def456";

            // Act
            var result = InputValidationHelper.ValidateHandshakeHeaders(
                address, time, uName, publicKey, signature, walletVersion, nonce);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Username length"));
        }

        [Fact]
        public void ValidateHandshakeHeaders_InvalidTime_ReturnsFalse()
        {
            // Arrange
            var address = "RValidAddress123456789012345678901234567890";
            var time = "not_a_number";
            var uName = "ValidUser";
            var publicKey = "04abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
            var signature = "ValidSignature123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890";
            var walletVersion = "1.0.0";
            var nonce = "abc123def456";

            // Act
            var result = InputValidationHelper.ValidateHandshakeHeaders(
                address, time, uName, publicKey, signature, walletVersion, nonce);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Time field is invalid"));
        }

        [Fact]
        public void ValidateHandshakeHeaders_EmptyRequiredFields_ReturnsFalse()
        {
            // Arrange & Act
            var result = InputValidationHelper.ValidateHandshakeHeaders(
                "", "", "", "", "", "", "");

            // Assert
            Assert.False(result.IsValid);
            Assert.True(result.Errors.Count >= 4); // Address, PublicKey, Signature, Nonce are required
        }

        #endregion

        #region NetworkValidator Validation Tests

        [Fact]
        public void ValidateNetworkValidator_NullValidator_ReturnsFalse()
        {
            // Act
            var result = InputValidationHelper.ValidateNetworkValidator(null);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("NetworkValidator is null", result.Errors);
        }

        [Fact]
        public void ValidateNetworkValidator_OversizedFields_ReturnsFalse()
        {
            // Arrange
            var validator = new NetworkValidator
            {
                Address = new string('A', InputValidationHelper.MAX_ADDRESS_LENGTH + 1),
                IPAddress = "192.168.1.1",
                UniqueName = new string('U', InputValidationHelper.MAX_UNIQUE_NAME_LENGTH + 1),
                PublicKey = new string('P', InputValidationHelper.MAX_PUBLIC_KEY_LENGTH + 1),
                Signature = new string('S', InputValidationHelper.MAX_SIGNATURE_LENGTH + 1),
                SignatureMessage = new string('M', InputValidationHelper.MAX_SIGNATURE_MESSAGE_LENGTH + 1)
            };

            // Act
            var result = InputValidationHelper.ValidateNetworkValidator(validator);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Address length"));
            Assert.Contains(result.Errors, e => e.Contains("UniqueName length"));
            Assert.Contains(result.Errors, e => e.Contains("PublicKey length"));
            Assert.Contains(result.Errors, e => e.Contains("Signature length"));
            Assert.Contains(result.Errors, e => e.Contains("SignatureMessage length"));
        }

        #endregion

        #region NetworkValidator List Validation Tests

        [Fact]
        public void ValidateNetworkValidatorList_OversizedList_ShouldTruncate()
        {
            // Arrange
            var validators = new List<NetworkValidator>();
            for (int i = 0; i < InputValidationHelper.MAX_VALIDATOR_LIST_SIZE + 100; i++)
            {
                validators.Add(new NetworkValidator
                {
                    Address = $"RValidAddress{i:D32}",
                    IPAddress = $"192.168.{(i / 254) + 1}.{(i % 254) + 1}",
                    UniqueName = $"Validator{i}",
                    PublicKey = "04abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
                    Signature = "ValidSignature123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890",
                    SignatureMessage = "ValidSignatureMessage"
                });
            }

            // Act
            var result = InputValidationHelper.ValidateNetworkValidatorList(validators);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("exceeds maximum allowed"));
            Assert.True(result.ShouldTruncate);
            Assert.Equal(InputValidationHelper.MAX_VALIDATOR_LIST_SIZE, result.TruncatedList.Count);
        }

        [Fact]
        public void ValidateNetworkValidatorList_NullList_ReturnsFalse()
        {
            // Act
            var result = InputValidationHelper.ValidateNetworkValidatorList(null);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("Validator list is null", result.Errors);
        }

        #endregion

        #region Utility Method Tests

        [Fact]
        public void SanitizeString_DangerousCharacters_RemovesThem()
        {
            // Arrange
            var maliciousInput = "script&test";
            var maxLength = 50;

            // Act
            var result = InputValidationHelper.SanitizeString(maliciousInput, maxLength);

            // Assert
            Assert.DoesNotContain("&", result);
            Assert.Equal("scripttest", result);
        }

        [Fact]
        public void SanitizeString_OversizedInput_TruncatesToMaxLength()
        {
            // Arrange
            var longInput = new string('A', 100);
            var maxLength = 50;

            // Act
            var result = InputValidationHelper.SanitizeString(longInput, maxLength);

            // Assert
            Assert.Equal(maxLength, result.Length);
        }

        [Fact]
        public void SanitizeString_NullOrEmptyInput_ReturnsEmpty()
        {
            // Act & Assert
            Assert.Equal(string.Empty, InputValidationHelper.SanitizeString(null, 50));
            Assert.Equal(string.Empty, InputValidationHelper.SanitizeString("", 50));
            Assert.Equal(string.Empty, InputValidationHelper.SanitizeString("   ", 50));
        }

        #endregion

        #region Validator List Limiting Tests

        [Fact]
        public void LimitValidatorListForBroadcast_SmallList_ReturnsOriginal()
        {
            // Arrange
            var validators = new List<NetworkValidator>();
            for (int i = 0; i < 100; i++)
            {
                validators.Add(new NetworkValidator
                {
                    Address = $"RValidAddress{i:D32}",
                    CheckFailCount = i % 3, // Varying fail counts
                    ConfirmingSources = new HashSet<string> { $"peer{i % 5}" },
                    FirstAdvertised = 1696435200 + i
                });
            }

            // Act
            var result = InputValidationHelper.LimitValidatorListForBroadcast(validators);

            // Assert
            Assert.Equal(validators.Count, result.Count);
        }

        [Fact]
        public void LimitValidatorListForBroadcast_LargeList_LimitsTo2000()
        {
            // Arrange
            var validators = new List<NetworkValidator>();
            for (int i = 0; i < 3000; i++)
            {
                validators.Add(new NetworkValidator
                {
                    Address = $"RValidAddress{i:D32}",
                    CheckFailCount = i % 10, // Varying fail counts
                    ConfirmingSources = new HashSet<string> { $"peer{i % 5}" },
                    FirstAdvertised = 1696435200 + i
                });
            }

            // Act
            var result = InputValidationHelper.LimitValidatorListForBroadcast(validators);

            // Assert
            Assert.Equal(InputValidationHelper.MAX_VALIDATOR_BROADCAST_SIZE, result.Count);
        }

        [Fact]
        public void LimitValidatorListForBroadcast_PrioritizesByFailCount()
        {
            // Arrange
            var validators = new List<NetworkValidator>();
            for (int i = 0; i < 100; i++)
            {
                validators.Add(new NetworkValidator
                {
                    Address = $"RValidAddress{i:D32}",
                    CheckFailCount = i, // Increasing fail counts
                    ConfirmingSources = new HashSet<string> { "peer1" },
                    FirstAdvertised = 1696435200
                });
            }

            // Act
            var result = InputValidationHelper.LimitValidatorListForBroadcast(validators);

            // Assert
            Assert.Equal(validators.Count, result.Count);
            // Should be ordered by CheckFailCount ascending (lower fail count = higher priority)
            for (int i = 0; i < result.Count - 1; i++)
            {
                Assert.True(result[i].CheckFailCount <= result[i + 1].CheckFailCount);
            }
        }

        [Fact]
        public void LimitValidatorListForBroadcast_NullList_ReturnsEmptyList()
        {
            // Act
            var result = InputValidationHelper.LimitValidatorListForBroadcast(null);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        #endregion

        #region Security Limits Tests

        [Fact]
        public void SecurityLimits_FieldLengthConstants_AreReasonable()
        {
            // Assert that our security limits are reasonable for production use
            Assert.True(InputValidationHelper.MAX_ADDRESS_LENGTH >= 40); // Blockchain addresses need sufficient length
            Assert.True(InputValidationHelper.MAX_PUBLIC_KEY_LENGTH >= 64); // Public keys need sufficient length
            Assert.True(InputValidationHelper.MAX_VALIDATOR_BROADCAST_SIZE == 2000); // HAL-15 requirement
            Assert.True(InputValidationHelper.MAX_VALIDATOR_LIST_SIZE == 2000); // HAL-15 requirement
        }

        [Fact]
        public void SecurityLimits_BroadcastLimit_PreventsMemoryExhaustion()
        {
            // Demonstrate that broadcast limit prevents memory attacks
            var validators = new List<NetworkValidator>();
            
            // Try to create an attack vector with 10,000 validators
            for (int i = 0; i < 10000; i++)
            {
                validators.Add(new NetworkValidator
                {
                    Address = $"AttackValidator{i}",
                    CheckFailCount = 0,
                    ConfirmingSources = new HashSet<string> { "attacker" },
                    FirstAdvertised = 1696435200
                });
            }

            // Act
            var result = InputValidationHelper.LimitValidatorListForBroadcast(validators);

            // Assert - Attack is mitigated
            Assert.True(result.Count <= InputValidationHelper.MAX_VALIDATOR_BROADCAST_SIZE);
            Assert.Equal(InputValidationHelper.MAX_VALIDATOR_BROADCAST_SIZE, result.Count);
        }

        #endregion
    }
}
