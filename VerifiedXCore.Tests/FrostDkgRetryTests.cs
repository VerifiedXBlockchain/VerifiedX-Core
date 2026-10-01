using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Mainnet report: a vBTC vault key ceremony with 162 validators finished on 159 and failed, because the contract
    /// needs an attestation from every participant - so any 3 nodes, by accident or on purpose, could block vault
    /// creation. The coordinator now drops the participants that caused a failed attempt and reruns (each attempt under
    /// its own contract UID), and the culprit is identified correctly: Round 1 commitments are checked by the coordinator,
    /// Round 2 shares by their recipients (Feldman check), and recipients' reports are resolved so a dishonest participant
    /// cannot remove honest ones for free.
    /// </summary>
    [Collection("FrostDkgRetry")]
    public class FrostDkgRetryTests : IDisposable
    {
        public void Dispose() => FrostMPCService.AttemptRunnerForTests = null;

        private static string Id(int i) => i.ToString("x").PadLeft(64, '0');

        // ── Round 2 share check (recipient side) ─────────────────────────────────────────────────────────────────

        /// <summary>A real n-participant Round 1 and the plaintext Round 2 shares each dealer produces.</summary>
        private static (string[] Commitments, Dictionary<int, JObject> SharesByDealer) RealDkg(int n, int min)
        {
            var commitments = new string[n + 1];
            var secrets = new string[n + 1];
            for (ushort i = 1; i <= n; i++)
            {
                var (c, s, e) = FrostNative.DKGRound1Generate(i, (ushort)n, (ushort)min);
                Assert.Equal(FrostNative.SUCCESS, e);
                (commitments[i], secrets[i]) = (c, s);
            }
            var shares = new Dictionary<int, JObject>();
            for (int dealer = 1; dealer <= n; dealer++)
            {
                var others = new JObject();
                for (int j = 1; j <= n; j++) if (j != dealer) others[Id(j)] = JToken.Parse(commitments[j]);
                var (sh, _, err) = FrostNative.DKGRound2GenerateShares(secrets[dealer], others.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(FrostNative.SUCCESS, err);
                shares[dealer] = JObject.Parse(sh);
            }
            return (commitments, shares);
        }

        [Fact]
        public void HonestShares_MatchTheirDealersCommitment()
        {
            var (commitments, shares) = RealDkg(5, 3);
            for (int dealer = 1; dealer <= 5; dealer++)
                for (int recipient = 1; recipient <= 5; recipient++)
                    if (recipient != dealer)
                        Assert.True(FrostDkgBlame.ShareMatchesCommitment(shares[dealer][Id(recipient)]!.ToString(), commitments[dealer], Id(recipient)));
        }

        [Fact]
        public void ABadShare_IsAttributedToItsDealer()
        {
            var (commitments, shares) = RealDkg(5, 3);
            // The share meant for participant 3, checked as if it were participant 2's: wrong evaluation point.
            Assert.False(FrostDkgBlame.ShareMatchesCommitment(shares[1][Id(3)]!.ToString(), commitments[1], Id(2)));
            // Dealer 1's share checked against dealer 4's commitment.
            Assert.False(FrostDkgBlame.ShareMatchesCommitment(shares[1][Id(2)]!.ToString(), commitments[4], Id(2)));
            // A tampered signing share.
            var tampered = (JObject)shares[1][Id(2)]!.DeepClone();
            var hex = (string)tampered["signing_share"]!;
            tampered["signing_share"] = (hex[0] == '0' ? "1" : "0") + hex.Substring(1);
            Assert.False(FrostDkgBlame.ShareMatchesCommitment(tampered.ToString(), commitments[1], Id(2)));
            // Unreadable input never counts as a match.
            Assert.False(FrostDkgBlame.ShareMatchesCommitment("garbage", commitments[1], Id(2)));
            Assert.False(FrostDkgBlame.ShareMatchesCommitment(shares[1][Id(2)]!.ToString(), "garbage", Id(2)));
        }

        // ── Round 1 commitment check (coordinator side) ──────────────────────────────────────────────────────────

        private static (List<string> Participants, Dictionary<string, string> Packages) Round1(int n, int min)
        {
            var participants = Enumerable.Range(0, n).Select(i => "R" + i.ToString("D3") + new string('v', 30)).ToList();
            var ids = FrostStartup.BuildAddressToIdentifierMap(participants);
            var packages = new Dictionary<string, string>();
            foreach (var a in participants)
            {
                var (c, _, e) = FrostNative.DKGRound1Generate((ushort)Convert.ToInt32(ids[a], 16), (ushort)n, (ushort)min);
                Assert.Equal(FrostNative.SUCCESS, e);
                packages[a] = c;
            }
            return (participants, packages);
        }

        [Fact]
        public void ValidRound1Packages_AreAllAccepted()
        {
            var (participants, packages) = Round1(7, 4);
            Assert.Empty(FrostDkgBlame.InvalidRound1Packages(packages, participants, 4)!);
        }

        [Fact]
        public void ValidRound1Packages_AtMainnetSize_AreAccepted()
        {
            var (participants, packages) = Round1(162, 83);
            Assert.Empty(FrostDkgBlame.InvalidRound1Packages(packages, participants, 83)!);
        }

        [Fact]
        public void InvalidRound1Packages_AreNamed()
        {
            var (participants, packages) = Round1(9, 5);
            var badProof = JObject.Parse(packages[participants[2]]);
            var proof = (string)badProof["proof_of_knowledge"]!;
            badProof["proof_of_knowledge"] = proof.Substring(0, proof.Length - 1) + (proof[^1] == '0' ? "1" : "0");
            packages[participants[2]] = badProof.ToString();                                  // invalid proof of knowledge
            packages[participants[4]] = Round1(9, 3).Packages.Values.First();                  // wrong commitment length (and identifier)
            packages[participants[6]] = "not json";                                             // unreadable
            packages[participants[7]] = packages[participants[8]];                              // another participant's package

            var invalid = FrostDkgBlame.InvalidRound1Packages(packages, participants, 5)!;
            Assert.Equal(new[] { participants[2], participants[4], participants[6], participants[7] }.OrderBy(a => a), invalid.OrderBy(a => a));
        }

        // ── Resolving recipients' reports ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ADealerReportedByManyRecipients_IsTheOneDropped()
        {
            var reports = new[] { "A", "B", "C", "E" }.Select(r => (r, "D"));
            Assert.Equal(new[] { "D" }, FrostDkgBlame.ResolveAccusations(reports));
        }

        [Fact]
        public void ARecipientReportingManyDealers_IsTheOneDropped()
        {
            // E.g. a node whose own key is unavailable: it cannot open anyone's share.
            var reports = new[] { "A", "B", "C", "D" }.Select(d => ("R", d));
            Assert.Equal(new[] { "R" }, FrostDkgBlame.ResolveAccusations(reports));
        }

        [Fact]
        public void AOneToOneReport_DropsBothSides()
        {
            // Unprovable either way: an accuser can only remove an honest dealer by losing itself.
            Assert.Equal(new[] { "H", "L" }, FrostDkgBlame.ResolveAccusations(new[] { ("L", "H") }).OrderBy(x => x));
        }

        [Fact]
        public void MixedReports_DropTheCulpritAndOnlyThePairedLiar()
        {
            var reports = new[] { "A", "B", "C" }.Select(r => (r, "D")).Append(("L", "H"));
            Assert.Equal(new[] { "D", "H", "L" }, FrostDkgBlame.ResolveAccusations(reports).OrderBy(x => x));
            Assert.Empty(FrostDkgBlame.ResolveAccusations(Array.Empty<(string, string)>()));
        }

        // ── The coordinator's attempt loop ───────────────────────────────────────────────────────────────────────

        private static List<VBTCValidator> Validators(int n) =>
            Enumerable.Range(0, n).Select(i => new VBTCValidator { ValidatorAddress = "R" + i.ToString("D3") + new string('w', 30), IPAddress = "10.0.0." + (i + 1) }).ToList();

        private static FrostDKGResult ResultFor(string uid, List<VBTCValidator> participants) => new()
        {
            SmartContractUID = uid, GroupPublicKey = "02" + new string('a', 64), TaprootAddress = "bc1ptest",
            ParticipantAddresses = participants.Select(v => v.ValidatorAddress).ToList(),
        };

        [Fact]
        public async Task FailingParticipants_AreDropped_AndTheCeremonyRerunsUnderANewContractUid()
        {
            var validators = Validators(162);
            var saboteurs = validators.Take(3).Select(v => v.ValidatorAddress).ToHashSet();
            var seen = new List<(string Uid, int Count)>();
            FrostMPCService.AttemptRunnerForTests = (uid, session, participants, auth) =>
            {
                seen.Add((uid, participants.Count));
                var attempt = new FrostMPCService.DkgAttempt();
                foreach (var p in participants.Where(p => saboteurs.Contains(p.ValidatorAddress)))
                    attempt.Failed[p.ValidatorAddress] = "did not finish the key ceremony";
                if (attempt.Failed.Count == 0) attempt.Result = ResultFor(uid, participants);
                else attempt.Error = "3 validators did not finish";
                return Task.FromResult(attempt);
            };

            var ceremonyId = FrostDkgAttestation.NewContractUid();
            var run = await FrostMPCService.CoordinateDKGCeremony(ceremonyId, "ROwner", validators, 51);

            Assert.NotNull(run.Result);
            Assert.Equal(2, run.Attempts);
            Assert.Equal(new[] { 162, 159 }, seen.Select(s => s.Count));
            Assert.Equal(ceremonyId, seen[0].Uid);                                  // an untroubled ceremony keeps its id
            Assert.NotEqual(ceremonyId, run.ContractUID);                           // a rerun is attested under its own UID
            Assert.Matches(new Regex("^[0-9a-f]{32}:[0-9]+$"), run.ContractUID!);    // NEW-10 contract UID format
            Assert.Equal(saboteurs.OrderBy(a => a), run.Excluded.Keys.OrderBy(a => a));
            Assert.DoesNotContain(run.Result!.ParticipantAddresses, saboteurs.Contains);
        }

        [Fact]
        public async Task Reruns_StopAtTheParticipationFloor()
        {
            var validators = Validators(20);
            var attempts = 0;
            FrostMPCService.AttemptRunnerForTests = (uid, session, participants, auth) =>
            {
                attempts++;
                var attempt = new FrostMPCService.DkgAttempt { Error = "failed" };
                attempt.Failed[participants[0].ValidatorAddress] = "did not finish the key ceremony";
                attempt.Failed[participants[1].ValidatorAddress] = "did not finish the key ceremony";
                return Task.FromResult(attempt);
            };

            // A floor of 18: after one attempt drops 2 validators, a second would still run; after it, 16 are left.
            var run = await FrostMPCService.CoordinateDKGCeremony("c", "ROwner", validators, 51,
                participantShortfall: n => n >= 18 ? null : $"Only {n} validator(s) are reachable");

            Assert.Null(run.Result);
            Assert.Equal(2, attempts);
            Assert.Equal(4, run.Excluded.Count);
            Assert.Contains("Only 16 validator(s)", run.Error);
            Assert.Contains("4 validator(s) were dropped", run.Error);
        }

        [Fact]
        public async Task AFailureThatNamesNobody_IsNotRetried()
        {
            var attempts = 0;
            FrostMPCService.AttemptRunnerForTests = (uid, session, participants, auth) =>
            {
                attempts++;
                return Task.FromResult(new FrostMPCService.DkgAttempt { Error = "No validator returned a usable key ceremony result." });
            };
            var run = await FrostMPCService.CoordinateDKGCeremony("c", "ROwner", Validators(10), 51);
            Assert.Null(run.Result);
            Assert.Equal(1, attempts);
            Assert.Equal("No validator returned a usable key ceremony result.", run.Error);
        }

        [Fact]
        public async Task AttemptsAreCapped()
        {
            var attempts = 0;
            FrostMPCService.AttemptRunnerForTests = (uid, session, participants, auth) =>
            {
                attempts++;
                var attempt = new FrostMPCService.DkgAttempt { Error = "failed" };
                attempt.Failed[participants[0].ValidatorAddress] = "did not finish the key ceremony";
                return Task.FromResult(attempt);
            };
            var run = await FrostMPCService.CoordinateDKGCeremony("c", "ROwner", Validators(50), 51);
            Assert.Null(run.Result);
            Assert.Equal(FrostMPCService.MaxDkgAttempts, attempts);
            Assert.Equal(FrostMPCService.MaxDkgAttempts, run.Excluded.Count);
        }

        [Fact]
        public async Task PreSignedSessions_OneAttemptEach_InOrder()
        {
            var sessions = new List<string>();
            FrostMPCService.AttemptRunnerForTests = (uid, session, participants, auth) =>
            {
                sessions.Add(session);
                var attempt = new FrostMPCService.DkgAttempt { Error = "failed" };
                attempt.Failed[participants[0].ValidatorAddress] = "did not finish the key ceremony";
                return Task.FromResult(attempt);
            };
            PreSignedLeaderAuth Auth(string s) => new() { SessionId = s, StartSignature = "sig", StartTimestamp = 1 };

            // An older web wallet signs one session: one attempt.
            await FrostMPCService.CoordinateDKGCeremony("c", "ROwner", Validators(10), 51, preSignedAuths: new[] { Auth("s1") });
            Assert.Equal(new[] { "s1" }, sessions);

            sessions.Clear();
            await FrostMPCService.CoordinateDKGCeremony("c", "ROwner", Validators(10), 51, preSignedAuths: new[] { Auth("s1"), Auth("s2"), Auth("s3") });
            Assert.Equal(new[] { "s1", "s2", "s3" }, sessions);
        }

        [Fact]
        public void ValidatorShareHandler_ReportsBadSenders_AndDoesNotFinalizeOnThem()
        {
            // No live FROST host in the test suite: pin the endpoint wiring (the checks themselves are tested above).
            var repo = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(ThisFile()))!;
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, "VerifiedXCore", "Bitcoin", "FROST", "FrostStartup.cs"));
            Assert.Contains("FrostDkgBlame.ShareMatchesCommitment(shareForMe, senderCommitment, myFrostIdentifier!)", src);
            Assert.Contains("accusations.Count == 0 && receivedCount >= totalOtherParticipants", src);
            Assert.Contains("Accusations = accusations", src);
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
