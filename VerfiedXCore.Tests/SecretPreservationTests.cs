using System;
using System.IO;
using System.Threading.Tasks;
using LiteDB;
using ReserveBlockCore;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// REGRESSION GUARD: the old ResetTreis wiped ALL collections in DB_vBTC and DB_Shares,
    /// destroying FROST validator signing keys, peer key backups, and arbiter secret shares —
    /// local material that can NEVER be rebuilt from chain replay. These tests pin the invariant
    /// that no recovery path (WipeChainDerivedState, snapshot restore) touches secret-bearing
    /// collections. If a future change re-introduces a wholesale wipe, these fail in CI.
    ///
    /// DB layout mirrors production file boundaries where it matters: the SC-state and DecShop
    /// wipes clear ALL collections in their files, so they get isolated databases here exactly
    /// as they are isolated files in production.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SecretPreservationTests : IDisposable
    {
        private readonly string _mainPath, _scdecPath, _secretPath, _snapPath;
        private readonly LiteDatabase _main, _scdec, _secret, _snap;

        // Secret / local collections that must survive every recovery path.
        private const string FrostKeys = "rsrv_frost_validator_keys";
        private const string FrostBackups = "rsrv_frost_peer_backups";
        private const string BridgeLocks = "rsrv_bridge_locks"; // BridgeLockRecord.COLLECTION_NAME (local mint tracking)
        private const string ExitCursor = "rsrv_bridge_exit_sync";

        public SecretPreservationTests()
        {
            _mainPath = TempDb(); _scdecPath = TempDb(); _secretPath = TempDb(); _snapPath = TempDb();
            _main = Open(_mainPath);
            _scdec = Open(_scdecPath);
            _secret = Open(_secretPath);
            _snap = Open(_snapPath);

            DbContext.DB_AccountStateTrei = _main;
            DbContext.DB_WorldStateTrei = _main;
            DbContext.DB_DNR = _main;
            DbContext.DB_TopicTrei = _main;
            DbContext.DB_Vote = _main;
            DbContext.DB_Wallet = _main;
            DbContext.DB_Reserve = _main;
            DbContext.DB_TokenizedWithdrawals = _main;
            DbContext.DB_VBTCWithdrawalRequests = _main;

            // Wiped via GetCollectionNames loops in production files of their own:
            DbContext.DB_SmartContractStateTrei = _scdec;
            DbContext.DB_DecShopStateTrei = _scdec;

            // Secret-bearing files (mixed content in production):
            DbContext.DB_vBTC = _secret;
            DbContext.DB_Shares = _secret;
            DbContext.DB_Privacy = _secret;

            DbContext.DB_Snapshot = _snap;

            Globals.TreisUpdating = false;
            Globals.LastBlock = new Block { Height = 100 };
            StateWriteContext.Clear();
            StateTreiStatusService.SetSynced(100);
        }

        public void Dispose()
        {
            StateWriteContext.Clear();
            foreach (var db in new[] { _main, _scdec, _secret, _snap })
                try { db.Dispose(); } catch { }
            DbContext.DB_AccountStateTrei = null;
            DbContext.DB_WorldStateTrei = null;
            DbContext.DB_DNR = null;
            DbContext.DB_TopicTrei = null;
            DbContext.DB_Vote = null;
            DbContext.DB_Wallet = null;
            DbContext.DB_Reserve = null;
            DbContext.DB_TokenizedWithdrawals = null;
            DbContext.DB_VBTCWithdrawalRequests = null;
            DbContext.DB_SmartContractStateTrei = null;
            DbContext.DB_DecShopStateTrei = null;
            DbContext.DB_vBTC = null;
            DbContext.DB_Shares = null;
            DbContext.DB_Privacy = null;
            DbContext.DB_Snapshot = null;
            foreach (var p in new[] { _mainPath, _scdecPath, _secretPath, _snapPath })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        private static string TempDb() => Path.Combine(Path.GetTempPath(), $"secret_test_{Guid.NewGuid():N}.db");
        private static LiteDatabase Open(string p) => new LiteDatabase(new ConnectionString { Filename = p, Connection = ConnectionType.Direct });

        private void SeedSecrets()
        {
            _secret.GetCollection(FrostKeys).Insert(new BsonDocument { ["KeyPackage"] = "frost-key-material" });
            _secret.GetCollection(FrostBackups).Insert(new BsonDocument { ["Backup"] = "peer-backup" });
            _secret.GetCollection(DbContext.RSRV_SHARES).Insert(new BsonDocument { ["Share"] = "arbiter-secret" });
            _secret.GetCollection(PrivacyDbContext.PRIV_WALLETS).Insert(new BsonDocument { ["SpendKey"] = "shielded-secret" });
            _secret.GetCollection(BridgeLocks).Insert(new BsonDocument { ["LockId"] = "local-lock" });
            _secret.GetCollection(ExitCursor).Insert(new BsonDocument { ["LastScannedBlock"] = 42L });
            _main.GetCollection(DbContext.RSRV_RESERVE_ACCOUNTS).Insert(new BsonDocument { ["PrivateKey"] = "reserve-key" });
            _main.GetCollection(DbContext.RSRV_ACCOUNTS).Insert(new BsonDocument { ["Address"] = "local-wallet" });
        }

        private void SeedChainDerived()
        {
            _main.GetCollection(DbContext.RSRV_ASTATE_TREI).Insert(new BsonDocument { ["Key"] = "addr", ["Balance"] = 5m });
            _scdec.GetCollection(DbContext.RSRV_SCSTATE_TREI).Insert(new BsonDocument { ["SmartContractUID"] = "sc:1" });
            _main.GetCollection(DbContext.RSRV_BITCOIN_ADNR).Insert(new BsonDocument { ["Name"] = "btc.adnr" });
            _secret.GetCollection(DbContext.RSRV_VBTC_V2_CONTRACTS).Insert(new BsonDocument { ["SmartContractUID"] = "vbtc:1" });
            _secret.GetCollection(DbContext.RSRV_VBTC_V2_CANCELLATIONS).Insert(new BsonDocument { ["Id"] = 1 });
            _secret.GetCollection(PrivacyDbContext.PRIV_COMMITMENTS).Insert(new BsonDocument { ["Commitment"] = "c1" });
            _secret.GetCollection(PrivacyDbContext.PRIV_NULLIFIERS).Insert(new BsonDocument { ["Nullifier"] = "n1" });
        }

        private long Count(LiteDatabase db, string coll) => db.GetCollection(coll).Count();

        [Fact]
        public void WipeChainDerivedState_ClearsConsensusState_ButNeverSecrets()
        {
            SeedSecrets();
            SeedChainDerived();

            BlockRollbackUtility.WipeChainDerivedState();

            // Chain-derived: gone (rebuilt by replay/restore).
            Assert.Equal(0, Count(_main, DbContext.RSRV_ASTATE_TREI));
            Assert.Equal(0, Count(_scdec, DbContext.RSRV_SCSTATE_TREI));
            Assert.Equal(0, Count(_main, DbContext.RSRV_BITCOIN_ADNR));
            Assert.Equal(0, Count(_secret, DbContext.RSRV_VBTC_V2_CONTRACTS));
            Assert.Equal(0, Count(_secret, DbContext.RSRV_VBTC_V2_CANCELLATIONS));
            Assert.Equal(0, Count(_secret, PrivacyDbContext.PRIV_COMMITMENTS));
            Assert.Equal(0, Count(_secret, PrivacyDbContext.PRIV_NULLIFIERS));

            // Secrets/local: every single one survives. If any assertion here fails, a recovery
            // path has started destroying non-rebuildable key material — do NOT ship.
            Assert.Equal(1, Count(_secret, FrostKeys));
            Assert.Equal(1, Count(_secret, FrostBackups));
            Assert.Equal(1, Count(_secret, DbContext.RSRV_SHARES));
            Assert.Equal(1, Count(_secret, PrivacyDbContext.PRIV_WALLETS));
            Assert.Equal(1, Count(_secret, BridgeLocks));
            Assert.Equal(1, Count(_secret, ExitCursor));
            Assert.Equal(1, Count(_main, DbContext.RSRV_RESERVE_ACCOUNTS));
            Assert.Equal(1, Count(_main, DbContext.RSRV_ACCOUNTS));
        }

        [Fact]
        public async Task SnapshotRestore_NeverRollsBackOrTouchesSecrets()
        {
            SeedSecrets();
            SeedChainDerived();

            // Take a snapshot, then change a secret AFTER the snapshot was taken.
            await StateSnapshotService.UpdateCycleAsync(40);
            _secret.GetCollection(FrostKeys).Insert(new BsonDocument { ["KeyPackage"] = "key-created-after-snapshot" });

            // Full restore path: wipe + copy slot back (as SnapshotRestoreUtility does).
            var slot = StateSnapshotService.PickSlotForRestore(40);
            Assert.NotNull(slot);
            BlockRollbackUtility.WipeChainDerivedState();
            Assert.True(StateSnapshotService.RestoreSlotToLive(slot!.SlotId));

            // Chain-derived state came back from the snapshot...
            Assert.Equal(1, Count(_secret, DbContext.RSRV_VBTC_V2_CONTRACTS));
            Assert.Equal(1, Count(_main, DbContext.RSRV_ASTATE_TREI));
            Assert.Equal(1, Count(_secret, PrivacyDbContext.PRIV_COMMITMENTS));

            // ...but secrets were neither wiped NOR rolled back to their snapshot-time state:
            // the key created after the snapshot must still exist (proves the snapshot never
            // contained secret collections at all).
            Assert.Equal(2, Count(_secret, FrostKeys));
            Assert.Equal(1, Count(_secret, FrostBackups));
            Assert.Equal(1, Count(_secret, DbContext.RSRV_SHARES));
            Assert.Equal(1, Count(_secret, PrivacyDbContext.PRIV_WALLETS));
            Assert.Equal(1, Count(_secret, BridgeLocks));
            Assert.Equal(1, Count(_main, DbContext.RSRV_RESERVE_ACCOUNTS));
        }
    }
}
