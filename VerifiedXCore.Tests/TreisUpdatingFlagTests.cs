using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Services;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// AddBlock waits while Globals.TreisUpdating is set, so UpdateTreis must clear it however the apply ends.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TreisUpdatingFlagTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorFlag;

        public TreisUpdatingFlagTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"tuf_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorFlag = Globals.TreisUpdating;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            Globals.TreisUpdating = _priorFlag;
            StateWriteContext.Clear();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task ApplyThatThrows_StillClearsTreisUpdating()
        {
            // A block with no transaction list makes the apply throw part-way through.
            var block = new Block { Height = 5, StateRoot = "sr", Transactions = null! };

            await Assert.ThrowsAnyAsync<Exception>(() => StateData.UpdateTreis(block));
            Assert.False(Globals.TreisUpdating);
        }

        [Fact]
        public async Task AnApplyThatThrows_AfterTheBlockIsCommitted_CountsAsIncomplete()
        {
            // Block validation applies a committed block through this: a throw is an incomplete apply (false), which the
            // caller flags, instead of escaping to a catch that only logged.
            var block = new Block { Height = 5, StateRoot = "sr", Transactions = null! };

            Assert.False(await BlockValidatorService.ApplyStateAsync(block));
            Assert.False(Globals.TreisUpdating);
            Assert.True(await BlockValidatorService.ApplyStateAsync(new Block { Height = 6, StateRoot = "sr", Transactions = new List<Transaction>() }));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AStorageFaultInOneTransaction_IsReportedAsAFailedStep_ADataFaultIsNot(bool storageFault)
        {
            var prior = StateData.BlockStartFailedAtHeight;
            try
            {
                StateData.BlockStartFailedAtHeight = -1;
                StateData.TxApplyForTests = _ => throw (storageFault ? new System.IO.IOException("disk") : new InvalidOperationException("what the transaction says"));
                var block = new Block { Height = 7, StateRoot = "sr", Transactions = new List<Transaction> { new Transaction { Hash = "t", FromAddress = "xA", ToAddress = "xB", Amount = 1M } } };

                Assert.False(await StateData.UpdateTreis(block)); // either way the block did not apply in full

                Assert.Equal(storageFault ? 7 : -1, StateData.BlockStartFailedAtHeight);
            }
            finally
            {
                StateData.TxApplyForTests = null;
                StateData.BlockStartFailedAtHeight = prior;
            }
        }

        /// <summary>
        /// A write the *Safe wrappers swallow inside a block's apply: on a collection of the state and from this node's
        /// storage it makes the apply incomplete; a throw the transaction's data could cause, or one on a collection that
        /// is not state, changes nothing.
        /// </summary>
        private static async Task<(bool Applied, long FailedAt)> ApplyWithSwallowedWrite(Action? write, long height)
        {
            var prior = StateData.BlockStartFailedAtHeight;
            try
            {
                StateData.BlockStartFailedAtHeight = -1;
                StateData.TxApplyForTests = _ => write?.Invoke();
                var block = new Block { Height = height, StateRoot = "sr", Transactions = new List<Transaction> { new Transaction { Hash = $"t{height}", FromAddress = "xA", ToAddress = "xB", Amount = 1M } } };
                var applied = await StateData.UpdateTreis(block);
                return (applied, StateData.BlockStartFailedAtHeight);
            }
            finally
            {
                StateData.TxApplyForTests = null;
                StateData.BlockStartFailedAtHeight = prior;
            }
        }

        [Fact]
        public async Task AStorageFaultSwallowedOnAStateCollection_MakesTheApplyIncomplete()
        {
            var (applied, failedAt) = await ApplyWithSwallowedWrite(
                () => StateData.GetAccountStateTrei().Command<bool, AccountStateTrei>(() => throw new IOException("disk")), 8);
            Assert.False(applied);
            Assert.Equal(8, failedAt);
        }

        [Fact]
        public async Task ADataFault_OrAFaultOffTheStateCollections_ChangesNothing()
        {
            var baseline = await ApplyWithSwallowedWrite(null, 9);

            var dataFault = await ApplyWithSwallowedWrite(
                () => StateData.GetAccountStateTrei().Command<bool, AccountStateTrei>(() => throw new InvalidOperationException("what the transaction says")), 10);
            Assert.Equal(baseline.Applied, dataFault.Applied);
            Assert.Equal(-1, dataFault.FailedAt);

            var offList = await ApplyWithSwallowedWrite(
                () => DbContext.DB_AccountStateTrei.GetCollection("some_other_collection").Command<bool, LiteDB.BsonDocument>(() => throw new IOException("disk")), 11);
            Assert.Equal(baseline.Applied, offList.Applied);
            Assert.Equal(-1, offList.FailedAt);
        }

        [Fact]
        public async Task AnIntegrityFaultSwallowedOnAContractUpdate_MakesTheApplyIncomplete()
        {
            // An unwatched vault's ledger is written through SmartContractStateTrei.UpdateSmartContract (UpdateSafe); LiteDB's
            // page-integrity check fails as a plain Exception, which the wrapper swallows.
            var (applied, failedAt) = await ApplyWithSwallowedWrite(
                () => DbContext.DB_SmartContractStateTrei.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI)
                    .Command<bool, SmartContractStateTrei>(() => throw new Exception(StateData.LiteDbEnsurePrefix + "page type must be data page")), 13);
            Assert.False(applied);
            Assert.Equal(13, failedAt);
        }

        [Fact]
        public async Task AStorageFaultSwallowedOnTheShieldedPool_MakesTheApplyIncomplete()
        {
            var (applied, failedAt) = await ApplyWithSwallowedWrite(
                () => VerifiedXCore.Privacy.PrivacyDbContext.Nullifiers().Command<bool, VerifiedXCore.Models.Privacy.NullifierRecord>(() => throw new IOException("disk")), 12);
            Assert.False(applied);
            Assert.Equal(12, failedAt);
        }

        /// <summary>Reserve settlement runs outside UpdateTreis and counts the same faults on its own.</summary>
        private static async Task<bool> SettleWithSwallowedWrite(Action write)
        {
            var row = new ReserveTransactions
            {
                Id = Guid.NewGuid(), Hash = $"r{Guid.NewGuid():N}", FromAddress = "xRBXsender", ToAddress = "xB", Amount = 1M, Height = 20,
                ConfirmTimestamp = 1, TransactionType = TransactionType.TX, ReserveTransactionStatus = ReserveTransactionStatus.Pending,
            };
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(row);
            try
            {
                StateData.SettleRowForTests = _ => write();
                return await StateData.UpdateTreiFromReserve(new List<ReserveTransactions> { row }, new Block { Height = 21, Timestamp = 100 });
            }
            finally { StateData.SettleRowForTests = null; }
        }

        [Fact]
        public async Task ReserveSettlement_AStorageFaultSwallowedOnTheState_MarksItIncomplete()
        {
            Assert.False(await SettleWithSwallowedWrite(() => StateData.GetAccountStateTrei().Command<bool, AccountStateTrei>(() => throw new IOException("disk"))));
            Assert.True(StateTreiStatusService.NeedsRepair());
        }

        [Fact]
        public async Task ReserveSettlement_ADataFault_MarksNothing()
        {
            Assert.True(await SettleWithSwallowedWrite(() => StateData.GetAccountStateTrei().Command<bool, AccountStateTrei>(() => throw new InvalidOperationException("what the row says"))));
            Assert.False(StateTreiStatusService.NeedsRepair());
        }

        [Fact]
        public async Task NormalApply_ReturnsTrue_AndClearsTreisUpdating()
        {
            var block = new Block { Height = 5, StateRoot = "sr", Transactions = new List<Transaction>() };

            Assert.True(await StateData.UpdateTreis(block));
            Assert.False(Globals.TreisUpdating);
        }
    }
}
