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

            // One leader holds two re-key places (an attempt and its retry), whatever the vault; it cannot keep the others
            // off a vault (fourth review: two places a vault, both held by one validator restarting its starts).
            Open("xValidatorA", rekeyOf: "vault1");
            Open("xValidatorA", rekeyOf: "vault1");
            Assert.Contains("for this leader (cap 2)", Refusal("xValidatorA", rekeyOf: "vault2"));
            Assert.Null(Refusal("xValidatorB", rekeyOf: "vault1"));
            Open("xValidatorB", rekeyOf: "vault1");
            Assert.Null(Refusal("xValidatorC", rekeyOf: "vault1"));

            // Re-keys do not count against the creations' network limit (validators may share a host), nor creations against theirs.
            _sessions.Clear();
            for (var i = 0; i < FrostSessionStorage.MAX_DKG_SESSIONS_PER_SOURCE; i++) Open("xV" + i, source: "203.0.113.0/24", rekeyOf: "vault" + i);
            Assert.Null(Refusal("xUser", source: "203.0.113.0/24"));
            Assert.Null(Refusal("xV99", source: "203.0.113.0/24", rekeyOf: "vault99"));

            // Only an active validator leads one.
            Assert.Null(FrostSessionStorage.RekeyLeaderRefusal("xValidator", a => a == "xValidator"));
            Assert.Contains("bonded for the minimum age", FrostSessionStorage.RekeyLeaderRefusal("xAnybody", a => a == "xValidator"));
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

        /// <summary>
        /// The leader's signature on a share batch did not say whom it was for: a participant replayed its own batch to
        /// another, which reported every dealer (to the replayer, so nothing was settled) and refused the leader's real
        /// batch, and was dropped (fifth review, round 15). A batch for another validator, or with no share for this one,
        /// is refused and reports nobody.
        /// </summary>
        [Fact]
        public async Task AShareBatchForAnotherValidator_IsRefused_AndReportsNobody()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var leader = new EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(leader.publicKey().toString()).ToLowerInvariant();
            var address = VerifiedXCore.Data.AccountData.GetHumanAddress(pub);
            var participants = new List<string> { "xV1", "xV2", "xV3" };
            var ids = FrostStartup.BuildAddressToIdentifierMap(participants);
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = "xV2";
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = address, StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = participants, RequiredThreshold = 67, MyParticipantIndex = 2, Round1SecretPackage = "secret", Round2Secret = "round2",
            };
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                string Batch(string? recipient, string forId) => JsonConvert.SerializeObject(new
                {
                    LeaderAddress = address, Timestamp = _now,
                    LeaderSignature = VerifiedXCore.Services.SignatureService.CreateSignature($"{session.SessionId}.{address}.{_now}", leader, pub),
                    Recipient = recipient,
                    AllGeneratedShares = new Dictionary<string, string>
                    {
                        ["xV1"] = new JObject { [forId] = FrostShareCrypto.Prefix + "AAAA" }.ToString(Formatting.None),
                        ["xV3"] = new JObject { [forId] = FrostShareCrypto.Prefix + "AAAA" }.ToString(Formatting.None),
                    },
                });
                async Task<HttpResponseMessage> Post(string body) =>
                    await client.PostAsync($"/frost/dkg/shares/{session.SessionId}", new StringContent(body, Encoding.UTF8, "application/json"));

                // xV1's batch, replayed here: named for xV1, or (from an older coordinator) unnamed with only xV1's shares.
                var named = await Post(Batch("xV1", ids["xV1"]));
                Assert.Equal(HttpStatusCode.Conflict, named.StatusCode);
                Assert.Contains("another validator", await named.Content.ReadAsStringAsync());
                var unnamed = await Post(Batch(null, ids["xV1"]));
                Assert.Equal(HttpStatusCode.Conflict, unnamed.StatusCode);
                Assert.Contains("No share in this batch", await unnamed.Content.ReadAsStringAsync());
                Assert.False(session.Reported);

                // A batch for this validator is taken (its made-up shares are then reported, as before).
                var own = await Post(Batch("xV2", ids["xV2"]));
                Assert.NotEqual(HttpStatusCode.Conflict, own.StatusCode);
                Assert.True(session.Reported);
            }
            finally
            {
                FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _);
                Globals.ValidatorAddress = priorValidator;
            }
        }

        /// <summary>
        /// A dealer sealed each share to the participant's key read from its registry at Round 2: a participant that had
        /// left the registry since the start (an exit, a lapsed heartbeat) failed every honest dealer's Round 2, and the
        /// dealers were dropped (fifth review, round 14). Shares are sealed to the keys taken at the start.
        /// </summary>
        [Fact]
        public async Task ARound2_SealsToTheKeysTakenAtTheStart_NotTheRegistryAtRound2()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var keys = Enumerable.Range(0, 3).Select(_ =>
            {
                var k = new EllipticCurve.PrivateKey("secp256k1");
                var p = "04" + Convert.ToHexString(k.publicKey().toString()).ToLowerInvariant();
                return (Pub: p, Address: VerifiedXCore.Data.AccountData.GetHumanAddress(p));
            }).OrderBy(k => k.Address, StringComparer.Ordinal).ToList();
            var r1 = Enumerable.Range(1, 3).Select(i => FrostNative.DKGRound1Generate((ushort)i, 3, 3)).ToList();
            Assert.All(r1, r => Assert.Equal(FrostNative.SUCCESS, r.Item3));
            var priorValidator = Globals.ValidatorAddress;
            var priorSynced = Globals.IsChainSynced;
            Globals.ValidatorAddress = keys[0].Address;
            Globals.IsChainSynced = false; // the registry answers nobody now
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xLeader", StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = keys.Select(k => k.Address).ToList(), RequiredThreshold = 67, MyParticipantIndex = 1, Round1SecretPackage = r1[0].Item2,
                ParticipantKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(keys.ToDictionary(k => k.Address, k => k.Pub)),
            };
            session.Round1Commitments.TryAdd(keys[0].Address, r1[0].Item1);
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                var body = JsonConvert.SerializeObject(Enumerable.Range(0, 3).ToDictionary(i => keys[i].Address, i => r1[i].Item1));
                var response = await client.PostAsync($"/frost/dkg/round2/{session.SessionId}", new StringContent(body, Encoding.UTF8, "application/json"));
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK, text);
                var shares = Newtonsoft.Json.Linq.JObject.Parse((string)Newtonsoft.Json.Linq.JObject.Parse(text)["GeneratedShares"]!);
                Assert.Equal(2, shares.Count);
                Assert.All(shares.Properties(), p => Assert.StartsWith(FrostShareCrypto.Prefix, (string)p.Value!));
            }
            finally
            {
                FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _);
                Globals.ValidatorAddress = priorValidator;
                Globals.IsChainSynced = priorSynced;
            }
        }

        /// <summary>
        /// Two Round 2 requests at once (the leader's signed request replayed by a participant) each sealed the shares with
        /// their own one-time keys; the last stored won while the leader relayed the other's ciphertext, so a reveal could
        /// not open it and the honest dealer was dropped (fifth review, round 13). A request that comes while another is
        /// being made waits for it and answers what it stored.
        /// </summary>
        [Fact]
        public async Task ARound2RequestWhileAnotherIsBeingMade_WaitsAndAnswersTheStoredShares()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var (_, secret, error) = FrostNative.DKGRound1Generate(1, 3, 2);
            Assert.Equal(FrostNative.SUCCESS, error);
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xLeader", StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 67, MyParticipantIndex = 1, Round1SecretPackage = secret,
            };
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                await session.Gate.WaitAsync(); // the first request, still being made
                var body = JsonConvert.SerializeObject(new Dictionary<string, string> { ["xV2"] = "{}", ["xV3"] = "{}" });
                var second = client.PostAsync($"/frost/dkg/round2/{session.SessionId}", new StringContent(body, Encoding.UTF8, "application/json"));
                await Task.Delay(300);
                Assert.False(second.IsCompleted);

                // The first stores its shares and lets go; the second answers exactly those.
                session.Round2Secret = "first-secret";
                session.GeneratedSharesJson = "{\"first\":\"enc1:AAAA\"}";
                session.Gate.Release();
                var response = await second;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var answer = Newtonsoft.Json.Linq.JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(session.GeneratedSharesJson, (string?)answer["GeneratedShares"]);
                Assert.Equal("first-secret", session.Round2Secret);
                Assert.Equal(1, session.Gate.CurrentCount); // and the request let go of the session
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _); }
        }

        /// <summary>
        /// A participant could put commitments or shares into another validator's session ahead of the leader's (the
        /// unauthenticated Round 2 route, the single-share and Round 1 routes, a share batch before Round 2): the real
        /// ones were dropped, the validator failed and was dropped from the ceremony, and its sender was never named; one
        /// validator could keep every re-key from finishing (fourth review, round 4). A leader-signed session takes its
        /// Round 2 set and shares from its leader only, shares are taken after Round 2 and checked, and the routes no
        /// coordinator uses change nothing.
        /// </summary>
        [Fact]
        public async Task ALeaderSignedSession_TakesItsContentFromItsLeaderOnly()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var leader = new EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(leader.publicKey().toString()).ToLowerInvariant();
            var address = VerifiedXCore.Data.AccountData.GetHumanAddress(pub);
            var (commitment, secret, error) = FrostNative.DKGRound1Generate(1, 3, 2);
            Assert.Equal(FrostNative.SUCCESS, error);
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = address, StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 67, MyParticipantIndex = 1, Round1SecretPackage = secret,
                LeaderSigned = true,
            };
            session.Round1Commitments.TryAdd("xV1", commitment);
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                var body = JsonConvert.SerializeObject(new Dictionary<string, string> { ["xV2"] = "{}", ["xV3"] = "{}" });
                async Task<HttpStatusCode> Post(string path, string content, string? signature)
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
                    if (signature != null) request.Headers.TryAddWithoutValidation(FrostDkgLeaderAuth.ContentSignatureHeader, signature);
                    return (await client.SendAsync(request)).StatusCode;
                }
                string Sign(string message) => VerifiedXCore.Services.SignatureService.CreateSignature(message, leader, pub);

                // Round 2 set: unsigned, or signed for another body, or by someone else: refused before anything is read.
                Assert.Equal(HttpStatusCode.Forbidden, await Post($"/frost/dkg/round2/{session.SessionId}", body, null));
                Assert.Equal(HttpStatusCode.Forbidden, await Post($"/frost/dkg/round2/{session.SessionId}", body, Sign(FrostDkgLeaderAuth.ContentMessage("ROUND2", session.SessionId, "{}"))));
                var stranger = new EllipticCurve.PrivateKey("secp256k1");
                var strangerPub = "04" + Convert.ToHexString(stranger.publicKey().toString()).ToLowerInvariant();
                Assert.Equal(HttpStatusCode.Forbidden, await Post($"/frost/dkg/round2/{session.SessionId}", body,
                    VerifiedXCore.Services.SignatureService.CreateSignature(FrostDkgLeaderAuth.ContentMessage("ROUND2", session.SessionId, body), stranger, strangerPub)));
                // Signed by the leader: taken (and then refused for its made-up commitments, which a real leader would not send).
                var signed = await Post($"/frost/dkg/round2/{session.SessionId}", body, Sign(FrostDkgLeaderAuth.ContentMessage("ROUND2", session.SessionId, body)));
                Assert.NotEqual(HttpStatusCode.Forbidden, signed);
                Assert.Equal(0, session.Stage);

                // Shares before this validator's Round 2: refused, whoever sends them.
                var shares = JsonConvert.SerializeObject(new { LeaderAddress = address, Timestamp = _now, LeaderSignature = Sign($"{session.SessionId}.{address}.{_now}"), AllGeneratedShares = new { xV2 = "{}" } });
                Assert.Equal(HttpStatusCode.Conflict, await Post($"/frost/dkg/shares/{session.SessionId}", shares, Sign(FrostDkgLeaderAuth.ContentMessage("SHARES", session.SessionId, shares))));

                // The routes no coordinator uses change nothing.
                Assert.Equal(HttpStatusCode.Gone, await Post("/frost/dkg/round1", JsonConvert.SerializeObject(new { SessionId = session.SessionId, ValidatorAddress = "xV2", CommitmentData = "{}" }), null));
                Assert.Equal(HttpStatusCode.Gone, await Post("/frost/dkg/share", "{}", null));
                Assert.Equal(HttpStatusCode.Gone, await Post("/frost/dkg/round3", "{}", null));
                Assert.Equal(new[] { "xV1" }, session.Round1Commitments.Keys.ToArray());
                Assert.True(session.ReceivedSharesJson.IsEmpty);
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _); }
        }

        /// <summary>
        /// A dealer reveals a share's sealing key against its recipient's signed report. A leader (or, for an unsigned
        /// creation, anyone holding the share-distribution signature) could draw such reports from participants after the
        /// ceremony had finished, have every dealer reveal, and interpolate the key of a vault that goes on chain (fourth
        /// review, round 10). A report and an attestation now exclude each other: a session that reports never finishes
        /// or attests and takes no more batches, and a finished session takes no batch, so it cannot be made to report.
        /// </summary>
        [Fact]
        public async Task ASessionThatReported_NeverAttests_AndAFinishedOneCannotBeMadeToReport()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var leader = new EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(leader.publicKey().toString()).ToLowerInvariant();
            var leaderAddress = VerifiedXCore.Data.AccountData.GetHumanAddress(pub);
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = "xV1";
            DKGSession Open(bool completed) => new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = leaderAddress, StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 67, MyParticipantIndex = 1, Round2Secret = "secret", IsCompleted = completed,
            };
            var reporting = Open(completed: false);
            var finished = Open(completed: true);
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(reporting.SessionId, reporting));
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(finished.SessionId, finished));
            try
            {
                async Task<(HttpStatusCode Code, string Body)> Batch(DKGSession session)
                {
                    var body = JsonConvert.SerializeObject(new
                    {
                        LeaderAddress = leaderAddress, Timestamp = _now,
                        LeaderSignature = VerifiedXCore.Services.SignatureService.CreateSignature($"{session.SessionId}.{leaderAddress}.{_now}", leader, pub),
                        AllGeneratedShares = new { xV2 = "", xV3 = "" },
                    });
                    var response = await client.PostAsync($"/frost/dkg/shares/{session.SessionId}", new StringContent(body, Encoding.UTF8, "application/json"));
                    return (response.StatusCode, await response.Content.ReadAsStringAsync());
                }

                // Empty entries: this validator reports both dealers, and that session is done for good.
                var (code, text) = await Batch(reporting);
                Assert.Equal(HttpStatusCode.OK, code);
                Assert.NotNull(JObject.Parse(text)["Accusations"]?["xV2"]);
                Assert.True(reporting.Reported);
                Assert.Equal(HttpStatusCode.Conflict, (await Batch(reporting)).Code);           // no second batch
                reporting.IsCompleted = true;                                                    // even were it marked finished
                var result = await client.GetAsync($"/frost/dkg/result/{reporting.SessionId}");
                Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);                        // no attestation

                // A finished session takes no batch, so no report can be drawn from it.
                (code, text) = await Batch(finished);
                Assert.Equal(HttpStatusCode.Conflict, code);
                Assert.False(finished.Reported);
            }
            finally
            {
                Globals.ValidatorAddress = priorValidator;
                FrostSessionStorage.DKGSessions.TryRemove(reporting.SessionId, out _);
                FrostSessionStorage.DKGSessions.TryRemove(finished.SessionId, out _);
            }
        }

        /// <summary>
        /// Two batches at once could leave one session both reporting and attesting. A batch is taken, and an attestation
        /// given, under the session's lock: an attestation asked for while a batch is being taken waits for it, and sees
        /// the report it made (fourth review, round 11).
        /// </summary>
        [Fact]
        public async Task AnAttestation_WaitsForTheBatchBeingTaken_AndSeesItsReport()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xL", StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 67, IsCompleted = true,
            };
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                await session.Gate.WaitAsync();                       // a batch being taken
                var asked = client.GetAsync($"/frost/dkg/result/{session.SessionId}");
                await Task.Delay(300);
                Assert.False(asked.IsCompleted);                      // the attestation waits for it
                session.Reported = true;                              // the batch reported a share
                session.Gate.Release();
                Assert.Equal(HttpStatusCode.Conflict, (await asked).StatusCode);
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _); }
        }

        /// <summary>The start's hash covers everything it says: another participant list, threshold, vault or contract is another start.</summary>
        [Fact]
        public void AStartsHash_CoversEverythingItSays()
        {
            FrostDKGStartRequest Start() => new() { SessionId = "s", SmartContractUID = "sc", LeaderAddress = "xL", Timestamp = 1, RequiredThreshold = 67, ParticipantAddresses = new List<string> { "xB", "xA", "xC" }, RekeyOf = "v" };
            var hash = FrostDkgLeaderAuth.StartBodyHash(Start());
            var reordered = Start(); reordered.ParticipantAddresses = new List<string> { "xC", "xB", "xA" };
            Assert.Equal(hash, FrostDkgLeaderAuth.StartBodyHash(reordered)); // the set, not its order
            foreach (var change in new Action<FrostDKGStartRequest>[]
            {
                r => r.ParticipantAddresses = new List<string> { "xA", "xB" }, r => r.RequiredThreshold = 100, r => r.RekeyOf = "w",
                r => r.SmartContractUID = "other", r => r.Timestamp = 2, r => r.LeaderAddress = "xM", r => r.SessionId = "t",
            })
            {
                var changed = Start();
                change(changed);
                Assert.NotEqual(hash, FrostDkgLeaderAuth.StartBodyHash(changed));
            }
        }

        /// <summary>
        /// A start's hash reads its participants as a set, so a copy listing one twice had the leader's hash and signature,
        /// opened a session with one signer too many, and its validator was later dropped as failing. Each participant is
        /// listed once. And an idle or given-up session no longer keeps another leader off its UID (fourth review, round 5).
        /// </summary>
        [Fact]
        public async Task AStart_ListsEachParticipantOnce_AndOnlyACeremonyUnderWayHoldsItsUid()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var client = server.CreateClient();
            var body = JsonConvert.SerializeObject(new FrostDKGStartRequest
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xLeader", LeaderSignature = "sig", Timestamp = _now,
                ParticipantAddresses = new List<string> { "xV1", "xV2", "xV2" }, RequiredThreshold = 67,
            });
            var response = await client.PostAsync("/frost/dkg/start", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("each participant once", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["Message"]!);

            var idle = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc-idle", LeaderAddress = "xOther", StartTimestamp = _now - 600, LastProgressTimestamp = _now - 600 };
            var given = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc-given", LeaderAddress = "xOther", StartTimestamp = _now, LastProgressTimestamp = _now, IsAborted = true };
            var working = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc-working", LeaderAddress = "xOther", StartTimestamp = _now, LastProgressTimestamp = _now };
            foreach (var s in new[] { idle, given, working }) Assert.True(FrostSessionStorage.DKGSessions.TryAdd(s.SessionId, s));
            try
            {
                Assert.False(FrostSessionStorage.HasInProgressDkgForContractByOtherLeader("sc-idle", "xLeader", null));
                Assert.False(FrostSessionStorage.HasInProgressDkgForContractByOtherLeader("sc-given", "xLeader", null));
                Assert.True(FrostSessionStorage.HasInProgressDkgForContractByOtherLeader("sc-working", "xLeader", null));
            }
            finally { foreach (var s in new[] { idle, given, working }) FrostSessionStorage.DKGSessions.TryRemove(s.SessionId, out _); }
        }

        /// <summary>
        /// A copy without the leader's full signature, with other content, opened a validator's session first and the
        /// leader's own start was refused. The signed start takes the place of such a session while nothing has been done
        /// in it, never of a signed one or one under way (fourth review, round 6).
        /// </summary>
        [Fact]
        public void TheLeadersSignedStart_TakesThePlaceOfAnUnsignedCopy_ThatHasDoneNothing()
        {
            var copy = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xL", StartTimestamp = _now, LastProgressTimestamp = _now, StartBodyHash = "other" };
            Assert.True(FrostSessionStorage.NoteDkgStart(copy.SessionId, _now));
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(copy.SessionId, copy));
            try
            {
                Assert.False(FrostSessionStorage.MakeWayForSignedStart(copy.SessionId, "other"));    // the same start: nothing to replace
                copy.IsCompleted = true;
                Assert.False(FrostSessionStorage.MakeWayForSignedStart(copy.SessionId, "signed"));   // finished
                copy.IsCompleted = false; copy.LeaderSigned = true;
                Assert.False(FrostSessionStorage.MakeWayForSignedStart(copy.SessionId, "signed"));   // signed already
                copy.LeaderSigned = false;
                Assert.True(FrostSessionStorage.MakeWayForSignedStart(copy.SessionId, "signed"));
                Assert.False(FrostSessionStorage.DKGSessions.ContainsKey(copy.SessionId));
                Assert.True(copy.IsAborted);
                Assert.False(FrostSessionStorage.DkgStartWasSeen(copy.SessionId));                    // the signed start can open it
                Assert.True(FrostSessionStorage.MakeWayForSignedStart(Guid.NewGuid().ToString(), "x")); // nothing there: nothing in the way
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(copy.SessionId, out _); }
        }

        /// <summary>
        /// A session of the very start the leader signs is never replaced, nor one that reported a share: a session that
        /// reported and was then replaced by a signed copy of its own start attested a key its reports still opened (round
        /// 11). (A copy moved on by someone else's Round 2 set then only delays a creation; a re-key is never unsigned.)
        /// </summary>
        [Fact]
        public void TheLeadersSignedStart_NeverReplacesItsOwnStart_OrASessionThatReported()
        {
            var same = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = "xL", StartTimestamp = _now, LastProgressTimestamp = _now, StartBodyHash = "same", Stage = 1 };
            var reported = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc2", LeaderAddress = "xL", StartTimestamp = _now, LastProgressTimestamp = _now, StartBodyHash = "other", Stage = 2, Reported = true };
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(same.SessionId, same));
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(reported.SessionId, reported));
            try
            {
                Assert.False(FrostSessionStorage.MakeWayForSignedStart(same.SessionId, "same"));
                Assert.False(FrostSessionStorage.MakeWayForSignedStart(reported.SessionId, "signed"));
                Assert.True(FrostSessionStorage.DKGSessions.ContainsKey(same.SessionId));
                Assert.True(FrostSessionStorage.DKGSessions.ContainsKey(reported.SessionId));
            }
            finally
            {
                FrostSessionStorage.DKGSessions.TryRemove(same.SessionId, out _);
                FrostSessionStorage.DKGSessions.TryRemove(reported.SessionId, out _);
            }
        }

        /// <summary>
        /// A session id was only a name: another validator opened one under the leader's id at a participant first, with its
        /// own signed start, and the leader's start was refused there. Ids carry their leader's tag, a tagged id is taken only
        /// from that leader, and a re-key's id is always tagged; a re-key's UID carries digits only its leader makes (round 7).
        /// </summary>
        [Fact]
        public void ASessionIdAndAReKeysUid_AreTheirLeadersOwn()
        {
            var id = FrostDkgLeaderAuth.NewSessionId("xLeader");
            Assert.Null(FrostDkgLeaderAuth.SessionIdRefusal(id, "xLeader", rekey: true, bodySigned: true));
            Assert.Contains("another leader", FrostDkgLeaderAuth.SessionIdRefusal(id, "xOther", rekey: false, bodySigned: true));
            Assert.Null(FrostDkgLeaderAuth.SessionIdRefusal(Guid.NewGuid().ToString(), "xOther", rekey: false, bodySigned: false)); // a web wallet's creation
            Assert.Contains("tagged with its leader", FrostDkgLeaderAuth.SessionIdRefusal(Guid.NewGuid().ToString(), "xLeader", rekey: true, bodySigned: true));
            // An unsigned copy under a leader's tagged id (it could be carried to a finished ceremony first; round 8).
            Assert.Contains("only with its leader's signature", FrostDkgLeaderAuth.SessionIdRefusal(id, "xLeader", rekey: false, bodySigned: false));

            var key = new VerifiedXCore.Bitcoin.Attestation.VaultDepositScriptRow { SmartContractUID = "v", Generation = 0, RekeyDueSinceVfxHeight = 5 };
            var uid = VerifiedXCore.Bitcoin.Attestation.BtcVaultKeys.NewKeyUid(key, "xLeader");
            Assert.Null(VerifiedXCore.Bitcoin.Attestation.BtcVaultKeys.KeyUidLeaderRefusal(uid, "xLeader"));
            Assert.Contains("another leader", VerifiedXCore.Bitcoin.Attestation.BtcVaultKeys.KeyUidLeaderRefusal(uid, "xOther"));
            Assert.InRange(long.Parse(VerifiedXCore.Bitcoin.Attestation.BtcVaultKeys.KeyUidTime(uid)), TimeUtil.GetTime() - 5, TimeUtil.GetTime() + 3_600);
        }

        /// <summary>
        /// A dealer's commitment of higher degree than the threshold makes a key that needs more signers than were
        /// attested. Its package is named invalid, and Round 2 refuses it (fourth review, round 6).
        /// </summary>
        [Fact]
        public void ACommitmentOfAnotherDegree_IsNamedInvalid()
        {
            var (two, _, e1) = FrostNative.DKGRound1Generate(1, 3, 2);
            var (three, _, e2) = FrostNative.DKGRound1Generate(2, 3, 3);
            Assert.Equal(FrostNative.SUCCESS, e1);
            Assert.Equal(FrostNative.SUCCESS, e2);
            Assert.True(FrostDkgBlame.CommitmentHasCoefficients(two, 2));
            Assert.False(FrostDkgBlame.CommitmentHasCoefficients(three, 2));
            Assert.False(FrostDkgBlame.CommitmentHasCoefficients("not json", 2));
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
