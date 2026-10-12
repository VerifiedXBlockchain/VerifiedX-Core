using System.Net;
using NBitcoin;
using NBitcoin.Protocol;

namespace VerifiedXCore.Bitcoin.Attestation.Net
{
    /// <summary>
    /// The node's view of Bitcoin that report building reads: the best header chain it follows and the blocks it has
    /// fetched and checked. A block that is not here yet is simply not here; nothing is ever built on a guess.
    /// </summary>
    public interface IBtcChainView
    {
        long TipHeight { get; }
        BtcHeader? HeaderAt(long height);
        /// <summary>The checked block at a height of the best chain; null when it has not been fetched yet (it is then queued).</summary>
        Block? BlockAt(long height);
    }

    /// <summary>Somewhere headers and blocks come from: a Bitcoin peer, or an explorer as a fallback. Never trusted (plan 2.3).</summary>
    public interface IBtcSource : IDisposable
    {
        string Name { get; }
        bool IsAlive { get; }
        /// <summary>The headers that follow the newest of <paramref name="locator"/> the source knows, oldest first; null when it did not answer.</summary>
        Task<List<BtcHeader>?> GetHeadersAsync(IReadOnlyList<string> locator, CancellationToken ct);
        /// <summary>The block with this hash; null when the source did not deliver it.</summary>
        Task<Block?> GetBlockAsync(string hash, CancellationToken ct);
    }

    /// <summary>
    /// A fetched block proves itself against a header already validated (plan 2.3): it must carry that header, start
    /// with the only coinbase transaction, and its transactions must hash to the header's Merkle root. A copy that
    /// repeats transactions (which can keep the root unchanged) is refused.
    /// </summary>
    public static class BtcBlockCheck
    {
        /// <summary>Null when <paramref name="block"/> is the block of <paramref name="header"/>.</summary>
        public static string? Verify(Block? block, BtcHeader header)
        {
            if (block == null) return "no block";
            string headerHex;
            try { headerHex = Convert.ToHexString(block.Header.ToBytes()).ToLowerInvariant(); }
            catch (Exception ex) { return $"the block's header cannot be read: {ex.Message}"; }
            if (!string.Equals(headerHex, header.Hex, StringComparison.Ordinal)) return "the block does not carry the header asked for";
            if (block.Transactions.Count == 0) return "the block has no transactions";
            if (!block.Transactions[0].IsCoinBase) return "the block's first transaction is not a coinbase";

            var ids = new List<string>(block.Transactions.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < block.Transactions.Count; i++)
            {
                var tx = block.Transactions[i];
                if (i > 0 && tx.IsCoinBase) return "the block has a second coinbase transaction";
                var id = tx.GetHash().ToString();
                if (!seen.Add(id)) return "the block repeats a transaction";
                ids.Add(id);
            }
            return string.Equals(BtcMerkle.RootOf(ids), header.MerkleRoot, StringComparison.Ordinal)
                ? null
                : "the block's transactions do not hash to the header's Merkle root";
        }
    }

    /// <summary>
    /// One Bitcoin peer, spoken to in Bitcoin's own peer protocol (plan 2.5): the handshake, "getheaders" and
    /// "getdata" for a block, exactly as a Bitcoin node asks its peers. Blocks are asked for without witness data:
    /// reports carry transactions that way, and the blocks are a third the size.
    /// </summary>
    public sealed class BtcPeerSource : IBtcSource
    {
        private readonly Node _node;
        private readonly SemaphoreSlim _oneAtATime = new(1, 1);

        public string Name { get; }
        public long StartHeight { get; }
        public bool IsAlive => _node.IsConnected;

        private BtcPeerSource(Node node, IPEndPoint endpoint)
        {
            _node = node;
            Name = $"{endpoint} {node.PeerVersion?.UserAgent}";
            StartHeight = node.PeerVersion?.StartHeight ?? 0;
        }

        /// <summary>Connects and completes the handshake; null when the peer does not answer or does not serve blocks.</summary>
        public static BtcPeerSource? Connect(Network network, IPEndPoint endpoint, TimeSpan timeout)
        {
            Node? node = null;
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                var parameters = new NodeConnectionParameters
                {
                    ConnectCancellation = cts.Token,
                    UserAgent = $"/VerifiedX:{Globals.MajorVer}.{Globals.MinorVer}.{Globals.RevisionVer}/",
                };
                node = Node.Connect(network, endpoint, parameters);
                node.VersionHandshake(cts.Token);
                // NODE_NETWORK (1) serves every block; NODE_NETWORK_LIMITED (1024) the last 288, which is all this needs.
                var services = (ulong)(node.PeerVersion?.Services ?? 0);
                if ((services & 1UL) == 0 && (services & 1024UL) == 0)
                {
                    node.Dispose();
                    return null;
                }
                return new BtcPeerSource(node, endpoint);
            }
            catch
            {
                try { node?.Dispose(); } catch { }
                return null;
            }
        }

