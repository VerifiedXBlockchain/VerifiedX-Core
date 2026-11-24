using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A vault's key ceremony is led by an account that holds, on chain, what the vault's creation costs (nothing is
    /// charged). And whoever leads them, all key ceremonies on a validator together hold a bounded amount of memory:
    /// a ceremony that no longer holds a place is dropped, oldest first, to make room, and when only ceremonies under
    /// way hold it, a new commitment set is answered busy before any work is done for it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostCeremonyMemoryTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly List<string> _sessions = new();

        public FrostCeremonyMemoryTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ceremony_memory_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.FrostFFIAvailable = true;
        }

        public void Dispose()
        {
            foreach (var id in _sessions) FrostSessionStorage.DKGSessions.TryRemove(id, out _);
            Globals.FrostFFIAvailable = _priorLibrary;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        // -----------------------------------------------------------------------------------------------------------
        // The leader's balance
        // -----------------------------------------------------------------------------------------------------------

        [Fact]
        public void The_leader_holds_what_the_largest_vault_creation_costs_and_nothing_is_charged()
        {
            Assert.Equal(0.00512M, FrostSessionStorage.MinDkgLeaderBalance); // 256 KB at 0.00002 VFX a KB
            Assert.Equal(VerifiedXCore.Services.FeeCalcService.CalculateFeeForBytes(Globals.MaxVbtcVaultTxSizeBytes), FrostSessionStorage.MinDkgLeaderBalance);

            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = "xFunded", Balance = FrostSessionStorage.MinDkgLeaderBalance, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = "xShort", Balance = 0.001M, Nonce = 0 });

            Assert.Null(FrostSessionStorage.LeaderBalanceRefusal("xFunded"));
            Assert.Contains("nothing is charged", FrostSessionStorage.LeaderBalanceRefusal("xShort"));
            Assert.Contains("nothing is charged", FrostSessionStorage.LeaderBalanceRefusal("xMadeUpForTheOccasion"));
            Assert.Equal(FrostSessionStorage.MinDkgLeaderBalance, StateData.GetSpecificAccountStateTrei("xFunded")!.Balance); // nothing moved
        }

        [Fact]
        public void The_start_route_asks_the_leader_balance_rule_for_every_vault_ceremony()
        {
            // The rule's production caller: the non-re-key branch of the start route's leader check.
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "VerifiedXCore", "Bitcoin", "FROST", "FrostStartup.cs"));
            Assert.Contains("string.IsNullOrEmpty(request.RekeyOf) ? FrostSessionStorage.LeaderBalanceRefusal(request.LeaderAddress)", source);
        }

        /// <summary>The repository holding this test file, wherever the build output is.</summary>
        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

        // -----------------------------------------------------------------------------------------------------------
        // Memory all ceremonies hold
        // -----------------------------------------------------------------------------------------------------------

        private DKGSession Held(long ageSeconds, int commitmentChars, bool completed = false)
        {
            var now = TimeUtil.GetTime();
            var s = new DKGSession
            {
                SessionId = $"held-{Guid.NewGuid():N}", SmartContractUID = "sc-held", LeaderAddress = "xFundedLeader" + Guid.NewGuid().ToString("N")[..6],
                ParticipantAddresses = new List<string> { "x1", "x2" }, StartTimestamp = now - ageSeconds, LastProgressTimestamp = now - ageSeconds, IsCompleted = completed,
            };
            s.Round1Commitments["x1"] = new string('c', commitmentChars);
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(s.SessionId, s));
            _sessions.Add(s.SessionId);
            return s;
        }

        private async Task<HttpResponseMessage> PostRound2(DKGSession session)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var package = JsonConvert.SerializeObject(new { header = new { version = 0 }, commitment = new[] { new string('0', 66) }, proof_of_knowledge = new string('1', 128) });
            var set = JsonConvert.SerializeObject(new Dictionary<string, string> { ["x1"] = package, ["x2"] = package });
            return await server.CreateClient().PostAsync($"/frost/dkg/round2/{session.SessionId}", new StringContent(set, Encoding.UTF8, "application/json"));
        }

        private DKGSession Receiving()
        {
            var now = TimeUtil.GetTime();
            var s = new DKGSession
            {
                SessionId = $"receiving-{Guid.NewGuid():N}", SmartContractUID = "sc-receiving", LeaderAddress = "xLeader", LeaderSigned = false,
                ParticipantAddresses = new List<string> { "x1", "x2" }, StartTimestamp = now, LastProgressTimestamp = now, Round1SecretPackage = "secret",
            };
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(s.SessionId, s));
            _sessions.Add(s.SessionId);
            return s;
        }

        [Fact]
        public async Task Ceremonies_that_hold_no_place_are_dropped_oldest_first_to_make_room()
        {
            var half = (int)(FrostSessionStorage.MAX_DKG_HELD_BYTES_PER_SOURCE / 4); // chars: each holds half the network's share
            var oldest = Held(ageSeconds: 900, commitmentChars: half);
            var older = Held(ageSeconds: 600, commitmentChars: half);
            var session = Receiving();

            var answer = await PostRound2(session);

            Assert.DoesNotContain("Busy: the key ceremonies", await answer.Content.ReadAsStringAsync());
            Assert.False(FrostSessionStorage.DKGSessions.ContainsKey(oldest.SessionId)); // dropped to make room
            Assert.True(FrostSessionStorage.DKGSessions.ContainsKey(older.SessionId));   // no more than needed
            Assert.True(oldest.IsAborted);
        }

        [Fact]
        public async Task When_only_ceremonies_under_way_hold_the_memory_a_commitment_set_is_answered_busy()
        {
            var half = (int)(FrostSessionStorage.MAX_DKG_HELD_BYTES_PER_SOURCE / 4);
            var a = Held(ageSeconds: 10, commitmentChars: half);
            var b = Held(ageSeconds: 10, commitmentChars: half);
            var session = Receiving();

            var answer = await PostRound2(session);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
            Assert.Contains("Busy: the key ceremonies started from this network", await answer.Content.ReadAsStringAsync());
            Assert.True(FrostSessionStorage.DKGSessions.ContainsKey(a.SessionId) && FrostSessionStorage.DKGSessions.ContainsKey(b.SessionId));
            Assert.True(string.IsNullOrEmpty(session.Round2Secret)); // no work was done for the refused set
        }

        [Fact]
        public void A_finished_ceremony_is_kept_until_its_leader_has_had_time_to_collect_it()
        {
            var now = TimeUtil.GetTime();
            var justFinished = Held(ageSeconds: 300, commitmentChars: 10, completed: true);
            var longFinished = Held(ageSeconds: FrostSessionStorage.DKG_LEADER_CAP_WINDOW_SECONDS + 60, commitmentChars: 10, completed: true);
            var idle = Held(ageSeconds: FrostSessionStorage.DKG_UNPROGRESSED_SECONDS + 10, commitmentChars: 10);
            var working = Held(ageSeconds: 5, commitmentChars: 10);

            Assert.False(FrostSessionStorage.DkgMayBeDropped(justFinished, now));
            Assert.True(FrostSessionStorage.DkgMayBeDropped(longFinished, now));
            Assert.True(FrostSessionStorage.DkgMayBeDropped(idle, now));
            Assert.False(FrostSessionStorage.DkgMayBeDropped(working, now));
        }

        [Fact]
        public void The_budgets_hold_a_networks_eight_honest_ceremonies_up_to_350_validators()
        {
            Assert.InRange(FrostSessionStorage.HonestCeremonyBytes(162), 1L * 1024 * 1024, 4L * 1024 * 1024);  // mainnet today
            Assert.InRange(FrostSessionStorage.HonestCeremonyBytes(350), 10L * 1024 * 1024, 14L * 1024 * 1024);
            Assert.Equal(FrostSessionStorage.HonestCeremonyBytes(350), FrostSessionStorage.HonestCeremonyBytes(500)); // sized no further
            // With no validators known (a test, or a node starting) the floors hold.
            Assert.True(FrostSessionStorage.MAX_DKG_HELD_BYTES_PER_SOURCE >= 32L * 1024 * 1024);
            Assert.True(FrostSessionStorage.MAX_DKG_HELD_BYTES >= Math.Max(256L * 1024 * 1024, 3 * FrostSessionStorage.MAX_DKG_HELD_BYTES_PER_SOURCE));
            Assert.True(FrostSessionStorage.MAX_DKG_SESSIONS_PER_SOURCE * FrostSessionStorage.HonestCeremonyBytes(350) <= 100L * 1024 * 1024);
        }

        [Fact]
        public void Room_given_to_one_request_is_not_given_to_another_until_it_is_released()
        {
            var now = TimeUtil.GetTime();
            var share = FrostSessionStorage.MAX_DKG_HELD_BYTES_PER_SOURCE;
            var first = Receiving();
            var second = Receiving();

            Assert.Null(FrostSessionStorage.MakeRoomForDkgState(first, share * 3 / 4, now, out var firstRoom));
            // The first has stored nothing yet, but its room is taken: the second cannot have it too.
            Assert.Contains("Busy", FrostSessionStorage.MakeRoomForDkgState(second, share * 3 / 4, now, out var none));
            Assert.Null(none);

            firstRoom!.Dispose();
            firstRoom.Dispose(); // giving it back twice gives back nothing more
            Assert.Null(FrostSessionStorage.MakeRoomForDkgState(second, share * 3 / 4, now, out var secondRoom));
            secondRoom!.Dispose();
        }

        [Fact]
        public void The_start_route_gives_back_taken_room_as_soon_as_the_state_is_kept()
        {
            // Production callers: the room is given back where the state is stored, before the answer is written.
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "VerifiedXCore", "Bitcoin", "FROST", "FrostStartup.cs"));
            var stored = source.IndexOf("FrostSessionStorage.MarkDkgStage(session, FrostSessionStorage.DkgStage.Round2);", StringComparison.Ordinal);
            Assert.True(stored > 0);
            Assert.InRange(source.IndexOf("round2Reserved?.Dispose();", stored, StringComparison.Ordinal) - stored, 1, 200);
            var started = source.IndexOf("FrostSessionStorage.MakeRoomForDkgState(session, 0, dkgNow, out var startTaken)", StringComparison.Ordinal);
            Assert.True(started > 0); // a start counts what it keeps before the session is kept
            Assert.True(source.IndexOf("FrostSessionStorage.DKGSessions.TryAdd(request.SessionId, session)", started, StringComparison.Ordinal) > started);
            var kept = source.IndexOf("var round1Kept = session.Round1Commitments.TryAdd", StringComparison.Ordinal);
            Assert.InRange(source.IndexOf("round1Reserved?.Dispose();", kept, StringComparison.Ordinal) - kept, 1, 200);
        }

        [Fact]
        public void What_a_ceremony_holds_counts_its_commitments_shares_and_secrets()
        {
            var s = new DKGSession { Round1SecretPackage = new string('s', 100), Round2Secret = new string('r', 50), GeneratedSharesJson = new string('g', 200) };
            s.Round1Commitments["x1"] = new string('c', 1000);
            s.ReceivedSharesJson["x2"] = new string('h', 300);
            Assert.Equal(2L * (100 + 50 + 200 + 1000 + 300), FrostSessionStorage.DkgHeldBytes(s));
        }
    }
}
