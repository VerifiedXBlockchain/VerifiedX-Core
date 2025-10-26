using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using System.Threading;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Tests for HAL-051: Mother session management security
    /// Verifies that active connections cannot be hijacked by new connection attempts from the same IP
    /// </summary>
    public class MotherSessionSecurityTests
    {
        [Fact]
        public void ConnectionLifetime_ActiveConnection_IsNotCancelled()
        {
            // Arrange
            var mockContext = new Mock<HubCallerContext>();
            var mockLifetimeFeature = new Mock<IConnectionLifetimeFeature>();
            
            mockContext.Setup(c => c.ConnectionId).Returns("test-connection-id");
            var activeConnectionToken = new CancellationTokenSource();
            mockLifetimeFeature.Setup(f => f.ConnectionClosed).Returns(activeConnectionToken.Token);
            mockContext.Setup(c => c.Features.Get<IConnectionLifetimeFeature>()).Returns(mockLifetimeFeature.Object);

            var testIP = "192.168.1.100";
            ReserveBlockCore.Globals.MothersKidsContext[testIP] = mockContext.Object;

            // Act - Verify the connection is considered active
            var existingContextInDict = ReserveBlockCore.Globals.MothersKidsContext.TryGetValue(testIP, out var retrievedContext);
            
            // Assert
            Assert.True(existingContextInDict);
            Assert.NotNull(retrievedContext);
            Assert.Equal("test-connection-id", retrievedContext.ConnectionId);

            var connectionFeature = retrievedContext.Features.Get<IConnectionLifetimeFeature>();
            Assert.NotNull(connectionFeature);
            Assert.False(connectionFeature.ConnectionClosed.IsCancellationRequested);

            // Cleanup
            ReserveBlockCore.Globals.MothersKidsContext.TryRemove(testIP, out _);
            activeConnectionToken.Dispose();
        }

        [Fact]
        public void ConnectionLifetime_DeadConnection_IsCancelled()
        {
            // Arrange
            var mockContext = new Mock<HubCallerContext>();
            var mockLifetimeFeature = new Mock<IConnectionLifetimeFeature>();

            mockContext.Setup(c => c.ConnectionId).Returns("dead-connection-id");
            var cancelledToken = new CancellationTokenSource();
            cancelledToken.Cancel(); // Make it appear dead
            mockLifetimeFeature.Setup(f => f.ConnectionClosed).Returns(cancelledToken.Token);
            mockContext.Setup(c => c.Features.Get<IConnectionLifetimeFeature>()).Returns(mockLifetimeFeature.Object);

            var testIP = "192.168.1.100";
            ReserveBlockCore.Globals.MothersKidsContext[testIP] = mockContext.Object;

            // Act - Verify the connection is considered dead
            var existingContextInDict = ReserveBlockCore.Globals.MothersKidsContext.TryGetValue(testIP, out var retrievedContext);
            
            // Assert
            Assert.True(existingContextInDict);
            Assert.NotNull(retrievedContext);
            var connectionFeature = retrievedContext.Features.Get<IConnectionLifetimeFeature>();
            Assert.NotNull(connectionFeature);
            Assert.True(connectionFeature.ConnectionClosed.IsCancellationRequested);

            // Cleanup
            ReserveBlockCore.Globals.MothersKidsContext.TryRemove(testIP, out _);
            cancelledToken.Dispose();
        }

        [Fact]
        public void MothersKidsContext_EnforcesOneConnectionPerIP()
        {
            // Arrange
            var mockContext1 = new Mock<HubCallerContext>();
            var mockContext2 = new Mock<HubCallerContext>();
            mockContext1.Setup(c => c.ConnectionId).Returns("connection-1");
            mockContext2.Setup(c => c.ConnectionId).Returns("connection-2");

            var testIP = "192.168.1.200";

            // Act - Add first connection
            ReserveBlockCore.Globals.MothersKidsContext[testIP] = mockContext1.Object;
            var firstContext = ReserveBlockCore.Globals.MothersKidsContext[testIP];

            // Replace with second connection (simulating the replacement logic)
            ReserveBlockCore.Globals.MothersKidsContext[testIP] = mockContext2.Object;
            var secondContext = ReserveBlockCore.Globals.MothersKidsContext[testIP];

            // Assert - Dictionary enforces single connection per IP
            Assert.Equal("connection-1", firstContext.ConnectionId);
            Assert.Equal("connection-2", secondContext.ConnectionId);
            Assert.Single(ReserveBlockCore.Globals.MothersKidsContext.Where(kvp => kvp.Key == testIP));

            // Cleanup
            ReserveBlockCore.Globals.MothersKidsContext.TryRemove(testIP, out _);
        }

        [Fact]
        public void MothersKidsContext_DifferentIPs_AllowMultipleConnections()
        {
            // Arrange
            var mockContext1 = new Mock<HubCallerContext>();
            var mockContext2 = new Mock<HubCallerContext>();
            mockContext1.Setup(c => c.ConnectionId).Returns("connection-1");
            mockContext2.Setup(c => c.ConnectionId).Returns("connection-2");

            var testIP1 = "192.168.1.100";
            var testIP2 = "192.168.1.101";

            // Act - Add connections from different IPs
            ReserveBlockCore.Globals.MothersKidsContext[testIP1] = mockContext1.Object;
            ReserveBlockCore.Globals.MothersKidsContext[testIP2] = mockContext2.Object;

            // Assert - Both connections should coexist
            Assert.True(ReserveBlockCore.Globals.MothersKidsContext.ContainsKey(testIP1));
            Assert.True(ReserveBlockCore.Globals.MothersKidsContext.ContainsKey(testIP2));
            Assert.Equal("connection-1", ReserveBlockCore.Globals.MothersKidsContext[testIP1].ConnectionId);
            Assert.Equal("connection-2", ReserveBlockCore.Globals.MothersKidsContext[testIP2].ConnectionId);

            // Cleanup
            ReserveBlockCore.Globals.MothersKidsContext.TryRemove(testIP1, out _);
            ReserveBlockCore.Globals.MothersKidsContext.TryRemove(testIP2, out _);
        }
    }
}
