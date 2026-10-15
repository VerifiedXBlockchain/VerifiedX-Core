using System;
using System.IO;
using System.Linq;
using LiteDB;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
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

        /// <summary>
        /// Free text a transaction names is not indexed: LiteDB refuses an index key over 1,023 bytes and the write is lost
        /// where the index exists (round 34). Indexes an earlier build made on such fields are dropped at start.
        /// </summary>
        [Fact]
        public void FreeTextFields_AreNotIndexed_AndALongValueIsWritten()
        {
            DbContext.DB_SmartContractStateTrei.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI).EnsureIndex(x => x.OwnerAddress);
            DbContext.DB_Wallet.GetCollection<TokenVote>(DbContext.RSRV_TOKEN_VOTE).EnsureIndex(x => x.TopicUID);
            DbContext.CloseDB();
            DbContext.Initialize();                                                              // a node that ran round 33
            var contractFields = IndexedFields(DbContext.DB_SmartContractStateTrei, DbContext.RSRV_SCSTATE_TREI);
            Assert.DoesNotContain("$.OwnerAddress", contractFields);
            Assert.DoesNotContain("$.TokenDetails.ContractOwner", contractFields);
            var voteFields = IndexedFields(DbContext.DB_Wallet, DbContext.RSRV_TOKEN_VOTE);
            Assert.DoesNotContain("$.TopicUID", voteFields);
            Assert.Contains("$.Address", voteFields);

            var longText = new string('A', 1100);
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = "long-owner", OwnerAddress = "xA", TokenDetails = new TokenDetails { ContractOwner = "xA" } });
            var contract = SmartContractStateTrei.GetSmartContractState("long-owner")!;
            contract.OwnerAddress = longText; contract.TokenDetails!.ContractOwner = longText;
            SmartContractStateTrei.UpdateSmartContract(contract);
            Assert.Equal(longText, SmartContractStateTrei.GetSmartContractState("long-owner")!.OwnerAddress);
            DbContext.DB_Wallet.GetCollection<TokenVote>(DbContext.RSRV_TOKEN_VOTE).Insert(new TokenVote { TopicUID = longText, Address = "xA", SmartContractUID = "long-owner" });
            Assert.Equal(1, DbContext.DB_Wallet.GetCollection<TokenVote>(DbContext.RSRV_TOKEN_VOTE).Count());
        }

        [Fact]
        public void BridgeLocks_AreNotIndexedByVault_AndStillFoundByIt()
        {
            var col = VBTCBridgeLockState.GetCollection();
            Assert.DoesNotContain("$.SmartContractUID", IndexedFields(DbContext.DB_VBTCWithdrawalRequests, VBTCBridgeLockState.CollectionName));
            col.Insert(new VBTCBridgeLockState { LockId = "a", SmartContractUID = "vault", OwnerAddress = "xA" });
            col.Insert(new VBTCBridgeLockState { LockId = "b", SmartContractUID = "other", OwnerAddress = "xB" });
            Assert.Equal(new[] { "xA" }, VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.OpenClaimants("vault").ToArray());
        }

        /// <summary>
        /// A request's contract UID is indexed only up to 200 characters: a longer one is written (a plain index lost the
        /// write) and still found; a contract's rows are those the collation matches, in the scan's order (round 34).
        /// </summary>
        [Fact]
        public void WithdrawalRequests_ByContract_AreTheRowsTheCollationMatches_LongOnesIncluded()
        {
            var col = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!;
            var padded = "vault" + new string('\u200B', 1100);
            col.Insert(new VBTCWithdrawalRequest { TransactionHash = "t1", SmartContractUID = "VAULT", RequestorAddress = "x1" });
            col.Insert(new VBTCWithdrawalRequest { TransactionHash = "t2", SmartContractUID = "other", RequestorAddress = "x2" });
            col.Insert(new VBTCWithdrawalRequest { TransactionHash = "t3", SmartContractUID = new string('v', 1100), RequestorAddress = "x3" });
            col.Insert(new VBTCWithdrawalRequest { TransactionHash = "t4", SmartContractUID = "vault", RequestorAddress = "x4" });
            col.Insert(new VBTCWithdrawalRequest { TransactionHash = "t5", SmartContractUID = null, RequestorAddress = "x5" });
            Assert.Equal(5, col.Count());                                          // the 1,100-character UID was written

            List<string> Scan(string uid) => col.Query().ToList().OrderBy(x => x.Id)
                .Where(x => x.SmartContractUID != null && DbContext.DB_VBTCWithdrawalRequests.Collation.Compare(x.SmartContractUID, uid) == 0)
                .Select(x => x.TransactionHash).ToList();
            foreach (var uid in new[] { "vault", "Vault", "other", new string('v', 1100), new string('V', 1100), "none", padded })
                Assert.Equal(Scan(uid), VBTCWithdrawalRequest.RowsOfContract(col, uid).Select(x => x.TransactionHash).ToList());
            Assert.Equal(new[] { "t1", "t4" }, VBTCWithdrawalRequest.RowsOfContract(col, "vault").Select(x => x.TransactionHash).ToArray());
        }

        [Fact]
        public void WithdrawalRequests_AreIndexed_AndTheFirstOfSeveralIsStillTheEarliestRow()
        {
            var fields = IndexedFields(DbContext.DB_VBTCWithdrawalRequests, DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);
            Assert.Contains("$.TransactionHash", fields);
            Assert.DoesNotContain("$.SmartContractUID", fields);                   // unbounded before the path-audit rules (round 34)
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
