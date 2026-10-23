using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NBitcoin;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation.Net
{
    /// <summary>
    /// The Bitcoin side of a signing validator (plan 2.5): a handful of Bitcoin peers found through the DNS seeds, the
    /// header chain they serve from the checkpoint on, and the recent blocks of that chain, each checked against its
    /// header before it is kept. No second process and no third party; an explorer is asked only when no peer delivers.
    ///
    /// Only validators that sign reports run this. Every other node judges a report from the report itself.
    /// </summary>
    public sealed class BtcLightClient : IBtcChainView, IDisposable
    {
        /// <summary>
        /// Peers kept connected, as many as a Bitcoin node keeps outbound. Each one is a few kilobytes a minute, and
        /// one honest peer among them is enough to learn of the heaviest chain. Peers are taken from different
        /// networks (the first two bytes of the address) while there are such to take, so that one hosting provider
        /// is not all of them.
        /// </summary>
        public const int TargetPeers = 8;
        /// <summary>Other validators asked for their headers in one round (see <see cref="BtcValidatorSource"/>).</summary>
        public const int ValidatorsAskedPerRound = 3;
        /// <summary>Seconds between rounds of asking other validators.</summary>
        public const int ValidatorRoundSeconds = 30;
        /// <summary>
        /// This long without a new header is not what Bitcoin looks like (ninety minutes without a block happens a
        /// few times a year): the explorers are then asked as well, as one more source to be checked like the others.
        /// </summary>
        public const int StaleTipSeconds = 90 * 60;
        /// <summary>Tests shorten these.</summary>
        internal TimeSpan StaleAfter = TimeSpan.FromSeconds(StaleTipSeconds);
        internal TimeSpan ValidatorRound = TimeSpan.FromSeconds(ValidatorRoundSeconds);
        private readonly DateTime _createdAt = DateTime.UtcNow;
        /// <summary>
        /// Blocks kept on disk below the tip: two quiet ranges, which is everything report building can ask for again
        /// (the unreported range and the look-back of a newly created vault), plus a margin. About 300 MB on mainnet.
        /// </summary>
        public const int KeepBlocks = 2 * BtcAttestRules.QuietBlocks + 32;
        private const int BlocksInMemory = 6;

        private static readonly string[] MainnetSeeds =
        {
            "seed.bitcoin.sipa.be", "dnsseed.bluematt.me", "seed.bitcoin.sprovoost.nl", "dnsseed.emzy.de",
            "seed.bitcoin.wiz.biz", "seed.btc.petertodd.net", "seed.mainnet.achownodes.xyz", "dnsseed.bitcoin.dashjr.org",
        };
        // The bundled NBitcoin knows testnet4's port and message start but carries no DNS seeds for it.
        private static readonly string[] Testnet4Seeds = { "seed.testnet4.bitcoin.sprovoost.nl", "seed.testnet4.wiz.biz" };

        private readonly Network _network;
        private readonly BtcHeaderChain _chain;
        private readonly string _blockDir;
        private readonly Func<Network, IPEndPoint, IBtcSource?> _connect;
        private readonly Func<CancellationToken, Task<List<IPEndPoint>>> _discover;
        private readonly List<IBtcSource> _fallbacks;
        private readonly Func<List<IBtcSource>> _validators;
        private DateTime _validatorsAskedAt = DateTime.MinValue;
        private DateTime _fallbacksAskedAt = DateTime.MinValue;
        private readonly List<IBtcSource> _peers = new();
        private readonly object _peerLock = new();
        private readonly ConcurrentDictionary<string, DateTime> _avoidUntil = new();
        private readonly ConcurrentDictionary<long, byte> _wanted = new();
        private readonly LinkedList<(string Hash, Block Block)> _recent = new();
        private readonly object _recentLock = new();
        private List<IPEndPoint> _candidates = new();
        private DateTime _candidatesAt = DateTime.MinValue;

        public BtcHeaderChain Chain => _chain;
        public long TipHeight => _chain.TipHeight;
        public BtcHeader? HeaderAt(long height) => _chain.At(height);
        public int PeerCount { get { lock (_peerLock) return _peers.Count(p => p.IsAlive); } }
        public string? LastProblem { get; private set; }
        public DateTime LastHeaderAtUtc { get; private set; }

        /// <param name="connect">How a peer is reached (tests supply their own); the Bitcoin peer protocol by default.</param>
        /// <param name="discover">Where peer addresses come from; the DNS seeds by default.</param>
        /// <param name="fallbacks">Sources asked when no peer delivers; the public explorers by default.</param>
        /// <param name="validators">The other validators asked for their headers this round; a few of the active ones, at random, by default.</param>
        public BtcLightClient(Network network, BtcHeaderChain chain, string blockDir,
            Func<Network, IPEndPoint, IBtcSource?>? connect = null, Func<CancellationToken, Task<List<IPEndPoint>>>? discover = null,
            List<IBtcSource>? fallbacks = null, Func<List<IBtcSource>>? validators = null)
        {
            _validators = validators ?? (connect == null ? SomeActiveValidators : () => new List<IBtcSource>());
            _network = network;
            _chain = chain;
            _blockDir = blockDir;
            _connect = connect ?? ((net, endpoint) => BtcPeerSource.Connect(net, endpoint, TimeSpan.FromSeconds(10)));
            _discover = discover ?? DiscoverFromSeedsAsync;
            _fallbacks = fallbacks ?? BtcEsploraSource.For(network).Cast<IBtcSource>().ToList();
            Directory.CreateDirectory(_blockDir);
        }

        private bool IsPeer(IBtcSource source)
        {
            lock (_peerLock) return _peers.Contains(source);
        }

        /// <summary>A few of the active validators other than this one, chosen anew every round.</summary>
        private static List<IBtcSource> SomeActiveValidators()
        {
            try
            {
                var tip = Globals.LastBlock?.Height ?? 0;
                var random = new Random();
                return VerifiedXCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidatorsAt(tip)
                    .Where(v => !string.IsNullOrEmpty(v.IPAddress) && v.ValidatorAddress != Globals.ValidatorAddress)
                    .OrderBy(_ => random.Next())
                    .Take(ValidatorsAskedPerRound)
                    .Select(v => (IBtcSource)new BtcValidatorSource(v.ValidatorAddress, v.IPAddress, Globals.FrostValidatorPort))
                    .ToList();
            }
            catch { return new List<IBtcSource>(); }
        }

        /// <summary>The network an address is in, as far as peer choice goes: the first 16 bits of an IPv4 address, the first 32 of an IPv6 one.</summary>
        internal static string NetworkGroup(string endpoint)
        {
            var host = endpoint.Split(' ')[0];
            if (IPEndPoint.TryParse(host, out var ep)) return VerifiedXCore.Utilities.RemoteIp.Group(ep.Address, 16, 32)!;
            return IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? VerifiedXCore.Utilities.RemoteIp.Group(ip, 16, 32)! : host;
        }

        private async Task<List<IPEndPoint>> DiscoverFromSeedsAsync(CancellationToken ct)
        {
            var found = new List<IPEndPoint>();
            foreach (var seed in _network == Network.Main ? MainnetSeeds : _network == Network.TestNet4 ? Testnet4Seeds : VfxSignet.Hosts)
            {
                try
                {
                    foreach (var ip in await Dns.GetHostAddressesAsync(seed, ct))
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                            found.Add(new IPEndPoint(ip, _network.DefaultPort));
                }
                catch { }
            }
            // The addresses compiled into the library, for a node whose DNS is filtered.
            try { found.AddRange(_network.SeedNodes.Select(s => s.Endpoint).OfType<IPEndPoint>().Where(e => e.AddressFamily == AddressFamily.InterNetwork)); } catch { }
            return found;
        }

        // ── Blocks ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The block at a height of the best chain, from memory or disk, checked against its header. Null when it has
        /// not been fetched; the height is then queued for the next pass of <see cref="RunOnceAsync"/>.
        /// </summary>
        public Block? BlockAt(long height)
        {
            var header = _chain.At(height);
            if (header == null) return null;

            lock (_recentLock)
            {
                for (var node = _recent.First; node != null; node = node.Next)
                    if (node.Value.Hash == header.Hash)
                    {
                        _recent.Remove(node);
                        _recent.AddFirst(node);
                        return node.Value.Block;
                    }
            }

            var path = PathOf(height, header.Hash);
            if (File.Exists(path))
            {
                try
                {
                    var block = Block.Load(File.ReadAllBytes(path), _network);
                    if (BtcBlockCheck.Verify(block, header, _network) == null)
                    {
                        Remember(header.Hash, block);
                        return block;
                    }
                }
                catch { }
                try { File.Delete(path); } catch { } // a damaged file: fetch it again
            }

            _wanted[height] = 0;
            return null;
        }

        private string PathOf(long height, string hash) => Path.Combine(_blockDir, $"{height}_{hash}.blk");

        private void Remember(string hash, Block block)
        {
            lock (_recentLock)
            {
                _recent.AddFirst((hash, block));
                while (_recent.Count > BlocksInMemory) _recent.RemoveLast();
            }
        }

        // ── One pass: peers, headers, wanted blocks, pruning ────────────────────────────────────────────────

        public async Task RunOnceAsync(CancellationToken ct)
        {
            await EnsurePeersAsync(ct);
            await SyncHeadersAsync(ct);
            await FetchWantedAsync(ct);
            Prune();
        }

        private List<IBtcSource> LivePeers()
        {
            lock (_peerLock)
            {
                foreach (var dead in _peers.Where(p => !p.IsAlive).ToList())
                {
                    _peers.Remove(dead);
                    try { dead.Dispose(); } catch { }
                }
                return _peers.ToList();
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<IBtcSource, int> _blockMisses = new();

        private void Drop(IBtcSource peer, string why, TimeSpan avoidFor)
        {
            lock (_peerLock) _peers.Remove(peer);
            _avoidUntil[peer.Name.Split(' ')[0]] = DateTime.UtcNow + avoidFor;
            try { peer.Dispose(); } catch { }
            LastProblem = $"dropped Bitcoin peer {peer.Name}: {why}";
        }

        private async Task EnsurePeersAsync(CancellationToken ct)
        {
            if (LivePeers().Count >= TargetPeers) return;

            if (_candidates.Count == 0 || DateTime.UtcNow - _candidatesAt > TimeSpan.FromMinutes(30))
            {
                var found = await _discover(ct);
                var random = new Random();
                _candidates = found.GroupBy(e => e.ToString()).Select(g => g.First()).OrderBy(_ => random.Next()).ToList();
                _candidatesAt = DateTime.UtcNow;
            }

            var attempts = 0;
            while (LivePeers().Count < TargetPeers && _candidates.Count > 0 && attempts < 12 && !ct.IsCancellationRequested)
            {
                // A candidate from a network none of the connected peers is in, while there is one.
                HashSet<string> groups;
                lock (_peerLock) groups = new HashSet<string>(_peers.Select(p => NetworkGroup(p.Name)), StringComparer.Ordinal);
                var pick = _candidates.FindIndex(c => !groups.Contains(NetworkGroup(c.ToString())));
                if (pick < 0) pick = 0;
                var endpoint = _candidates[pick];
                _candidates.RemoveAt(pick);
                if (_avoidUntil.TryGetValue(endpoint.ToString(), out var until) && until > DateTime.UtcNow) continue;
                lock (_peerLock)
                    if (_peers.Any(p => p.Name.StartsWith(endpoint.ToString() + " ", StringComparison.Ordinal))) continue;
                attempts++;
                var peer = await Task.Run(() => _connect(_network, endpoint), ct);
                if (peer == null)
                {
                    _avoidUntil[endpoint.ToString()] = DateTime.UtcNow + TimeSpan.FromMinutes(20);
                    continue;
                }
                lock (_peerLock) _peers.Add(peer);
            }
        }

        private async Task SyncHeadersAsync(CancellationToken ct)
        {
            var sources = LivePeers();
            // No peer at all: an explorer keeps the header chain moving, a few headers at a time. And when the tip is
            // older than Bitcoin leaves it, the explorers are asked as well, now and then.
            if (sources.Count == 0) sources = _fallbacks.ToList();
            else if (DateTime.UtcNow - (LastHeaderAtUtc > _createdAt ? LastHeaderAtUtc : _createdAt) > StaleAfter && DateTime.UtcNow - _fallbacksAskedAt > TimeSpan.FromMinutes(10))
            {
                _fallbacksAskedAt = DateTime.UtcNow;
                sources.AddRange(_fallbacks);
            }
            // The other validators, a few each round: what they follow is checked like what a peer serves, and the
            // branch with the most work is followed wherever it came from.
            var fromValidators = new HashSet<IBtcSource>();
            if (DateTime.UtcNow - _validatorsAskedAt >= ValidatorRound)
            {
                _validatorsAskedAt = DateTime.UtcNow;
                try
                {
                    foreach (var validator in _validators().Where(v => !_avoidUntil.TryGetValue(v.Name, out var until) || until <= DateTime.UtcNow))
                    {
                        fromValidators.Add(validator);
                        sources.Add(validator);
                    }
                }
                catch { }
            }

            foreach (var source in sources)
            {
                for (var round = 0; round < 60 && !ct.IsCancellationRequested; round++)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var headers = await source.GetHeadersAsync(_chain.Locator(), timeout.Token);
                    if (headers == null)
                    {
                        if (IsPeer(source)) Drop(source, "no answer to a header request", TimeSpan.FromMinutes(10));
                        // A validator that does not answer, or answers with more than an answer holds, is not asked
                        // again for ten minutes (it may only be restarting).
                        else if (fromValidators.Contains(source)) _avoidUntil[source.Name] = DateTime.UtcNow + TimeSpan.FromMinutes(10);
                        break;
                    }
                    if (headers.Count == 0) break;

                    var outcome = _chain.Add(headers, out var reason);
                    if (outcome == BtcHeaderAdd.Extended || outcome == BtcHeaderAdd.Reorganised)
                    {
                        LastHeaderAtUtc = DateTime.UtcNow;
                        if (outcome == BtcHeaderAdd.Reorganised)
                            LogUtility.Log($"Bitcoin view: {reason} (now at {_chain.TipHeight} {_chain.Tip.Hash}).", "BtcLightClient.SyncHeadersAsync()");
                    }
                    else if (outcome == BtcHeaderAdd.Invalid || outcome == BtcHeaderAdd.Unconnected)
                    {
                        // Headers that are not Bitcoin, or another chain altogether: this peer is of no use. A
                        // validator that serves such is not asked again for an hour.
                        if (IsPeer(source)) Drop(source, reason ?? outcome.ToString(), TimeSpan.FromHours(6));
                        else if (fromValidators.Contains(source)) _avoidUntil[source.Name] = DateTime.UtcNow + TimeSpan.FromHours(1);
                        break;
                    }
                    if (outcome != BtcHeaderAdd.Extended || headers.Count < 2000) break;
                }
            }
        }

        private async Task FetchWantedAsync(CancellationToken ct)
        {
            foreach (var height in _wanted.Keys.OrderBy(h => h).ToList())
            {
                if (ct.IsCancellationRequested) return;
                var header = _chain.At(height);
                if (header == null || File.Exists(PathOf(height, header.Hash)))
                {
                    _wanted.TryRemove(height, out _);
                    continue;
                }

                Block? block = null;
                foreach (var source in LivePeers().Concat(_fallbacks))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var candidate = await source.GetBlockAsync(header.Hash, timeout.Token);
                    if (candidate == null)
                    {
                        // A peer that answers headers but never delivers blocks held the report loop a minute a block, a
                        // peer at a time, and was never replaced (fifth review, round 22): two misses running drop it.
                        if (IsPeer(source) && _blockMisses.AddOrUpdate(source, 1, (_, n) => n + 1) >= 2)
                        {
                            _blockMisses.TryRemove(source, out _);
                            Drop(source, $"delivered no block twice running (last {height})", TimeSpan.FromHours(1));
                        }
                        continue;
                    }
                    _blockMisses.TryRemove(source, out _);
                    var problem = BtcBlockCheck.Verify(candidate, header, _network);
                    if (problem != null)
                    {
                        if (IsPeer(source)) Drop(source, $"served a wrong block for {height}: {problem}", TimeSpan.FromHours(6));
                        continue;
                    }
                    block = candidate;
                    break;
                }
                if (block == null)
                {
                    LastProblem = $"no source delivered Bitcoin block {height} {header.Hash}";
                    continue; // stays wanted; tried again on the next pass
                }

                try
                {
                    var path = PathOf(height, header.Hash);
                    var temp = path + ".tmp";
                    await File.WriteAllBytesAsync(temp, block.ToBytes(), ct);
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex) { LastProblem = $"could not store Bitcoin block {height}: {ex.Message}"; }
                Remember(header.Hash, block);
                _wanted.TryRemove(height, out _);
            }
        }

        /// <summary>Removes block files below the kept range, and files of blocks that are no longer on the best chain.</summary>
        private void Prune()
        {
            try
            {
                var floor = _chain.TipHeight - KeepBlocks;
                foreach (var file in Directory.EnumerateFiles(_blockDir, "*.blk"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var split = name.IndexOf('_');
                    if (split <= 0 || !long.TryParse(name.Substring(0, split), out var height)) continue;
                    var onChain = _chain.At(height)?.Hash == name.Substring(split + 1);
                    if (height < floor || !onChain)
                        try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            lock (_peerLock)
            {
                foreach (var peer in _peers) try { peer.Dispose(); } catch { }
                _peers.Clear();
            }
        }

        // ── The running instance ────────────────────────────────────────────────────────────────────────────

        /// <summary>The client of this node, once started. Null on a node that does not sign reports.</summary>
        public static BtcLightClient? Current { get; private set; }

        /// <summary>
        /// Creates the client for this node from the configured checkpoint, and brings its header chain up to the
        /// headers the chain has already attested (they are in state, already checked). Null with the reason when the
        /// checkpoint is not set.
        /// </summary>
        public static BtcLightClient? Create(out string? reason)
        {
            var checkpoint = BtcCheckpoint.Configured(out reason);
            if (checkpoint == null) return null;
            var network = FrostDkgAttestation.ConsensusNetwork;
            var chain = new BtcHeaderChain(checkpoint, BtcChainRules.Current);

            try
            {
                var meta = BtcAttestStore.GetMeta();
                if (meta != null && meta.CheckpointHeight == checkpoint.Height)
                {
                    var batch = new List<BtcHeader>(2000);
                    for (var h = checkpoint.Height + 1; h <= meta.TipHeight; h++)
                    {
                        var header = BtcAttestStore.HeaderAt(h);
                        if (header == null) break;
                        batch.Add(header);
                        if (batch.Count == 2000) { chain.Add(batch, out _); batch.Clear(); }
                    }
                    if (batch.Count > 0) chain.Add(batch, out _);
                }
            }
            catch { }

            var dir = Path.Combine(GetPathUtility.GetDatabasePath(), "BtcBlocks");
            Current = new BtcLightClient(network, chain, dir);
            return Current;
        }
    }
}
