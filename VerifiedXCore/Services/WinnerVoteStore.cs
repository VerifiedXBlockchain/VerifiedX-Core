using System.Collections.Concurrent;

namespace VerifiedXCore.Services
{
    /// <summary>VX-08: one caster's winner vote for a height, signed by that caster.</summary>
    public class SignedWinnerVote
    {
        public long BlockHeight { get; set; }
        public string VoterAddress { get; set; } = "";
        public string WinnerAddress { get; set; } = "";
        public List<string> ExcludedAddresses { get; set; } = new();
        /// <summary>Per-voter increasing value (unix ms). A caster re-votes on retries within a height;
        /// only a NEWER signed vote from the same voter replaces its earlier one, so replaying an older
        /// vote changes nothing.</summary>
        public long Sequence { get; set; }
        public string Signature { get; set; } = "";

        public string SigningMessage() =>
            ConsensusMessageFormatter.FormatWinnerVoteV1(BlockHeight, VoterAddress, WinnerAddress, ExcludedAddresses, Sequence);
    }

    /// <summary>
    /// VX-08: the only way a winner vote enters the tally. Votes were unsigned, the route wrote them by
    /// indexer (so anyone could overwrite a caster's vote), heights were unbounded, and the agreement
    /// client merged the vote map each peer returned — including votes that peer merely relayed — with no
    /// check at all. Now every vote, first-hand or relayed, must be signed by its voter, the voter must be
    /// an accepted caster, and a vote can only be replaced by a newer one signed by the same voter.
    /// <see cref="Globals.CasterWinnerVoteDict"/> (voter → winner, read by the tally) is written only here.
    /// </summary>
    public enum WinnerVoteRecordResult { Invalid, Stored, AlreadyHeld }

    public static class WinnerVoteStore
    {
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, SignedWinnerVote>> _signed = new();
        private static long _lastSequence;

        /// <summary>Records a vote if it is well-formed, signed by its voter and the voter is accepted.</summary>
        public static WinnerVoteRecordResult TryRecord(SignedWinnerVote? vote, Func<string, bool> isAcceptedVoter)
        {
            if (vote == null || vote.BlockHeight <= 0 || string.IsNullOrEmpty(vote.VoterAddress) || string.IsNullOrEmpty(vote.WinnerAddress))
                return WinnerVoteRecordResult.Invalid;
            if (!isAcceptedVoter(vote.VoterAddress))
                return WinnerVoteRecordResult.Invalid;
            if (!ConsensusRequestAuth.VerifySigner(vote.VoterAddress, vote.SigningMessage(), vote.Signature))
                return WinnerVoteRecordResult.Invalid;

            var perHeight = _signed.GetOrAdd(vote.BlockHeight, _ => new ConcurrentDictionary<string, SignedWinnerVote>());
            var stored = perHeight.AddOrUpdate(vote.VoterAddress, vote, (_, existing) => vote.Sequence > existing.Sequence ? vote : existing);
            if (!ReferenceEquals(stored, vote))
                return WinnerVoteRecordResult.AlreadyHeld; // an equal or newer vote from this voter is held

            Globals.CasterWinnerVoteDict.GetOrAdd(vote.BlockHeight, _ => new ConcurrentDictionary<string, string>())[vote.VoterAddress] = vote.WinnerAddress;
            if (vote.ExcludedAddresses != null && vote.ExcludedAddresses.Any())
                Globals.CasterExcludedAddressDict.GetOrAdd(vote.BlockHeight, _ => new ConcurrentDictionary<string, List<string>>())[vote.VoterAddress] = vote.ExcludedAddresses;
            return WinnerVoteRecordResult.Stored;
        }

        /// <summary>The signed votes held for a height — what a peer returns so relays stay verifiable.</summary>
        public static List<SignedWinnerVote> GetSigned(long height) =>
            _signed.TryGetValue(height, out var d) ? d.Values.ToList() : new List<SignedWinnerVote>();

        /// <summary>Signs this node's own vote with the local validator key.</summary>
        public static SignedWinnerVote CreateOwn(long height, string winnerAddress, List<string>? excluded = null)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long seq;
            while (true)
            {
                var last = Interlocked.Read(ref _lastSequence);
                seq = Math.Max(now, last + 1);
                if (Interlocked.CompareExchange(ref _lastSequence, seq, last) == last) break;
            }
            var vote = new SignedWinnerVote
            {
                BlockHeight = height,
                VoterAddress = Globals.ValidatorAddress,
                WinnerAddress = winnerAddress,
                ExcludedAddresses = excluded ?? new List<string>(),
                Sequence = seq,
            };
            vote.Signature = SignatureService.ValidatorSignature(vote.SigningMessage());
            return vote;
        }

        public static void PruneBelow(long height)
        {
            foreach (var k in _signed.Keys.Where(k => k < height).ToList())
                _signed.TryRemove(k, out _);
        }
    }
}