        public async Task<List<BtcHeader>?> GetHeadersAsync(IReadOnlyList<string> locator, CancellationToken ct)
        {
            await _oneAtATime.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                {
                    using var listener = _node.CreateListener();
                    var blockLocator = new BlockLocator();
                    foreach (var hash in locator) blockLocator.Blocks.Add(uint256.Parse(hash));
                    _node.SendMessage(new GetHeadersPayload(blockLocator));
                    var payload = listener.ReceivePayload<HeadersPayload>(ct);
                    var headers = new List<BtcHeader>(payload.Headers.Count);
                    foreach (var raw in payload.Headers)
                    {
                        if (!BtcHeader.TryParse(Convert.ToHexString(raw.ToBytes()).ToLowerInvariant(), out var header) || header == null)
                            return null;
                        headers.Add(header);
                    }
                    return headers;
                }, ct);
            }
            catch { return null; }
            finally { _oneAtATime.Release(); }
        }

        public async Task<Block?> GetBlockAsync(string hash, CancellationToken ct)
        {
            await _oneAtATime.WaitAsync(ct);
            try
            {
                return await Task.Run(() =>
                {
                    using var listener = _node.CreateListener();
                    var wanted = uint256.Parse(hash);
                    _node.SendMessage(new GetDataPayload(new InventoryVector(InventoryType.MSG_BLOCK, wanted)));
                    while (true)
                    {
                        var block = listener.ReceivePayload<BlockPayload>(ct).Object;
                        if (block.GetHash() == wanted) return block;
                    }
                }, ct);
            }
            catch { return null; }
            finally { _oneAtATime.Release(); }
        }

        public void Dispose()
        {
            try { _node.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// A block explorer speaking the Esplora API (mempool.space, blockstream.info), used only when no peer delivers.
    /// What it returns is checked like anything a peer returns.
    /// </summary>
    public sealed class BtcEsploraSource : IBtcSource
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
        private readonly string _baseUrl;
        private readonly Network _network;

        public string Name => _baseUrl;
        public bool IsAlive => true;

        public BtcEsploraSource(string baseUrl, Network network)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _network = network;
        }

        /// <summary>The explorers for a network (testnet4 is served by mempool.space only).</summary>
        public static List<BtcEsploraSource> For(Network network) =>
            network == Network.Main
                ? new List<BtcEsploraSource> { new("https://mempool.space/api", network), new("https://blockstream.info/api", network) }
                : new List<BtcEsploraSource> { new("https://mempool.space/testnet4/api", network) };

        /// <summary>A few headers at a time: enough for a fallback, never the bulk of a sync.</summary>
        public const int HeadersPerCall = 12;

        public async Task<List<BtcHeader>?> GetHeadersAsync(IReadOnlyList<string> locator, CancellationToken ct)
        {
            try
            {
                foreach (var hash in locator)
                {
                    using var status = await Http.GetAsync($"{_baseUrl}/block/{hash}/status", ct);
                    if (!status.IsSuccessStatusCode) continue;
                    var json = Newtonsoft.Json.Linq.JObject.Parse(await status.Content.ReadAsStringAsync(ct));
                    if (json.Value<bool?>("in_best_chain") != true) continue;
                    var height = json.Value<long>("height");

                    var headers = new List<BtcHeader>();
                    for (var h = height + 1; h <= height + HeadersPerCall; h++)
                    {
                        using var atHeight = await Http.GetAsync($"{_baseUrl}/block-height/{h}", ct);
                        if (!atHeight.IsSuccessStatusCode) break; // past the tip
                        var nextHash = (await atHeight.Content.ReadAsStringAsync(ct)).Trim();
                        var hex = (await Http.GetStringAsync($"{_baseUrl}/block/{nextHash}/header", ct)).Trim().ToLowerInvariant();
                        if (!BtcHeader.TryParse(hex, out var header) || header == null) return null;
                        headers.Add(header);
                    }
                    return headers;
                }
                return null;
            }
            catch { return null; }
        }

        public async Task<Block?> GetBlockAsync(string hash, CancellationToken ct)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync($"{_baseUrl}/block/{hash}/raw", ct);
                var block = Block.Load(bytes, _network);
                // Explorers serve the block with witness data; the rest of the node holds blocks without it.
                var stripped = block.WithOptions(TransactionOptions.None);
                return stripped;
            }
            catch { return null; }
        }

        public void Dispose() { }
    }
}
