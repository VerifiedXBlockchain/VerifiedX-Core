using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VerifiedXCore.Arbiter;
using VerifiedXCore.Beacon;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Every HTTP route of the node's network-facing hosts, found by asking each host for its routes, is sent the body
    /// shapes that have taken nodes down before, by a caller the node does not know and with no signature. A route
    /// fails when that caller makes it allocate many times the body's size, answer slowly, or end in an unhandled
    /// error.
    ///
    /// The routes are not listed here: a route added tomorrow is probed without anyone remembering to add it. What is
    /// listed, in <see cref="NetworkSurfaceBaseline"/>, is what fails today. The test passes while the failures are
    /// exactly that list, so it fails for a new one and it fails when a listed one is fixed and the list was not cut.
    /// The list only gets shorter.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NetworkSurfaceTests : IDisposable
    {
        private const int Kb = 1024;
        private const int Mb = 1024 * 1024;

        /// <summary>A request fails when the node allocates this many times its body and at least <see cref="FailBytes"/>.</summary>
        private const int FailRatio = 20;
        private const long FailBytes = 8L * Mb;
        private static readonly TimeSpan Slow = TimeSpan.FromSeconds(2);
        /// <summary>
        /// A measurement is not the same twice, so a failure has two lines. A route fails from <see cref="FailRatio"/>
        /// and <see cref="Slow"/> up. A route in the baseline counts as fixed only once it is under these lower lines.
        /// Between the two a route is reported and neither fails nor counts as fixed.
        /// </summary>
        private const int FixedRatio = 6;
        private static readonly TimeSpan FixedSlow = TimeSpan.FromMilliseconds(750);
        /// <summary>Below the failing size, a failing ratio from this many bytes up is reported and does not fail.</summary>
        private const long NoteBytes = 1L * Mb;
        /// <summary>What every route value is sent as.</summary>
        private const string RouteValue = "x";
        private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(20);

        private readonly ITestOutputHelper _output;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly string? _priorValidatorAddress = Globals.ValidatorAddress;
        private int _caller;

        public NetworkSurfaceTests(ITestOutputHelper output)
        {
            _output = output;
            _tempRoot = Path.Combine(Path.GetTempPath(), $"network_surface_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.FrostFFIAvailable = true;                      // a node whose FROST library passed its check
            Globals.ValidatorAddress ??= "xNetworkSurfaceValidator"; // the validator API maps its routes only on a validator
            // A key ceremony under way here, as any caller with a fresh key can open one. Routes that name a session
            // are probed with this one (every route value is sent as "x"), not with a session that does not exist.
            FrostSessionStorage.DKGSessions[RouteValue] = new DKGSession
            {
                SessionId = RouteValue, SmartContractUID = "sc-network-surface", LeaderAddress = "xNetworkSurfaceLeader", LeaderSigned = true,
                ParticipantAddresses = new List<string>(), StartTimestamp = TimeUtil.GetTime(),
            };
        }

        public void Dispose()
        {
            FrostSessionStorage.DKGSessions.TryRemove(RouteValue, out _);
            Globals.FrostFFIAvailable = _priorLibrary;
            Globals.ValidatorAddress = _priorValidatorAddress;
            DbContext.CloseDB();
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* a file still held by the OS is left for the temp cleaner */ }
        }

        // ---------------------------------------------------------------------------------------------------------
        // The test
        // ---------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task An_unknown_caller_cannot_make_any_route_do_more_than_the_baseline_lists()
        {
            var failures = new SortedSet<string>(StringComparer.Ordinal);
            var notFixed = new HashSet<string>(StringComparer.Ordinal);
            var report = new StringBuilder();
            report.AppendLine("host | method | route | body type | shape | body bytes | status | ms | allocated MB | times body | heap growth MB | verdict");

            foreach (var (name, build) in Hosts())
            {
                var (host, port) = await StartAsync(build);
                using var _ = host;
                try
                {
                    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = GiveUp };
                    foreach (var route in RoutesOf(host))
                        foreach (var probe in ProbesFor(route))
                        {
                            var result = await SendFlaggedAgainAsync(client, route, probe);
                            report.AppendLine($"{name} | {route.Method} | {route.Path} | {route.BodyType?.Name ?? "-"} | {probe.Shape} | {probe.Body?.Length ?? 0} | {result.Status} | {result.Elapsed.TotalMilliseconds:F0} | {result.Allocated / (double)Mb:F1} | {result.TimesBody:F0} | {result.HeapGrowth / (double)Mb:F1} | {result.Verdict}");
                            foreach (var flag in result.Flags) failures.Add($"{name} {route.Method} {route.Path} {flag}");
                            foreach (var flag in result.NotFixed) notFixed.Add($"{name} {route.Method} {route.Path} {flag}");
                        }
                }
                finally { await host.StopAsync(); }
            }

            var reportPath = Path.Combine(Path.GetTempPath(), "vfx-network-surface-report.txt");
            File.WriteAllText(reportPath, report.ToString());
            _output.WriteLine($"Full report: {reportPath}");
            _output.WriteLine("Failing now:");
            foreach (var failure in failures) _output.WriteLine("  " + failure);

            var known = NetworkSurfaceBaseline.KnownFailures;
            var added = failures.Where(f => !known.Contains(f)).ToList();
            var gone = known.Where(k => !notFixed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.True(added.Count == 0,
                "A route does more for an unknown caller than the baseline lists. Bound the route (do not add it to the baseline):\n  "
                + string.Join("\n  ", added) + $"\nFull report: {reportPath}");
            Assert.True(gone.Count == 0,
                "These no longer fail. Remove them from NetworkSurfaceBaseline.KnownFailures so they cannot come back:\n  "
                + string.Join("\n  ", gone));
        }

        /// <summary>
        /// SignalR hub methods are entry points too. NetworkHubSurfaceTests invokes every one of them as an unknown caller;
        /// this list still makes a new hub method a moment to ask what a caller the node does know can make it do.
        /// </summary>
        [Fact]
        public void Every_hub_method_is_one_the_baseline_knows()
        {
            var methods = typeof(Globals).Assembly.GetTypes()
                .Where(t => typeof(Hub).IsAssignableFrom(t) && !t.IsAbstract)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == t)
                    .Select(m => $"{t.Name}.{m.Name}"))
                .Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

            _output.WriteLine("Hub methods now:");
            foreach (var method in methods) _output.WriteLine("  " + method);

            var known = NetworkSurfaceBaseline.KnownHubMethods;
            var added = methods.Where(m => !known.Contains(m)).ToList();
            var gone = known.Where(k => !methods.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(added.Count == 0, "New hub methods. Check what an unknown caller can make each do, then list it:\n  " + string.Join("\n  ", added));
            Assert.True(gone.Count == 0, "Hub methods that no longer exist. Remove them from NetworkSurfaceBaseline.KnownHubMethods:\n  " + string.Join("\n  ", gone));
        }

        // ---------------------------------------------------------------------------------------------------------
        // Hosts, built the way production builds them
        // ---------------------------------------------------------------------------------------------------------

        private static IEnumerable<(string Name, Func<(IHost Host, int Port)> Build)> Hosts()
        {
            yield return ("frost", () =>
            {
                var port = FrostConnectionLimitsTests.FreePortPair();
                var limits = new FrostConnectionLimits(() => Array.Empty<string>());
                limits.ReadValidators();
                return (Build<FrostStartup>(options => FrostServer.ConfigureKestrel(options, port, limits)), port);
            });
            yield return ("valapi", () =>
            {
                var port = FrostConnectionLimitsTests.FreePortPair();
                return (Build<StartupP2PCaster>(options => ValidatorService.CasterApiKestrel(options, port)), port);
            });
            yield return ("arbiter", () =>
            {
                var port = FrostConnectionLimitsTests.FreePortPair();
                return (Build<ArbiterStartup>(options => ArbiterServer.ConfigureKestrel(options, port)), port);
            });
            // The Beacon server sets nothing on Kestrel beyond the port (BeaconServerFast).
            yield return ("beacon", () =>
            {
                var port = FrostConnectionLimitsTests.FreePortPair();
                return (Build<BeaconStartup>(options => options.ListenAnyIP(port)), port);
            });
        }

        /// <summary>A port found free can be taken before the host binds it, so a host that cannot bind is built again on another.</summary>
        private static async Task<(IHost Host, int Port)> StartAsync(Func<(IHost Host, int Port)> build)
        {
            for (var attempt = 1; ; attempt++)
            {
                var (host, port) = build();
                try
                {
                    await host.StartAsync();
                    return (host, port);
                }
                catch (IOException) when (attempt < 5) { host.Dispose(); }
            }
        }

        private static IHost Build<TStartup>(Action<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions> kestrel) where TStartup : class =>
            new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel(kestrel)
                    .ConfigureServices(services => services.AddTransient<IStartupFilter, UnknownCaller>())
                    .UseStartup<TStartup>())
                .Build();

        /// <summary>
        /// The test connects from this machine, and several routes trust this machine. Each request therefore names the
        /// address it is to be seen as coming from: one the node has never heard of, in another /24 each time, so that
        /// one probe's refusal does not put the next probe's caller on a ban list.
        /// </summary>
        private sealed class UnknownCaller : IStartupFilter
        {
            public const string Header = "X-Surface-Test-Caller";

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

        // ---------------------------------------------------------------------------------------------------------
        // Routes, read from the running host
        // ---------------------------------------------------------------------------------------------------------

        private sealed record Route(string Method, string Path, Type? BodyType);

        private static List<Route> RoutesOf(IHost host)
        {
            var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
            foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
            {
                if (endpoint.Metadata.GetMetadata<HubMetadata>() != null) continue; // hubs: see the hub-method test
                var raw = endpoint.RoutePattern.RawText ?? "";
                var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
                if (action != null)
                    raw = raw.Replace("[controller]", action.ControllerName, StringComparison.OrdinalIgnoreCase)
                             .Replace("{controller}", action.ControllerName, StringComparison.OrdinalIgnoreCase)
                             .Replace("{action}", action.ActionName, StringComparison.OrdinalIgnoreCase);
                // Every route value becomes "x": the probes ask what the route does for a caller that names nothing real.
                var path = "/" + Regex.Replace(raw, @"\{[^}]*\}", RouteValue).TrimStart('/');
                var bodyType = action?.Parameters.FirstOrDefault(p => p.BindingInfo?.BindingSource == BindingSource.Body)?.ParameterType;
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                foreach (var method in methods is { Count: > 0 } ? methods : new[] { "GET", "POST" })
                    routes[$"{method} {path}"] = new Route(method.ToUpperInvariant(), path, bodyType);
            }
            return routes.Values.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ToList();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Probes
        // ---------------------------------------------------------------------------------------------------------

        private sealed record Probe(string Shape, byte[]? Body);

        private static IEnumerable<Probe> ProbesFor(Route route)
        {
            if (route.Method != "POST" && route.Method != "PUT" && route.Method != "PATCH")
            {
                yield return new Probe("none", null);
                yield break;
            }
            // A route may refuse by size first, so each shape is sent at several sizes: the largest one a route reads
            // is the one that shows what reading costs.
            foreach (var bytes in new[] { 16 * Kb, 60 * Kb, 256 * Kb, 1000 * Kb, 4 * Mb, 7 * Mb })
            {
                yield return new Probe("wide", Utf8(Wide(bytes)));
                yield return new Probe("array", Utf8(Elements("{}", bytes)));
                if (route.BodyType != null) yield return new Probe("typed", Utf8(Typed(route.BodyType, bytes)));
            }
            yield return new Probe("deep", Utf8(new string('[', 2000) + new string(']', 2000)));
            yield return new Probe("long-text", Utf8("{\"a\":\"" + new string('A', 1 * Mb) + "\"}"));
        }

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

        /// <summary>An object holding one array of empty objects: three bytes of body for each object a parser builds.</summary>
        private static string Wide(int bytes) => "{\"a\":" + Elements("{}", bytes - 6) + "}";

        private static string Elements(string element, int bytes)
        {
            var count = Math.Max(1, bytes / (element.Length + 1));
            var text = new StringBuilder(bytes + 2).Append('[');
            for (var i = 0; i < count; i++) { if (i > 0) text.Append(','); text.Append(element); }
            return text.Append(']').ToString();
        }

        /// <summary>
        /// The body the route's own model asks for, filled to <paramref name="bytes"/>: every list holds as many empty
        /// items as fit, and every text field is as long as fits.
        /// </summary>
        private static string Typed(Type type, int bytes)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (ElementOf(type) is { } element) return Elements(Empty(element), bytes);
            if (type == typeof(string)) return "\"" + new string('A', Math.Max(1, bytes - 2)) + "\"";

            var fields = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && (p.PropertyType == typeof(string) || ElementOf(p.PropertyType) != null)).ToList();
            if (fields.Count == 0) return Wide(bytes);
            var share = Math.Max(16, bytes / fields.Count);
            var parts = fields.Select(p => p.PropertyType == typeof(string)
                ? $"\"{p.Name}\":\"{new string('A', share)}\""
                : $"\"{p.Name}\":{Elements(Empty(ElementOf(p.PropertyType)!), share)}");
            return "{" + string.Join(",", parts) + "}";
        }

        private static Type? ElementOf(Type type)
        {
            if (type == typeof(string)) return null;
            if (type.IsArray) return type.GetElementType();
            var sequence = new[] { type }.Concat(type.GetInterfaces())
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (sequence != null) return sequence.GetGenericArguments()[0];
            return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
        }

        private static string Empty(Type element)
        {
            element = Nullable.GetUnderlyingType(element) ?? element;
            if (element == typeof(string)) return "\"\"";
            if (element.IsPrimitive || element == typeof(decimal)) return "0";
            return "{}";
        }

        // ---------------------------------------------------------------------------------------------------------
        // One request, and what it cost the node
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Allocated is every byte the process allocated while the request was served, whether or not it was still
        /// held at the end: it measures the work done for the caller. HeapGrowth is the most the heap was seen to grow
        /// by during the request, sampled, and includes memory not yet collected.
        /// </summary>
        private sealed record Result(string Status, TimeSpan Elapsed, long Allocated, double TimesBody, long HeapGrowth, string Verdict, List<string> Flags, List<string> NotFixed);

        /// <summary>
        /// A probe that fails is sent twice more, and fails only for what it does every time: other work on the machine
        /// can make one request slow or count another's allocations, and a real failure repeats.
        /// </summary>
        private async Task<Result> SendFlaggedAgainAsync(HttpClient client, Route route, Probe probe)
        {
            var result = await SendAsync(client, route, probe);
            for (var again = 0; again < 2 && result.Flags.Count > 0; again++)
            {
                var next = await SendAsync(client, route, probe);
                var flags = result.Flags.Intersect(next.Flags).ToList();
                result = (flags.Count == next.Flags.Count ? next : result) with { Flags = flags, Verdict = flags.Count > 0 ? "FAIL " + string.Join("+", flags) : "ok on a second try" };
            }
            return result;
        }

        private async Task<Result> SendAsync(HttpClient client, Route route, Probe probe)
        {
            var caller = Interlocked.Increment(ref _caller);
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
            request.Headers.Add(UnknownCaller.Header, $"198.{18 + caller / 65536 % 2}.{caller % 256}.{1 + caller / 256 % 250}"); // RFC 2544 test addresses
            if (probe.Body != null)
            {
                request.Content = new ByteArrayContent(probe.Body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var heapPeak = heapBefore;
            using var sampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    heapPeak = Math.Max(heapPeak, GC.GetTotalMemory(forceFullCollection: false));
                    await Task.Delay(2);
                }
            });
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            string status;
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                status = ((int)response.StatusCode).ToString();
            }
            catch (TaskCanceledException) { status = "no answer"; }
            catch (HttpRequestException) { status = "cut off"; } // refused before the body was taken: the node closed the connection
            clock.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            sampling.Cancel();
            await sampler;

            var bodyBytes = Math.Max(probe.Body?.Length ?? 0, 64 * Kb); // a request with no body is held to the cost of a small one
            var timesBody = allocated / (double)bodyBytes;
            var flags = new List<string>();
            var notFixed = new List<string>();
            var notes = new List<string>();
            if (allocated >= FailBytes && timesBody >= FixedRatio) notFixed.Add("allocation");
            if (allocated >= FailBytes && timesBody >= FailRatio) flags.Add("allocation");
            else if (allocated >= FailBytes && timesBody >= FixedRatio) notes.Add("allocation, between the fixed and failing lines");
            else if (allocated >= NoteBytes && timesBody >= FailRatio) notes.Add("allocation, under the failing size");
            if (status == "no answer" || clock.Elapsed >= FixedSlow) notFixed.Add("slow");
            if (status == "no answer" || clock.Elapsed >= Slow) flags.Add("slow");
            else if (clock.Elapsed >= FixedSlow) notes.Add("slow, between the fixed and failing lines");
            if (status == "500") { flags.Add("unhandled-error"); notFixed.Add("unhandled-error"); }
            var verdict = flags.Count > 0 ? "FAIL " + string.Join("+", flags) : notes.Count > 0 ? "note: " + string.Join("; ", notes) : "ok";
            return new Result(status, clock.Elapsed, allocated, timesBody, heapPeak - heapBefore, verdict, flags, notFixed);
        }
    }
}
