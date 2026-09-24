using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// VX-19: the only way a block enters <see cref="BlockDownloadService.BlockDict"/> (the staging map ValidateBlocks
    /// drains contiguously from tip+1).
    ///
    /// Before: the general hub (and the gossip client) staged ANY block above the tip whose ChainRefId matched — no
    /// header, hash, size or duplicate check — and ValidateBlocks only ever evicts heights below the tip, so blocks at
    /// non-contiguous future heights were held forever. The queue cost was the wire Size (a negative value credited the
    /// budget).
    ///
    /// Now:
    ///  - Unsolicited blocks (gossip pushes) go through <see cref="TryStageGossip"/>: only the next height (tip+1) is
    ///    staged, after a hash recompute, a local size check, a version check, a validator-signature check and a
    ///    known-producer check (<see cref="PassesGossipPreChecks"/>). Anything else is not stored; the caller may start
    ///    the normal downloader instead, which fetches from peers and validates in order.
    ///  - Solicited blocks (the downloader, a GetBlock reply, our own agreed block, a caster's correction) go through
    ///    <see cref="Stage"/>: deduplicated, no gossip caps.
    ///  - Every insert is de-duplicated by hash; list mutation is locked; gossip staging is capped in total entries,
    ///    bytes (locally measured) and per source; entries at non-contiguous heights older than 10 minutes are evicted.
    /// </summary>
    public static class BlockStaging
    {
        public const int MaxStagedBlocks = 2_000;
        public const int MaxStagedPerSource = 50;
        public const long MaxStagedBytes = 100L * 1024 * 1024;
        public const long StaleSeconds = 600;

        private static readonly object _lock = new();
        // hash -> (added unix time, locally measured size, source)
        private static readonly ConcurrentDictionary<string, (long AddedAt, long Size, string Source)> _meta = new(StringComparer.Ordinal);

        /// <summary>Locally measured serialized size — never the wire Size field.</summary>
        public static long LocalSize(Block? block)
        {
            if (block == null) return 0;
            try { return Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(block)); }
            catch { return long.MaxValue; }
        }

        /// <summary>Queue cost for a received block (local size, clamped to int, at least 1 KB).</summary>
        public static int QueueCost(Block? block) => (int)Math.Min(int.MaxValue, Math.Max(1024, LocalSize(block)));

        /// <summary>
        /// Checks for an unsolicited block before it is staged. Consensus rules that ValidateBlock also enforces
        /// (header hash, validator signature, version), plus local limits. Deliberately NOT the parent-hash check: a node
        /// on a minority fork must still stage the network's next block so ValidateBlock's PREVHASH-MISMATCH recovery runs.
        /// </summary>
        public static bool PassesGossipPreChecks(Block? block, out string reason)
        {
            reason = "";
            if (block == null) { reason = "null block"; return false; }
            if (block.ChainRefId != BlockchainData.ChainRef) { reason = "wrong chain"; return false; }
            if (block.Size < 0) { reason = "negative size"; return false; }
            var size = LocalSize(block);
            if (size > Globals.MaxBlockSizeBytes) { reason = $"size {size} exceeds {Globals.MaxBlockSizeBytes}"; return false; }
            if (block.Height != Globals.LastBlock.Height + 1) { reason = $"not the next height (tip {Globals.LastBlock.Height}, block {block.Height})"; return false; }
            if (block.Version != BlockVersionUtility.GetBlockVersion(block.Height)) { reason = "wrong version"; return false; }
            string recomputed;
            try { recomputed = block.GetBlockHash(); } catch { reason = "malformed header"; return false; }
            if (string.IsNullOrEmpty(block.Hash) || !string.Equals(block.Hash, recomputed, StringComparison.Ordinal)) { reason = "hash does not match header"; return false; }
            bool sigOk;
            try { sigOk = SignatureService.VerifySignature(block.Validator, block.Hash, block.ValidatorSignature); } catch { sigOk = false; }
            if (!sigOk) { reason = "bad validator signature"; return false; }
            if (!IsKnownProducer(block.Validator)) { reason = "producer not a known validator or caster"; return false; }
            return true;
        }

        /// <summary>
        /// The producer must be in this node's validator registry or caster set. A node with no registry at all (a plain
        /// wallet node that never learned one) cannot apply this filter; it relies on the signature check and on full
        /// validation, which still runs before anything is committed.
        /// </summary>
        private static bool IsKnownProducer(string? address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            if (Globals.NetworkValidators.ContainsKey(address)) return true;
            if (Globals.BlockCasters.ToList().Any(c => c.ValidatorAddress == address)) return true;
            return Globals.NetworkValidators.IsEmpty && Globals.BlockCasters.IsEmpty;
        }

        /// <summary>Stages an unsolicited block that passed <see cref="PassesGossipPreChecks"/>, within the gossip caps.</summary>
        public static bool TryStageGossip(Block block, string sourceIp)
        {
            var size = LocalSize(block);
            lock (_lock)
            {
                EvictStaleLocked();
                if (IsStagedLocked(block)) return false;
                var (count, bytes, fromSource) = TotalsLocked(sourceIp);
                if (count >= MaxStagedBlocks || bytes + size > MaxStagedBytes || fromSource >= MaxStagedPerSource)
                    return false;
                AddLocked(block, sourceIp, size);
                return true;
            }
        }

        /// <summary>Stages a block this node asked for (or produced). De-duplicated; no gossip caps.</summary>
        public static bool Stage(Block block, string sourceIp)
        {
            if (block == null || string.IsNullOrEmpty(block.Hash)) return false;
            lock (_lock)
            {
                EvictStaleLocked();
                if (IsStagedLocked(block)) return false;
                AddLocked(block, sourceIp, LocalSize(block));
                return true;
            }
        }

        private static bool IsStagedLocked(Block block) =>
            BlockDownloadService.BlockDict.TryGetValue(block.Height, out var list) && list.Any(b => b.block?.Hash == block.Hash);

        private static void AddLocked(Block block, string sourceIp, long size)
        {
            BlockDownloadService.BlockDict.AddOrUpdate(
                block.Height,
                _ => new List<(Block, string)> { (block, sourceIp) },
                (_, existing) =>
                {
                    if (!existing.Any(b => b.block?.Hash == block.Hash))
                        existing.Add((block, sourceIp));
                    return existing;
                });
            _meta[block.Hash] = (TimeUtil.GetTime(), size, sourceIp ?? "");
        }

        private static (int Count, long Bytes, int FromSource) TotalsLocked(string sourceIp)
        {
            int count = 0, fromSource = 0;
            long bytes = 0;
            foreach (var list in BlockDownloadService.BlockDict.Values)
            {
                foreach (var (b, ip) in list.ToList())
                {
                    count++;
                    if (ip == sourceIp) fromSource++;
                    bytes += b?.Hash != null && _meta.TryGetValue(b.Hash, out var m) ? m.Size : Math.Max(0, b?.Size ?? 0);
                }
            }
            return (count, bytes, fromSource);
        }

        /// <summary>Removes entries at non-contiguous heights (not tip+1) staged more than 10 minutes ago.</summary>
        private static void EvictStaleLocked()
        {
            var now = TimeUtil.GetTime();
            var next = Globals.LastBlock.Height + 1;
            foreach (var kv in BlockDownloadService.BlockDict.ToList())
            {
                if (kv.Key == next) continue;
                var list = kv.Value;
                var stale = list.Where(b => b.block?.Hash != null && _meta.TryGetValue(b.block.Hash, out var m) && now - m.AddedAt > StaleSeconds).ToList();
                if (stale.Count == 0) continue;
                var kept = list.Except(stale).ToList();
                if (kept.Count == 0) BlockDownloadService.BlockDict.TryRemove(kv);
                else BlockDownloadService.BlockDict.TryUpdate(kv.Key, kept, list);
                foreach (var s in stale) _meta.TryRemove(s.block.Hash, out _);
            }
            // forget metadata for blocks no longer staged
            if (_meta.Count > MaxStagedBlocks * 4)
            {
                var live = new HashSet<string>(BlockDownloadService.BlockDict.Values.SelectMany(l => l.ToList()).Where(b => b.block?.Hash != null).Select(b => b.block.Hash), StringComparer.Ordinal);
                foreach (var h in _meta.Keys.Where(h => !live.Contains(h)).ToList()) _meta.TryRemove(h, out _);
            }
        }

        /// <summary>Test hook: run stale eviction now.</summary>
        internal static void EvictStaleNow() { lock (_lock) EvictStaleLocked(); }

        /// <summary>Test hook: age a staged entry.</summary>
        internal static void Backdate(string hash, long seconds)
        {
            if (_meta.TryGetValue(hash, out var m)) _meta[hash] = (m.AddedAt - seconds, m.Size, m.Source);
        }
    }
}
