using System.Collections.Concurrent;
using VerifiedXCore.Models;

namespace VerifiedXCore.Services
{
    public enum ProofSetRecordResult { Invalid, Stored, AlreadyHeld }

    /// <summary>
    /// VX-16: the only way a proof-set commitment enters <see cref="Globals.CasterProofSetCommitDict"/> (the map the
    /// agreement tally reads). Commitments were unsigned; the route stored one for ANY caller-supplied address at ANY
    /// height (205 fabricated identities in the audit), and the agreement client merged every commitment a peer
    /// relayed. Now each commitment, first-hand or relayed, must be signed by its caster over VFX_PROOFSET_V1
    /// (height, caster, hash, sequence); the caster must be accepted by the caller's rule; the hash must match the
    /// list; and a caster's commitment can only be replaced by a newer one it signed (a caster re-commits when a
    /// height is retried with exclusions, so first-write-wins would freeze a stale set). Storage per height is
    /// therefore bounded by the number of accepted casters.
    /// </summary>
    public static class ProofSetCommitmentStore
    {
        /// <summary>Upper bound on addresses in one commitment (the proof set is one address per validator).</summary>
        public const int MaxAddresses = 10_000;

        private static long _lastSequence;

        public static string SigningMessage(ProofSetCommitment c) =>
            ConsensusMessageFormatter.FormatProofSetV1(c.BlockHeight, c.CasterAddress, c.CommitmentHash, c.Sequence);

        /// <summary>Records a commitment if it is well-formed, signed by its caster and the caster is accepted.</summary>
        public static ProofSetRecordResult TryRecord(ProofSetCommitment? c, Func<string, bool> isAcceptedCaster)
        {
            if (c == null || c.BlockHeight <= 0 || string.IsNullOrEmpty(c.CasterAddress) || string.IsNullOrEmpty(c.CommitmentHash))
                return ProofSetRecordResult.Invalid;
            var addresses = c.ProofAddressesSorted ?? new List<string>();
            if (addresses.Count > MaxAddresses)
                return ProofSetRecordResult.Invalid;
            if (!string.Equals(Nodes.BlockcasterNode.ComputeProofSetCommitmentHash(addresses), c.CommitmentHash, StringComparison.Ordinal))
                return ProofSetRecordResult.Invalid;
            if (!isAcceptedCaster(c.CasterAddress))
                return ProofSetRecordResult.Invalid;
            if (!ConsensusRequestAuth.VerifySigner(c.CasterAddress, SigningMessage(c), c.Signature))
                return ProofSetRecordResult.Invalid;

            var perHeight = Globals.CasterProofSetCommitDict.GetOrAdd(c.BlockHeight, _ => new ConcurrentDictionary<string, ProofSetCommitment>());
            var stored = perHeight.AddOrUpdate(c.CasterAddress, c, (_, existing) => c.Sequence > existing.Sequence ? c : existing);
            return ReferenceEquals(stored, c) ? ProofSetRecordResult.Stored : ProofSetRecordResult.AlreadyHeld;
        }

        /// <summary>Signs this node's own commitment (sets caster, a fresh increasing sequence and the signature).</summary>
        public static ProofSetCommitment SignOwn(ProofSetCommitment c)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long seq;
            while (true)
            {
                var last = Interlocked.Read(ref _lastSequence);
                seq = Math.Max(now, last + 1);
                if (Interlocked.CompareExchange(ref _lastSequence, seq, last) == last) break;
            }
            c.CasterAddress = Globals.ValidatorAddress ?? "";
            c.Sequence = seq;
            c.Signature = SignatureService.ValidatorSignature(SigningMessage(c));
            return c;
        }

        /// <summary>Drops heights below <paramref name="height"/> (the route calls this so a non-caster node, which
        /// never runs the agreement loop's own cleanup, cannot accumulate heights).</summary>
        public static void PruneBelow(long height)
        {
            foreach (var k in Globals.CasterProofSetCommitDict.Keys.Where(k => k < height).ToList())
                Globals.CasterProofSetCommitDict.TryRemove(k, out _);
        }

        /// <summary>The signed commitments held for a height (what a peer returns, so relays stay verifiable).</summary>
        public static Dictionary<string, ProofSetCommitment> Snapshot(long height)
        {
            var snapshot = new Dictionary<string, ProofSetCommitment>();
            if (Globals.CasterProofSetCommitDict.TryGetValue(height, out var d))
                foreach (var kv in d)
                    snapshot[kv.Key] = kv.Value;
            return snapshot;
        }
    }
}
