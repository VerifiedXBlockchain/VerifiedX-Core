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
