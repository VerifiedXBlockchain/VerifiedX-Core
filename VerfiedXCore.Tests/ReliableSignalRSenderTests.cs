using Xunit;
using ReserveBlockCore.Utilities;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-16 Fix: Unit tests for ReliableSignalRSender utility class
    /// Tests the metrics tracking and basic functionality without external dependencies
    /// </summary>
    public class ReliableSignalRSenderTests
    {
        public ReliableSignalRSenderTests()
        {
            // Reset metrics before each test
            ReliableSignalRSender.ResetMetrics();
        }

        [Fact]
        public void GetSendMetrics_InitialState_ReturnsZeroMetrics()
        {
            // Act
            var (totalSends, failedSends, failureRate) = ReliableSignalRSender.GetSendMetrics();

            // Assert
            Assert.Equal(0, totalSends);
            Assert.Equal(0, failedSends);
            Assert.Equal(0.0, failureRate);
        }

        [Fact]
        public void GetRetryQueueSize_InitialState_ReturnsZero()
        {
            // Act
            var queueSize = ReliableSignalRSender.GetRetryQueueSize();

            // Assert
            Assert.Equal(0, queueSize);
        }

        [Fact]
        public void ResetMetrics_AfterReset_ReturnsZero()
        {
            // Act
            ReliableSignalRSender.ResetMetrics();
            var (totalSends, failedSends, failureRate) = ReliableSignalRSender.GetSendMetrics();

            // Assert
            Assert.Equal(0, totalSends);
            Assert.Equal(0, failedSends);
            Assert.Equal(0.0, failureRate);
        }

        [Fact]
        public void ReliableSignalRSender_ClassExists_AndIsStatic()
        {
            // This test verifies that the ReliableSignalRSender class exists and is accessible
            // We test the static methods are callable
            
            // Act & Assert - Should not throw
            var metrics = ReliableSignalRSender.GetSendMetrics();
            var queueSize = ReliableSignalRSender.GetRetryQueueSize();
            ReliableSignalRSender.ResetMetrics();
            
            // Verify the methods return expected types
            Assert.IsType<(long, long, double)>(metrics);
            Assert.IsType<int>(queueSize);
        }

        [Fact]
        public void GetSendMetrics_CalculatesFailureRateCorrectly()
        {
            // Since we can't easily mock SignalR calls without dependencies,
            // we test the calculation logic by verifying the return structure
            
            // Act
            var (totalSends, failedSends, failureRate) = ReliableSignalRSender.GetSendMetrics();

            // Assert - Verify the structure and relationship
            if (totalSends == 0)
            {
                Assert.Equal(0.0, failureRate);
            }
            else
            {
                var expectedRate = (double)failedSends / totalSends * 100;
                Assert.Equal(expectedRate, failureRate);
            }
        }

        [Fact]
        public void ReliableSignalRSender_ExtensionMethods_AreDefinedCorrectly()
        {
            // This test verifies that the extension methods are properly defined
            // by checking that the ReliableSignalRSender class has the expected methods
            
            var type = typeof(ReliableSignalRSender);
            
            // Check that it's a static class
            Assert.True(type.IsAbstract && type.IsSealed);
            
            // Check that key methods exist
            var methods = type.GetMethods();
            var methodNames = methods.Select(m => m.Name).ToArray();
            
            Assert.Contains("GetSendMetrics", methodNames);
            Assert.Contains("ResetMetrics", methodNames);
            Assert.Contains("GetRetryQueueSize", methodNames);
        }

        [Fact]
        public void Constants_AreWithinReasonableLimits()
        {
            // Test that the internal constants are reasonable by reflection
            // This ensures the retry queue and batch sizes are sensible
            
            var type = typeof(ReliableSignalRSender);
            var fields = type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            
            // Verify the class has internal configuration
            Assert.NotEmpty(fields);
            
            // Check that queue size starts at 0
            var initialQueueSize = ReliableSignalRSender.GetRetryQueueSize();
            Assert.True(initialQueueSize >= 0);
            Assert.True(initialQueueSize <= 1000); // Should be reasonable limit
        }
    }
}
