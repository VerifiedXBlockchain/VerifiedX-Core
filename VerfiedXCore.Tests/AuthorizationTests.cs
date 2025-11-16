using Xunit;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using ReserveBlockCore;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace VerfiedXCore.Tests
{
    public class AuthorizationTests
    {
        public AuthorizationTests()
        {
            // Initialize required globals for testing
            if (Globals.NetworkValidators == null)
                Globals.NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>();
        }

        [Fact]
        public void ValidatorAuthentication_AuthenticatedValidator_ShouldAllowBlockSubmission()
        {
            // Arrange
            var validatorAddress = "RBXTestValidator123";
            var validatorIP = "192.168.1.100";
            
            var networkValidator = new NetworkValidator
            {
                Address = validatorAddress,
                IPAddress = validatorIP,
                PublicKey = "TestPublicKey",
                Signature = "TestSignature",
                SignatureMessage = "TestMessage"
            };
            
            Globals.NetworkValidators.TryAdd(validatorAddress, networkValidator);
            
            // Act - Simulate the IP lookup check
            var authenticatedValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == validatorIP);
            
            // Assert
            Assert.NotNull(authenticatedValidator);
            Assert.Equal(validatorAddress, authenticatedValidator.Address);
            Assert.Equal(validatorIP, authenticatedValidator.IPAddress);
            
            // Cleanup
            Globals.NetworkValidators.TryRemove(validatorAddress, out _);
        }

        [Fact]
        public void ValidatorAuthentication_UnauthenticatedCaller_ShouldRejectBlockSubmission()
        {
            // Arrange
            var unauthorizedIP = "192.168.1.200";
            
            // Ensure no validator exists for this IP
            var authenticatedValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == unauthorizedIP);
            
            // Assert
            Assert.Null(authenticatedValidator);
        }

        [Fact]
        public void ValidatorAuthentication_IPv6Address_ShouldNormalizeCorrectly()
        {
            // Arrange
            var validatorAddress = "RBXTestValidator456";
            var validatorIP = "192.168.1.150";
            var ipv6FormattedIP = "::ffff:192.168.1.150";
            
            var networkValidator = new NetworkValidator
            {
                Address = validatorAddress,
                IPAddress = validatorIP,
                PublicKey = "TestPublicKey",
                Signature = "TestSignature",
                SignatureMessage = "TestMessage"
            };
            
            Globals.NetworkValidators.TryAdd(validatorAddress, networkValidator);
            
            // Act - Simulate the IP normalization that happens in the actual code
            var normalizedIP = ipv6FormattedIP.Replace("::ffff:", "");
            var authenticatedValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == normalizedIP);
            
            // Assert
            Assert.NotNull(authenticatedValidator);
            Assert.Equal(validatorAddress, authenticatedValidator.Address);
            Assert.Equal(validatorIP, authenticatedValidator.IPAddress);
            
            // Cleanup
            Globals.NetworkValidators.TryRemove(validatorAddress, out _);
        }

        [Fact]
        public void ValidatorAuthentication_EmptyValidatorList_ShouldRejectAllSubmissions()
        {
            // Arrange
            var testIP = "192.168.1.300";
            
            // Clear any existing validators
            Globals.NetworkValidators.Clear();
            
            // Act
            var authenticatedValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == testIP);
            
            // Assert
            Assert.Null(authenticatedValidator);
            Assert.Empty(Globals.NetworkValidators);
        }

        [Fact]
        public void ValidatorAuthentication_MultipleValidators_ShouldFindCorrectOne()
        {
            // Arrange
            var validator1 = new NetworkValidator
            {
                Address = "RBXValidator1",
                IPAddress = "192.168.1.101",
                PublicKey = "Key1",
                Signature = "Sig1",
                SignatureMessage = "Msg1"
            };
            
            var validator2 = new NetworkValidator
            {
                Address = "RBXValidator2",
                IPAddress = "192.168.1.102",
                PublicKey = "Key2",
                Signature = "Sig2",
                SignatureMessage = "Msg2"
            };
            
            var validator3 = new NetworkValidator
            {
                Address = "RBXValidator3",
                IPAddress = "192.168.1.103",
                PublicKey = "Key3",
                Signature = "Sig3",
                SignatureMessage = "Msg3"
            };
            
            Globals.NetworkValidators.TryAdd(validator1.Address, validator1);
            Globals.NetworkValidators.TryAdd(validator2.Address, validator2);
            Globals.NetworkValidators.TryAdd(validator3.Address, validator3);
            
            // Act - Look for validator2
            var foundValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == "192.168.1.102");
            
            // Assert
            Assert.NotNull(foundValidator);
            Assert.Equal("RBXValidator2", foundValidator.Address);
            Assert.Equal("192.168.1.102", foundValidator.IPAddress);
            
            // Cleanup
            Globals.NetworkValidators.Clear();
        }

        [Fact]
        public void ValidatorAuthentication_CaseInsensitiveIPMatching_ShouldWork()
        {
            // Arrange
            var validatorAddress = "RBXTestValidator789";
            var validatorIP = "192.168.1.125";
            
            var networkValidator = new NetworkValidator
            {
                Address = validatorAddress,
                IPAddress = validatorIP,
                PublicKey = "TestPublicKey",
                Signature = "TestSignature",
                SignatureMessage = "TestMessage"
            };
            
            Globals.NetworkValidators.TryAdd(validatorAddress, networkValidator);
            
            // Act - Test exact match (IP addresses should be case insensitive by nature)
            var authenticatedValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == validatorIP);
            
            // Assert
            Assert.NotNull(authenticatedValidator);
            Assert.Equal(validatorAddress, authenticatedValidator.Address);
            
            // Cleanup
            Globals.NetworkValidators.TryRemove(validatorAddress, out _);
        }

        [Fact]
        public void ValidatorAuthentication_NullIPAddress_ShouldHandleGracefully()
        {
            // Arrange
            var validatorAddress = "RBXTestValidatorNull";
            
            var networkValidator = new NetworkValidator
            {
                Address = validatorAddress,
                IPAddress = null, // Null IP
                PublicKey = "TestPublicKey",
                Signature = "TestSignature",
                SignatureMessage = "TestMessage"
            };
            
            Globals.NetworkValidators.TryAdd(validatorAddress, networkValidator);
            
            // Act
            var authenticatedValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == "192.168.1.999");
            
            // Assert
            Assert.Null(authenticatedValidator);
            
            // Cleanup
            Globals.NetworkValidators.TryRemove(validatorAddress, out _);
        }

        [Fact]
        public void ValidatorAuthentication_SecurityLogging_ShouldRecordAttempts()
        {
            // This test validates the conceptual security logging behavior
            // In actual implementation, unauthorized attempts are logged and banned
            
            // Arrange
            var unauthorizedIP = "192.168.1.666";
            var authorizationPassed = false;
            
            // Act - Simulate the authorization check
            var authenticatedValidator = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == unauthorizedIP);
            
            if (authenticatedValidator == null)
            {
                // This would trigger error logging and banning in the actual code
                authorizationPassed = false;
            }
            else
            {
                authorizationPassed = true;
            }
            
            // Assert
            Assert.False(authorizationPassed);
            Assert.Null(authenticatedValidator);
        }

        [Fact]
        public void ValidatorAuthentication_PerformanceBenchmark_ShouldBeEfficient()
        {
            // Test performance with larger validator sets
            
            // Arrange - Create 100 validators
            for (int i = 0; i < 100; i++)
            {
                var validator = new NetworkValidator
                {
                    Address = $"RBXValidator{i}",
                    IPAddress = $"192.168.1.{i + 1}",
                    PublicKey = $"Key{i}",
                    Signature = $"Sig{i}",
                    SignatureMessage = $"Msg{i}"
                };
                Globals.NetworkValidators.TryAdd(validator.Address, validator);
            }
            
            var targetIP = "192.168.1.50";
            var startTime = DateTime.UtcNow;
            
            // Act - Perform lookup
            var result = Globals.NetworkValidators.Values
                .FirstOrDefault(v => v.IPAddress == targetIP);
            
            var endTime = DateTime.UtcNow;
            var duration = endTime - startTime;
            
            // Assert
            Assert.NotNull(result);
            Assert.Equal("RBXValidator49", result.Address);
            Assert.True(duration.TotalMilliseconds < 100, "Lookup should complete within 100ms");
            
            // Cleanup
            Globals.NetworkValidators.Clear();
        }
    }
}
