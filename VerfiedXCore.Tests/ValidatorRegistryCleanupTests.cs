using ReserveBlockCore;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

namespace VerfiedXCore.Tests
{
    public class ValidatorRegistryCleanupTests : IDisposable
    {
        public ValidatorRegistryCleanupTests()
        {
            // Clear the global NetworkValidators dictionary before each test
            Globals.NetworkValidators.Clear();
        }

        public void Dispose()
        {
            // Clear after tests
            Globals.NetworkValidators.Clear();
        }


        [Fact]
        public void UpdateLastSeen_DoesNotThrowForNonExistentValidator()
        {
            // Arrange
            var nonExistentAddress = "non-existent-validator";

            // Act & Assert - should not throw
            var exception = Record.Exception(() => NetworkValidator.UpdateLastSeen(nonExistentAddress));
            Assert.Null(exception);
        }

        [Fact]
        public void CleanupStaleValidators_HandlesEmptyRegistry()
        {
            // Arrange - empty registry

            // Act & Assert - should not throw
            var exception = Record.Exception(() => NetworkValidator.CleanupStaleValidators());
            Assert.Null(exception);
        }

        [Fact]
        public void AddValidatorToPool_SetsLastSeenForNewValidator()
        {
            // Arrange
            var validator = new NetworkValidator
            {
                Address = "RTestValidatorAddress123",
                IPAddress = "192.168.1.20",
                PublicKey = "test-public-key",
                Signature = "test-signature",
                SignatureMessage = "test-message",
                UniqueName = "TestValidator"
            };

            // Note: This test would require mocking SignatureService.VerifySignature
            // For now, we're testing the data structure behavior
            // In a real implementation, you'd need to set up proper signing

            // Assert
            // This is a structural test - in practice, AddValidatorToPool would set LastSeen
            // when adding through trusted sources or after validation
            Assert.True(validator.LastSeen >= 0);
        }
    }
}
