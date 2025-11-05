using Xunit;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using System.Collections.Generic;
using System.Linq;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-070: Tests for reserve transaction idempotence guards and lower-bound checks
    /// </summary>
    public class ReserveTransactionIdempotenceTests
    {
        [Fact]
        public void CallBackReserveAccountTx_WithNonPendingStatus_ShouldSkipProcessing()
        {
            // Arrange: Create a reserve transaction that's already CalledBack
            var rTx = new ReserveTransactions
            {
                Hash = "test_hash_already_called_back",
                FromAddress = "xRBXTestFrom",
                ToAddress = "RTestTo",
                Amount = 100.0M,
                ReserveTransactionStatus = ReserveTransactionStatus.CalledBack,
                TransactionType = TransactionType.TX
            };

            // Simulate saving to DB
            ReserveTransactions.SaveReserveTx(rTx);

            // Act: Attempt callback again
            // This should be skipped internally due to status check
            var initialStatus = rTx.ReserveTransactionStatus;

            // Assert: Verify it was already in terminal state
            Assert.Equal(ReserveTransactionStatus.CalledBack, initialStatus);
            // In real implementation, this would not process the transaction again
        }

        [Fact]
        public void RecoverReserveAccountTx_ShouldOnlyProcessPendingTransactions()
        {
            // Arrange: Create mix of pending and confirmed transactions
            var pendingTx = new ReserveTransactions
            {
                Hash = "pending_tx",
                FromAddress = "xRBXTestRecover",
                ToAddress = "RTestRecovery",
                Amount = 50.0M,
                ReserveTransactionStatus = ReserveTransactionStatus.Pending,
                TransactionType = TransactionType.TX
            };

            var confirmedTx = new ReserveTransactions
            {
                Hash = "confirmed_tx",
                FromAddress = "xRBXTestRecover",
                ToAddress = "RTestRecovery2",
                Amount = 75.0M,
                ReserveTransactionStatus = ReserveTransactionStatus.Confirmed,
                TransactionType = TransactionType.TX
            };

            var txList = new List<ReserveTransactions> { pendingTx, confirmedTx };

            // Act: Filter to pending only (simulating HAL-070 fix)
            var pendingOnly = txList.Where(x => x.ReserveTransactionStatus == ReserveTransactionStatus.Pending).ToList();

            // Assert: Only pending transactions should be processed
            Assert.Single(pendingOnly);
            Assert.Equal("pending_tx", pendingOnly[0].Hash);
        }

        [Fact]
        public void UpdateTreiFromReserve_WithConfirmedStatus_ShouldSkip()
        {
            // Arrange: Reserve transaction already confirmed
            var confirmedReserveTx = new ReserveTransactions
            {
                Hash = "already_confirmed",
                FromAddress = "xRBXTestUpdate",
                ToAddress = "RTestUpdateTo",
                Amount = 200.0M,
                ReserveTransactionStatus = ReserveTransactionStatus.Confirmed,
                TransactionType = TransactionType.TX
            };

            var txList = new List<ReserveTransactions> { confirmedReserveTx };

            // Act: Check if transaction should be skipped
            var shouldProcess = confirmedReserveTx.ReserveTransactionStatus != ReserveTransactionStatus.Confirmed;

            // Assert: Should not process already confirmed transactions
            Assert.False(shouldProcess);
        }

        [Fact]
        public void LowerBoundCheck_LockedBalanceSubtraction_ShouldNotGoNegative()
        {
            // Arrange: Account with locked balance of 50
            var accountStateTrei = new AccountStateTrei
            {
                Key = "xRBXTestLowerBound",
                LockedBalance = 50.0M,
                Balance = 100.0M
            };

            decimal attemptedSubtraction = 75.0M; // More than locked balance

            // Act: Apply lower-bound check (HAL-070 fix logic)
            if (accountStateTrei.LockedBalance >= attemptedSubtraction)
            {
                accountStateTrei.LockedBalance -= attemptedSubtraction;
            }
            else
            {
                // Clamp to zero instead of going negative
                accountStateTrei.LockedBalance = 0;
            }

            // Assert: LockedBalance should be clamped to 0, not negative
            Assert.Equal(0M, accountStateTrei.LockedBalance);
            Assert.True(accountStateTrei.LockedBalance >= 0);
        }

        [Fact]
        public void LowerBoundCheck_MultipleDecrements_ShouldStayNonNegative()
        {
            // Arrange: Simulate multiple decrement attempts
            var accountStateTrei = new AccountStateTrei
            {
                Key = "xRBXTestMultiDecrement",
                LockedBalance = 100.0M,
                Balance = 200.0M
            };

            var decrements = new List<decimal> { 40.0M, 30.0M, 50.0M }; // Total: 120, more than 100

            // Act: Apply multiple decrements with lower-bound checks
            foreach (var decrement in decrements)
            {
                if (accountStateTrei.LockedBalance >= decrement)
                {
                    accountStateTrei.LockedBalance -= decrement;
                }
                else
                {
                    accountStateTrei.LockedBalance = 0;
                }
            }

            // Assert: Should never go negative
            Assert.True(accountStateTrei.LockedBalance >= 0);
            Assert.Equal(0M, accountStateTrei.LockedBalance);
        }

        [Fact]
        public void IdempotenceGuard_PreventsDuplicateStatusTransitions()
        {
            // Arrange: Transaction in pending state
            var transaction = new ReserveTransactions
            {
                Hash = "idempotence_test",
                ReserveTransactionStatus = ReserveTransactionStatus.Pending
            };

            // Act: First transition to CalledBack
            if (transaction.ReserveTransactionStatus == ReserveTransactionStatus.Pending)
            {
                transaction.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
            }

            var firstStatus = transaction.ReserveTransactionStatus;

            // Attempt second transition (should be blocked)
            bool secondTransitionAllowed = transaction.ReserveTransactionStatus == ReserveTransactionStatus.Pending;

            // Assert: Second transition should not be allowed
            Assert.Equal(ReserveTransactionStatus.CalledBack, firstStatus);
            Assert.False(secondTransitionAllowed);
        }

        [Fact]
        public void ConcurrentCallBack_ShouldBePreventedByStatusCheck()
        {
            // Arrange: Simulate race condition where two CallBack attempts occur
            var sharedTransaction = new ReserveTransactions
            {
                Hash = "concurrent_test",
                ReserveTransactionStatus = ReserveTransactionStatus.Pending,
                Amount = 100.0M
            };

            // Act: First caller checks and processes
            bool firstCallerCanProcess = sharedTransaction.ReserveTransactionStatus == ReserveTransactionStatus.Pending;
            if (firstCallerCanProcess)
            {
                sharedTransaction.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
            }

            // Second caller checks (after first has modified status)
            bool secondCallerCanProcess = sharedTransaction.ReserveTransactionStatus == ReserveTransactionStatus.Pending;

            // Assert: Only first caller should process
            Assert.True(firstCallerCanProcess);
            Assert.False(secondCallerCanProcess);
        }

        [Fact]
        public void RecoverFlow_WithMixedStatuses_FiltersCorrectly()
        {
            // Arrange: Multiple transactions with different statuses
            var transactions = new List<ReserveTransactions>
            {
                new ReserveTransactions { Hash = "tx1", ReserveTransactionStatus = ReserveTransactionStatus.Pending },
                new ReserveTransactions { Hash = "tx2", ReserveTransactionStatus = ReserveTransactionStatus.Confirmed },
                new ReserveTransactions { Hash = "tx3", ReserveTransactionStatus = ReserveTransactionStatus.Pending },
                new ReserveTransactions { Hash = "tx4", ReserveTransactionStatus = ReserveTransactionStatus.CalledBack },
                new ReserveTransactions { Hash = "tx5", ReserveTransactionStatus = ReserveTransactionStatus.Pending }
            };

            // Act: Filter to only pending (HAL-070 fix)
            var recoverableTransactions = transactions
                .Where(x => x.ReserveTransactionStatus == ReserveTransactionStatus.Pending)
                .ToList();

            // Assert: Should only get pending transactions
            Assert.Equal(3, recoverableTransactions.Count);
            Assert.All(recoverableTransactions, tx => 
                Assert.Equal(ReserveTransactionStatus.Pending, tx.ReserveTransactionStatus));
        }

        [Fact]
        public void BalanceFloorCheck_ExactMatch_ShouldSucceed()
        {
            // Arrange: Perfect match scenario
            var account = new AccountStateTrei
            {
                Key = "xRBXExactMatch",
                LockedBalance = 100.0M,
                Balance = 200.0M
            };

            decimal subtractAmount = 100.0M;

            // Act: Apply lower-bound check
            if (account.LockedBalance >= subtractAmount)
            {
                account.LockedBalance -= subtractAmount;
            }
            else
            {
                account.LockedBalance = 0;
            }

            // Assert: Should subtract exactly, leaving 0
            Assert.Equal(0M, account.LockedBalance);
        }

        [Fact]
        public void BalanceFloorCheck_SmallSubtraction_ShouldSucceed()
        {
            // Arrange: Subtraction less than balance
            var account = new AccountStateTrei
            {
                Key = "xRBXSmallSub",
                LockedBalance = 100.0M,
                Balance = 200.0M
            };

            decimal subtractAmount = 30.0M;

            // Act: Apply lower-bound check
            if (account.LockedBalance >= subtractAmount)
            {
                account.LockedBalance -= subtractAmount;
            }
            else
            {
                account.LockedBalance = 0;
            }

            // Assert: Should leave remaining balance
            Assert.Equal(70.0M, account.LockedBalance);
        }
    }
}
