using System.Text;

namespace VerifiedXCore.Services
{
    /// <summary>Canonical UTF-8 signing strings (CONSENSUS_DECENTRALIZATION_PLAN §12).</summary>
    public static class ConsensusMessageFormatter
    {
        public static string NormalizeHash(string? hash)
        {
            if (string.IsNullOrEmpty(hash))
                return hash ?? "";
            return hash.ToLowerInvariant();
        }

        /// <summary>§12.1 — no timestamp in signed payload.</summary>
        public static string FormatAttestationV1(long blockHeight, string blockHash, string winnerAddress, string prevHash)
        {
            return $"VFX_ATTEST_V1|{blockHeight}|{NormalizeHash(blockHash)}|{winnerAddress}|{NormalizeHash(prevHash)}";
        }

        /// <summary>§12.2</summary>
        public static string FormatRequestBlockV1(long blockHeight, string casterAddress, string winnerAddress, long timestampUnix)
        {
            return $"VFX_REQBLK_V1|{blockHeight}|{casterAddress}|{winnerAddress}|{timestampUnix}";
        }

        // ── Security audit Sep 2026 (BB-1): signed payloads for routes that were unauthenticated ──
        // Height-bound consensus messages (proof, winner vote, proof-set commitment) carry no timestamp:
        // the signed height makes a replay useless once the round has passed, and first-write-wins storage
        // makes an in-round replay a no-op. Registry messages (status, validator list) are not height-bound,
        // so they carry a timestamp and are checked for freshness and replay.

        /// <summary>VX-05: a block-producer proof, bound to the signer, height, parent hash and VRF value.</summary>
        public static string FormatProofV1(long blockHeight, string prevHash, string address, uint vrfNumber)
            => $"VFX_PROOF_V1|{blockHeight}|{NormalizeHash(prevHash)}|{address}|{vrfNumber}";

        /// <summary>VX-08: one caster's winner vote for a height. Excluded addresses sorted ordinal.</summary>
        public static string FormatWinnerVoteV1(long blockHeight, string voterAddress, string winnerAddress, IEnumerable<string>? excludedAddresses)
        {
            var excluded = excludedAddresses == null ? "" : string.Join(",", excludedAddresses.Where(a => !string.IsNullOrEmpty(a)).OrderBy(a => a, StringComparer.Ordinal));
            return $"VFX_WINVOTE_V1|{blockHeight}|{voterAddress}|{winnerAddress}|{excluded}";
        }

        /// <summary>VX-16: one caster's proof-set commitment for a height.</summary>
        public static string FormatProofSetV1(long blockHeight, string casterAddress, string commitmentHash)
            => $"VFX_PROOFSET_V1|{blockHeight}|{casterAddress}|{NormalizeHash(commitmentHash)}";

        /// <summary>VX-07: a validator's own registry advertisement (address, key and IP it claims).</summary>
        public static string FormatValidatorStatusV1(string address, long timestampUnix, string publicKey, string ipAddress)
            => $"VFX_VALSTATUS_V1|{address}|{timestampUnix}|{publicKey}|{ipAddress}";

        /// <summary>VX-15: a caster's validator-list exchange; <paramref name="entriesHash"/> covers the entries.</summary>
        public static string FormatValidatorListV1(string casterAddress, long timestampUnix, string entriesHash)
            => $"VFX_VALLIST_V1|{casterAddress}|{timestampUnix}|{NormalizeHash(entriesHash)}";

        /// <summary>
        /// Wave 3 (membership record): canonical payload for a CasterMembershipRecord.
        /// Casters MUST be sorted by Address ordinal ascending before calling. The record's
        /// RecordHash is SHA256 of this string; rotation signatures sign this string.
        /// </summary>
        public static string FormatCasterMembershipV1(long recordSeq, long effectiveFromHeight, string prevRecordHash,
            string changeType, string changedAddress, IEnumerable<(string Address, string PeerIP, string PublicKey)> castersSortedByAddress)
        {
            var sb = new StringBuilder();
            sb.Append("VFX_MEMBERSHIP_V1|");
            sb.Append(recordSeq);
            sb.Append('|');
            sb.Append(effectiveFromHeight);
            sb.Append('|');
            sb.Append(NormalizeHash(prevRecordHash));
            sb.Append('|');
            sb.Append(changeType ?? "");
            sb.Append('|');
            sb.Append(changedAddress ?? "");
            foreach (var (address, peerIp, pubKey) in castersSortedByAddress)
            {
                sb.Append('|');
                sb.Append(address);
                sb.Append('|');
                sb.Append(peerIp ?? "");
                sb.Append('|');
                sb.Append(pubKey ?? "");
            }
            return sb.ToString();
        }

        /// <summary>§12.3 — casters sorted by Address ascending before calling.</summary>
        public static string FormatSignedCasterListV1(int asOfBlockHeight, IEnumerable<(string Address, string PeerIP, string PublicKey)> castersSortedByAddress)
        {
            var sb = new StringBuilder();
            sb.Append("VFX_CASTERS_V1|");
            foreach (var (address, peerIp, pubKey) in castersSortedByAddress)
            {
                sb.Append(address);
                sb.Append('|');
                sb.Append(peerIp ?? "");
                sb.Append('|');
                sb.Append(pubKey ?? "");
                sb.Append('|');
            }
            sb.Append(asOfBlockHeight);
            return sb.ToString();
        }
    }
}
