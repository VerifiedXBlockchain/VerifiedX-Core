using VerifiedXCore.Data;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// xUnit collection used to serialize tests that mutate <see cref="VerifiedXCore.Globals.BlockCasters"/>
    /// (and adjacent caster-pool globals like <see cref="VerifiedXCore.Globals.NetworkValidators"/>,
    /// <see cref="VerifiedXCore.Globals.KnownCasters"/>, <see cref="VerifiedXCore.Globals.LastBlock"/>).
    ///
    /// Why: xUnit runs tests across different classes in parallel by default. Each of these
    /// tests carefully save/restore the affected globals in a try/finally, which is correct
    /// for a single test, but two tests running concurrently can still interleave their
    /// mutations and stomp on each other's snapshot. That manifested as
    /// <c>CasterPoolOverflowTests.AddBlockCasterIfRoomAndUnique_ConcurrentCalls_NeverExceedsCap</c>
    /// passing in isolation but failing in a full run when its baseline pool was being
    /// changed mid-test by a concurrent test class.
    ///
    /// Putting all caster-state-mutating test classes in one [Collection] with parallelization
    /// disabled forces them to run one-at-a-time, which is the simplest correct fix.
    /// </summary>
    [CollectionDefinition("GlobalCasterState", DisableParallelization = true)]
    public class GlobalCasterStateCollection : ICollectionFixture<ClosedConfigDbGuard>
    {
    }

    /// <summary>
    /// Runs once before the first test of the GlobalCasterState collection.
    ///
    /// The caster code reads its membership record through <see cref="DbContext.DB_Config"/>
    /// (<see cref="CasterMembershipStore"/>) and treats a null handle as "no record", which is what these tests
    /// rely on: none of them opens a database. A DbContextSequential class that ran earlier leaves that static
    /// handle pointing at a database it has closed, and LiteDB answers a query on a closed database with
    /// "Database lock timeout when entering in transaction mode after 00:01:00" (at once, not after a minute).
    /// xUnit orders the collections differently from run to run, so the same nineteen caster tests failed with
    /// that message whenever this collection came second, and passed whenever it came first or ran alone.
    /// </summary>
    public sealed class ClosedConfigDbGuard
    {
        public ClosedConfigDbGuard()
        {
            DbContext.DB_Config = null;
            CasterMembershipStore.InvalidateCache();
        }
    }
}
