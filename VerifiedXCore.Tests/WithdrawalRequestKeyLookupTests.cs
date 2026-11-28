using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A withdrawal request's rows under one (requester, UniqueId, contract) key are read through an index on a hash of
    /// the key that every row carries: the same rows, in the same order, as reading every row of the requester and
    /// keeping the matching ones, at a cost that does not grow with the requester's other rows, however long their keys.
    /// </summary>
    [Collection("DbContextSequential")]
    public class WithdrawalRequestKeyLookupTests : IDisposable
    {
        private const long Gate = 1000;
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _holder;
        private readonly string _vault;

        public WithdrawalRequestKeyLookupTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"wd_key_lookup_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.WithdrawalUniqueIdRulesHeight;
            Globals.WithdrawalUniqueIdRulesHeight = Gate;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = Gate + 10 };
            _holder = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 1000M, Nonce = 0 });
            _vault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = _vault, ContractData = VbtcTestContracts.VaultContractData(_vault, "xMinter", BtcDest),
                MinterAddress = "xMinter", OwnerAddress = "xMinter", Nonce = 0,
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.WithdrawalUniqueIdRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static LiteDB.ILiteCollection<VBTCWithdrawalRequest> Rows => VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!;

        /// <summary>The lookup as it was: every row of the requester read, the matching ones kept, by Id.</summary>
        private static List<long> FullRead(string? address, string? uniqueId, string? scUID) =>
            Rows.Query().Where(x => x.RequestorAddress == address && x.OriginalUniqueId == uniqueId && x.SmartContractUID == scUID)
                .ToList().OrderBy(x => x.Id).Select(x => x.Id).ToList();

        private static List<long> Indexed(string? address, string? uniqueId, string? scUID) =>
            VBTCWithdrawalRequest.RowsUnderKey(Rows, address, uniqueId, scUID).Select(x => x.Id).ToList();

        private static VBTCWithdrawalRequest Row(string? address, string? uniqueId, string? scUID, string hash = "") => new VBTCWithdrawalRequest
        {
            RequestorAddress = address!, OriginalUniqueId = uniqueId!, SmartContractUID = scUID!, TransactionHash = hash,
            Amount = 0.001M, BTCDestination = BtcDest, FeeRate = 10, Timestamp = 1, RequestBlockHeight = Gate,
            Status = VBTCWithdrawalStatus.Cancelled, IsCompleted = true,
        };

        private Transaction Request(string uniqueId, decimal amount)
        {
            var tx = new Transaction
            {
                FromAddress = _holder.Address, ToAddress = _holder.Address, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalRequest()", ContractUID = _vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = uniqueId }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _holder.Key, _holder.Pub);
            return tx;
        }

        [Fact]
        public void EveryKey_FindsTheRowsTheFullReadFound_InTheSameOrder()
        {
            var longId = new string('q', 400);
            var longVault = new string('v', 300) + ":1";
            var addresses = new string?[] { "xAlice", "XALICE", "xBob", "xAlice|u1", "", null };
            var uniqueIds = new string?[] { "u1", "U1", " u1 ", "u\u200d1", "u2", "u1|vault:1", "a|b", longId, longId.ToUpperInvariant(), "", null };
            var vaults = new string?[] { "vault:1", "VAULT:1", "vault:2", "1", longVault, "", null };

            // Every combination is stored, twice over (two rows under one key are read in Id order).
            var rows = new List<VBTCWithdrawalRequest>();
            for (var pass = 0; pass < 2; pass++)
                foreach (var a in addresses)
                    foreach (var u in uniqueIds)
                        foreach (var v in vaults)
                            rows.Add(Row(a, u, v, pass == 0 ? "" : $"h{rows.Count}"));
            Rows.InsertBulk(rows);

            var asked = 0;
            var found = 0;
            foreach (var a in addresses.Concat(new[] { " xalice ", "xCarol", "\u200d" }))
                foreach (var u in uniqueIds.Concat(new[] { "u\u200d\u200d1", "u3", longId + "\u200d", "\u200d" }))
                    foreach (var v in vaults.Concat(new[] { " vault:1", "vault:3", "\u200d" }))
                    {
                        var expected = FullRead(a, u, v);
                        Assert.Equal(expected, Indexed(a, u, v));
                        asked++;
                        found += expected.Count;
                    }
            Assert.True(asked > 900 && found > 2000, $"{asked} keys asked, {found} rows found");
        }

        private static LiteDB.ILiteCollection<LiteDB.BsonDocument> Raw => DbContext.DB_VBTCWithdrawalRequests.GetCollection(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);

        [Fact]
        public void TheKeyIsReadThroughItsIndex_AndSoIsTheCheckThatEveryRowCarriesItsHash()
        {
            var plan = Raw.Query().Where(LiteDB.Query.EQ("$.RequestKeyHash", new string('a', 64))).GetPlan();
            Assert.Equal("RequestKeyHash", plan["index"]["name"].AsString);
            Assert.StartsWith("INDEX SEEK", plan["index"]["mode"].AsString);
            Assert.False(plan.ContainsKey("filters")); // nothing left to check row by row: only the rows under the hash are read

            foreach (var ordered in new[] { Raw.Query().OrderBy("$.RequestKeyHashVersion"), Raw.Query().OrderByDescending("$.RequestKeyHashVersion") })
            {
                var check = ordered.Select("$.RequestKeyHashVersion").Limit(1).GetPlan();
                Assert.Equal("RequestKeyHashVersion", check["index"]["name"].AsString);
                Assert.False(check.ContainsKey("orderBy")); // the index gives the order: no row is sorted
            }
        }

        [Fact]
        public void EveryWayARowIsWritten_GivesItTheHashOfItsKeyAsStored()
        {
            var version = VBTCWithdrawalRequest.KeyHashVersion();
            Assert.NotNull(version);
            string Stored(long id, string field) => Raw.FindById(id)[field].AsString;

            // Inserted, with blanks the database trims: hashed as stored, so the key as stored finds it.
            Assert.True(VBTCWithdrawalRequest.Save(Row(" xAlice ", " u1 ", "vault:1", "h1")));
            var row = Rows.FindOne(x => x.TransactionHash == "h1");
            Assert.Equal(VBTCWithdrawalRequest.KeyHash("xAlice", "u1", "vault:1"), Stored(row.Id, "RequestKeyHash"));
            Assert.Equal(version, Stored(row.Id, "RequestKeyHashVersion"));
            Assert.Equal(new[] { row.Id }, Indexed("XALICE", "U1", "VAULT:1"));

            // Updated to another key: the hash follows.
            row.OriginalUniqueId = "u2";
            Assert.True(Rows.Update(row));
            Assert.Equal(VBTCWithdrawalRequest.KeyHash("xAlice", "u2", "vault:1"), Stored(row.Id, "RequestKeyHash"));
            Assert.Empty(Indexed("xAlice", "u1", "vault:1"));
            Assert.Equal(new[] { row.Id }, Indexed("xAlice", "u2", "vault:1"));

            // Written in bulk, and a row missing a part of its key.
            Rows.InsertBulk(new[] { Row("xBob", "u9", "vault:1", "h2"), Row("xBob", "", "vault:1", "h3") });
            Assert.Equal(VBTCWithdrawalRequest.KeyHash("xBob", "u9", "vault:1"), Stored(Rows.FindOne(x => x.TransactionHash == "h2").Id, "RequestKeyHash"));
            Assert.Equal(VBTCWithdrawalRequest.NoKeyHash, Stored(Rows.FindOne(x => x.TransactionHash == "h3").Id, "RequestKeyHash"));

            // The hash is the node's own: it is not part of a row sent as JSON.
            Assert.DoesNotContain("RequestKeyHash", JsonConvert.SerializeObject(row));
            Assert.DoesNotContain("RequestKeyHash", System.Text.Json.JsonSerializer.Serialize(row));
        }

        /// <summary>Removes the hash from every row and its indexes, as a database written by an earlier build has them.</summary>
        private static void AsAnEarlierBuildLeftIt()
        {
            foreach (var doc in Raw.FindAll().ToList())
            {
                doc.Remove("RequestKeyHash");
                doc.Remove("RequestKeyHashVersion");
                Raw.Update(doc);
            }
            Raw.DropIndex("RequestKeyHash");
            Raw.DropIndex("RequestKeyHashVersion");
        }

        [Fact]
        public void ADatabaseWrittenBeforeTheHash_HasEveryRowHashedWhenItIsOpened()
        {
            Rows.InsertBulk(Enumerable.Range(0, 500).Select(i => Row("xAlice", $"id{i % 100}", "vault:1", $"h{i}")));
            Rows.Insert(Row("xAlice", new string('\u20ac', 400), "vault:1", "long"));
            var before = FullRead("xAlice", "id7", "vault:1");
            Assert.Equal(5, before.Count);
            AsAnEarlierBuildLeftIt();
            Assert.DoesNotContain(IndexNames(), n => n.StartsWith("RequestKeyHash"));

            DbContext.CloseDB();
            DbContext.Initialize();

            Assert.Contains("RequestKeyHash", IndexNames());
            Assert.Contains("RequestKeyHashVersion", IndexNames());
            Assert.All(Raw.FindAll(), doc => Assert.Equal(VBTCWithdrawalRequest.KeyHashVersion(), doc["RequestKeyHashVersion"].AsString));
            Assert.Equal(before, Indexed("xAlice", "id7", "vault:1"));
            Assert.Equal(FullRead("xalice", "ID7", "VAULT:1"), Indexed("xalice", "ID7", "VAULT:1"));
            Assert.Single(Indexed("xAlice", new string('\u20ac', 400), "vault:1"));
        }

        [Fact]
        public void RowsThatArriveWithoutTheHash_OrWithAnotherComparisons_AreHashedBeforeTheIndexIsRead()
        {
            Rows.InsertBulk(Enumerable.Range(0, 200).Select(i => Row("xAlice", $"id{i}", "vault:1", $"h{i}")));

            // Rows put back from a copy made before the hash existed (a snapshot restore writes documents as they were).
            foreach (var doc in Raw.Find(LiteDB.Query.EQ("TransactionHash", "h7")).ToList())
            {
                doc.Remove("RequestKeyHash");
                doc.Remove("RequestKeyHashVersion");
                Raw.Update(doc);
            }
            Assert.Equal(FullRead("xAlice", "id7", "vault:1"), Indexed("xAlice", "id7", "vault:1"));
            Assert.Single(Indexed("xAlice", "id7", "vault:1"));

            // Rows hashed under another comparison (the platform's comparison data changed): their hash is not trusted.
            foreach (var doc in Raw.FindAll().ToList())
            {
                doc["RequestKeyHash"] = new string('0', 64);
                doc["RequestKeyHashVersion"] = doc["TransactionHash"].AsString == "h9" ? "v0older" : "v9newer";
                Raw.Update(doc);
            }
            Assert.Single(Indexed("xAlice", "id9", "vault:1"));
            Assert.Single(Indexed("xAlice", "id150", "vault:1"));
            Assert.All(Raw.FindAll(), doc => Assert.Equal(VBTCWithdrawalRequest.KeyHashVersion(), doc["RequestKeyHashVersion"].AsString));
            Assert.Equal(VBTCWithdrawalRequest.KeyHash("xAlice", "id9", "vault:1"), Raw.FindOne(LiteDB.Query.EQ("TransactionHash", "h9"))["RequestKeyHash"].AsString);
        }

        [Fact]
        public void ARowWithoutAHash_IsNeverTakenForHashed_AndAnIndexAnEarlierBuildMadeIsDropped()
        {
            // A row carries the version only together with its hash: one stored without a hash has no version, so the
            // check that every row is hashed finds it.
            var row = Row("xAlice", "u1", "vault:1", "h1");
            Assert.NotNull(row.RequestKeyHash);
            Assert.Equal(VBTCWithdrawalRequest.KeyHashVersion(), row.RequestKeyHashVersion);
            Rows.Insert(row);
            var doc = Raw.FindById(row.Id);
            doc.Remove("RequestKeyHash"); // as if the hash could not be made when it was written
            doc.Remove("RequestKeyHashVersion");
            doc["OriginalUniqueId"] = "";  // and a part of the key stored empty, which the mapper never writes
            Raw.Update(doc);
            Raw.EnsureIndex(VBTCWithdrawalRequest.RetiredKeyIndexName, "$.RequestorAddress + '|' + $.OriginalUniqueId");

            Assert.Empty(Indexed("xAlice", "u1", "vault:1"));
            Assert.Equal(VBTCWithdrawalRequest.NoKeyHash, Raw.FindById(row.Id)["RequestKeyHash"].AsString);

            VBTCWithdrawalRequest.EnsureKeyHashes(DbContext.DB_VBTCWithdrawalRequests);
            Assert.DoesNotContain(VBTCWithdrawalRequest.RetiredKeyIndexName, IndexNames());
        }

        private static List<string> IndexNames() =>
            DbContext.DB_VBTCWithdrawalRequests.GetCollection("$indexes").Find(LiteDB.Query.EQ("collection", DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS))
                .Select(d => d["name"].AsString).ToList();

        /// <summary>
        /// A requester whose 20,000 earlier rows all have long keys, UniqueIds of 300 characters, half of them in
        /// characters of three bytes: one more request, long-keyed or not, is judged without reading them.
        /// </summary>
        [Fact]
        public void ARequesterWithManyLongAndMultibyteUniqueIds_IsJudgedWithoutReadingThem()
        {
            const int earlier = 20_000;
            string LongId(int i) => (i % 2 == 0 ? new string('q', 290) : new string('\u20ac', 290)) + i.ToString("D10");
            Rows.InsertBulk(Enumerable.Range(0, earlier).Select(i => Row(_holder.Address, LongId(i), _vault, $"mined-{i}")));
            Assert.Equal(earlier, Rows.Count());

            Assert.Contains("mined-19999", VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, LongId(19999), _vault, "new"));
            Assert.Contains("mined-19998", VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, LongId(19998).ToUpperInvariant(), _vault, "new"));
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, LongId(19999), _vault, "mined-19999"));
            Assert.Equal(FullRead(_holder.Address, LongId(123), _vault), Indexed(_holder.Address, LongId(123), _vault));

            var oneRead = Allocated(() => Assert.Equal(earlier, Rows.Find(x => x.RequestorAddress == _holder.Address).Count()));
            var checks = Allocated(() =>
            {
                for (var i = 0; i < 1_290; i++)
                {
                    Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, LongId(earlier + i), _vault, "new")); // long, unused
                    Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, $"short-{i}", _vault, "new"));
                }
            });
            Assert.True(checks < 100 * oneRead, $"2,580 checks allocated {checks:N0} bytes; one read of the requester's rows allocates {oneRead:N0}");
        }

        /// <summary>A requester with 50,000 earlier rows: one more request is judged without reading them.</summary>
        [Fact]
        public async Task ARequesterWithFiftyThousandRows_IsJudgedWithoutReadingThem()
        {
            const int earlier = 50_000;
            Rows.InsertBulk(Enumerable.Range(0, earlier).Select(i => Row(_holder.Address, $"used-{i}", _vault, $"mined-{i}")));
            Assert.Equal(earlier, Rows.Count());

            // The rule itself: an unused UniqueId passes, a used one is refused, as the full read decides.
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, "fresh", _vault, "new"));
            Assert.Contains("mined-49999", VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, "used-49999", _vault, "new"));
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, "used-49999", _vault, "mined-49999"));
            Assert.Equal(FullRead(_holder.Address, "used-123", _vault), Indexed(_holder.Address, "used-123", _vault));

            // A block's worth of checks (about 1,290 requests fit one), measured by what they allocate: reading the
            // requester's rows for each check allocated every one of them, 1,290 times over.
            var oneRead = Allocated(() => Assert.Equal(earlier, Rows.Find(x => x.RequestorAddress == _holder.Address).Count()));
            var checks = Allocated(() =>
            {
                for (var i = 0; i < 1_290; i++)
                    Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, $"fresh-{i}", _vault, "new"));
            });
            Assert.True(checks < 100 * oneRead, $"1,290 checks allocated {checks:N0} bytes; one read of the requester's rows allocates {oneRead:N0}");

            // As block validation judges a request: the reuse is still what refuses it.
            var (ok, why) = await TransactionValidatorService.VerifyTX(Request("used-7", 0.001M), false, true, false, null, false, Gate + 11);
            Assert.False(ok);
            Assert.Contains("cannot be reused", why);
            var (_, whyFresh) = await TransactionValidatorService.VerifyTX(Request("never-used", 0.001M), false, true, false, null, false, Gate + 11);
            Assert.DoesNotContain("cannot be reused", whyFresh ?? "");
        }

        private static long Allocated(Action run)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            run();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>The row store finds a request's own row the same way when a block is applied.</summary>
        [Fact]
        public void TheRowStore_StillFindsARequestsRowUnderItsKey()
        {
            Rows.InsertBulk(Enumerable.Range(0, 2_000).Select(i => Row(_holder.Address, $"used-{i}", _vault, $"mined-{i}")));
            var local = Row(_holder.Address, "mine", _vault);
            local.Status = VBTCWithdrawalStatus.Requested; local.IsCompleted = false; local.RequestBlockHeight = 0;
            Assert.True(VBTCWithdrawalRequest.Save(local));

            var mined = Row(_holder.Address, "MINE", _vault, "H1"); // the key as the database compares it
            mined.Status = VBTCWithdrawalStatus.Requested; mined.IsCompleted = false;
            Assert.True(VBTCWithdrawalRequest.Save(mined, update: true));

            var row = Assert.Single(FullRead(_holder.Address, "mine", _vault));
            Assert.Equal("H1", Rows.FindById(row).TransactionHash);
            Assert.Equal(2_001, Rows.Count());
            Assert.Equal(row, VBTCWithdrawalRequest.GetByUniqueId(_holder.Address, "mine", _vault)!.Id);
            Assert.Null(VBTCWithdrawalRequest.AnotherMinedRequestsRow(mined));
            Assert.NotNull(VBTCWithdrawalRequest.AnotherMinedRequestsRow(Row(_holder.Address, "mine", _vault, "H2")));
        }
    }
}
