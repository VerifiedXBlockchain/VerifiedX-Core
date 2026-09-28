using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.ElectrumX;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Replaces the Electrum server list and the per-server balance query for one test: each named server either
    /// answers with a BTC balance or does not answer. Health is recorded on the pool as the real query does.
    /// </summary>
    internal sealed class FakeElectrum : IDisposable
    {
        private readonly List<ClientSettings>? _priorServers = Globals.ClientSettings;
        private readonly Func<ClientSettings, string, Task<(bool Ok, decimal Btc)>> _priorQuery = DepositBalanceLookup.QueryServer;
        private readonly Dictionary<string, (bool Answers, decimal Btc)> _answers = new();

        /// <summary>Hosts asked, in order.</summary>
        public List<string> Asked { get; } = new();

        public FakeElectrum(params (string Host, bool Answers, decimal Btc)[] servers)
        {
            foreach (var s in servers)
                _answers[s.Host] = (s.Answers, s.Btc);
            Globals.ClientSettings = servers.Select(s => new ClientSettings { Host = s.Host, Port = 50002, UseSsl = true }).ToList();
            DepositBalanceLookup.ClearCache();
            DepositBalanceLookup.QueryServer = (server, address) =>
            {
                lock (Asked)
                    Asked.Add(server.Host);
                var (answers, btc) = _answers[server.Host];
                if (answers)
                    ElectrumServerPool.ReportSuccess(server, 1);
                else
                    ElectrumServerPool.ReportFailure(server, "fake: no answer");
                return Task.FromResult((answers, btc));
            };
        }

        public void Set(string host, bool answers, decimal btc) => _answers[host] = (answers, btc);

        public void Dispose()
        {
            DepositBalanceLookup.QueryServer = _priorQuery;
            DepositBalanceLookup.ClearCache();
            Globals.ClientSettings = _priorServers;
        }
    }
}
