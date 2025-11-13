using Xunit;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using ReserveBlockCore;

namespace VerfiedXCore.Tests
{
    public class TransactionTimestampValidationTests
    {
        [Fact]
        public async Task IsTxTimestampStale_RecentTransaction_ShouldNotBeStale()
        {
            // Arrange - Transaction created 30 minutes ago
            var currentTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                Timestamp = currentTime - (30 * 60), // 30 minutes ago
                FromAddress = "test_address",
                ToAddress = "test_address_2",
                Amount = 10.0M,
                Fee = 0.1M
            };

            // Act
            var isStale = await TransactionData.IsTxTimestampStale(tx, allowHistorical: false);

            // Assert
            Assert.False(isStale, "Transaction within 60 minute window should not be stale");
        }

        [Fact]
        public async Task IsTxTimestampStale_OldTransaction_ShouldBeStale()
        {
            // Arrange - Transaction created 61 minutes ago (exceeds MaxTxAgeSeconds)
            var currentTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                Timestamp = currentTime - (Globals.MaxTxAgeSeconds + 60), // 61 minutes ago
                FromAddress = "test_address",
                ToAddress = "test_address_2",
                Amount = 10.0M,
                Fee = 0.1M
            };

            // Act
            var isStale = await TransactionData.IsTxTimestampStale(tx, allowHistorical: false);

            // Assert
            Assert.True(isStale, "Transaction older than MaxTxAgeSeconds should be stale");
        }

        [Fact]
        public async Task IsTxTimestampStale_FutureClockedTransaction_ShouldBeStale()
        {
            // Arrange - Transaction timestamp is 3 minutes in the future (exceeds MaxFutureSkewSeconds)
            var currentTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                Timestamp = currentTime + (Globals.MaxFutureSkewSeconds + 60), // 3 minutes in future
                FromAddress = "test_address",
                ToAddress = "test_address_2",
                Amount = 10.0M,
                Fee = 0.1M
            };

            // Act
            var isStale = await TransactionData.IsTxTimestampStale(tx, allowHistorical: false);

            // Assert
            Assert.True(isStale, "Transaction with timestamp too far in future should be stale");
        }

        [Fact]
        public async Task IsTxTimestampStale_WithinFutureSkewTolerance_ShouldNotBeStale()
        {
            // Arrange - Transaction timestamp is 1 minute in the future (within tolerance)
            var currentTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                Timestamp = currentTime + 60, // 1 minute in future
                FromAddress = "test_address",
                ToAddress = "test_address_2",
                Amount = 10.0M,
                Fee = 0.1M
            };

            // Act
            var isStale = await TransactionData.IsTxTimestampStale(tx, allowHistorical: false);

            // Assert
            Assert.False(isStale, "Transaction with minor clock skew should not be stale");
        }

        [Fact]
        public async Task IsTxTimestampStale_HistoricalTransaction_ShouldNeverBeStale()
        {
            // Arrange - Very old transaction from years ago
            var currentTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                Timestamp = currentTime - (365 * 24 * 60 * 60), // 1 year ago
                FromAddress = "test_address",
                ToAddress = "test_address_2",
                Amount = 10.0M,
                Fee = 0.1M
            };

            // Act
            var isStale = await TransactionData.IsTxTimestampStale(tx, allowHistorical: true);

            // Assert
            Assert.False(isStale, "Historical transactions should never be marked as stale during block sync");
        }

        [Fact]
        public async Task IsTxTimestampStale_ExactlyAtMaxAge_ShouldNotBeStale()
        {
            // Arrange - Transaction exactly at the 60 minute boundary
            var currentTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                Timestamp = currentTime - Globals.MaxTxAgeSeconds,
                FromAddress = "test_address",
                ToAddress = "test_address_2",
                Amount = 10.0M,
                Fee = 0.1M
            };

            // Act
            var isStale = await TransactionData.IsTxTimestampStale(tx, allowHistorical: false);

            // Assert
            Assert.False(isStale, "Transaction exactly at MaxTxAgeSeconds should not be stale");
        }

        [Fact]
        public async Task IsTxTimestampStale_OneSecondOverMaxAge_ShouldBeStale()
        {
            // Arrange - Transaction one second past the limit
            var currentTime = TimeUtil.GetTime();
            var tx = new Transaction
            {
                Timestamp = currentTime - (Globals.MaxTxAgeSeconds + 1),
                FromAddress = "test_address",
                ToAddress = "test_address_2",
                Amount = 10.0M,
                Fee = 0.1M
            };

            // Act
            var isStale = await TransactionData.IsTxTimestampStale(tx, allowHistorical: false);

            // Assert
            Assert.True(isStale, "Transaction one second past MaxTxAgeSeconds should be stale");
        }

        [Fact]
        public void GlobalConstants_ShouldHaveCorrectValues()
        {
            // Assert - Verify the configuration constants match the requirements
            Assert.Equal(3600, Globals.MaxTxAgeSeconds); // 60 minutes
            Assert.Equal(120, Globals.MaxFutureSkewSeconds); // 2 minutes
        }
    }
}
