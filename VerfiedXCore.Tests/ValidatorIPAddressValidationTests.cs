using Xunit;
using ReserveBlockCore.Utilities;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// FIND-007 Fix: Unit tests for validator IP address validation (SSRF protection)
    /// Tests the ValidateValidatorIPAddress method to ensure only public IPs are allowed
    /// </summary>
    public class ValidatorIPAddressValidationTests
    {
        [Theory]
        [InlineData("1.1.1.1")]           // Cloudflare public DNS
        [InlineData("8.8.8.8")]           // Google public DNS
        [InlineData("45.142.120.5")]      // Random public IP
        [InlineData("100.64.0.1")]        // Carrier-grade NAT (should be allowed)
        [InlineData("203.0.113.1")]       // TEST-NET-3 (documentation only, but valid format)
        public void ValidateValidatorIPAddress_ValidPublicIPs_ReturnsTrue(string ipAddress)
        {
            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(ipAddress, out string errorMessage);

            // Assert
            Assert.True(result, $"Expected {ipAddress} to be valid but got error: {errorMessage}");
            Assert.Empty(errorMessage);
        }

        [Theory]
        [InlineData("127.0.0.1", "Loopback addresses")]              // Loopback
        [InlineData("127.0.0.100", "Loopback addresses")]            // Loopback range
        [InlineData("127.255.255.254", "Loopback addresses")]        // Loopback range end
        [InlineData("10.0.0.1", "Private IP addresses")]             // RFC 1918 - 10.0.0.0/8
        [InlineData("10.255.255.255", "Private IP addresses")]       // RFC 1918 - 10.0.0.0/8 end
        [InlineData("172.16.0.1", "Private IP addresses")]           // RFC 1918 - 172.16.0.0/12
        [InlineData("172.31.255.255", "Private IP addresses")]       // RFC 1918 - 172.16.0.0/12 end
        [InlineData("192.168.0.1", "Private IP addresses")]          // RFC 1918 - 192.168.0.0/16
        [InlineData("192.168.255.255", "Private IP addresses")]      // RFC 1918 - 192.168.0.0/16 end
        [InlineData("169.254.0.1", "Link-local addresses")]          // Link-local
        [InlineData("169.254.169.254", "Link-local addresses")]      // AWS metadata service
        [InlineData("224.0.0.1", "Multicast addresses")]             // Multicast
        [InlineData("239.255.255.255", "Multicast addresses")]       // Multicast end
        [InlineData("0.0.0.0", "Reserved IP address ranges")]        // Reserved
        [InlineData("240.0.0.1", "Reserved IP address ranges")]      // Reserved
        [InlineData("255.255.255.255", "Reserved IP address ranges")] // Broadcast
        public void ValidateValidatorIPAddress_DangerousIPs_ReturnsFalse(string ipAddress, string expectedErrorContains)
        {
            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(ipAddress, out string errorMessage);

            // Assert
            Assert.False(result, $"Expected {ipAddress} to be rejected but it was accepted");
            Assert.Contains(expectedErrorContains, errorMessage, StringComparison.OrdinalIgnoreCase);
        }


        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ValidateValidatorIPAddress_NullOrEmpty_ReturnsFalse(string input)
        {
            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(input, out string errorMessage);

            // Assert
            Assert.False(result);
            Assert.Contains("cannot be null or empty", errorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateValidatorIPAddress_IPv6_ReturnsFalse()
        {
            // Currently IPv6 is not supported - this test ensures it's rejected
            // Arrange
            string ipv6 = "2001:0db8:85a3:0000:0000:8a2e:0370:7334";

            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(ipv6, out string errorMessage);

            // Assert
            Assert.False(result);
            Assert.Contains("IPv6", errorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateValidatorIPAddress_ExtremelyLongString_ReturnsFalse()
        {
            // Arrange
            string longString = new string('1', 100);

            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(longString, out string errorMessage);

            // Assert
            Assert.False(result);
            Assert.NotEmpty(errorMessage);
        }

        [Theory]
        [InlineData("1.1.1.1/32")]           // CIDR notation
        [InlineData("1.1.1.1 ")]            // Trailing space
        [InlineData(" 1.1.1.1")]            // Leading space
        [InlineData("1.1.1.1\n")]           // Newline
        [InlineData("1.1.1.1\t")]           // Tab
        public void ValidateValidatorIPAddress_IPWithWhitespaceOrSpecialChars_ReturnsFalse(string input)
        {
            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(input, out string errorMessage);

            // Assert
            Assert.False(result, $"Expected {input} to be rejected");
        }

        [Fact]
        public void ValidateValidatorIPAddress_ErrorMessageProvided_ContainsUsefulInformation()
        {
            // Arrange
            string privateIP = "192.168.1.1";

            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(privateIP, out string errorMessage);

            // Assert
            Assert.False(result);
            Assert.NotEmpty(errorMessage);
            Assert.Contains("private", errorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("public", errorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("172.15.255.255")]    // Just below RFC1918 range
        [InlineData("172.32.0.0")]        // Just above RFC1918 range
        [InlineData("169.253.255.255")]   // Just below link-local
        [InlineData("169.255.0.0")]       // Just above link-local
        [InlineData("9.255.255.255")]     // Just below 10.0.0.0/8
        [InlineData("11.0.0.0")]          // Just above 10.0.0.0/8
        [InlineData("192.167.255.255")]   // Just below 192.168.0.0/16
        [InlineData("192.169.0.0")]       // Just above 192.168.0.0/16
        public void ValidateValidatorIPAddress_BoundaryPublicIPs_ReturnsTrue(string ipAddress)
        {
            // These are public IPs just outside private ranges
            // Act
            var result = InputValidationHelper.ValidateValidatorIPAddress(ipAddress, out string errorMessage);

            // Assert
            Assert.True(result, $"Expected boundary public IP {ipAddress} to be valid but got error: {errorMessage}");
        }
    }
}
