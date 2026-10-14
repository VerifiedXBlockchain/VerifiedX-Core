using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Room for key ceremonies on a validator (third pass, 14 October 2026).
    ///
    /// Anyone may lead a key ceremony: the leader carries messages and holds no key material. A validator kept 50
    /// ceremonies under way, four a leader, each for fifteen minutes whether or not its leader ever came back, so
    /// thirteen made-up leader keys from one machine took every place and no vault could be created with that
    /// validator. And a start carried its leader's signature over a session id and a timestamp that nothing checked:
    /// an old start, sent again by anybody who had seen it, opened a session under that leader and, because a leader's
    /// new start for a vault replaces its earlier ones, removed the ceremony that leader was running.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PathAudit3_CeremonyStartTests : IDisposable
    {
        private readonly long _now = TimeUtil.GetTime();
        private readonly List<DKGSession> _sessions = new();

        public PathAudit3_CeremonyStartTests() { FrostSessionStorage.ForgetDkgStarts(); }
        public void Dispose() { FrostSessionStorage.ForgetDkgStarts(); }

        private DKGSession Open(string leader, string? source = null, long secondsAgo = 0, bool progressed = false, bool completed = false)
        {
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc-" + Guid.NewGuid().ToString("N"), LeaderAddress = leader, SourceIp = source,
                ParticipantAddresses = new List<string> { "xV1", "xV2" }, RequiredThreshold = 67, StartTimestamp = _now - secondsAgo, IsCompleted = completed,
            };
            session.Round1Commitments.TryAdd("xV1", "own");
            if (progressed) session.Round1Commitments.TryAdd("xV2", "theirs");
            _sessions.Add(session);
            return session;
        }

        private string? Refusal(string leader, string? source = "198.51.100.7", long? timestamp = null, string? sessionId = null) =>
            FrostSessionStorage.DkgStartRefusal(_sessions, _now, sessionId ?? Guid.NewGuid().ToString(), timestamp ?? _now, leader, source);

        [Fact]
        public void AStartThatIsNotRecent_IsRefused_BeforeAnythingElseIsLookedAt()
        {
            var old = _now - FrostSessionStorage.DKG_START_MAX_AGE_SECONDS - 1;
            Assert.Contains("not recent", FrostSessionStorage.DkgStartRefusal(_sessions, _now, "s", old, null, null));
            Assert.Contains("not recent", Refusal("xLeader", timestamp: _now + FrostSessionStorage.DKG_START_MAX_AGE_SECONDS + 1));
            Assert.Null(Refusal("xLeader", timestamp: _now - FrostSessionStorage.DKG_START_MAX_AGE_SECONDS));
        }

        [Fact]
        public void AStartUsedBefore_IsNotAcceptedAgain_EvenAfterItsSessionIsGone()
        {
            const string id = "the-leaders-first-attempt";
            Assert.Null(Refusal("xLeader", sessionId: id));
            Assert.True(FrostSessionStorage.NoteDkgStart(id, _now));
            // The session it opened has since been replaced by the leader's second attempt (it is not among the
            // sessions held). Sent again, the first start used to be taken, and removed the second.
            Assert.Contains("used before", Refusal("xLeader", sessionId: id));
            Assert.False(FrostSessionStorage.NoteDkgStart(id, _now + 5));
            Assert.Null(Refusal("xLeader", sessionId: "the-leaders-second-attempt"));
        }

        [Fact]
        public void AStartThatNeverContinues_GivesItsPlaceBackAfterTwoMinutes()
        {
            var fresh = Open("xA");
            var abandoned = Open("xA", secondsAgo: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1);
            var working = Open("xA", secondsAgo: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1, progressed: true);
            var longGone = Open("xA", secondsAgo: FrostSessionStorage.DKG_LEADER_CAP_WINDOW_SECONDS + 1, progressed: true);
            var done = Open("xA", completed: true);
            Assert.True(FrostSessionStorage.DkgIsUnderWay(fresh, _now));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(abandoned, _now));
            Assert.True(FrostSessionStorage.DkgIsUnderWay(working, _now));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(longGone, _now));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(done, _now));

            // Thirteen made-up leaders, four starts each, none ever continued: two minutes later none holds a place.
            _sessions.Clear();
            for (var leader = 0; leader < 13; leader++)
                for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER; i++)
                    Open("xMadeUp" + leader, source: "203.0.113." + leader, secondsAgo: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1);
            Assert.Null(Refusal("xHonest"));
            Assert.Null(Refusal("xMadeUp0", source: "203.0.113.0"));
        }

        [Fact]
        public void OneLeader_AndOneNetworkSource_EachHoldOnlyAFewPlaces()
        {
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER; i++) Open("xLeader", source: "198.51.100." + i);
            Assert.Contains("for this leader", Refusal("xLeader", source: "198.51.100.200"));
            Assert.Null(Refusal("xOther", source: "198.51.100.200"));

            // Many leader keys from one machine.
            _sessions.Clear();
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_SOURCE; i++) Open("xKey" + i, source: "203.0.113.9");
            Assert.Contains("from this network address", Refusal("xKeyNew", source: "203.0.113.9"));
            Assert.Null(Refusal("xKeyNew", source: "203.0.113.10"));
        }

        [Fact]
        public void TheValidatorsTotal_CountsOnlyCeremoniesUnderWay()
        {
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS - 1; i++) Open("xL" + i, source: "10.0." + (i / 200) + "." + (i % 200), progressed: true, secondsAgo: 300);
            for (var i = 0; i < 500; i++) Open("xGone" + i, secondsAgo: 300);                 // started, never continued
            Assert.Null(Refusal("xNew"));
            Open("xLast", source: "10.9.9.9", progressed: true);
            Assert.Contains("Maximum concurrent DKG sessions", Refusal("xNew"));
            Assert.Contains("Maximum concurrent DKG sessions", FrostSessionStorage.DkgStartRefusal(_sessions, _now, "s", _now, null, null));
        }

        [Fact]
        public async Task TheRoute_RefusesAnOldStart()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var body = JsonConvert.SerializeObject(new FrostDKGStartRequest
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xLeader", LeaderSignature = "sig",
                Timestamp = _now - FrostSessionStorage.DKG_START_MAX_AGE_SECONDS - 60, ParticipantAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 67,
            });
            var response = await client.PostAsync("/frost/dkg/start", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("not recent", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["Message"]!);
        }

        [Fact]
        public async Task TheHeadersRoute_AnswersANodeThatDoesNotFollowBitcoin_AndRefusesAnOversizedRequest()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var ask = JsonConvert.SerializeObject(new { Locator = new[] { new string('0', 64) } });
            var response = await client.PostAsync("/frost/btcreport/headers", new StringContent(ask, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = JObject.Parse(await response.Content.ReadAsStringAsync());
            // This test process runs no light client: the route says so and serves nothing.
            if (VerifiedXCore.Bitcoin.Attestation.Net.BtcLightClient.Current == null)
            {
                Assert.False((bool)answer["Success"]!);
                Assert.Empty((JArray)answer["Headers"]!);
            }
            var large = JsonConvert.SerializeObject(new { Locator = Enumerable.Range(0, 200).Select(_ => new string('0', 64)).ToArray() });
            response = await client.PostAsync("/frost/btcreport/headers", new StringContent(large, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }
}
