using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Utilities;
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
        public FrostDkgRetryTests() => FrostMPCService.RecentDkgFailures.Clear();

        public void Dispose()
        {
            FrostMPCService.AttemptRunnerForTests = null;
            FrostMPCService.RecentDkgFailures.Clear();
        }

        private static string Id(int i) => i.ToString("x").PadLeft(64, '0');

        /// <summary>Each dealer's Round 2 secret from the last RealDkg (what it finishes Round 3 with).</summary>
        private readonly Dictionary<int, string> _round2Secrets = new();

        /// <summary>
        /// Every participant of an honest ceremony finishes with the same public key package, byte for byte, so the hash
        /// each signs into its attestation (FrostDkgAttestation.KeyPackageHash) is the same for all; a participant given
        /// another dealer's commitments finishes with another package (fourth review, round 3).
        /// </summary>
        [Fact]
        public void HonestParticipants_FinishWithTheSamePublicKeyPackage_AndOneGivenOtherCommitmentsDoesNot()
        {
            const int n = 3, min = 2;
            var (commitments, sharesByDealer) = RealDkg(n, min);
            string Finish(int me, string[] seen, Dictionary<int, JObject> shares)
            {
                // Each participant's own Round 2 secret: run Round 2 again for it from the commitments it saw.
                var others = new JObject();
                for (int j = 1; j <= n; j++) if (j != me) others[Id(j)] = JToken.Parse(seen[j]);
                var received = new JObject();
                for (int j = 1; j <= n; j++) if (j != me) received[Id(j)] = shares[j][Id(me)];
                var (gpk, keyPackage, pubkeyPackage, err) = FrostNative.DKGRound3Finalize(_round2Secrets[me], others.ToString(Newtonsoft.Json.Formatting.None), received.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(FrostNative.SUCCESS, err);
                Assert.False(string.IsNullOrEmpty(pubkeyPackage));
                return pubkeyPackage;
            }
            var packages = Enumerable.Range(1, n).Select(i => Finish(i, commitments, sharesByDealer)).ToList();
            Assert.Single(packages.Distinct());
            Assert.Single(packages.Select(FrostDkgAttestation.KeyPackageHash).Distinct());

            // Participant 1 is given another commitment for dealer 2 (and a share that matches it): it does not finish
            // with the package the others finished with.
            var (other2, other2Secret, e1) = FrostNative.DKGRound1Generate(2, n, min);
            Assert.Equal(FrostNative.SUCCESS, e1);
            var for2 = new JObject { [Id(1)] = JToken.Parse(commitments[1]), [Id(3)] = JToken.Parse(commitments[3]) };
            var (other2Shares, _, e2) = FrostNative.DKGRound2GenerateShares(other2Secret, for2.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(FrostNative.SUCCESS, e2);
            var seenBy1 = (string[])commitments.Clone();
            seenBy1[2] = other2;
            var sharesSeenBy1 = new Dictionary<int, JObject>(sharesByDealer) { [2] = JObject.Parse(other2Shares) };
            var seen = new JObject();
            for (int j = 2; j <= n; j++) seen[Id(j)] = JToken.Parse(seenBy1[j]);
            var received = new JObject();
            for (int j = 2; j <= n; j++) received[Id(j)] = sharesSeenBy1[j][Id(1)];
            var (_, _, misled, err) = FrostNative.DKGRound3Finalize(_round2Secrets[1], seen.ToString(Newtonsoft.Json.Formatting.None), received.ToString(Newtonsoft.Json.Formatting.None));
            Assert.True(err != FrostNative.SUCCESS || misled != packages[1], "a participant given other commitments finished with the same package");
        }

        // ── Round 2 share check (recipient side) ─────────────────────────────────────────────────────────────────

        /// <summary>A real n-participant Round 1 and the plaintext Round 2 shares each dealer produces.</summary>
        private (string[] Commitments, Dictionary<int, JObject> SharesByDealer) RealDkg(int n, int min)
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
                var (sh, round2Secret, err) = FrostNative.DKGRound2GenerateShares(secrets[dealer], others.ToString(Newtonsoft.Json.Formatting.None));
                Assert.Equal(FrostNative.SUCCESS, err);
                shares[dealer] = JObject.Parse(sh);
                _round2Secrets[dealer] = round2Secret;
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

        /// <summary>
        /// Reports were weighed by count, so two colluding validators could name one honest dealer and have only it
        /// dropped, attempt after attempt. A report is now settled by the share: the dealer reveals the one-time key it
        /// sealed the share with, the relayed ciphertext opens, and the share is checked against the dealer's commitment
        /// (fourth review, round 9). Real library, real sealing.
        /// </summary>
        [Fact]
        public void AReportedShare_IsShownGoodOrBad_ByTheKeyItWasSealedWith()
        {
            var (commitments, shares) = RealDkg(4, 3);
            using var recipientKey = new NBitcoin.Key();
            var recipientPub = Convert.ToHexString(recipientKey.PubKey.ToBytes()).ToLowerInvariant();
            const string sid = "session";

            // An honest dealer's share, reported as bad: the revealed key opens it and it matches (the report was false).
            var good = FrostShareCrypto.Seal(shares[1][Id(2)]!.ToString(), recipientPub, sid, Id(2), out var goodKey);
            Assert.True(FrostDkgBlame.DisputedShareIsGood(good, goodKey, recipientPub, sid, Id(2), commitments[1]));

            // A dealer that sealed another recipient's share, or a tampered one: shown bad.
            var wrongPoint = FrostShareCrypto.Seal(shares[1][Id(3)]!.ToString(), recipientPub, sid, Id(2), out var wrongPointKey);
            Assert.False(FrostDkgBlame.DisputedShareIsGood(wrongPoint, wrongPointKey, recipientPub, sid, Id(2), commitments[1]));
            var tampered = (JObject)shares[1][Id(2)]!.DeepClone();
            var hex = (string)tampered["signing_share"]!;
            tampered["signing_share"] = (hex[0] == '0' ? "1" : "0") + hex.Substring(1);
            var bad = FrostShareCrypto.Seal(tampered.ToString(), recipientPub, sid, Id(2), out var badKey);
            Assert.False(FrostDkgBlame.DisputedShareIsGood(bad, badKey, recipientPub, sid, Id(2), commitments[1]));

            // A dealer that gives no key, or a key that is not the share's: shown bad (the dealer is dropped).
            Assert.False(FrostDkgBlame.DisputedShareIsGood(good, null, recipientPub, sid, Id(2), commitments[1]));
            Assert.False(FrostDkgBlame.DisputedShareIsGood(good, wrongPointKey, recipientPub, sid, Id(2), commitments[1]));
            // Sealed for another session or identifier: it does not open as this one.
            Assert.False(FrostDkgBlame.DisputedShareIsGood(good, goodKey, recipientPub, "other", Id(2), commitments[1]));

            // The recipient opens the same ciphertext with its own key, as it always did.
            Assert.True(FrostShareCrypto.TryOpen(good, recipientKey.ToBytes(), sid, Id(2), out var opened));
            Assert.Equal(shares[1][Id(2)]!.ToString(), opened);
        }

        /// <summary>
        /// The dealer reveals a share's key only against its recipient's signed report about that share: the share is
        /// the reporting recipient's own, which it holds already. Nobody else (a coordinator collecting shares) gets one.
        /// </summary>
        [Fact]
        public async Task TheRevealRoute_AnswersOnlyTheRecipientsSignedReport()
        {
            var dealer = new EllipticCurve.PrivateKey("secp256k1");
            var dealerPub = "04" + Convert.ToHexString(dealer.publicKey().toString()).ToLowerInvariant();
            var dealerAddress = VerifiedXCore.Data.AccountData.GetHumanAddress(dealerPub);
            var recipient = new EllipticCurve.PrivateKey("secp256k1");
            var recipientPub = "04" + Convert.ToHexString(recipient.publicKey().toString()).ToLowerInvariant();
            var recipientAddress = VerifiedXCore.Data.AccountData.GetHumanAddress(recipientPub);
            var other = new EllipticCurve.PrivateKey("secp256k1");
            var otherPub = "04" + Convert.ToHexString(other.publicKey().toString()).ToLowerInvariant();
            var otherAddress = VerifiedXCore.Data.AccountData.GetHumanAddress(otherPub);
            var participants = new List<string> { dealerAddress, recipientAddress, otherAddress };
            var ids = FrostStartup.BuildAddressToIdentifierMap(participants);
            var session = new DKGSession { SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = otherAddress, ParticipantAddresses = participants,
                StartTimestamp = TimeUtil.GetTime(), LastProgressTimestamp = TimeUtil.GetTime() };
            session.SealEphemerals[ids[recipientAddress]] = "ab".PadLeft(64, '0');
            session.SealEphemerals[ids[otherAddress]] = "cd".PadLeft(64, '0');
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = dealerAddress;
            try
            {
                using var server = new Microsoft.AspNetCore.TestHost.TestServer(new Microsoft.AspNetCore.Hosting.WebHostBuilder().UseStartup<FrostStartup>());
                var client = server.CreateClient();
                async Task<(System.Net.HttpStatusCode Code, string Body)> Reveal(string accuser, string signature)
                {
                    var response = await client.PostAsync($"/frost/dkg/reveal/{session.SessionId}",
                        new System.Net.Http.StringContent(JsonConvert.SerializeObject(new { Accuser = accuser, Signature = signature }), System.Text.Encoding.UTF8, "application/json"));
                    return (response.StatusCode, await response.Content.ReadAsStringAsync());
                }
                string Sign(EllipticCurve.PrivateKey key, string pub, string message) => VerifiedXCore.Services.SignatureService.CreateSignature(message, key, pub);

                // The recipient's own signed report: its share's key.
                var (code, body) = await Reveal(recipientAddress, Sign(recipient, recipientPub, FrostDkgBlame.AccusationMessage(session.SessionId, recipientAddress, dealerAddress)));
                Assert.Equal(System.Net.HttpStatusCode.OK, code);
                Assert.Equal("ab".PadLeft(64, '0'), (string)JObject.Parse(body)["EphemeralKey"]!);
                // Somebody else's signature on that recipient's behalf, or a report about another dealer: nothing.
                (code, _) = await Reveal(recipientAddress, Sign(other, otherPub, FrostDkgBlame.AccusationMessage(session.SessionId, recipientAddress, dealerAddress)));
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, code);
                (code, _) = await Reveal(recipientAddress, Sign(recipient, recipientPub, FrostDkgBlame.AccusationMessage(session.SessionId, recipientAddress, otherAddress)));
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, code);
                // Not a participant, or the dealer itself: nothing.
                (code, _) = await Reveal("xStranger", "sig");
                Assert.Equal(System.Net.HttpStatusCode.NotFound, code);
                (code, _) = await Reveal(dealerAddress, Sign(dealer, dealerPub, FrostDkgBlame.AccusationMessage(session.SessionId, dealerAddress, dealerAddress)));
                Assert.Equal(System.Net.HttpStatusCode.NotFound, code);
            }
            finally
            {
                Globals.ValidatorAddress = priorValidator;
                FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _);
            }
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

        /// <summary>
        /// A validator that answers a start with "no room" (HTTP 429) did nothing wrong: its room can be filled by anybody
        /// for a while. It sits out this ceremony and is not remembered as failing, so the next ceremony asks it again
        /// (third pass, 14 October 2026: the coordinator remembered it for six hours).
        /// </summary>
        [Fact]
        public async Task AValidatorWithNoRoom_SitsOutThisCeremony_ButIsNotRememberedAsFailing()
        {
            var validators = Validators(20);
            var busy = validators[0].ValidatorAddress;
            var broken = validators[1].ValidatorAddress;
            FrostMPCService.AttemptRunnerForTests = (uid, session, participants, auth) =>
            {
                var attempt = new FrostMPCService.DkgAttempt();
                if (participants.Any(p => p.ValidatorAddress == busy))
                {
                    attempt.Failed[busy] = "refused the DKG start (HTTP 429)";
                    attempt.Busy.Add(busy);
                    attempt.Failed[broken] = "refused the DKG start (HTTP 500)";
                    attempt.Error = "2 of 20 validators did not join the key ceremony.";
                }
                else attempt.Result = ResultFor(uid, participants);
                return Task.FromResult(attempt);
            };

            var run = await FrostMPCService.CoordinateDKGCeremony("c", "ROwner", validators, 51);
            Assert.NotNull(run.Result);
            Assert.DoesNotContain(busy, run.Result!.ParticipantAddresses);
            Assert.False(FrostMPCService.RecentDkgFailures.ContainsKey(busy));
            Assert.True(FrostMPCService.RecentDkgFailures.ContainsKey(broken));
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

        /// <summary>Fails any attempt that includes one of these participants (like the mainnet validators that never finish).</summary>
        private static Func<string, string, List<VBTCValidator>, PreSignedLeaderAuth?, Task<FrostMPCService.DkgAttempt>> FailWhenPresent(
            HashSet<string> bad, List<int> participantCounts) => (uid, session, participants, auth) =>
        {
            participantCounts.Add(participants.Count);
            var attempt = new FrostMPCService.DkgAttempt();
            foreach (var p in participants.Where(p => bad.Contains(p.ValidatorAddress)))
                attempt.Failed[p.ValidatorAddress] = "did not finish the key ceremony";
            if (attempt.Failed.Count == 0) attempt.Result = ResultFor(uid, participants);
            else attempt.Error = "failed";
            return Task.FromResult(attempt);
        };

        [Fact]
        public async Task ALaterCeremony_LeavesOutRecentFailures_FromTheStart()
        {
            var validators = Validators(162);
            var bad = validators.Skip(10).Take(3).Select(v => v.ValidatorAddress).ToHashSet();
            var counts = new List<int>();
            FrostMPCService.AttemptRunnerForTests = FailWhenPresent(bad, counts);

            Assert.NotNull((await FrostMPCService.CoordinateDKGCeremony("c1", "ROwner", validators, 51)).Result);
            Assert.Equal(new[] { 162, 159 }, counts);

            counts.Clear();
            var second = await FrostMPCService.CoordinateDKGCeremony("c2", "ROwner", validators, 51);
            Assert.NotNull(second.Result);
            Assert.Equal(new[] { 159 }, counts);                        // no attempt spent rediscovering them
            Assert.Equal("c2", second.ContractUID);
            Assert.All(bad, a => Assert.StartsWith("failed a recent key ceremony", second.Excluded[a]));
        }

        [Fact]
        public async Task RecentFailures_AreKept_WhenLeavingThemOutWouldBreachTheFloor_AndASuccessClearsThem()
        {
            var validators = Validators(20);
            var flaky = validators[0].ValidatorAddress;
            FrostMPCService.RecentDkgFailures[flaky] = (VerifiedXCore.Utilities.TimeUtil.GetTime() + 3600, "did not finish the key ceremony");
            var counts = new List<int>();
            FrostMPCService.AttemptRunnerForTests = FailWhenPresent(new HashSet<string>(), counts);

            var run = await FrostMPCService.CoordinateDKGCeremony("c", "ROwner", validators, 51,
                participantShortfall: n => n >= 20 ? null : $"Only {n} validator(s) are reachable");

            Assert.NotNull(run.Result);
            Assert.Equal(new[] { 20 }, counts);
            Assert.Empty(run.Excluded);
            Assert.False(FrostMPCService.RecentDkgFailures.ContainsKey(flaky));   // it finished this time
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
