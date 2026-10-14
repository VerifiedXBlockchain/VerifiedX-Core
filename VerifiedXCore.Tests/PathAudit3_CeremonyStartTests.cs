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

        /// <summary>A session started <paramref name="secondsAgo"/>, whose last step (Round 2 made) was <paramref name="lastStepAgo"/> seconds ago, if any.</summary>
        private DKGSession Open(string leader, string? source = null, long secondsAgo = 0, long? lastStepAgo = null, bool completed = false, string? rekeyOf = null)
        {
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc-" + Guid.NewGuid().ToString("N"), LeaderAddress = leader, SourceIp = source,
                ParticipantAddresses = new List<string> { "xV1", "xV2" }, RequiredThreshold = 67, StartTimestamp = _now - secondsAgo, IsCompleted = completed,
                LastProgressTimestamp = _now - secondsAgo, RekeyOf = rekeyOf,
            };
            session.Round1Commitments.TryAdd("xV1", "own");
            if (lastStepAgo is long ago) FrostSessionStorage.MarkDkgStage(session, FrostSessionStorage.DkgStage.Round2, _now - ago);
            _sessions.Add(session);
            return session;
        }

        private string? Refusal(string leader, string? source = "198.51.100.0/24", long? timestamp = null, string? sessionId = null, string? rekeyOf = null) =>
            FrostSessionStorage.DkgStartRefusal(_sessions, _now, sessionId ?? Guid.NewGuid().ToString(), timestamp ?? _now, leader, source, rekeyOf);

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
            var working = Open("xA", secondsAgo: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1, lastStepAgo: 10);
            var stalled = Open("xA", secondsAgo: 400, lastStepAgo: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1);
            var longGone = Open("xA", secondsAgo: FrostSessionStorage.DKG_LEADER_CAP_WINDOW_SECONDS + 1, lastStepAgo: 1);
            var done = Open("xA", completed: true);
            Assert.True(FrostSessionStorage.DkgIsUnderWay(fresh, _now));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(abandoned, _now));
            Assert.True(FrostSessionStorage.DkgIsUnderWay(working, _now));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(stalled, _now));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(longGone, _now));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(done, _now));

            // Commitments posted with a Round 2 request (anybody who knows the session id can post them) are no step.
            var posted = Open("xA", secondsAgo: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1);
            posted.Round1Commitments.TryAdd("xV2", "made up");
            posted.Round1Commitments.TryAdd("junk", "made up");
            Assert.False(FrostSessionStorage.DkgIsUnderWay(posted, _now));

            // A step already taken changes nothing when it is reported again.
            FrostSessionStorage.MarkDkgStage(stalled, FrostSessionStorage.DkgStage.Round2, _now);
            Assert.False(FrostSessionStorage.DkgIsUnderWay(stalled, _now));

            // Thirteen made-up leaders, four starts each, none ever continued: two minutes later none holds a place.
            _sessions.Clear();
            for (var leader = 0; leader < 13; leader++)
                for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER; i++)
                    Open("xMadeUp" + leader, source: $"203.0.{leader}.0/24", secondsAgo: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1);
            Assert.Null(Refusal("xHonest"));
            Assert.Null(Refusal("xMadeUp0", source: "203.0.0.0/24"));
        }

        /// <summary>
        /// A ceremony held its place for fifteen minutes once anything had been posted for it, and the only thing it
        /// needed was a Round 2 request with made-up commitments, which anybody who knows the session id can send.
        /// Now it holds its place while it goes a step further every two minutes, and its steps are each taken once.
        /// </summary>
        [Fact]
        public void ACeremonyHoldsItsPlace_OnlyWhileItGoesOn()
        {
            var session = Open("xA");
            Assert.True(FrostSessionStorage.DkgIsUnderWay(session, _now + FrostSessionStorage.DKG_UNPROGRESSED_SECONDS));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(session, _now + FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1));
            FrostSessionStorage.MarkDkgStage(session, FrostSessionStorage.DkgStage.Round2, _now + 100);
            FrostSessionStorage.MarkDkgStage(session, FrostSessionStorage.DkgStage.Round2, _now + 200); // again: nothing
            Assert.False(FrostSessionStorage.DkgIsUnderWay(session, _now + 100 + FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1));
            FrostSessionStorage.MarkDkgStage(session, FrostSessionStorage.DkgStage.SharesIn, _now + 300);
            Assert.True(FrostSessionStorage.DkgIsUnderWay(session, _now + 400));
            // However it goes on, never past the window.
            Assert.False(FrostSessionStorage.DkgIsUnderWay(session, _now + FrostSessionStorage.DKG_LEADER_CAP_WINDOW_SECONDS + 1));
        }

        /// <summary>The coordinator gives a failed attempt up: it holds no place and its secrets are gone. A finished ceremony is left alone.</summary>
        [Fact]
        public void AnAttemptGivenUp_HoldsNoPlace_AndAFinishedOneIsNotTouched()
        {
            var session = Open("xA", lastStepAgo: 1);
            session.Round1SecretPackage = "secret";
            session.Round2Secret = "secret";
            Assert.True(FrostSessionStorage.AbortDkg(session));
            Assert.False(FrostSessionStorage.DkgIsUnderWay(session, _now));
            Assert.Null(session.Round1SecretPackage);
            Assert.Null(session.Round2Secret);
            Assert.False(FrostSessionStorage.AbortDkg(session));

            var done = Open("xA", completed: true);
            done.FinalKeyPackage = "key";
            Assert.False(FrostSessionStorage.AbortDkg(done));
            Assert.False(done.IsAborted);
            Assert.Equal("key", done.FinalKeyPackage);
            // An abort is signed under its own message: a start's signature does not abort.
            Assert.NotEqual($"s.xA.{_now}", FrostDKGAbortRequest.Message("s", "xA", _now));
        }

        /// <summary>
        /// A vault whose key is losing its holders must get a new one whoever is filling the creations' places. Re-keys
        /// have places of their own, which only an active validator can take, and only two per vault.
        /// </summary>
        [Fact]
        public void ReKeys_HavePlacesOfTheirOwn_ThatOnlyValidatorsCanTake()
        {
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS; i++) Open("xFill" + i, source: $"10.{i / 250}.{i % 250}.0/24");
            Assert.Contains("Maximum concurrent DKG sessions", Refusal("xNew"));
            Assert.Null(Refusal("xValidator", rekeyOf: "vault1"));
            Assert.Null(FrostSessionStorage.DkgStartRefusal(_sessions, _now, "s", _now, null, null, "vault1"));

            // Two leaders of one vault's re-key at once, not three; another vault still has room.
            Open("xValidatorA", rekeyOf: "vault1");
            Open("xValidatorB", rekeyOf: "vault1");
            Assert.Contains("already under way", Refusal("xValidatorC", rekeyOf: "vault1"));
            Assert.Null(Refusal("xValidatorC", rekeyOf: "vault2"));

            // Re-keys do not count against the creations' network limit (validators may share a host), nor creations against theirs.
            _sessions.Clear();
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_SOURCE; i++) Open("xV" + i, source: "203.0.113.0/24", rekeyOf: "vault" + i);
            Assert.Null(Refusal("xUser", source: "203.0.113.0/24"));
            Assert.Null(Refusal("xV99", source: "203.0.113.0/24", rekeyOf: "vault99"));

            // Only an active validator leads one.
            Assert.Null(FrostSessionStorage.RekeyLeaderRefusal("xValidator", a => a == "xValidator"));
            Assert.Contains("active vBTC validator", FrostSessionStorage.RekeyLeaderRefusal("xAnybody", a => a == "xValidator"));
            Assert.NotNull(FrostSessionStorage.RekeyLeaderRefusal(null, a => true));
        }

        /// <summary>One host has a whole /24 or /64 to send from: its starts count as one network's.</summary>
        [Fact]
        public void AddressesOfOneNetwork_AreOneSource()
        {
            string G(string ip) => RemoteIp.Group(IPAddress.Parse(ip))!;
            Assert.Equal(G("203.0.113.9"), G("203.0.113.200"));
            Assert.NotEqual(G("203.0.113.9"), G("203.0.114.9"));
            Assert.Equal(G("203.0.113.9"), G("::ffff:203.0.113.77"));
            Assert.Equal(G("2001:db8:1:2::1"), G("2001:db8:1:2:ffff:ffff:ffff:9"));
            Assert.NotEqual(G("2001:db8:1:2::1"), G("2001:db8:1:3::1"));
            Assert.Equal("203.0.113.0/24", G("203.0.113.9"));
            Assert.Null(RemoteIp.Group(null));
            // Bitcoin peers are told apart by a wider network: an IPv4 /16, an IPv6 /32.
            Assert.Equal(VerifiedXCore.Bitcoin.Attestation.Net.BtcLightClient.NetworkGroup("203.0.1.1:8333"), VerifiedXCore.Bitcoin.Attestation.Net.BtcLightClient.NetworkGroup("203.0.200.9:8333"));
            Assert.Equal(VerifiedXCore.Bitcoin.Attestation.Net.BtcLightClient.NetworkGroup("[2001:db8:1::1]:8333"), VerifiedXCore.Bitcoin.Attestation.Net.BtcLightClient.NetworkGroup("[2001:db8:ff::9]:8333"));
            Assert.NotEqual(VerifiedXCore.Bitcoin.Attestation.Net.BtcLightClient.NetworkGroup("[2001:db8:1::1]:8333"), VerifiedXCore.Bitcoin.Attestation.Net.BtcLightClient.NetworkGroup("[2001:db9:1::1]:8333"));
        }

        [Fact]
        public void OneLeader_AndOneNetworkSource_EachHoldOnlyAFewPlaces()
        {
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_LEADER; i++) Open("xLeader", source: $"198.51.{i}.0/24");
            Assert.Contains("for this leader", Refusal("xLeader", source: "198.51.200.0/24"));
            Assert.Null(Refusal("xOther", source: "198.51.200.0/24"));

            // Many leader keys from one network.
            _sessions.Clear();
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_SOURCE; i++) Open("xKey" + i, source: "203.0.113.0/24");
            Assert.Contains("from this network", Refusal("xKeyNew", source: "203.0.113.0/24"));
            Assert.Null(Refusal("xKeyNew", source: "203.0.114.0/24"));
        }

        [Fact]
        public void TheValidatorsTotal_CountsOnlyCeremoniesUnderWay()
        {
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS - 1; i++) Open("xL" + i, source: $"10.{i / 200}.{i % 200}.0/24", lastStepAgo: 30, secondsAgo: 300);
            for (var i = 0; i < 500; i++) Open("xGone" + i, secondsAgo: 300);                 // started, never continued
            for (var i = 0; i < 500; i++) Open("xStalled" + i, secondsAgo: 600, lastStepAgo: 300); // went on once, then stopped
            Assert.Null(Refusal("xNew"));
            Open("xLast", source: "10.9.9.0/24", lastStepAgo: 0);
            Assert.Contains("Maximum concurrent DKG sessions", Refusal("xNew"));
            Assert.Contains("Maximum concurrent DKG sessions", FrostSessionStorage.DkgStartRefusal(_sessions, _now, "s", _now, null, null));
        }

        [Fact]
        public async Task TheAbortRoute_TakesOnlyTheLeadersSignature()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var leader = new EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(leader.publicKey().toString()).ToLowerInvariant();
            var address = VerifiedXCore.Data.AccountData.GetHumanAddress(pub);
            var session = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = address, StartTimestamp = _now, LastProgressTimestamp = _now, Round2Secret = "secret" };
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                async Task<HttpStatusCode> Abort(string message, string signer = "")
                {
                    var request = new FrostDKGAbortRequest { SessionId = session.SessionId, LeaderAddress = address, Timestamp = _now,
                        LeaderSignature = VerifiedXCore.Services.SignatureService.CreateSignature(message, leader, pub) };
                    var response = await client.PostAsync("/frost/dkg/abort", new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json"));
                    return response.StatusCode;
                }
                // A start's signature (or any other message) does not abort; the leader's abort does.
                Assert.Equal(HttpStatusCode.Forbidden, await Abort($"{session.SessionId}.{address}.{_now}"));
                Assert.False(session.IsAborted);
                Assert.Equal(HttpStatusCode.OK, await Abort(FrostDKGAbortRequest.Message(session.SessionId, address, _now)));
                Assert.True(session.IsAborted);
                Assert.Null(session.Round2Secret);
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _); }
        }

        /// <summary>A Round 2 request that makes no Round 2 stores nothing, and the ceremony has not gone on.</summary>
        [Fact]
        public async Task TheRound2Route_StoresNothing_FromARequestThatMakesNoRound2()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var (commitment, secret, error) = FrostNative.DKGRound1Generate(1, 3, 2);
            Assert.Equal(FrostNative.SUCCESS, error);
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xLeader", StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 67, MyParticipantIndex = 1, Round1SecretPackage = secret,
            };
            session.Round1Commitments.TryAdd("xV1", commitment);
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                var body = JsonConvert.SerializeObject(new Dictionary<string, string> { ["xJunk1"] = "{}", ["xJunk2"] = "{}", ["xV2"] = "not a commitment" });
                var response = await client.PostAsync($"/frost/dkg/round2/{session.SessionId}", new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.False(response.IsSuccessStatusCode);
                Assert.Equal(new[] { "xV1" }, session.Round1Commitments.Keys.ToArray());
                Assert.Equal(0, session.Stage);
                Assert.False(FrostSessionStorage.DkgIsUnderWay(session, _now + FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 1));
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _); }
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
