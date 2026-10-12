namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Where this chain's view of Bitcoin starts: twelve consecutive Bitcoin headers ending at the first block of a
    /// difficulty interval (a height divisible by 2016). Twelve, because the median-time rule for the first header after
    /// the checkpoint reads the eleven before it and the header it extends; ending at an interval's first block, because
    /// every target rule that follows looks back no further than that.
    ///
    /// Nothing about a checkpoint is trusted except its own bytes: the headers must link to one another and each must
    /// meet its own proof-of-work. The operator fills Globals.BitcoinCheckpointHeightMainnet / ...HeadersMainnet (and
    /// the testnet pair) from any block explorer at release time.
    /// docs/PLAN-vBTC-Bitcoin-Attestation.md, section 8.1.
    /// </summary>
    public sealed class BtcCheckpoint
    {
        public const int HeaderCount = 12;

        /// <summary>Height of the last of the twelve headers.</summary>
        public long Height { get; }
        /// <summary>The twelve headers, oldest first.</summary>
        public IReadOnlyList<BtcHeader> Headers { get; }
        public BtcHeader Tip => Headers[^1];

        private BtcCheckpoint(long height, List<BtcHeader> headers)
        {
            Height = height;
            Headers = headers;
        }

        /// <summary>The header at <paramref name="height"/> when the checkpoint holds it, otherwise null.</summary>
        public BtcHeader? At(long height)
        {
            var index = height - (Height - (HeaderCount - 1));
            return index >= 0 && index < HeaderCount ? Headers[(int)index] : null;
        }

        /// <summary>
        /// Parses and checks a checkpoint. <paramref name="headersHex"/> is the twelve headers' hex joined in order
        /// (1,920 hex characters; whitespace, commas and line breaks between headers are ignored).
        /// </summary>
        public static bool TryParse(long height, string? headersHex, out BtcCheckpoint? checkpoint, out string? reason)
        {
            checkpoint = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(headersHex)) { reason = "no checkpoint headers are set"; return false; }
            if (height <= 0 || height % BtcChainRules.Interval != 0)
            { reason = $"checkpoint height {height} must be the first block of a difficulty interval (divisible by {BtcChainRules.Interval})"; return false; }
            if (height < HeaderCount) { reason = "checkpoint height is too low"; return false; }

            var compact = new string(headersHex.Where(c => !char.IsWhiteSpace(c) && c != ',' && c != ';').ToArray()).ToLowerInvariant();
            if (compact.Length != HeaderCount * BtcHeader.Size * 2)
            { reason = $"a checkpoint is {HeaderCount} headers ({HeaderCount * BtcHeader.Size * 2} hex characters); got {compact.Length}"; return false; }

            var headers = new List<BtcHeader>(HeaderCount);
            for (var i = 0; i < HeaderCount; i++)
            {
                if (!BtcHeader.TryParse(compact.Substring(i * BtcHeader.Size * 2, BtcHeader.Size * 2), out var header) || header == null)
                { reason = $"checkpoint header {i + 1} is not valid hex"; return false; }
                if (!header.MeetsOwnTarget())
                { reason = $"checkpoint header {i + 1} ({header.Hash}) does not meet its own proof-of-work target"; return false; }
                if (i > 0 && !string.Equals(header.PrevHash, headers[i - 1].Hash, StringComparison.Ordinal))
                { reason = $"checkpoint header {i + 1} does not extend header {i}"; return false; }
                headers.Add(header);
            }

            checkpoint = new BtcCheckpoint(height, headers);
            return true;
        }

        /// <summary>The checkpoint configured for the network this node runs on, or null with the reason.</summary>
        public static BtcCheckpoint? Configured(out string? reason)
        {
            var height = Globals.IsTestNet ? Globals.BitcoinCheckpointHeightTestnet : Globals.BitcoinCheckpointHeightMainnet;
            var hex = Globals.IsTestNet ? Globals.BitcoinCheckpointHeadersTestnet : Globals.BitcoinCheckpointHeadersMainnet;
            return TryParse(height, hex, out var checkpoint, out reason) ? checkpoint : null;
        }
    }
}
