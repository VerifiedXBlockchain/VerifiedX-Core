using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Block processing hands an accepted block to the shielded-note scanner's own queue: it never waits on a wallet's
    /// lock, so a manual scan or resync holding that lock for a long range does not hold up this node's blocks. The
    /// scanner itself waits for the lock and scans the block once it is free.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ShieldedScannerNeverBlocksBlocksTests : IDisposable
    {
        private const string SeedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ShieldedScannerNeverBlocksBlocksTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"scanner_lock_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task HandingABlockToTheScanner_ReturnsWhileTheWalletsLockIsHeld()
        {
            // A wallet the scanner trial-decrypts for, so it does take this wallet's lock for a private block.
            var keys = ShieldedHdDerivation.DeriveShieldedKeyMaterial(SeedHex, ShieldedAddressConstants.DefaultBip44CoinType, 17);
            ShieldedWalletService.Upsert(new ShieldedWallet { ShieldedAddress = keys.ZfxAddress, ViewingKey = keys.ViewingKey32, IsViewOnly = true });
            var height = Math.Max(ShieldedNoteAutoScanner.ProcessedThrough(), 0) + 1_000;
            var block = new Block
            {
                Height = height,
                Transactions = new List<Transaction> { new() { Hash = "p", TransactionType = TransactionType.VFX_SHIELD, Data = "{}" } },
            };

            var reachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ShieldedNoteAutoScanner.WaitingForWalletLock = address => { if (address == keys.ZfxAddress) reachedLock.TrySetResult(); };
            try
            {
                var held = ShieldedWalletLock.Acquire(keys.ZfxAddress); // as a long resync holds it
                try
                {
                    var handOff = Task.Run(() => ShieldedNoteAutoScanner.Enqueue(block));
                    Assert.Same(handOff, await Task.WhenAny(handOff, Task.Delay(5_000)));
                    await handOff; // completed, and without a fault
                    // The worker took up this block and is waiting on this wallet's lock.
                    Assert.Same(reachedLock.Task, await Task.WhenAny(reachedLock.Task, Task.Delay(5_000)));
                    Assert.True(ShieldedNoteAutoScanner.ProcessedThrough() < height);
                }
                finally { ShieldedWalletLock.Release(held); }

                // Once the lock is free the scanner scans the block for this wallet, inside this test's database.
                for (var i = 0; i < 100 && (ShieldedWalletService.FindByZfxAddress(keys.ZfxAddress)?.LastScannedBlock ?? 0) < height; i++)
                    await Task.Delay(50);
                Assert.True(ShieldedWalletService.FindByZfxAddress(keys.ZfxAddress)!.LastScannedBlock >= height);
                for (var i = 0; i < 100 && ShieldedNoteAutoScanner.ProcessedThrough() < height; i++)
                    await Task.Delay(50);
                Assert.True(ShieldedNoteAutoScanner.ProcessedThrough() >= height);
            }
            finally { ShieldedNoteAutoScanner.WaitingForWalletLock = null; }
        }
    }
}
