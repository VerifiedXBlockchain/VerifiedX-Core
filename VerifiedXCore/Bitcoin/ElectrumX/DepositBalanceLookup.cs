using System.Collections.Concurrent;
using NBitcoin;

namespace VerifiedXCore.Bitcoin.ElectrumX
{
    /// <summary>A deposit address's confirmed BTC balance, or the fact that no server answered.</summary>
    public readonly record struct DepositBalanceAnswer(bool Answered, decimal ConfirmedBtc, string? Server, bool NoOtherServer)
    {
        public static DepositBalanceAnswer NoAnswer(bool noOtherServer = false) => new(false, 0M, null, noOtherServer);
    }

    /// <summary>
    /// Confirmed balance of one Bitcoin address, as used by the vBTC owner-balance checks and displays. A failed
    /// lookup is reported as such, never as a zero balance. Answers are remembered for <see cref="CacheTtl"/> per
    /// address, so relaying and block building do not re-ask for every transaction on the same vault.
    /// </summary>
    public static class DepositBalanceLookup
    {
        public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        /// <summary>Bound on the best server's attempt, and again on the fan-out to the others.</summary>
        public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

        private static readonly ConcurrentDictionary<string, (decimal Btc, string Server, DateTime AtUtc)> _cache = new();

        /// <summary>
        /// How one server is asked for a balance (Ok, BTC). Tests replace it; production asks Electrum through
        /// <see cref="ElectrumServerPool.AttemptAsync"/>, which also records the server's health.
        /// </summary>
        internal static Func<ClientSettings, string, Task<(bool Ok, decimal Btc)>> QueryServer = QueryElectrumAsync;

        /// <param name="address">The Bitcoin address.</param>
        /// <param name="bypassCache">Ask the network even when a recent answer is remembered.</param>
        /// <param name="excludeServer">Label of a server not to ask (the one whose answer is being double-checked).</param>
        public static async Task<DepositBalanceAnswer> GetConfirmedBalanceAsync(string address, bool bypassCache = false, string? excludeServer = null)
        {
            if (string.IsNullOrEmpty(address))
                return DepositBalanceAnswer.NoAnswer();

            if (!bypassCache && _cache.TryGetValue(address, out var hit) && DateTime.UtcNow - hit.AtUtc < CacheTtl)
                return new DepositBalanceAnswer(true, hit.Btc, hit.Server, false);

            var candidates = ElectrumServerPool.GetCandidates().Where(s => s.Label != excludeServer).ToList();
            if (candidates.Count == 0)
            {
                // "No other server" only when none is configured besides the excluded one — not when they are all cooling down.
                var configured = Globals.ClientSettings?.Count(s => s.Label != excludeServer) ?? 0;
                return DepositBalanceAnswer.NoAnswer(noOtherServer: configured == 0);
            }

            var found = await ElectrumServerPool.FirstSuccessAsync(candidates, s => QueryServer(s, address));
            if (found == null)
                return DepositBalanceAnswer.NoAnswer();

            var (server, btc) = found.Value;
            _cache[address] = (btc, server.Label, DateTime.UtcNow);
            return new DepositBalanceAnswer(true, btc, server.Label, false);
        }

        /// <summary>The last answer for this address, however old, for display when no server answers now.</summary>
        public static decimal? LastKnownBalance(string address)
        {
            return _cache.TryGetValue(address, out var hit) ? hit.Btc : null;
        }

        internal static void ClearCache() => _cache.Clear();

        private static Task<(bool Ok, decimal Btc)> QueryElectrumAsync(ClientSettings server, string address)
        {
            BitcoinAddress destination;
            try
            {
                destination = BitcoinAddress.Create(address, Globals.BTCNetwork);
            }
            catch
            {
                return Task.FromResult((true, 0M)); // not an address on this network: it holds nothing, no server needed
            }

            return ElectrumServerPool.AttemptAsync(server,
                async c => (await c.GetBalance(destination)).Confirmed / 100_000_000M,
                AttemptTimeout);
        }
    }
}
