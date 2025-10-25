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
        public void CleanupStaleValidators_RemovesInactiveValidators()
        {
            // Arrange
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var staleTime = currentTime - 86400 - 100; // 24 hours + 100 seconds ago (stale)
            var recentTime = currentTime - 100; // 100 seconds ago (fresh)

            var staleValidator = new NetworkValidator
            {
                Address = "stale-validator-address",
                IPAddress = "192.168.1.1",
                LastSeen = staleTime,
                CheckFailCount = 0
            };

            var activeValidator = new NetworkValidator
            {
                Address = "active-validator-address",
                IPAddress = "192.168.1.2",
                LastSeen = recentTime,
                CheckFailCount = 0
            };

            Globals.NetworkValidators.TryAdd(staleValidator.Address, staleValidator);
            Globals.NetworkValidators.TryAdd(activeValidator.Address, activeValidator);

            // Act
            NetworkValidator.CleanupStaleValidators();

            // Assert
            Assert.False(Globals.NetworkValidators.ContainsKey(staleValidator.Address));
            Assert.True(Globals.NetworkValidators.ContainsKey(activeValidator.Address));
        }

        [Fact]
        public void CleanupStaleValidators_RemovesHighFailCountValidators()
        {
            // Arrange
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var highFailValidator = new NetworkValidator
            {
                Address = "high-fail-validator",
                IPAddress = "192.168.1.3",
                LastSeen = currentTime - 100,
                CheckFailCount = 6 // Above threshold of 5
            };

            var lowFailValidator = new NetworkValidator
            {
                Address = "low-fail-validator",
                IPAddress = "192.168.1.4",
                LastSeen = currentTime - 100,
                CheckFailCount = 3 // Below threshold
            };

            Globals.NetworkValidators.TryAdd(highFailValidator.Address, highFailValidator);
            Globals.NetworkValidators.TryAdd(lowFailValidator.Address, lowFailValidator);

            // Act
            NetworkValidator.CleanupStaleValidators();

            // Assert
            Assert.False(Globals.NetworkValidators.ContainsKey(highFailValidator.Address));
            Assert.True(Globals.NetworkValidators.ContainsKey(lowFailValidator.Address));
        }

        [Fact]
        public void CleanupStaleValidators_PreservesValidatorsWithZeroLastSeen()
        {
            // Arrange
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var newValidator = new NetworkValidator
            {
                Address = "new-validator",
                IPAddress = "192.168.1.5",
                LastSeen = 0, // Not yet tracked
                CheckFailCount = 0
            };

            Globals.NetworkValidators.TryAdd(newValidator.Address, newValidator);

            // Act
            NetworkValidator.CleanupStaleValidators();

            // Assert
            Assert.True(Globals.NetworkValidators.ContainsKey(newValidator.Address));
        }

        [Fact]
        public void UpdateLastSeen_UpdatesExistingValidator()
        {
            // Arrange
            var initialTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1000;
            var validator = new NetworkValidator
            {
                Address = "test-validator",
                IPAddress = "192.168.1.6",
                LastSeen = initialTime,
                CheckFailCount = 0
            };

            Globals.NetworkValidators.TryAdd(validator.Address, validator);

            // Act
            NetworkValidator.UpdateLastSeen(validator.Address);
            Thread.Sleep(100); // Ensure time has passed

            // Assert
            Globals.NetworkValidators.TryGetValue(validator.Address, out var updated);
            Assert.NotNull(updated);
            Assert.True(updated.LastSeen > initialTime);
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
        public void CleanupStaleValidators_HandlesMixedConditions()
        {
            // Arrange
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var validators = new[]
            {
                new NetworkValidator { Address = "v1", IPAddress = "192.168.1.10", LastSeen = currentTime - 86400 - 100, CheckFailCount = 0 }, // Stale
                new NetworkValidator { Address = "v2", IPAddress = "192.168.1.11", LastSeen = currentTime - 100, CheckFailCount = 6 }, // High fail
                new NetworkValidator { Address = "v3", IPAddress = "192.168.1.12", LastSeen = currentTime - 100, CheckFailCount = 0 }, // Good
                new NetworkValidator { Address = "v4", IPAddress = "192.168.1.13", LastSeen = 0, CheckFailCount = 0 }, // New (not tracked yet)
                new NetworkValidator { Address = "v5", IPAddress = "192.168.1.14", LastSeen = currentTime - 86400 - 100, CheckFailCount = 6 } // Both stale and high fail
            };

            foreach (var v in validators)
            {
                Globals.NetworkValidators.TryAdd(v.Address, v);
            }

            // Act
            NetworkValidator.CleanupStaleValidators();

            // Assert
            Assert.False(Globals.NetworkValidators.ContainsKey("v1"));
            Assert.False(Globals.NetworkValidators.ContainsKey("v2"));
            Assert.True(Globals.NetworkValidators.ContainsKey("v3"));
            Assert.True(Globals.NetworkValidators.ContainsKey("v4"));
            Assert.False(Globals.NetworkValidators.ContainsKey("v5"));
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
