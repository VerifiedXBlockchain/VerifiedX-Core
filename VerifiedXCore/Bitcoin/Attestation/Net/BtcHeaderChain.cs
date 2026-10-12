using System.Numerics;

namespace VerifiedXCore.Bitcoin.Attestation.Net
{
    /// <summary>What happened to a run of headers offered to <see cref="BtcHeaderChain"/>.</summary>
    public enum BtcHeaderAdd
    {
        /// <summary>Nothing new (every header was already held, or the run was empty).</summary>
        Nothing,
        /// <summary>The best chain grew.</summary>
        Extended,
        /// <summary>A branch with more work replaced the end of the best chain.</summary>
        Reorganised,
        /// <summary>A valid branch with no more work than the best chain: kept out, not an offence.</summary>
        SideBranch,
        /// <summary>The run does not connect to any header held: another chain, or beyond what this node follows.</summary>
        Unconnected,
        /// <summary>A header breaks Bitcoin's rules.</summary>
        Invalid,
    }

    /// <summary>
    /// This node's own view of Bitcoin's best header chain, from the checkpoint on (plan 2.5). It is what the node's
    /// Bitcoin peers serve, checked header by header with the rules consensus uses (<see cref="BtcChainRules"/>), so a
    /// peer can withhold headers or serve a weaker branch but cannot make the node follow headers that are not Bitcoin.
    ///
    /// Local to the node, never chain state: it decides which blocks this validator fetches and what it offers to
    /// sign. What the chain accepts is decided by <see cref="BtcReportVerifier"/> from the report alone.
    /// </summary>
    public sealed class BtcHeaderChain
    {
        private readonly BtcChainRules _rules;
        private readonly object _lock = new();
        private readonly long _firstHeight;
        private readonly List<BtcHeader> _headers = new();
        private readonly Dictionary<string, long> _heightByHash = new(StringComparer.Ordinal);

        /// <summary>Height of the checkpoint's last header: nothing at or below it is ever replaced.</summary>
        public long CheckpointHeight { get; }

        public BtcHeaderChain(BtcCheckpoint checkpoint, BtcChainRules rules)
        {
            _rules = rules;
            CheckpointHeight = checkpoint.Height;
            _firstHeight = checkpoint.Height - (BtcCheckpoint.HeaderCount - 1);
            foreach (var header in checkpoint.Headers) Append(header);
        }

        public long TipHeight { get { lock (_lock) return _firstHeight + _headers.Count - 1; } }
        public BtcHeader Tip { get { lock (_lock) return _headers[^1]; } }

        public BtcHeader? At(long height)
        {
            lock (_lock)
            {
                var index = height - _firstHeight;
                return index >= 0 && index < _headers.Count ? _headers[(int)index] : null;
            }
        }

        public long? HeightOf(string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            lock (_lock) return _heightByHash.TryGetValue(hash, out var height) ? height : null;
        }

        /// <summary>The headers' hex from <paramref name="from"/> through <paramref name="to"/>; empty when the range is not all held.</summary>
        public List<string> Hexes(long from, long to)
        {
            var result = new List<string>();
            lock (_lock)
            {
                if (to < from || from < _firstHeight || to > _firstHeight + _headers.Count - 1) return result;
                for (var h = from; h <= to; h++) result.Add(_headers[(int)(h - _firstHeight)].Hex);
            }
            return result;
        }

        /// <summary>
        /// Block hashes a peer is asked to continue from: the tip, then back in growing steps to the checkpoint, so a
        /// peer on another branch still finds the last block it shares with this node.
        /// </summary>
        public List<string> Locator()
        {
            var hashes = new List<string>();
            lock (_lock)
            {
                var index = _headers.Count - 1;
                var floor = (int)(CheckpointHeight - _firstHeight);
                var step = 1;
                while (index > floor)
                {
                    hashes.Add(_headers[index].Hash);
                    if (hashes.Count >= 10) step *= 2;
                    index -= step;
                }
                hashes.Add(_headers[floor].Hash);
            }
            return hashes;
        }

        /// <summary>
        /// Offers consecutive headers, oldest first, as a peer sends them. They are accepted only when they connect to a
        /// header held, every one passes the rules, and (when they do not simply extend the tip) their branch carries
        /// more work than the part of the best chain it would replace.
        /// </summary>
        public BtcHeaderAdd Add(IReadOnlyList<BtcHeader> run, out string? reason)
        {
            reason = null;
            if (run == null || run.Count == 0) return BtcHeaderAdd.Nothing;
            lock (_lock)
            {
                if (!_heightByHash.TryGetValue(run[0].PrevHash, out var parent))
                {
                    reason = $"header {run[0].Hash} does not connect to any header held";
                    return BtcHeaderAdd.Unconnected;
                }

                // Skip what is already on the best chain.
                var start = 0;
                var height = parent;
                while (start < run.Count && At(height + 1) is { } held && held.Hash == run[start].Hash)
                {
                    start++;
                    height++;
                }
                if (start == run.Count) return BtcHeaderAdd.Nothing;

                var forkHeight = height; // the last height the run shares with the best chain
                if (forkHeight < CheckpointHeight)
                {
                    reason = $"the branch leaves the chain at {forkHeight}, below the checkpoint {CheckpointHeight}";
                    return BtcHeaderAdd.Unconnected;
                }

                var branch = new Dictionary<long, BtcHeader>();
                BtcHeader? Lookup(long h) => h > forkHeight ? (branch.TryGetValue(h, out var b) ? b : null) : At(h);
                var branchWork = BigInteger.Zero;
                for (var i = start; i < run.Count; i++)
                {
                    height++;
                    var refusal = _rules.Validate(run[i], height, Lookup);
                    if (refusal != null)
                    {
                        reason = refusal;
                        return BtcHeaderAdd.Invalid;
                    }
                    branch[height] = run[i];
                    branchWork += run[i].Work();
                }

                var tip = _firstHeight + _headers.Count - 1;
                if (forkHeight == tip)
                {
                    for (var h = forkHeight + 1; h <= height; h++) Append(branch[h]);
                    return BtcHeaderAdd.Extended;
                }

                var replacedWork = BigInteger.Zero;
                for (var h = forkHeight + 1; h <= tip; h++) replacedWork += At(h)!.Work();
                if (branchWork <= replacedWork)
                {
                    reason = $"a branch from {forkHeight} with no more work than the best chain";
                    return BtcHeaderAdd.SideBranch;
                }

                for (var h = tip; h > forkHeight; h--)
                {
                    _heightByHash.Remove(_headers[^1].Hash);
                    _headers.RemoveAt(_headers.Count - 1);
                }
                for (var h = forkHeight + 1; h <= height; h++) Append(branch[h]);
                reason = $"the best chain changed above {forkHeight}";
                return BtcHeaderAdd.Reorganised;
            }
        }

        private void Append(BtcHeader header)
        {
            _headers.Add(header);
            _heightByHash[header.Hash] = _firstHeight + _headers.Count - 1;
        }
    }
}
