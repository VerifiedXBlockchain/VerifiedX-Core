using System;
using System.IO;
using Newtonsoft.Json.Linq;
using VerifiedXCore.BrowserWalletServices;
using VerifiedXCore.Data;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The wallet's vBTC pool-state read never creates a pool row: for a contract with no pool it answers an empty pool
    /// (as the PrivacyV1 route does; Aaron's Round 49 (4) for #208 D16) and the pool-state collection is unchanged; for a
    /// contract with a pool it returns that pool as stored.
    /// </summary>
    [Collection("DbContextSequential")]
    public class WalletVbtcPoolStateReadTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public WalletVbtcPoolStateReadTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcpool_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static JObject Read(string scUID) => JObject.FromObject(WalletPrivacyVbtcService.GetVbtcShieldedPoolState(scUID));

        [Fact]
        public void AContractWithNoPool_IsAnEmptyPool_AndNoRowIsWritten()
        {
            var rowsBefore = PrivacyDbContext.PoolState().Count();

            var answer = Read("any-caller-chosen-uid:1");

            Assert.True(answer.Value<bool>("success"));
            Assert.Equal(0L, answer.Value<long>("totalCommitments"));
            Assert.Equal(0M, answer.Value<decimal>("totalShieldedSupply"));
            Assert.Null(ShieldedPoolService.GetState(VbtcPrivacyAsset.FormatAssetKey("any-caller-chosen-uid:1")));
            Assert.Equal(rowsBefore, PrivacyDbContext.PoolState().Count());
        }

        [Fact]
        public void AContractWithAPool_ReturnsItAsStored()
        {
            var asset = VbtcPrivacyAsset.FormatAssetKey("vbtc-contract:1");
            var pool = ShieldedPoolService.GetOrCreateState(asset);
            pool.TotalCommitments = 3;
            pool.TotalShieldedSupply = 0.25M;
            pool.LastUpdateHeight = 42;
            PrivacyDbContext.PoolState().Update(pool);
            var rowsBefore = PrivacyDbContext.PoolState().Count();

            var answer = Read("vbtc-contract:1");

            Assert.True(answer.Value<bool>("success"));
            Assert.Equal(asset, answer.Value<string>("assetType"));
            Assert.Equal(3, answer.Value<long>("totalCommitments"));
            Assert.Equal(0.25M, answer.Value<decimal>("totalShieldedSupply"));
            Assert.Equal(42, answer.Value<long>("lastUpdateHeight"));
            Assert.Equal(rowsBefore, PrivacyDbContext.PoolState().Count());
        }
    }
}
