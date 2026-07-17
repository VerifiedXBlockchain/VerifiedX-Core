using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// Test classes that reassign the static <c>ReserveBlockCore.Data.DbContext</c> database
    /// handles must run sequentially — parallel classes swapping the same statics corrupt each
    /// other's fixtures.
    /// </summary>
    [CollectionDefinition("DbContextSequential", DisableParallelization = true)]
    public class DbContextSequentialCollection
    {
    }
}
