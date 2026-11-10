using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The casters' burn reader against a provider that refuses log reads over 200 blocks, as sepolia.base.org does. It
    /// doubled its span into the cap and cut it to a quarter on each refusal, about 95 blocks a 12-second poll; with Base
    /// at 200 ms blocks a caster that had fallen behind barely gained on the chain and every burn waited as long
    /// (testnet, 10 November 2026). Driven through the loop's own poll, <see cref="BaseBridgeExitWatchService.ReadUntilCaughtUpAsync"/>.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseBridgeExitReaderSpanTests : IDisposable
    {
        private const long Head = 1_000_000;
        private const int ProviderCap = 200;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;
        private readonly string _priorRpc = BaseBridgeService.BaseRpcUrl;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;

        public BaseBridgeExitReaderSpanTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"exit_reader_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100, Timestamp = TimeUtil.GetTime() };
            Globals.IsBlockCaster = false;
            BaseBridgeService.ContractAddress = "0x3669EE313f9Aa14b9eBb787DfdB0E97Ca67A0510";
            BaseBridgeExitWatchService.ResetSpanForTests();
            BaseBridgeExitWatchService.ResetRewindForTests();
        }

        public void Dispose()
        {
            BaseBridgeService.BaseRpcUrl = _priorRpc;
            BaseBridgeService.ContractAddress = _priorContract;
            BaseBridgeExitWatchService.ResetSpanForTests();
            BaseBridgeExitWatchService.ResetRewindForTests();
            Globals.IsBlockCaster = _priorIsBlockCaster;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task ACasterBehindBase_CatchesUpInAFewPolls_AndSettlesAtTheProvidersCap()
        {
            var rpc = new CappedRpc();
            try
            {
                BaseBridgeService.BaseRpcUrl = rpc.Url;
                var state = BridgeExitSyncState.GetOrCreate();
                state.LastScannedBlock = Head - 6_000;
                BridgeExitSyncState.Save(state);

                var polls = 0;
                while (BridgeExitSyncState.GetOrCreate().LastScannedBlock < Head && polls < 12)
                {
                    await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
                    polls++;
                }

                Assert.Equal(Head, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
                Assert.Equal(ProviderCap, BaseBridgeExitWatchService.CurrentSpan);
                // A handful of refusals while it finds the cap (4000, 1000, 250, then the bisection), not one every few reads.
                Assert.InRange(rpc.RefusedReads, 1, 10);

                // Settled: the next stretch is read at the cap without a refusal.
                var refusedBefore = rpc.RefusedReads;
                state = BridgeExitSyncState.GetOrCreate();
                state.LastScannedBlock = Head - 2_000;
                BridgeExitSyncState.Save(state);
                await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
                Assert.Equal(Head, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
                Assert.Equal(refusedBefore, rpc.RefusedReads);
            }
            finally
            {
                rpc.Dispose();
            }
        }

        [Fact]
        public void ARangeThatFailsAtASpanThatWorked_ShrinksAsBefore_AndGrowsBackToIt()
        {
            Assert.Equal(1000, BaseBridgeExitWatchService.SpanAfterFailure(4000));     // no span has worked yet
            Assert.Equal(2100, BaseBridgeExitWatchService.SpanAfterSuccess(200));      // worked: halfway to the narrowest refused (4000)
            Assert.Equal(200, BaseBridgeExitWatchService.SpanAfterFailure(400));       // refused above 200: back to 200
            Assert.Equal(50, BaseBridgeExitWatchService.SpanAfterFailure(200));        // refused at a span that worked: a dense range, a quarter
            Assert.Equal(100, BaseBridgeExitWatchService.SpanAfterSuccess(50));        // and back up to the span that worked
            Assert.Equal(200, BaseBridgeExitWatchService.SpanAfterSuccess(100));
            Assert.Equal(300, BaseBridgeExitWatchService.SpanAfterSuccess(200));       // then halfway to the narrowest refused (400)
        }

        /// <summary>A JSON-RPC endpoint on loopback: head <see cref="Head"/>, no logs, eth_getLogs over <see cref="ProviderCap"/> blocks refused.</summary>
        private sealed class CappedRpc : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private int _refused;
            public int RefusedReads => Volatile.Read(ref _refused);
            public string Url { get; }

            public CappedRpc()
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

            private string Answer(JObject request)
            {
                var id = request["id"];
                switch (request["method"]?.ToString())
                {
                    case "eth_blockNumber":
                        return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = "0x" + Head.ToString("x") }.ToString();
                    case "eth_getLogs":
                        var filter = (JObject)request["params"]![0]!;
                        var from = Convert.ToInt64(filter["fromBlock"]!.ToString(), 16);
                        var to = Convert.ToInt64(filter["toBlock"]!.ToString(), 16);
                        if (to - from + 1 > ProviderCap)
                        {
                            Interlocked.Increment(ref _refused);
                            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32614, ["message"] = $"eth_getLogs is limited to a {ProviderCap} range" } }.ToString();
                        }
                        return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JArray() }.ToString();
                    default:
                        return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32601, ["message"] = "method not found" } }.ToString();
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                try { _listener.Stop(); } catch { }
            }
        }
    }
}
