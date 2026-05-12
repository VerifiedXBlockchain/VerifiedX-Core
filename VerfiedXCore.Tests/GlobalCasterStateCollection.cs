using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// xUnit collection used to serialize tests that mutate <see cref="ReserveBlockCore.Globals.BlockCasters"/>
    /// (and adjacent caster-pool globals like <see cref="ReserveBlockCore.Globals.NetworkValidators"/>,
    /// <see cref="ReserveBlockCore.Globals.KnownCasters"/>, <see cref="ReserveBlockCore.Globals.LastBlock"/>).
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
    public class GlobalCasterStateCollection
    {
    }
}
