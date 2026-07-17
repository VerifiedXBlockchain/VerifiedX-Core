using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;
using ReserveBlockCore;
using ReserveBlockCore.Data;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// REGRESSION GUARD: DbContext.Initialize() must open EVERY declared database. A database
    /// property that is declared but never assigned stays null for the process lifetime and can
    /// silently disable whole subsystems — exactly what happened when DB_Snapshot was added to
    /// the class and to MigrateDbNewChainRef() but not to Initialize(), leaving the entire
    /// snapshot fast-recovery layer inert with no error.
    /// </summary>
    [Collection("DbContextSequential")]
    public class DbContextInitializeTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public DbContextInitializeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"dbinit_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot; // GetDatabasePath → {CustomPath}RBX\Databases\
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            foreach (var prop in DatabaseProperties())
                try { prop.SetValue(null, null); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static PropertyInfo[] DatabaseProperties() =>
            typeof(DbContext)
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(LiteDatabase))
                .ToArray();

        [Fact]
        public void Initialize_OpensEveryDeclaredDatabase()
        {
            DbContext.Initialize();

            var unopened = DatabaseProperties()
                .Where(p => p.GetValue(null) == null)
                .Select(p => p.Name)
                .ToList();

            Assert.True(unopened.Count == 0,
                $"DbContext.Initialize() left these database properties null (declared but never opened): {string.Join(", ", unopened)}");

            // The snapshot DB file must exist on disk from startup — its absence was the
            // user-visible symptom of the original bug.
            var dbDir = Path.Combine(_tempRoot, "RBX", "Databases");
            Assert.True(File.Exists(Path.Combine(dbDir, DbContext.RSRV_DB_SNAPSHOT)),
                "rsrvsnapshot.db was not created by DbContext.Initialize().");
        }
    }
}
