using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The burn watch's cursor says which block was read last by its hash, not only by its number. Before reading on, the
    /// watch compares that hash with the chain as two providers give it alike; when the chain holds another block there,
    /// it goes back to the newest earlier position the chain still holds and reads on from that. A provider whose
    /// backend does not hold the block a range ends at has not read the range, however empty its answer. Driven through
    /// the watch's own poll against providers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseCursorTests : BaseProviderTestBase
    {
        private static BridgeExitSyncState Cursor => BridgeExitSyncState.GetOrCreate();

        private static void StartAt(long block)
        {
            var state = BridgeExitSyncState.GetOrCreate();
            state.LastScannedBlock = block;
            BridgeExitSyncState.Save(state);
        }

        [Fact]
        public async Task TheCursorKeepsTheHashOfTheBlockItReached_AndWhereItStoodBefore()
        {
            var all = Providers(2);
            all.At(5_000);
            StartAt(4_990);

            await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(5_000, Cursor.LastScannedBlock);
            Assert.Equal(all[0].HashAt(5_000), Cursor.LastScannedBlockHash);
            Assert.Empty(Cursor.Marks); // the start had no hash to keep

            all.At(5_010);
            await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(5_010, Cursor.LastScannedBlock);
            Assert.Equal(all[0].HashAt(5_010), Cursor.LastScannedBlockHash);
            var mark = Assert.Single(Cursor.Marks);
            Assert.Equal((5_000L, all[0].HashAt(5_000)), (mark.Block, mark.Hash));

            // A rescan sets the cursor to a block that was not read: no hash is kept for it, and no later position.
            BaseBridgeExitWatchService.RescanFromBlock(4_001);
            Assert.Equal(4_000, Cursor.LastScannedBlock);
            Assert.Null(Cursor.LastScannedBlockHash);
            Assert.Empty(Cursor.Marks);
        }

        /// <summary>
        /// The chain holds another block at the cursor than the one read, and with it a burn below the cursor that was not
        /// there before. The watch goes back to the last position the chain still holds and reads the burn.
        /// </summary>
        [Fact]
        public async Task WhenTheChainHoldsAnotherBlockAtTheCursor_TheWatchGoesBack_AndReadsWhatIsThereNow()
        {
            var all = Providers(2);
            StartAt(4_990);
            all.At(5_000, finalized: 4_995);
            await BaseBridgeExitWatchService.PollOnce();
            all.Finalize(5_000);
            await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(5_000, Cursor.LastScannedBlock);

            // Blocks 4,996 to 5,000 are replaced: block 5,000 has another hash, and a burn sits in the new block 4,998.
            foreach (var p in all) p.BlockHashes[5_000] = FakeBaseRpc.BlockHash("another block at 5000");
            all.AddBurn(NewBurn(), Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_998);
            all.At(5_010);

            var (taken, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();

            Assert.Equal(1, taken); // read from the position kept at 4,995 on
            Assert.Equal(5_010, scannedTo);
            Assert.Equal(all[0].HashAt(5_010), Cursor.LastScannedBlockHash);
            Assert.DoesNotContain(Cursor.Marks, m => m.Block == 5_000);
        }

        /// <summary>When the chain holds none of the positions kept either, the watch goes back the stretch a restart reads again.</summary>
        [Fact]
        public async Task WhenTheChainHoldsNoneOfThePositionsKept_TheWatchGoesBackTheRestartStretch()
        {
            var all = Providers(2);
            StartAt(4_990);
            all.At(5_000, finalized: 4_995);
            await BaseBridgeExitWatchService.PollOnce();
            all.Finalize(5_000);
            await BaseBridgeExitWatchService.PollOnce();

            foreach (var p in all)
            {
                p.BlockHashes[4_995] = FakeBaseRpc.BlockHash("another block at 4995");
                p.BlockHashes[5_000] = FakeBaseRpc.BlockHash("another block at 5000");
            }

            var (_, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();

            // From the start of the chain here (the stretch is longer than this chain), one span on.
            Assert.True(scannedTo < 4_995, $"read on from {scannedTo}");
            Assert.True(Cursor.LastScannedBlock < 4_995);
            Assert.DoesNotContain(Cursor.Marks, m => m.Block is 4_995 or 5_000);
        }

        /// <summary>One provider's word does not move the cursor back: two that name the block as it was read decide.</summary>
        [Fact]
        public async Task OneProviderNamingAnotherBlockAtTheCursor_DoesNotMoveIt()
        {
            var all = Providers(3);
            all.At(5_000);
            StartAt(4_990);
            await BaseBridgeExitWatchService.PollOnce();
            all[0].BlockHashes[5_000] = FakeBaseRpc.BlockHash("another block at 5000");
            all.At(5_010);

            var (_, scannedTo, _) = await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(5_010, scannedTo);
            Assert.Contains(Cursor.Marks, m => m.Block == 5_000 && m.Hash == all[1].HashAt(5_000));

            // With only that provider and one other left, the two do not name the block alike: nothing is read, and the
            // cursor stays (that is the providers' failing, and the watch waits for them).
            all[2].Down = true;
            all[0].BlockHashes[5_010] = FakeBaseRpc.BlockHash("another block at 5010");
            all.At(5_020);
            await Assert.ThrowsAsync<BaseProviderException>(() => BaseBridgeExitWatchService.PollOnce());
            Assert.Equal(5_010, Cursor.LastScannedBlock);
        }

        /// <summary>
        /// Three providers: one answers ranges of 100 blocks at most, one of 200, and the third names a finalized block
        /// but does not hold the blocks the ranges end at, so its reads never count. The watch reads at a span the two
        /// that work both take, and moves on. Sized by the third's untouched span as well, the range stayed at what only
        /// the 200-block provider reads, and the cursor stopped with two providers working.
        /// </summary>
        [Fact]
        public async Task TwoWorkingProvidersWithDifferentCaps_KeepTheWatchMoving_BesideOneThatCannotReadTheRange()
        {
            var all = Providers(3);
            all.At(5_000);
            all[0].LogRangeCap = 100;
            all[1].LogRangeCap = 200;
            all[2].HeldUpTo = 3_000; // it gives 5,000 as final, and holds no block the watch reads to
            StartAt(4_000);
            all.AddBurn(NewBurn(), Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_900);

            var taken = 0;
            for (var i = 0; i < 80 && Cursor.LastScannedBlock < 5_000; i++)
                taken += (await BaseBridgeExitWatchService.PollOnce()).Processed;

            Assert.Equal(5_000, Cursor.LastScannedBlock);
            Assert.True(taken >= 1);
            Assert.InRange(BaseBridgeExitWatchService.CurrentSpan, 1, 100); // what both working providers take
            Assert.True(all[2].CallsTo("eth_getLogs") > 0);                 // the third was asked all along; its reads did not count
        }

        /// <summary>
        /// The providers give 5,000 as final, but the backends that answer for blocks and logs hold the chain only up to
        /// 4,992: they answer the range with no logs and no error. That is not "no burns": the cursor stays until the
        /// providers hold the block the range ends at, and the burn in it is then read.
        /// </summary>
        [Fact]
        public async Task AnEmptyAnswerFromBackendsBehindTheHead_DoesNotMoveTheCursor()
        {
            var all = Providers(2);
            all.At(5_000);
            StartAt(4_990);
            all.AddBurn(NewBurn(), Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_995);
            foreach (var p in all) p.HeldUpTo = 4_992;

            var taken = 0;
            for (var i = 0; i < 3; i++) taken += (await BaseBridgeExitWatchService.PollOnce()).Processed;
            Assert.Equal(0, taken);
            Assert.Equal(4_990, Cursor.LastScannedBlock);
            Assert.True(all[0].CallsTo("eth_getLogs") > 0); // it was asked, and what it answered did not count

            // One provider catches up: its read counts, the other's still does not, and one read does not pass the range.
            all[0].HeldUpTo = null;
            taken = (await BaseBridgeExitWatchService.PollOnce()).Processed;
            Assert.Equal(1, taken); // the burn is taken from the provider that holds it
            Assert.Equal(4_990, Cursor.LastScannedBlock);

            all[1].HeldUpTo = null;
            await BaseBridgeExitWatchService.PollOnce();
            Assert.Equal(5_000, Cursor.LastScannedBlock);
            Assert.Equal(all[0].HashAt(5_000), Cursor.LastScannedBlockHash);
        }
    }
}
