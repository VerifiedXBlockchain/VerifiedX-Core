using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethereum.ABI;
using Nethereum.Util;
using Newtonsoft.Json.Linq;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A Base provider on loopback for tests: a JSON-RPC endpoint the node's own client talks to, so a test reads Base
    /// the way the node does. It answers eth_chainId with <see cref="ChainId"/>, eth_blockNumber with <see cref="Head"/>,
    /// eth_getBlockByNumber for the "finalized" tag with <see cref="Finalized"/> and for a number up to the head with
    /// that block, eth_getTransactionReceipt from <see cref="Receipts"/> and eth_getLogs from <see cref="Logs"/>;
    /// <see cref="On"/> answers anything first when it is set. Every request is counted by method. Several of these
    /// stand in for several providers.
    /// </summary>
    public sealed class FakeBaseRpc : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public string Url { get; }

        /// <summary>The chain id this provider gives; null gives the one the node expects when it is asked.</summary>
        public long? ChainId { get; set; }

        /// <summary>The provider's newest block.</summary>
        public long Head { get; set; } = 1_000;

        /// <summary>The provider's finalized block; null is the head (everything it holds is final).</summary>
        public long? Finalized { get; set; }

        /// <summary>Hashes of blocks that differ from <see cref="BlockHash(string)"/> of "block" and the number.</summary>
        public ConcurrentDictionary<long, string> BlockHashes { get; } = new();

        /// <summary>How many times the finalized block was asked for by its tag.</summary>
        public int FinalizedAsked => Volatile.Read(ref _finalizedAsked);
        private int _finalizedAsked;

        /// <summary>The highest block any eth_getLogs asked up to; -1 when none was asked.</summary>
        public long HighestLogBlockAsked => Interlocked.Read(ref _highestLogBlockAsked);
        private long _highestLogBlockAsked = -1;

        /// <summary>The block each eth_call and eth_getCode was asked at, as sent ("latest", or a number in hex).</summary>
        public ConcurrentQueue<string> StateBlocksAsked { get; } = new();

        /// <summary>The hash this provider's chain holds at <paramref name="number"/>.</summary>
        public string HashAt(long number) => BlockHashes.TryGetValue(number, out var hash) ? hash : BlockHash("block" + number);

        /// <summary>When true every request fails, as from a provider that is down.</summary>
        public bool Down { get; set; }

        /// <summary>Receipts by transaction hash (lower case, with 0x).</summary>
        public ConcurrentDictionary<string, JObject> Receipts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Logs served to eth_getLogs when their block is inside the asked range and their address and first topic match.</summary>
        public List<JObject> Logs { get; } = new();

        /// <summary>Answers a request before the defaults: the result, or null to leave it to them.</summary>
        public Func<string, JArray, JToken?>? On { get; set; }

        public FakeBaseRpc()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            });
        }

        /// <summary>How many requests of <paramref name="method"/> were made.</summary>
        public int CallsTo(string method) => _calls.TryGetValue(method, out var n) ? n : 0;

        /// <summary>How many requests were made, of any method.</summary>
        public int TotalCalls => _calls.Values.Sum();

        public void ForgetCalls() => _calls.Clear();

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int headerEnd = -1, length = 0;
                    while (true)
                    {
                        var n = await stream.ReadAsync(chunk);
                        if (n == 0) return;
                        buffer.Write(chunk, 0, n);
                        var text = Encoding.ASCII.GetString(buffer.ToArray());
                        if (headerEnd < 0 && (headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                            foreach (var line in text[..headerEnd].Split("\r\n"))
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    length = int.Parse(line["Content-Length:".Length..].Trim());
                        if (headerEnd >= 0 && buffer.Length >= headerEnd + 4 + length) break;
                    }
                    var body = Encoding.UTF8.GetString(buffer.ToArray(), headerEnd + 4, length);
                    var reply = Encoding.UTF8.GetBytes(Answer(JObject.Parse(body)));
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {reply.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(reply);
                }
                catch { }
            }
        }

        private static string Result(JToken? id, JToken result) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToString();

        private static string Error(JToken? id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } }.ToString();

        private string Answer(JObject request)
        {
            var id = request["id"];
            var method = request["method"]?.ToString() ?? "";
            var args = request["params"] as JArray ?? new JArray();
            _calls.AddOrUpdate(method, 1, (_, n) => n + 1);
            if (Down) return Error(id, -32000, "the provider is down");
            if (method is "eth_call" or "eth_getCode" && args.Count > 1) StateBlocksAsked.Enqueue(args[1]!.ToString());
            try
            {
                if (On?.Invoke(method, args) is { } scripted) return Result(id, scripted);
            }
            catch (Exception ex) { return Error(id, -32000, ex.Message); }
            switch (method)
            {
                case "eth_chainId":
                    return Result(id, Hex(ChainId ?? Globals.BaseEvmChainId));
                case "eth_blockNumber":
                    return Result(id, Hex(Head));
                case "eth_getBlockByNumber":
                    var tag = args[0]!.ToString();
                    if (tag == "finalized") Interlocked.Increment(ref _finalizedAsked);
                    long number;
                    if (tag == "finalized") number = Finalized ?? Head;
                    else if (tag == "latest") number = Head;
                    else if (tag.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) number = Convert.ToInt64(tag, 16);
                    else return Error(id, -32602, $"unknown block tag {tag}");
                    if (number > Head) return Result(id, JValue.CreateNull());
                    return Result(id, new JObject
                    {
                        ["number"] = Hex(number), ["hash"] = HashAt(number), ["parentHash"] = number > 0 ? HashAt(number - 1) : "0x" + new string('0', 64),
                        ["timestamp"] = Hex(1_700_000_000 + number * 2), ["transactions"] = new JArray(),
                    });
                case "eth_getTransactionReceipt":
                    return Result(id, Receipts.TryGetValue(args[0]!.ToString(), out var receipt) ? receipt : JValue.CreateNull());
                case "eth_getLogs":
                    var filter = (JObject)args[0]!;
                    var from = Convert.ToInt64(filter["fromBlock"]!.ToString(), 16);
                    var to = Convert.ToInt64(filter["toBlock"]!.ToString(), 16);
                    long seen;
                    while (to > (seen = Interlocked.Read(ref _highestLogBlockAsked)) && Interlocked.CompareExchange(ref _highestLogBlockAsked, to, seen) != seen) { }
                    var addresses = filter["address"] is JArray many ? many.Select(a => a.ToString()).ToList()
                        : filter["address"] != null ? new List<string> { filter["address"]!.ToString() } : new List<string>();
                    var topic0 = (filter["topics"] as JArray)?.FirstOrDefault() is { } first
                        ? first is JArray any ? any.Select(t => t.ToString()).ToList() : first.Type == JTokenType.Null ? null : new List<string> { first.ToString() }
                        : null;
                    List<JObject> served;
                    lock (Logs)
                        served = Logs.Where(l =>
                        {
                            var block = Convert.ToInt64(l["blockNumber"]!.ToString(), 16);
                            return block >= from && block <= to
                                && (addresses.Count == 0 || addresses.Contains(l["address"]!.ToString(), StringComparer.OrdinalIgnoreCase))
                                && (topic0 == null || topic0.Contains(((JArray)l["topics"]!)[0]!.ToString(), StringComparer.OrdinalIgnoreCase));
                        }).ToList();
                    return Result(id, new JArray(served));
                default:
                    return Error(id, -32601, "method not found");
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
        }

        // ── What a provider serves ───────────────────────────────────────────────────────

        public static string Hex(long value) => "0x" + value.ToString("x");

        /// <summary>A block hash made from a label, so two labels are two blocks.</summary>
        public static string BlockHash(string label) => "0x" + Sha3Keccack.Current.CalculateHash(label);

        /// <summary>
        /// A bridge burn's log as the contract emits it: VfxExitBurned for an exit to VFX, BTCExitBurned for one to
        /// Bitcoin, with the burner indexed and the amount, the destination and the chain id in the data.
        /// </summary>
        public static JObject BurnLog(string txHash, string contract, bool vfxExit, long amountSats, string destination, long chainId,
            long blockNumber = 900, int logIndex = 0, string? blockHash = null, string burner = "0x00000000000000000000000000000000000000b1")
        {
            var signature = vfxExit ? "VfxExitBurned(address,uint256,string,uint256)" : "BTCExitBurned(address,uint256,string,uint256)";
            var data = new ABIEncode().GetABIEncoded(
                new ABIValue("uint256", new BigInteger(amountSats)),
                new ABIValue("string", destination),
                new ABIValue("uint256", new BigInteger(chainId)));
            return new JObject
            {
                ["address"] = contract,
                ["topics"] = new JArray("0x" + Sha3Keccack.Current.CalculateHash(signature), "0x" + burner[2..].PadLeft(64, '0')),
                ["data"] = "0x" + Convert.ToHexString(data).ToLowerInvariant(),
                ["blockNumber"] = Hex(blockNumber),
                ["blockHash"] = blockHash ?? BlockHash("block" + blockNumber),
                ["transactionHash"] = txHash,
                ["transactionIndex"] = "0x0",
                ["logIndex"] = Hex(logIndex),
                ["removed"] = false,
            };
        }

        /// <summary>A transaction's receipt holding <paramref name="logs"/>, in the block the first of them names.</summary>
        public static JObject Receipt(string txHash, bool succeeded, params JObject[] logs)
        {
            var blockNumber = logs.Length > 0 ? logs[0]["blockNumber"]!.ToString() : Hex(900);
            var blockHash = logs.Length > 0 ? logs[0]["blockHash"]!.ToString() : BlockHash("block900");
            return new JObject
            {
                ["transactionHash"] = txHash,
                ["transactionIndex"] = "0x0",
                ["blockHash"] = blockHash,
                ["blockNumber"] = blockNumber,
                ["from"] = "0x00000000000000000000000000000000000000b1",
                ["to"] = logs.Length > 0 ? logs[0]["address"]!.ToString() : "0x00000000000000000000000000000000000000c1",
                ["cumulativeGasUsed"] = "0x5208",
                ["gasUsed"] = "0x5208",
                ["effectiveGasPrice"] = "0x1",
                ["contractAddress"] = JValue.CreateNull(),
                ["logs"] = new JArray(logs.Cast<object>().ToArray()),
                ["logsBloom"] = "0x" + new string('0', 512),
                ["status"] = succeeded ? "0x1" : "0x0",
                ["type"] = "0x2",
            };
        }

        /// <summary>Adds a burn to what this provider serves: its receipt, and its log for eth_getLogs.</summary>
        public JObject AddBurn(string txHash, string contract, bool vfxExit, long amountSats, string destination, long? chainId = null, long blockNumber = 900)
        {
            var log = BurnLog(txHash, contract, vfxExit, amountSats, destination, chainId ?? Globals.BaseEvmChainId, blockNumber);
            Receipts[txHash] = Receipt(txHash, true, log);
            lock (Logs) Logs.Add(log);
            return log;
        }
    }
}
