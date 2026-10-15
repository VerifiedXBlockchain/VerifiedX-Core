using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: a signed withdrawal transaction is dead only once one of its inputs is spent by a confirmed transaction,
    /// and an input is "spent" only if a confirmed transaction created it at the vault's address. An input no
    /// transaction on chain created (its creator not broadcast yet, or an output index the funding transaction does not
    /// have) leaves the signed transaction able to confirm later. Driven through
    /// BitcoinTransactionService.GetOutpointSpendVerdict, which every dead-transaction decision reads (the sign/start
    /// guard, the pin release, the cancellation vote's evidence), against an Electrum server on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_OutpointCreatedOnChainTests : IDisposable
    {
        private static readonly Network Net = Network.TestNet;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly List<ClientSettings>? _priorServers = Globals.ClientSettings;
        private readonly BitcoinAddress _vault = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        private readonly BitcoinAddress _elsewhere = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        private readonly NBitcoin.Transaction _funding;   // confirmed: vout 0 and 1 pay the vault, vout 2 pays elsewhere
        private readonly NBitcoin.Transaction _spender;   // confirmed: spends funding:0
        private readonly LoopbackElectrum _electrum;

        public Round37_OutpointCreatedOnChainTests()
        {
            Globals.BTCNetwork = Net;
            _funding = Net.CreateTransaction();
            _funding.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0));
            _funding.Outputs.Add(Money.Satoshis(100_000), _vault);
            _funding.Outputs.Add(Money.Satoshis(200_000), _vault);
            _funding.Outputs.Add(Money.Satoshis(300_000), _elsewhere);
            _spender = Net.CreateTransaction();
            _spender.Inputs.Add(new OutPoint(_funding.GetHash(), 0));
            _spender.Outputs.Add(Money.Satoshis(90_000), _elsewhere);

            var raw = new Dictionary<string, string> { [Id(_funding)] = _funding.ToHex(), [Id(_spender)] = _spender.ToHex() };
            _electrum = new LoopbackElectrum(req =>
            {
                var p = req["params"] as JArray;
                switch ((string?)req["method"])
                {
                    case "server.version": return new JArray("Loopback", "1.4");
                    case "blockchain.scripthash.listunspent":
                        return new JArray(new JObject { ["tx_hash"] = Id(_funding), ["tx_pos"] = 1, ["height"] = 100, ["value"] = 200_000 });
                    case "blockchain.scripthash.get_history":
                        return new JArray(new JObject { ["tx_hash"] = Id(_funding), ["height"] = 100 }, new JObject { ["tx_hash"] = Id(_spender), ["height"] = 101 });
                    case "blockchain.transaction.get":
                        return raw.TryGetValue((string)p![0]!, out var hex) ? hex : null;
                    default: return null;
                }
            });
            Globals.ClientSettings = new List<ClientSettings> { new ClientSettings { Host = "127.0.0.1", Port = _electrum.Port, UseSsl = true } };
        }

        public void Dispose()
        {
            _electrum.Dispose();
            Globals.ClientSettings = _priorServers;
            Globals.BTCNetwork = _priorNetwork;
        }

        private static string Id(NBitcoin.Transaction tx) => tx.GetHash().ToString();

        [Fact]
        public async Task AnInputAConfirmedTransactionCreatedAndAnotherSpent_IsSpentByConfirmed()
        {
            var (verdict, detail) = await BitcoinTransactionService.GetOutpointSpendVerdict(_vault.ToString(), new[] { $"{Id(_funding)}:0" });
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed, detail);
        }

        [Fact]
        public async Task AnInputWhoseCreatorIsNotOnChain_LeavesTheTransactionAlive()
        {
            var phantom = $"{new uint256(RandomUtils.GetBytes(32))}:0";
            var (verdict, detail) = await BitcoinTransactionService.GetOutpointSpendVerdict(_vault.ToString(), new[] { $"{Id(_funding)}:1", phantom });
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, detail);
        }

        [Theory]
        [InlineData(2)]  // the funding transaction's output exists but pays another address
        [InlineData(7)]  // the funding transaction has no such output
        public async Task AnOutputIndexTheFundingTransactionDoesNotPayTheVault_LeavesTheTransactionAlive(int vout)
        {
            var (verdict, detail) = await BitcoinTransactionService.GetOutpointSpendVerdict(_vault.ToString(), new[] { $"{Id(_funding)}:{vout}" });
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, detail);
        }

        /// <summary>Electrum JSON-RPC over TLS on loopback: one request per connection, as the client makes them.</summary>
        private sealed class LoopbackElectrum : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly X509Certificate2 _cert;
            private readonly Func<JObject, JToken?> _handler;
            private readonly CancellationTokenSource _stop = new();
            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public LoopbackElectrum(Func<JObject, JToken?> handler)
            {
                _handler = handler;
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                _cert = new X509Certificate2(selfSigned.Export(X509ContentType.Pfx));
                _listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            }

            private async Task Serve(TcpClient client)
            {
                using (client)
                using (var ssl = new SslStream(client.GetStream(), false))
                {
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(_cert);
                        using var reader = new StreamReader(ssl, Encoding.UTF8, false, 1024, leaveOpen: true);
                        var line = await reader.ReadLineAsync();
                        if (line == null) return;
                        var req = JObject.Parse(line);
                        var result = _handler(req);
                        var reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = req["id"] };
                        if (result == null) reply["error"] = new JObject { ["code"] = 2, ["message"] = "not found" };
                        else reply["result"] = result;
                        var bytes = Encoding.UTF8.GetBytes(reply.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                        await ssl.WriteAsync(bytes);
                        await ssl.FlushAsync();
                    }
                    catch { }
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
                _cert.Dispose();
            }
        }
    }
}
