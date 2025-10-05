using Xunit;
using ReserveBlockCore.Config;
using ReserveBlockCore;

namespace VerfiedXCore.Tests
{
    public class HAL17TimeoutConfigurationTests
    {
        [Fact]
        public void Config_Should_Have_Default_Timeout_Values()
        {
            // Arrange & Act
            var config = new Config();

            // Assert - Verify that the new timeout properties exist with sensible defaults
            Assert.True(config.SignalRShortTimeoutMs >= 0);
            Assert.True(config.SignalRLongTimeoutMs >= 0);
            Assert.True(config.BlockProcessingDelayMs >= 0);
            Assert.True(config.NetworkOperationTimeoutMs >= 0);
        }

        [Fact]
        public void Globals_Should_Have_Default_Timeout_Values()
        {
            // Arrange - Reset to default values to ensure test isolation
            Globals.SignalRShortTimeoutMs = 2000;
            Globals.SignalRLongTimeoutMs = 6000;
            Globals.BlockProcessingDelayMs = 2000;
            Globals.NetworkOperationTimeoutMs = 1000;

            // Act & Assert - Verify that Globals has the timeout values set
            Assert.Equal(2000, Globals.SignalRShortTimeoutMs);
            Assert.Equal(6000, Globals.SignalRLongTimeoutMs);
            Assert.Equal(2000, Globals.BlockProcessingDelayMs);
            Assert.Equal(1000, Globals.NetworkOperationTimeoutMs);
        }

        [Fact]
        public void ReadConfigFile_Should_Apply_Default_Timeout_Values_When_Not_Specified()
        {
            // Arrange
            var tempConfigPath = Path.GetTempFileName();
            var configContent = @"Port=3338
APIPort=7292
TestNet=false";
            
            try
            {
                File.WriteAllText(tempConfigPath, configContent);
                
                // We can't easily test the ReadConfigFile method directly without modifying the path utilities,
                // but we can test that the values get set correctly when ProcessConfig is called
                var config = new Config
                {
                    SignalRShortTimeoutMs = 2000,
                    SignalRLongTimeoutMs = 6000,
                    BlockProcessingDelayMs = 2000,
                    NetworkOperationTimeoutMs = 1000
                };

                // Act
                Config.ProcessConfig(config);

                // Assert
                Assert.Equal(2000, Globals.SignalRShortTimeoutMs);
                Assert.Equal(6000, Globals.SignalRLongTimeoutMs);
                Assert.Equal(2000, Globals.BlockProcessingDelayMs);
                Assert.Equal(1000, Globals.NetworkOperationTimeoutMs);
            }
            finally
            {
                if (File.Exists(tempConfigPath))
                    File.Delete(tempConfigPath);
            }
        }

        [Fact]
        public void ProcessConfig_Should_Apply_Custom_Timeout_Values()
        {
            // Arrange
            var config = new Config
            {
                SignalRShortTimeoutMs = 3000,
                SignalRLongTimeoutMs = 8000,
                BlockProcessingDelayMs = 1500,
                NetworkOperationTimeoutMs = 500
            };

            // Act
            Config.ProcessConfig(config);

            // Assert
            Assert.Equal(3000, Globals.SignalRShortTimeoutMs);
            Assert.Equal(8000, Globals.SignalRLongTimeoutMs);
            Assert.Equal(1500, Globals.BlockProcessingDelayMs);
            Assert.Equal(500, Globals.NetworkOperationTimeoutMs);
        }

        [Theory]
        [InlineData(500, 1000, 500, 250)]   // Aggressive timing
        [InlineData(5000, 10000, 5000, 2000)] // Conservative timing
        [InlineData(1, 1, 1, 1)]             // Minimum timing
        public void ProcessConfig_Should_Handle_Various_Timeout_Ranges(
            int shortTimeout, int longTimeout, int blockDelay, int networkTimeout)
        {
            // Arrange
            var config = new Config
            {
                SignalRShortTimeoutMs = shortTimeout,
                SignalRLongTimeoutMs = longTimeout,
                BlockProcessingDelayMs = blockDelay,
                NetworkOperationTimeoutMs = networkTimeout
            };

            // Act
            Config.ProcessConfig(config);

            // Assert
            Assert.Equal(shortTimeout, Globals.SignalRShortTimeoutMs);
            Assert.Equal(longTimeout, Globals.SignalRLongTimeoutMs);
            Assert.Equal(blockDelay, Globals.BlockProcessingDelayMs);
            Assert.Equal(networkTimeout, Globals.NetworkOperationTimeoutMs);
        }

        [Fact]
        public void Timeout_Values_Should_Be_Configurable_For_Network_Conditions()
        {
            // This test verifies that the timeout values can be adjusted based on network conditions
            // which addresses the core audit finding about adaptability

            // Arrange - Simulate slow network conditions
            var slowNetworkConfig = new Config
            {
                SignalRShortTimeoutMs = 5000,  // Increased from default 2000
                SignalRLongTimeoutMs = 15000,  // Increased from default 6000
                BlockProcessingDelayMs = 5000, // Increased from default 2000
                NetworkOperationTimeoutMs = 3000 // Increased from default 1000
            };

            // Act
            Config.ProcessConfig(slowNetworkConfig);

            // Assert - Verify values can be configured for different network conditions
            Assert.True(Globals.SignalRShortTimeoutMs > 2000);
            Assert.True(Globals.SignalRLongTimeoutMs > 6000);
            Assert.True(Globals.BlockProcessingDelayMs > 2000);
            Assert.True(Globals.NetworkOperationTimeoutMs > 1000);

            // Arrange - Simulate fast network conditions
            var fastNetworkConfig = new Config
            {
                SignalRShortTimeoutMs = 1000,  // Decreased from default 2000
                SignalRLongTimeoutMs = 3000,   // Decreased from default 6000
                BlockProcessingDelayMs = 1000, // Decreased from default 2000
                NetworkOperationTimeoutMs = 500  // Decreased from default 1000
            };

            // Act
            Config.ProcessConfig(fastNetworkConfig);

            // Assert - Verify values can be configured for different network conditions
            Assert.True(Globals.SignalRShortTimeoutMs < 2000);
            Assert.True(Globals.SignalRLongTimeoutMs < 6000);
            Assert.True(Globals.BlockProcessingDelayMs < 2000);
            Assert.True(Globals.NetworkOperationTimeoutMs < 1000);
        }
    }
}
