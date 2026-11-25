using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VerifiedXCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Every SignalR hub method on the node's two hub hosts, found by reflection on the hubs each host maps, is invoked by
    /// a caller the node does not know: once with small arguments and once with arguments as large as the hub takes in one
    /// message. A method fails when that caller makes it end in an error the hub did not handle, allocate many times the
    /// message, or answer slowly, on the same lines as <see cref="NetworkSurfaceTests"/> holds HTTP routes to.
    /// What fails today is <see cref="NetworkSurfaceBaseline.KnownHubFailures"/>, a list that only gets shorter. A hub
    /// method added tomorrow is probed without anyone listing it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NetworkHubSurfaceTests : IDisposable
    {
        private const int Kb = 1024;
        private const int Mb = 1024 * 1024;
        private const int FailRatio = 20;
        private const long FailBytes = 8L * Mb;
        private const int FixedRatio = 6;
        private static readonly TimeSpan Slow = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FixedSlow = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(10);
        private const string Header = "X-Surface-Test-Caller";

        private readonly ITestOutputHelper _output;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly string? _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, VerifiedXCore.Models.Peers>? _priorBanned = Globals.BannedIPs;
        private int _caller;

        public NetworkHubSurfaceTests(ITestOutputHelper output)
        {
            _output = output;
            _tempRoot = Path.Combine(Path.GetTempPath(), $"hub_surface_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.ValidatorAddress ??= "xHubSurfaceValidator"; // the validator host maps its hubs only on a validator
            Globals.BannedIPs ??= new(); // as startup leaves it: the ban list, empty
        }

        public void Dispose()
        {
            Globals.ValidatorAddress = _priorValidatorAddress;
            Globals.BannedIPs = _priorBanned!;
            DbContext.CloseDB();
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task An_unknown_caller_cannot_make_any_hub_method_do_more_than_the_baseline_lists()
        {
            var failures = new SortedSet<string>(StringComparer.Ordinal);
            var notFixed = new HashSet<string>(StringComparer.Ordinal);
            var report = new StringBuilder();
            report.AppendLine("host | hub | method | shape | message bytes | outcome | ms | allocated MB | times message | verdict");
            var probed = 0;

            foreach (var (name, build) in Hosts())
            {
                var priorAdjudicator = Globals.AdjudicateAccount;
                if (name == "p2padj") Globals.AdjudicateAccount = new VerifiedXCore.Models.Account { Address = "xHubSurfaceAdjudicator" };
                var (host, port) = await StartAsync(build);
                Globals.AdjudicateAccount = priorAdjudicator; // the hubs are mapped once the host has started
                using var _ = host;
                try
                {
                    foreach (var (hubType, path, maxMessage) in HubsOf(host))
                        foreach (var method in MethodsOf(hubType))
                            foreach (var shape in new[] { "small", "large" })
                            {
                                var args = Arguments(method, shape, maxMessage);
                                var result = await InvokeFlaggedAgainAsync(port, path, method.Name, args);
                                probed++;
                                var key = $"{name} {hubType.Name}.{method.Name}";
                                report.AppendLine($"{name} | {hubType.Name} | {method.Name} | {shape} | {result.MessageBytes} | {result.Outcome} | {result.Elapsed.TotalMilliseconds:F0} | {result.Allocated / (double)Mb:F1} | {result.TimesMessage:F0} | {result.Verdict}");
                                foreach (var flag in result.Flags) failures.Add($"{key} {flag}");
                                foreach (var flag in result.NotFixed) notFixed.Add($"{key} {flag}");
                            }
                }
                finally { await host.StopAsync(); }
            }

            var reportPath = Path.Combine(Path.GetTempPath(), "vfx-hub-surface-report.txt");
            File.WriteAllText(reportPath, report.ToString());
            _output.WriteLine($"Full report: {reportPath}");
            _output.WriteLine("Failing now:");
            foreach (var failure in failures) _output.WriteLine("  " + failure);

            Assert.True(probed > 0, "no hub method was probed");
            var known = NetworkSurfaceBaseline.KnownHubFailures;
            var added = failures.Where(f => !known.Contains(f)).ToList();
            var gone = known.Where(k => !notFixed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(added.Count == 0, "A hub method does more for an unknown caller than the baseline lists. Bound the method (do not add it to the baseline):\n  "
                + string.Join("\n  ", added) + $"\nFull report: {reportPath}");
            Assert.True(gone.Count == 0, "These no longer fail. Remove them from NetworkSurfaceBaseline.KnownHubFailures so they cannot come back:\n  " + string.Join("\n  ", gone));
        }

        // ── Hosts, as production builds them, without their background work ───────────────────────────────────

        private static IEnumerable<(string Name, Func<int, IHost> Build)> Hosts()
        {
            yield return ("p2p", port => Build<StartupP2P>(port));
            yield return ("p2pval", port => Build<StartupP2PValidator>(port));
            // An adjudicator maps its own hub in place of the public ones: the host is built as one.
            yield return ("p2padj", port => Build<StartupP2P>(port));
        }

        private static IHost Build<TStartup>(int port) where TStartup : class =>
            new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel(options => options.ListenLocalhost(port))
                    .ConfigureServices(services => services.AddTransient<IStartupFilter, UnknownCaller>())
                    .UseStartup<TStartup>()
                    // The node's background services (block production, consensus, peer calls) are not the surface.
                    .ConfigureTestServices(services =>
                    {
                        foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                            services.Remove(hosted);
                    }))
                .Build();

        private static async Task<(IHost Host, int Port)> StartAsync(Func<int, IHost> build)
        {
            for (var attempt = 1; ; attempt++)
            {
                var port = FrostConnectionLimitsTests.FreePortPair();
                var host = build(port);
                try { await host.StartAsync(); return (host, port); }
                catch (IOException) when (attempt < 5) { host.Dispose(); }
            }
        }

        /// <summary>Each request is seen as coming from an address the node has never heard of (see NetworkSurfaceTests).</summary>
        private sealed class UnknownCaller : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, following) =>
                {
                    if (context.Request.Headers.TryGetValue(Header, out var caller) && IPAddress.TryParse(caller.ToString(), out var address))
                        context.Connection.RemoteIpAddress = address;
                    await following();
                });
                next(app);
            };
        }

        // ── Hubs and methods, read from the running host ───────────────────────────────────────────────────────

        private static List<(Type Hub, string Path, int MaxMessage)> HubsOf(IHost host)
        {
            var hubs = new List<(Type, string, int)>();
            var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions>>().Value;
            foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
            {
                var hub = endpoint.Metadata.GetMetadata<HubMetadata>();
                var raw = endpoint.RoutePattern.RawText ?? "";
                if (hub == null || raw.EndsWith("/negotiate", StringComparison.Ordinal)) continue;
                var perHub = (IOptions)Activator.CreateInstance(typeof(OptionsReader<>).MakeGenericType(hub.HubType), host.Services)!;
                var max = (int)(perHub.MaximumReceiveMessageSize ?? options.MaximumReceiveMessageSize ?? 32 * Kb);
                if (hubs.All(h => h.Item1 != hub.HubType)) hubs.Add((hub.HubType, "/" + raw.TrimStart('/'), max));
            }
            return hubs;
        }

        private interface IOptions { long? MaximumReceiveMessageSize { get; } }

        private sealed class OptionsReader<THub> : IOptions where THub : Hub
        {
            private readonly HubOptions<THub> _options;
            public OptionsReader(IServiceProvider services) => _options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions<THub>>>().Value;
            public long? MaximumReceiveMessageSize => _options.MaximumReceiveMessageSize;
        }

        private static IEnumerable<MethodInfo> MethodsOf(Type hub) =>
            hub.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == hub)
                .OrderBy(m => m.Name, StringComparer.Ordinal);

        // ── Arguments ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Small: an empty or zero value of each parameter's kind. Large: each text-carrying parameter holds an equal share
        /// of the most the hub takes in one message, as a string, an object member or a list item.
        /// </summary>
        private static object?[] Arguments(MethodInfo method, string shape, int maxMessage)
        {
            var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
            var share = Math.Max(1, (maxMessage - 8 * Kb) / Math.Max(1, parameters.Length));
            return parameters.Select(p => Value(p.ParameterType, shape == "large" ? new string('x', share) : null)).ToArray();
        }

        private static object? Value(Type type, string? big)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return big ?? "x";
            if (type == typeof(bool)) return false;
            if (type.IsEnum || type.IsPrimitive || type == typeof(decimal)) return 0;
            if (type == typeof(DateTime)) return DateTime.UnixEpoch;
            if (type == typeof(byte[])) return big == null ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(big[..Math.Max(1, big.Length * 3 / 4)]);
            if (typeof(IEnumerable).IsAssignableFrom(type) && !typeof(IDictionary).IsAssignableFrom(type))
                return big == null ? Array.Empty<object>() : new object[] { new Dictionary<string, object> { ["x"] = big } };
            return big == null ? new Dictionary<string, object>() : new Dictionary<string, object> { ["x"] = big };
        }

        // ── One invocation, and what it cost the node ──────────────────────────────────────────────────────────

        private sealed record Result(string Outcome, int MessageBytes, TimeSpan Elapsed, long Allocated, double TimesMessage, string Verdict, List<string> Flags, List<string> NotFixed);

        private async Task<Result> InvokeFlaggedAgainAsync(int port, string path, string method, object?[] args)
        {
            var result = await InvokeAsync(port, path, method, args);
            for (var again = 0; again < 2 && result.Flags.Count > 0; again++)
            {
                var next = await InvokeAsync(port, path, method, args);
                var flags = result.Flags.Intersect(next.Flags).ToList();
                result = (flags.Count == next.Flags.Count ? next : result) with { Flags = flags, Verdict = flags.Count > 0 ? "FAIL " + string.Join("+", flags) : "ok on a second try" };
            }
            return result;
        }

        private async Task<Result> InvokeAsync(int port, string path, string method, object?[] args)
        {
            var caller = Interlocked.Increment(ref _caller);
            var ip = $"198.{18 + caller / 65536 % 2}.{caller % 256}.{1 + caller / 256 % 250}"; // RFC 2544 test addresses
            var connection = new HubConnectionBuilder()
                .WithUrl($"http://127.0.0.1:{port}{path}", o =>
                {
                    o.Transports = HttpTransportType.WebSockets;
                    o.SkipNegotiation = true;
                    o.Headers[Header] = ip;
                    o.ApplicationMaxBufferSize = 16 * Mb;
                    o.TransportMaxBufferSize = 16 * Mb;
                })
                .Build();
            var messageBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(args).Length;
            string outcome;
            var before = 0L;
            var clock = new Stopwatch();
            using var deadline = new CancellationTokenSource(GiveUp);
            try
            {
                await connection.StartAsync(deadline.Token);
                before = GC.GetTotalAllocatedBytes(precise: true);
                clock.Start();
                await connection.InvokeCoreAsync(method, typeof(object), args, deadline.Token);
                outcome = "answered";
            }
            catch (HubException ex) when (ex.Message.Contains("An unexpected error occurred", StringComparison.Ordinal)) { outcome = "unhandled error"; }
            catch (HubException ex) { outcome = "refused: " + ex.Message.Split((char)10)[0].TrimEnd((char)13).Replace("|", "/"); }
            // The client cancels what is pending when the hub closes the connection: only the deadline is no answer.
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { outcome = clock.IsRunning ? "no answer" : "no connection"; }
            catch (Exception ex) { outcome = "closed: " + ex.GetType().Name; } // the hub closed the connection: a refusal
            clock.Stop();
            var allocated = before == 0 ? 0 : GC.GetTotalAllocatedBytes(precise: true) - before;
            try { await connection.DisposeAsync(); } catch { }

            var counted = Math.Max(messageBytes, 64 * Kb);
            var times = allocated / (double)counted;
            var flags = new List<string>();
            var notFixed = new List<string>();
            if (allocated >= FailBytes && times >= FixedRatio) notFixed.Add("allocation");
            if (allocated >= FailBytes && times >= FailRatio) flags.Add("allocation");
            if (outcome == "no answer" || clock.Elapsed >= FixedSlow) notFixed.Add("slow");
            if (outcome == "no answer" || clock.Elapsed >= Slow) flags.Add("slow");
            if (outcome == "unhandled error") { flags.Add("unhandled-error"); notFixed.Add("unhandled-error"); }
            var verdict = flags.Count > 0 ? "FAIL " + string.Join("+", flags) : "ok";
            return new Result(outcome, messageBytes, clock.Elapsed, allocated, times, verdict, flags, notFixed);
        }
    }
}
