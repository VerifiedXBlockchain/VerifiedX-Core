using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A caster's answer in a winner-vote or proof-set exchange holds one entry a committee member, and is refused by its
    /// shape before it is read when it holds more. A vote or commitment no newer than the one held from its sender is
    /// not verified again: copies of one signed vote cost one signature check, not one each.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class AgreementAnswerBoundsTests : IDisposable
    {
        private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, string>> _priorVotes = Globals.CasterWinnerVoteDict;
        private readonly (PrivateKey Key, string Pub, string Address) _caster = NewKey();
        private const long Height = 41;

        public AgreementAnswerBoundsTests() => Globals.CasterWinnerVoteDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, string>>();

        public void Dispose()
        {
            WinnerVoteStore.PruneBelow(long.MaxValue);
            Globals.CasterWinnerVoteDict = _priorVotes;
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private SignedWinnerVote Vote(long sequence, string winner = "xWinner")
        {
            var vote = new SignedWinnerVote { BlockHeight = Height, VoterAddress = _caster.Address, WinnerAddress = winner, Sequence = sequence };
            vote.Signature = SignatureService.CreateSignature(vote.SigningMessage(), _caster.Key, _caster.Pub);
            return vote;
        }

        [Fact]
        public void A_copy_of_a_held_vote_is_not_verified_again()
        {
            bool IsCommittee(string a) => a == _caster.Address;
            Assert.Equal(WinnerVoteRecordResult.Stored, WinnerVoteStore.TryRecord(Vote(5), IsCommittee));

            // Copies whose signatures are broken: a copy no newer than the held vote is not checked at all.
            var copy = Vote(5);
            copy.Signature = "broken";
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 2_000; i++) Assert.Equal(WinnerVoteRecordResult.AlreadyHeld, WinnerVoteStore.TryRecord(copy, IsCommittee, relayed: true));
            Assert.True(clock.ElapsedMilliseconds < 500, $"took {clock.ElapsedMilliseconds} ms");

            // A newer vote is still verified: a broken one is refused, a signed one replaces the held vote.
            var newer = Vote(6, "xOther");
            newer.Signature = "broken";
            Assert.Equal(WinnerVoteRecordResult.Invalid, WinnerVoteStore.TryRecord(newer, IsCommittee, relayed: true));
            Assert.Equal(WinnerVoteRecordResult.Invalid, WinnerVoteStore.TryRecord(copy, IsCommittee)); // a caster's own request is always verified
            Assert.Equal(WinnerVoteRecordResult.Stored, WinnerVoteStore.TryRecord(Vote(6, "xOther"), IsCommittee));
            Assert.Equal("xOther", Globals.CasterWinnerVoteDict[Height][_caster.Address]);
        }

        [Fact]
        public void An_answer_holds_one_vote_a_committee_member_and_no_more()
        {
            var vote = Vote(1);
            var honest = new WinnerVoteExchangeResponse { BlockHeight = Height, SignedVotes = Enumerable.Repeat(vote, 10).ToList() };
            Assert.Null(BoundedJson.Refusal(JsonConvert.SerializeObject(honest), BoundedJson.ValidatorApi, typeof(WinnerVoteExchangeResponse)));

            var flooded = new WinnerVoteExchangeResponse { BlockHeight = Height, SignedVotes = Enumerable.Repeat(vote, 2_000).ToList() };
            Assert.Contains("list holds more than", BoundedJson.Refusal(JsonConvert.SerializeObject(flooded), BoundedJson.ValidatorApi, typeof(WinnerVoteExchangeResponse)));

            var proofSets = new ProofSetExchangeResponse
            {
                BlockHeight = Height,
                Commitments = Enumerable.Range(0, 2_000).ToDictionary(i => "x" + i, _ => new ProofSetCommitment { BlockHeight = Height, CommitmentHash = "h" }),
            };
            Assert.Contains("object holds more than", BoundedJson.Refusal(JsonConvert.SerializeObject(proofSets), BoundedJson.ValidatorApi, typeof(ProofSetExchangeResponse)));
        }

        [Fact]
        public void A_vote_may_exclude_as_many_producers_as_there_are_validators()
        {
            var vote = Vote(1);
            vote.ExcludedAddresses = Enumerable.Range(0, 300).Select(i => "xProducer" + i).ToList();
            var answer = new WinnerVoteExchangeResponse { BlockHeight = Height, SignedVotes = new List<SignedWinnerVote> { vote } };
            Assert.Null(BoundedJson.Refusal(JsonConvert.SerializeObject(answer), BoundedJson.ValidatorApi, typeof(WinnerVoteExchangeResponse)));
        }

        [Fact]
        public void Both_agreement_rounds_judge_an_answer_by_its_shape_before_reading_it()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "VerifiedXCore", "Nodes", "BlockcasterNode.cs"));
            Assert.Contains("BoundedJson.Refusal(body, Utilities.BoundedJson.ValidatorApi, typeof(WinnerVoteExchangeResponse))", source);
            Assert.Contains("BoundedJson.Refusal(body, Utilities.BoundedJson.ValidatorApi, typeof(Models.ProofSetExchangeResponse))", source);
            Assert.DoesNotContain("DeserializeAnonymousType(body, new { BlockHeight = 0L, SignedVotes", source);
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
    }
}
