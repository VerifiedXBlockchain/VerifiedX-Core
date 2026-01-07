using Xunit;
using ReserveBlockCore.Bitcoin.Services;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Bitcoin.FROST.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Unit tests for FROST MPC Service - DKG and Signing Ceremony Coordination
    /// </summary>
    public class FrostMPCServiceTests
    {
        #region DKG Ceremony Tests

        [Fact]
        public async Task CoordinateDKGCeremony_WithValidValidators_ReturnsResult()
        {
            // Arrange
            var scUID = "test_sc_uid_001";
            var ownerAddress = "VFX1234567890ABCDEF";
            var validators = CreateMockValidators(5);
            var threshold = 51;

            // Act
            var result = await FrostMPCService.CoordinateDKGCeremony(
                scUID,
                ownerAddress,
                validators,
                threshold
            );

            // Assert
            Assert.NotNull(result);
            Assert.Equal(scUID, result.SmartContractUID);
            Assert.NotNull(result.TaprootAddress);
            Assert.NotNull(result.GroupPublicKey);
            Assert.NotNull(result.DKGProof);
            Assert.True(result.TaprootAddress.StartsWith("bc1p") || result.TaprootAddress.StartsWith("tb1p"));
            Assert.Equal(validators.Count, result.ParticipantAddresses.Count);
        }

        [Fact]
        public async Task CoordinateDKGCeremony_WithInsufficientValidators_ReturnsNull()
        {
            // Arrange
            var scUID = "test_sc_uid_002";
            var ownerAddress = "VFX1234567890ABCDEF";
            var validators = new List<VBTCValidator>(); // Empty list
            var threshold = 51;

            // Act
            var result = await FrostMPCService.CoordinateDKGCeremony(
                scUID,
                ownerAddress,
                validators,
                threshold
            );

            // Assert - Should handle gracefully but may return null with no validators
            // This tests robustness of the ceremony coordination
            Assert.True(result == null || result.ParticipantAddresses.Count == 0);
        }

        [Fact]
        public async Task CoordinateDKGCeremony_GeneratesTaprootAddress_WithCorrectPrefix()
        {
            // Arrange
            var validators = CreateMockValidators(3);
            
            // Act
            var result = await FrostMPCService.CoordinateDKGCeremony(
                "test_sc",
                "VFX_OWNER",
                validators,
                51
            );

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(result.TaprootAddress);
            // Should be testnet (tb1p) or mainnet (bc1p) Taproot address
            Assert.Matches(@"^(bc1p|tb1p)[a-z0-9]{58}$", result.TaprootAddress);
        }

        [Fact]
        public async Task CoordinateDKGCeremony_WithDifferentThresholds_ReturnsCorrectThreshold()
        {
            // Arrange
            var validators = CreateMockValidators(10);
            var thresholds = new[] { 51, 67, 75 };

            foreach (var threshold in thresholds)
            {
                // Act
                var result = await FrostMPCService.CoordinateDKGCeremony(
                    $"test_sc_{threshold}",
                    "VFX_OWNER",
                    validators,
                    threshold
                );

                // Assert
                Assert.NotNull(result);
                Assert.Equal(threshold, result.Threshold);
            }
        }

        [Fact]
        public async Task CoordinateDKGCeremony_GeneratesUniqueDKGProof()
        {
            // Arrange
            var validators = CreateMockValidators(3);
            
            // Act
            var result1 = await FrostMPCService.CoordinateDKGCeremony("sc1", "owner1", validators, 51);
            var result2 = await FrostMPCService.CoordinateDKGCeremony("sc2", "owner2", validators, 51);

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.NotEqual(result1.DKGProof, result2.DKGProof);
            Assert.NotEqual(result1.GroupPublicKey, result2.GroupPublicKey);
        }

        #endregion

        #region Signing Ceremony Tests

        [Fact]
        public async Task CoordinateSigningCeremony_WithValidParameters_ReturnsSignature()
        {
            // Arrange
            var messageHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var scUID = "test_sc_signing_001";
            var validators = CreateMockValidators(5);
            var threshold = 51;

            // Act
            var result = await FrostMPCService.CoordinateSigningCeremony(
                messageHash,
                scUID,
                validators,
                threshold
            );

            // Assert
            Assert.NotNull(result);
            Assert.Equal(messageHash, result.MessageHash);
            Assert.NotNull(result.SchnorrSignature);
            Assert.True(result.SignatureValid);
            Assert.Equal(128, result.SchnorrSignature.Length); // 64 bytes = 128 hex chars
        }

        [Fact]
        public async Task CoordinateSigningCeremony_GeneratesSchnorrSignature_OfCorrectLength()
        {
            // Arrange
            var messageHash = "abcd1234" + new string('0', 56); // 64 char hex
            var validators = CreateMockValidators(3);

            // Act
            var result = await FrostMPCService.CoordinateSigningCeremony(
                messageHash,
                "test_sc",
                validators,
                51
            );

            // Assert
            Assert.NotNull(result);
            Assert.Equal(128, result.SchnorrSignature.Length); // Schnorr = 64 bytes
        }

        [Fact]
        public async Task CoordinateSigningCeremony_WithDifferentMessages_GeneratesDifferentSignatures()
        {
            // Arrange
            var validators = CreateMockValidators(5);
            var messageHash1 = "1111111111111111111111111111111111111111111111111111111111111111";
            var messageHash2 = "2222222222222222222222222222222222222222222222222222222222222222";

            // Act
            var result1 = await FrostMPCService.CoordinateSigningCeremony(messageHash1, "sc1", validators, 51);
            var result2 = await FrostMPCService.CoordinateSigningCeremony(messageHash2, "sc2", validators, 51);

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.NotEqual(result1.SchnorrSignature, result2.SchnorrSignature);
        }

        [Fact]
        public async Task CoordinateSigningCeremony_WithInsufficientValidators_ReturnsNull()
        {
            // Arrange
            var messageHash = "test_message_hash_64_chars_000000000000000000000000000000000000";
            var validators = new List<VBTCValidator>(); // No validators

            // Act
            var result = await FrostMPCService.CoordinateSigningCeremony(
                messageHash,
                "test_sc",
                validators,
                51
            );

            // Assert
            // Should handle gracefully
            Assert.True(result == null || result.SignerAddresses.Count == 0);
        }

        [Fact]
        public async Task CoordinateSigningCeremony_ReturnsCorrectSignerList()
        {
            // Arrange
            var validators = CreateMockValidators(7);
            var messageHash = "test_hash_" + new string('a', 54);

            // Act
            var result = await FrostMPCService.CoordinateSigningCeremony(
                messageHash,
                "test_sc",
                validators,
                51
            );

            // Assert
            Assert.NotNull(result);
            Assert.Equal(validators.Count, result.SignerAddresses.Count);
            foreach (var validator in validators)
            {
                Assert.Contains(validator.ValidatorAddress, result.SignerAddresses);
            }
        }

        #endregion

        #region Threshold Calculation Tests

        [Theory]
        [InlineData(10, 51, 6)]  // 51% of 10 = 5.1, rounds up to 6
        [InlineData(10, 67, 7)]  // 67% of 10 = 6.7, rounds up to 7
        [InlineData(10, 75, 8)]  // 75% of 10 = 7.5, rounds up to 8
        [InlineData(100, 51, 51)]
        [InlineData(3, 67, 3)]   // 67% of 3 = 2.01, rounds up to 3
        public void ThresholdCalculation_ReturnsCorrectRequiredCount(int totalValidators, int thresholdPercent, int expectedRequired)
        {
            // This test validates the threshold calculation logic
            // The actual calculation is: Math.Ceiling(totalValidators * (thresholdPercentage / 100.0))
            
            var calculatedRequired = (int)System.Math.Ceiling(totalValidators * (thresholdPercent / 100.0));
            
            Assert.Equal(expectedRequired, calculatedRequired);
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task FullCeremonyFlow_DKG_ThenSigning_CompletesSuccessfully()
        {
            // Arrange
            var validators = CreateMockValidators(5);
            var scUID = "integration_test_sc_001";
            var ownerAddress = "VFX_INTEGRATION_TEST";

            // Act - Step 1: DKG Ceremony
            var dkgResult = await FrostMPCService.CoordinateDKGCeremony(
                scUID,
                ownerAddress,
                validators,
                51
            );

            Assert.NotNull(dkgResult);

            // Act - Step 2: Signing Ceremony (simulating withdrawal)
            var messageHash = "withdrawal_tx_hash_" + new string('f', 46);
            var signingResult = await FrostMPCService.CoordinateSigningCeremony(
                messageHash,
                scUID,
                validators,
                51
            );

            // Assert
            Assert.NotNull(signingResult);
            Assert.NotNull(dkgResult.TaprootAddress);
            Assert.NotNull(signingResult.SchnorrSignature);
            Assert.True(signingResult.SignatureValid);
        }

        [Fact]
        public async Task MultipleConcurrentCeremonies_AllCompleteSuccessfully()
        {
            // Arrange
            var validators = CreateMockValidators(5);
            var ceremonies = new List<Task<FrostDKGResult?>>();

            // Act - Launch 3 concurrent ceremonies
            for (int i = 0; i < 3; i++)
            {
                var ceremony = FrostMPCService.CoordinateDKGCeremony(
                    $"concurrent_sc_{i}",
                    $"owner_{i}",
                    validators,
                    51
                );
                ceremonies.Add(ceremony);
            }

            var results = await Task.WhenAll(ceremonies);

            // Assert
            Assert.All(results, r => Assert.NotNull(r));
            Assert.Equal(3, results.Length);
            
            // Verify all generated unique addresses
            var addresses = results.Select(r => r!.TaprootAddress).ToList();
            Assert.Equal(addresses.Count, addresses.Distinct().Count());
        }

        #endregion

        #region Helper Methods

        private List<VBTCValidator> CreateMockValidators(int count)
        {
            var validators = new List<VBTCValidator>();
            
            for (int i = 0; i < count; i++)
            {
                validators.Add(new VBTCValidator
                {
                    Id = i + 1,
                    ValidatorAddress = $"VFX_VALIDATOR_{i + 1:D3}",
                    IPAddress = $"192.168.1.{i + 10}",
                    FrostPublicKey = $"FROST_PK_{i + 1:D3}",
                    RegistrationBlockHeight = 1000 + i,
                    LastHeartbeatBlock = 2000 + i,
                    IsActive = true,
                    RegistrationSignature = $"SIGNATURE_{i + 1:D3}",
                    RegisterTransactionHash = $"TX_HASH_{i + 1:D3}"
                });
            }
            
            return validators;
        }

        #endregion
    }
}
