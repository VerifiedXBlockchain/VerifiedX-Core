using System;
using System.IO;
using LiteDB;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A producer orders name registrations by the pool row's id (its arrival on this node). An id a sender puts in a
    /// transaction's JSON does not survive deserialization, and admission replaces any id the transaction carries.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MempoolArrivalIdTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public MempoolArrivalIdTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"arrival_id_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Registration(ObjectId id) => new()
        {
            Id = id, Hash = "forged-id-" + Guid.NewGuid().ToString("N"), FromAddress = "xA", ToAddress = "xB", Amount = 1M,
            Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.ADNR, Data = "{\"Function\":\"AdnrCreate()\",\"Name\":\"alice\"}",
        };

        [Fact]
        public void AnIdInTheJson_DoesNotSurviveDeserialization()
        {
            var forged = new ObjectId(1, 1, 1, 1); // as early as an id can be
            var received = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(Registration(forged)))!;
            Assert.NotEqual(forged, received.Id);
        }

        [Fact]
        public void ADeserializedTransaction_IsGivenAnArrivalIdOnAdmission()
        {
            var sent = Registration(new ObjectId(1, 1, 1, 1));
            var received = JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(sent))!;

            var pool = TransactionData.GetPool();
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(received, pool, out var reason), reason);
            var stored = pool.FindOne(x => x.Hash == sent.Hash);
            Assert.NotNull(stored);
            Assert.NotEqual(sent.Id, stored.Id);
            Assert.NotEqual(ObjectId.Empty, stored.Id);
        }

        [Fact]
        public void AnIdOnTheAdmittedTransaction_IsReplacedByTheArrivalId()
        {
            var forged = new ObjectId(1, 1, 1, 1);
            var tx = Registration(forged); // carries the id, with no JSON in between

            var pool = TransactionData.GetPool();
            Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, pool, out var reason), reason);
            var stored = pool.FindOne(x => x.Hash == tx.Hash);
            Assert.NotNull(stored);
            Assert.NotEqual(forged, stored.Id);
            Assert.NotEqual(ObjectId.Empty, stored.Id);
        }
    }
}
