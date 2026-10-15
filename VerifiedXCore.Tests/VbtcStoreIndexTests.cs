using System;
using System.IO;
using System.Linq;
using LiteDB;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Stores every node reads in block validation and apply are indexed on the fields they are looked up by, so a lookup
    /// does not read every row ever stored; readers keep the order rows were read in before (fifth review, round 33).
    /// </summary>
    [Collection("DbContextSequential")]
    public class VbtcStoreIndexTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VbtcStoreIndexTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vsi_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private static string[] IndexedFields(LiteDatabase db, string collection) =>
            db.GetCollection("$indexes").Find(Query.EQ("collection", collection)).Select(d => d["expression"].AsString).ToArray();

        [Fact]
        public void BridgeLocks_AreIndexedByVault()
        {
            var col = VBTCBridgeLockState.GetCollection();
            Assert.Contains("$.SmartContractUID", IndexedFields(DbContext.DB_VBTCWithdrawalRequests, VBTCBridgeLockState.CollectionName));
            col.Insert(new VBTCBridgeLockState { LockId = "a", SmartContractUID = "vault", OwnerAddress = "xA" });
            col.Insert(new VBTCBridgeLockState { LockId = "b", SmartContractUID = "other", OwnerAddress = "xB" });
            Assert.Equal(new[] { "xA" }, VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.OpenClaimants("vault").ToArray());
        }

        [Fact]
        public void WithdrawalRequests_AreIndexed_AndTheFirstOfSeveralIsStillTheEarliestRow()
        {
            var fields = IndexedFields(DbContext.DB_VBTCWithdrawalRequests, DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);
            Assert.Contains("$.TransactionHash", fields);
            Assert.Contains("$.SmartContractUID", fields);
            Assert.Contains("$.RequestorAddress", fields);

            var col = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!;
            col.Insert(new VBTCWithdrawalRequest { TransactionHash = "req", SmartContractUID = "vault", RequestorAddress = "xA", Amount = 1M });
            col.Insert(new VBTCWithdrawalRequest { TransactionHash = "req", SmartContractUID = "vault", RequestorAddress = "xB", Amount = 2M });
            var first = col.Query().ToList().OrderBy(x => x.Id).First();
            first.TransactionHash = "moved"; col.Update(first);             // its index entry moves behind the other's
            first.TransactionHash = "req"; col.Update(first);
            Assert.Equal("xA", VBTCWithdrawalRequest.GetByTransactionHash("req", "vault")!.RequestorAddress);
            Assert.Equal("xA", VBTCWithdrawalRequest.GetByTransactionHash("req")!.RequestorAddress);
        }
    }
}
