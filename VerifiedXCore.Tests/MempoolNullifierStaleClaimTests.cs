using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Privacy;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A nullifier claim held by a TX that has left the mempool without releasing it must not keep the notes unspendable.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MempoolNullifierStaleClaimTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly TimeSpan _priorGrace;

        public MempoolNullifierStaleClaimTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"mnsc_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorGrace = MempoolNullifierTracker.StaleClaimGrace;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            foreach (var h in new[] { "held", "taker", "pending", "fresh" })
                MempoolNullifierTracker.ReleaseClaimsForTxHash(h);
            MempoolNullifierTracker.StaleClaimGrace = _priorGrace;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string Nullifier(byte tag)
        {
            var b = new byte[32];
            b[0] = tag;
            b[31] = 0x5a;
            return Convert.ToBase64String(b);
        }

        private static void PutInMempool(string hash) =>
            TransactionData.GetPool().Insert(new Transaction { Hash = hash, FromAddress = "x", ToAddress = "x", TransactionType = TransactionType.VFX_PRIVATE_TRANSFER });

        [Fact]
        public void ClaimOfATxNoLongerInMempool_IsTakenOverAfterGrace_AndItsOtherClaimsAreReleased()
        {
            MempoolNullifierTracker.StaleClaimGrace = TimeSpan.Zero;
            var a = Nullifier(1);
            var b = Nullifier(2);
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("held", "VFX", new[] { a, b }, out var e1), e1);
            // "held" passed VerifyTX but never reached the mempool (or was removed without a release).

            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("taker", "VFX", new[] { a }, out var e2), e2);
            Assert.Equal("taker", Globals.MempoolNullifiers[MempoolNullifierTracker.MakeKey("VFX", a)]);
            Assert.DoesNotContain(Globals.MempoolNullifiers, kv => kv.Value == "held");
        }

        [Fact]
        public void ClaimOfATxStillInMempool_StillBlocksAConflictingSpend()
        {
            MempoolNullifierTracker.StaleClaimGrace = TimeSpan.Zero;
            var a = Nullifier(3);
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("pending", "VFX", new[] { a }, out var e1), e1);
            PutInMempool("pending");

            Assert.False(MempoolNullifierTracker.TryRegisterForMempool("taker", "VFX", new[] { a }, out var err));
            Assert.Contains("already spends", err);
            Assert.Equal("pending", Globals.MempoolNullifiers[MempoolNullifierTracker.MakeKey("VFX", a)]);
        }

        [Fact]
        public void FreshClaimNotYetInMempool_StillBlocksAConflictingSpend()
        {
            MempoolNullifierTracker.StaleClaimGrace = TimeSpan.FromMinutes(5);
            var a = Nullifier(4);
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("fresh", "VFX", new[] { a }, out var e1), e1);
            // Between VerifyTX and the mempool insert: not in the pool yet, but must still hold the note.

            Assert.False(MempoolNullifierTracker.TryRegisterForMempool("taker", "VFX", new[] { a }, out _));
            Assert.Equal("fresh", Globals.MempoolNullifiers[MempoolNullifierTracker.MakeKey("VFX", a)]);
        }

        [Fact]
        public void IsClaimedByPendingTx_IgnoresAStaleClaim_ButNotALiveOne()
        {
            MempoolNullifierTracker.StaleClaimGrace = TimeSpan.Zero;
            var gone = MempoolNullifierTracker.MakeKey("VFX", Nullifier(5));
            var live = MempoolNullifierTracker.MakeKey("VFX", Nullifier(6));
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("held", "VFX", new[] { Nullifier(5) }, out var e1), e1);
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("pending", "VFX", new[] { Nullifier(6) }, out var e2), e2);
            PutInMempool("pending");

            // "held" left the mempool without releasing its claim: its note is not being spent.
            Assert.False(MempoolNullifierTracker.IsClaimedByPendingTx(gone));
            Assert.True(MempoolNullifierTracker.IsClaimedByPendingTx(live));
            Assert.False(MempoolNullifierTracker.IsClaimedByPendingTx(MempoolNullifierTracker.MakeKey("VFX", Nullifier(7))));
        }

        [Fact]
        public void IsClaimedByPendingTx_CountsAFreshClaimNotYetInTheMempool()
        {
            MempoolNullifierTracker.StaleClaimGrace = TimeSpan.FromMinutes(5);
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("fresh", "VFX", new[] { Nullifier(8) }, out var e1), e1);
            Assert.True(MempoolNullifierTracker.IsClaimedByPendingTx(MempoolNullifierTracker.MakeKey("VFX", Nullifier(8))));
        }
    }
}
