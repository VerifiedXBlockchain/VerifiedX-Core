using System.Diagnostics;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.ElectrumX
{
    /// <summary>
    /// The one place that decides which Electrum server a call uses (it replaces five selection loops that each
    /// did it differently). Rules:
    ///   - the best server is the healthy one that answered fastest; it keeps being used while it works, instead
    ///     of rotating by use count onto whichever server is worst;
    ///   - a failure puts the server in a cooldown that doubles with each consecutive failure (15 s up to 10 min),
    ///     and the first success clears it;
    ///   - a server more than <see cref="MaxTipLagBlocks"/> behind the others is used only when nothing else is;
    ///   - when every server is cooling down there is no candidate: callers get "no answer" at once instead of
    ///     waiting out a timeout per server. The health probe brings servers back.
    /// </summary>
    public static class ElectrumServerPool
    {
        public static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(60);
        public const int MaxTipLagBlocks = 3;

        private static readonly object _lock = new();
        private static int _probeLoopStarted;

        /// <summary>Servers a caller may use now, best first. Empty when none is configured or all are cooling down.</summary>
        public static List<ClientSettings> GetCandidates() => GetCandidates(DateTime.UtcNow);

        internal static List<ClientSettings> GetCandidates(DateTime nowUtc)
        {
            var servers = Globals.ClientSettings;
            if (servers == null)
                return new List<ClientSettings>();

            lock (_lock)
            {
                var ready = servers.Where(s => s.CooldownUntilUtc <= nowUtc).ToList();
                var inStep = ready.Where(s => !s.IsLagging).ToList();
                // OrderBy is stable: servers never measured keep their configured order.
                return (inStep.Any() ? inStep : ready)
                    .OrderBy(s => s.FailCount)
                    .ThenBy(s => s.LastLatencyMs ?? long.MaxValue)
                    .ToList();
            }
        }

        public static void ReportSuccess(ClientSettings server, long latencyMs)
        {
            lock (_lock)
            {
                if (server.FailCount > 0)
                    LogUtility.Log($"Electrum server {server.Label} answered again after {server.FailCount} failure(s)", "ElectrumServerPool");
                server.Count++;
                server.FailCount = 0;
                server.CooldownUntilUtc = DateTime.MinValue;
                server.LastSuccessUtc = DateTime.UtcNow;
                server.LastLatencyMs = latencyMs;
            }
        }

        /// <summary>The server answered, but with a JSON-RPC error (unknown transaction, say): it is up.</summary>
        public static void ReportAnswered(ClientSettings server)
        {
            lock (_lock)
            {
                server.Count++;
                server.FailCount = 0;
                server.CooldownUntilUtc = DateTime.MinValue;
                server.LastSuccessUtc = DateTime.UtcNow;
            }
        }

        public static void ReportFailure(ClientSettings server, string? reason)
        {
            TimeSpan cooldown;
            ulong failures;
            lock (_lock)
            {
                server.Count++;
                server.FailCount++;
                failures = server.FailCount;
                cooldown = CooldownFor(failures);
                server.CooldownUntilUtc = DateTime.UtcNow + cooldown;
            }

            // First failure, then every 10th: a dead server is re-probed every minute.
            if (failures == 1 || failures % 10 == 0)
                ErrorLogUtility.LogError($"Electrum server {server.Label} failed ({reason ?? "no answer"}); {failures} in a row, not used for {cooldown.TotalSeconds:0} s",
                    "ElectrumServerPool.ReportFailure()");
        }

        internal static TimeSpan CooldownFor(ulong consecutiveFailures)
        {
            if (consecutiveFailures == 0)
                return TimeSpan.Zero;
            var doublings = (int)Math.Min(consecutiveFailures - 1, 16);
            var seconds = BaseCooldown.TotalSeconds * Math.Pow(2, doublings);
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxCooldown.TotalSeconds));
        }

        /// <summary>
        /// Runs one call against one server with fresh short-lived connections, records the outcome on the server,
        /// and returns (Ok, value). <paramref name="timeout"/>, when given, bounds the whole attempt.
        /// </summary>
        public static async Task<(bool Ok, T Value)> AttemptAsync<T>(ClientSettings server, Func<Client, Task<T>> call, TimeSpan? timeout = null)
        {
            // Always TLS, as every caller did before this pool existed; UseSsl from config.txt was never honored and
            // configs in the field rely on that (a bare host:port there means an SSL port).
            var client = new Client(server.Host, server.Port, true);
            if (timeout is TimeSpan t)
            {
                client.ConnectTimeout = t;
                client.RequestTimeout = t;
            }

            var sw = Stopwatch.StartNew();
            T value;
            try
            {
                var task = call(client);
                value = timeout is TimeSpan bound ? await task.WaitAsync(bound) : await task;
            }
            catch (Exception ex)
            {
                ReportFailure(server, ex.Message);
                return (false, default!);
            }

            if (client.LastCallSucceeded)
            {
                ReportSuccess(server, sw.ElapsedMilliseconds);
                return (true, value);
            }

            if (client.LastCallAnswered)
                ReportAnswered(server);
            else
                ReportFailure(server, client.LastFailure);
            return (false, default!);
        }

        /// <summary>
        /// Asks the best candidate; if it does not succeed, asks all the others at once and takes the first success.
        /// Null when none succeeded. Bounded by the attempt timeout, twice at most, rather than once per server.
        /// </summary>
        public static async Task<(ClientSettings Server, T Value)?> FirstSuccessAsync<T>(
            IReadOnlyList<ClientSettings> candidates, Func<ClientSettings, Task<(bool Ok, T Value)>> attempt)
        {
            if (candidates.Count == 0)
                return null;

            var first = await attempt(candidates[0]);
            if (first.Ok)
                return (candidates[0], first.Value);

            var pending = candidates.Skip(1).Select(async s => (Server: s, Result: await attempt(s))).ToList();
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var (server, result) = await done;
                if (result.Ok)
                    return (server, result.Value);
            }
            return null;
        }

        /// <summary>
        /// A client whose server just answered a version handshake, or null when no server answered. For callers
        /// that run several commands; single balance lookups should use DepositBalanceLookup.
        /// </summary>
        public static async Task<Client?> GetClientAsync()
        {
            var found = await FirstSuccessAsync(GetCandidates(), s => AttemptAsync(s, async c =>
            {
                await c.GetServerVersion();
                return c;
            }));

            if (found == null)
            {
                ErrorLogUtility.LogError("No Electrum server answered (all failed or cooling down)", "ElectrumServerPool.GetClientAsync()");
                return null;
            }
            return found.Value.Value;
        }

        /// <summary>Starts the background health probe once per process; later calls do nothing.</summary>
        public static Task StartHealthProbeLoop()
        {
            if (Interlocked.Exchange(ref _probeLoopStarted, 1) == 1)
                return Task.CompletedTask;
            return Task.Run(ProbeLoop);
        }

        private static async Task ProbeLoop()
        {
            while (true)
            {
                try
                {
                    await ProbeAllAsync();
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Electrum health probe failed: {ex.Message}", "ElectrumServerPool.ProbeLoop()");
                }
                await Task.Delay(ProbeInterval);
            }
        }

        /// <summary>
        /// Asks every configured server, cooling down or not, for its tip height: recovers servers that came back,
        /// cools down ones that died, and marks servers that fell behind.
        /// </summary>
        public static async Task ProbeAllAsync()
        {
            var servers = Globals.ClientSettings?.ToList();
            if (servers == null || servers.Count == 0)
            {
                Globals.ElectrumXConnected = false;
                return;
            }

            await Task.WhenAll(servers.Select(async s =>
            {
                var (ok, tip) = await AttemptAsync(s, c => c.GetTipHeight());
                lock (_lock)
                    s.TipHeight = ok ? tip : null;
            }));

            ApplyTipLag(servers);

            if (servers.Any(s => s.FailCount == 0 && s.LastSuccessUtc != null))
            {
                Globals.ElectrumXConnected = true;
                Globals.ElectrumXLastCommunication = DateTime.Now;
            }
            else
            {
                Globals.ElectrumXConnected = false;
            }
        }

        /// <summary>
        /// Marks servers more than <see cref="MaxTipLagBlocks"/> behind the reference tip. The reference is the
        /// second-highest tip reported, so one server claiming a false height cannot sideline all the others.
        /// </summary>
        internal static void ApplyTipLag(IReadOnlyCollection<ClientSettings> servers)
        {
            lock (_lock)
            {
                var tips = servers.Where(s => s.TipHeight.HasValue).Select(s => s.TipHeight!.Value).OrderByDescending(h => h).ToList();
                var reference = tips.Count >= 2 ? tips[1] : tips.FirstOrDefault();

                foreach (var s in servers)
                {
                    var lagging = s.TipHeight.HasValue && reference - s.TipHeight.Value > MaxTipLagBlocks;
                    if (lagging && !s.IsLagging)
                        ErrorLogUtility.LogError($"Electrum server {s.Label} is behind: height {s.TipHeight} vs {reference}; used only as a last resort",
                            "ElectrumServerPool.ApplyTipLag()");
                    s.IsLagging = lagging;
                }
            }
        }
    }
}
