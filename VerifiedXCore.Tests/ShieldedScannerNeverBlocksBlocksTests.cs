using System.Collections.Generic;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Block processing hands an accepted block to the shielded-note scanner's own queue: it never waits on a wallet's
    /// lock, so a manual scan or resync holding that lock for a long range does not hold up this node's blocks.
    /// </summary>
    [Collection("PrivacySequential")]
    public class ShieldedScannerNeverBlocksBlocksTests
    {
        [Fact]
        public async Task HandingABlockToTheScanner_ReturnsWhileAWalletsLockIsHeld()
        {
            var held = ShieldedWalletLock.Acquire("zfx-test-wallet-held-by-a-resync");
            try
            {
                var block = new Block
                {
                    Height = 42,
                    Transactions = new List<Transaction> { new() { Hash = "p", TransactionType = TransactionType.VFX_SHIELD } },
                };
                var handOff = Task.Run(() => ShieldedNoteAutoScanner.Enqueue(block));
                Assert.Same(handOff, await Task.WhenAny(handOff, Task.Delay(5_000)));
            }
            finally { ShieldedWalletLock.Release(held); }
        }
    }
}
