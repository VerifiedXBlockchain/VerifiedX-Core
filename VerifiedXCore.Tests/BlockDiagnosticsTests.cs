using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using System.Collections.Concurrent;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Tests for the in-memory rejection/rollback diagnostic rings (BlockDiagnostics) and the
    /// read-only valapi diagnostic endpoints (GetRejectionLog / GetConsensusState / GetRoundAudit)
    /// added so consensus failures can be diagnosed remotely without SSH.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class BlockDiagnosticsTests
    {
        private static ValidatorController MakeController() => new ValidatorController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        private static string BodyOf(ActionResult<string> result)
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            return Assert.IsType<string>(ok.Value);
        }

        [Fact]
        public void Rollback_And_Rejection_AreRecorded_NewestFirst()
        {
            BlockDiagnostics.Clear();
            try
            {
                BlockDiagnostics.RecordRollback("ValidateBlock()-cert", "");
                BlockDiagnostics.RecordRollback("ValidateBlock()-9", "ts");
                BlockDiagnostics.RecordBlockRejection(1234, "xTestValidator", "abcdef0123456789deadbeef");

                var rollbacks = BlockDiagnostics.RollbackSnapshot();
                Assert.Equal(2, rollbacks.Count);
                Assert.Equal("ValidateBlock()-9", rollbacks[0].Location); // newest first

                var rejections = BlockDiagnostics.RejectionSnapshot();
                var rej = Assert.Single(rejections);
                Assert.Equal(1234, rej.Height);
                Assert.Equal("xTestValidator", rej.Validator);
                Assert.Equal(16, rej.Hash.Length); // truncated
                // The rejection captured the most recent rollback tag as its reason.
                Assert.Equal("ValidateBlock()-9", rej.LastRollbackTag);
            }
            finally
            {
                BlockDiagnostics.Clear();
            }
        }

        [Fact]
        public void Rings_EnforceCap()
        {
            BlockDiagnostics.Clear();
            try
            {
                for (int i = 0; i < BlockDiagnostics.MaxEntries + 50; i++)
                    BlockDiagnostics.RecordRollback($"tag{i}", "");

                var snapshot = BlockDiagnostics.RollbackSnapshot();
                Assert.True(snapshot.Count <= BlockDiagnostics.MaxEntries + 5, $"cap not enforced: {snapshot.Count}");
                Assert.Equal($"tag{BlockDiagnostics.MaxEntries + 49}", snapshot[0].Location); // newest kept
            }
            finally
            {
                BlockDiagnostics.Clear();
            }
        }

        [Fact]
        public void Rings_ParallelRecords_DoNotThrow()
        {
            BlockDiagnostics.Clear();
            try
            {
                Parallel.For(0, 500, i =>
                {
                    BlockDiagnostics.RecordRollback($"p{i}", "");
                    BlockDiagnostics.RecordBlockRejection(i, "xV", "hash");
                });
                Assert.True(BlockDiagnostics.RollbackSnapshot().Count > 0);
            }
            finally
            {
                BlockDiagnostics.Clear();
            }
        }

        [Fact]
        public void GetRejectionLog_ReturnsRecordedEntries()
        {
            BlockDiagnostics.Clear();
            try
            {
                BlockDiagnostics.RecordRollback("ValidateBlock()-cert", "");
                BlockDiagnostics.RecordBlockRejection(999, "xRejVal", "ffff0000ffff0000ffff");

                var body = BodyOf(MakeController().GetRejectionLog());
                var json = JObject.Parse(body);

                var rejections = (JArray)json["BlockRejections"]!;
                Assert.Single(rejections);
                Assert.Equal(999, (long)rejections[0]["Height"]!);
                Assert.Equal("xRejVal", (string)rejections[0]["Validator"]!);
                Assert.Equal("ValidateBlock()-cert", (string)rejections[0]["LastRollbackTag"]!);

                var rollbacks = (JArray)json["RecentRollbacks"]!;
                Assert.Single(rollbacks);
            }
            finally
            {
                BlockDiagnostics.Clear();
            }
        }

        [Fact]
        public void GetConsensusState_ReturnsSnapshot_WithoutIPsInRounds()
        {
            var originalBag = Globals.BlockCasters;
            var originalRounds = Globals.CasterRoundDict;
            var originalApproved = Globals.CasterApprovedBlockHashDict;
            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>
                {
                    new Peers { ValidatorAddress = "xCasterA", PeerIP = "::ffff:10.0.0.1", ValidatorPublicKey = "pkA" }
                };
                Globals.CasterRoundDict = new ConcurrentDictionary<long, CasterRound>();
                Globals.CasterRoundDict.TryAdd(500, new CasterRound
                {
                    BlockHeight = 500,
                    Validator = "xWinner",
                    RoundAttempts = 1,
                    Proof = new Proof { Address = "xWinner", IPAddress = "10.9.9.9" }
                });
                Globals.CasterApprovedBlockHashDict = new ConcurrentDictionary<long, string>();
                Globals.CasterApprovedBlockHashDict.TryAdd(500, "hash500");

                var body = BodyOf(MakeController().GetConsensusState());
                var json = JObject.Parse(body);

                Assert.NotNull(json["Height"]);
                Assert.NotNull(json["IsChainSynced"]);
                Assert.Equal("xCasterA", (string)json["BlockCasters"]![0]!["Address"]!);
                Assert.Equal("10.0.0.1", (string)json["BlockCasters"]![0]!["PeerIP"]!); // normalized

                var round = ((JArray)json["RecentRounds"]!).First(r => (long)r["Height"]! == 500);
                Assert.Equal("xWinner", (string)round["Validator"]!);
                // Proof (and its IPAddress) must not be serialized into the diagnostics payload.
                Assert.DoesNotContain("10.9.9.9", body);

                Assert.Equal("hash500", (string)((JArray)json["ApprovedBlockHashes"]!)
                    .First(h => (long)h["Height"]! == 500)["Hash"]!);
            }
            finally
            {
                Globals.BlockCasters = originalBag;
                Globals.CasterRoundDict = originalRounds;
                Globals.CasterApprovedBlockHashDict = originalApproved;
            }
        }

        [Fact]
        public void GetRoundAudit_ByHeight_ReturnsArchivedAudit()
        {
            var originalAudits = Globals.CasterRoundAuditDict;
            try
            {
                Globals.CasterRoundAuditDict = new ConcurrentDictionary<long, CasterRoundAudit>();
                var audit = new CasterRoundAudit(777);
                audit.AddStep("proofs collected");
                audit.AddStep("winner agreed");
                Globals.CasterRoundAuditDict.TryAdd(777, audit);

                var body = BodyOf(MakeController().GetRoundAudit(777));
                var json = JObject.Parse(body);

                Assert.True((bool)json["Found"]!);
                Assert.Equal(777, (long)json["BlockHeight"]!);
                Assert.Equal("winner agreed", (string)json["CurrentStepMessage"]!);
                Assert.Equal(2, ((JArray)json["StepHistory"]!).Count);
            }
            finally
            {
                Globals.CasterRoundAuditDict = originalAudits;
            }
        }

        [Fact]
        public void GetRoundAudit_UnknownHeight_ReturnsNotFound()
        {
            var body = BodyOf(MakeController().GetRoundAudit(long.MaxValue - 1));
            var json = JObject.Parse(body);
            Assert.False((bool)json["Found"]!);
        }
    }
}
