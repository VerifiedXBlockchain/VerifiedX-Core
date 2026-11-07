using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The validator API takes membership records as they are: an ordinary record has no election hash, and an election
    /// record no changed address. A probe carrying them reaches the validator's answer, and so does an announced record.
    /// The web layer once refused both with a 400 before the route ran, so no caster was ever acknowledged after the
    /// committee changed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VreeProbeRouteTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VreeProbeRouteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vreeprobe_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private static CasterMembershipRecord Ordinary(long seq) => new()
        {
            RecordSeq = seq, EffectiveFromHeight = 100 + seq, PrevRecordHash = "prev", RecordHash = $"hash{seq}",
            ChangeType = "Promotion", ChangedAddress = "xAddr", EpochHash = null!,
            Casters = new List<CasterInfo> { new() { Address = "xA", PeerIP = "1.2.3.4", PublicKey = "04ab" } },
        };

        private static CasterMembershipRecord Election(long seq) => new()
        {
            RecordSeq = seq, EffectiveFromHeight = 100 + seq, PrevRecordHash = "prev", RecordHash = $"hash{seq}",
            ChangeType = VreeElection.EmergencyElectionChangeType, ChangedAddress = "", EpochHash = "epoch", Rung = 0,
            Casters = new List<CasterInfo> { new() { Address = "xA", PeerIP = "1.2.3.4", PublicKey = "04ab" } },
        };

        private static async Task<(HttpStatusCode Status, string Body)> Post(string route, object payload)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<StartupP2PCaster>());
            var client = server.CreateClient();
            var r = await client.PostAsync($"/valapi/validator/{route}",
                new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"));
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task AProbeCarryingOrdinaryAndElectionRecords_ReachesTheValidatorsAnswer()
        {
            var probe = new VreeProbe
            {
                Height = 107_681, Hash = "aa", PrevHash = "bb", Validator = "xV", Version = 1, Prober = "xP",
                Records = new List<CasterMembershipRecord> { Ordinary(26), Ordinary(27), Election(28) },
            };

            var (status, body) = await Post("ProbeTip", probe);

            Assert.Equal(HttpStatusCode.OK, status);
            var answer = JsonConvert.DeserializeObject<VreeProbeAnswer>(body);
            Assert.NotNull(answer);
            Assert.False(answer!.Ack); // this node holds no validator key: refused by the route itself, not by the web layer
            Assert.False(string.IsNullOrEmpty(answer.Reason));
        }

        [Fact]
        public async Task AnAnnouncedRecordWithoutAnElectionHash_ReachesTheRoute()
        {
            var (_, body) = await Post("AnnounceMembershipRecord", Ordinary(27));
            Assert.DoesNotContain("validation errors", body);
            Assert.DoesNotContain("EpochHash", body);
        }
    }
}
