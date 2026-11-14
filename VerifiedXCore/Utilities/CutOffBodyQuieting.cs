using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using VerifiedXCore.Services;

namespace VerifiedXCore
{
    /// <summary>
    /// A request whose body is malformed or ends before its Content-Length is the client's doing (Kestrel throws
    /// BadHttpRequestException). Placed right after UseExceptionHandler, this ends such a request with its status (400, or
    /// 413 for a body over the host's limit) and without the error-and-stack-trace log entry the exception handler writes,
    /// so a sender cannot flood the console. One line a minute says how many there were. Every other exception still
    /// reaches the exception handler and is logged.
    /// An address that sends <see cref="BanAfter"/> of them within a minute is banned (BanService.BanPeer, which never bans
    /// a caster or a known validator), and a banned address's request is dropped before its body is read. Requests from
    /// this machine are never counted.
    /// </summary>
    public static class CutOffBodyQuieting
    {
        public const int BanAfter = 10;

        /// <summary>The window bad requests are counted in; tests shorten it.</summary>
        internal static long WindowMilliseconds = 60_000;

        private static long _count;
        private static long _windowStart = Environment.TickCount64;
        private static readonly ConcurrentDictionary<string, int> _byAddress = new();

        public static IApplicationBuilder UseQuietCutOffBodies(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            // No address to judge from this machine, or before startup has loaded the ban list.
            var address = remote == null || Globals.BannedIPs == null || IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
                ? null
                : remote.ToString().Replace("::ffff:", "");
            if (address != null && Globals.BannedIPs!.ContainsKey(address))
            {
                context.Abort();
                return;
            }

            try
            {
                await next();
            }
            catch (BadHttpRequestException e) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = e.StatusCode;
                // A window that has run out is closed before this request is counted, so only bad requests within one
                // window add up to a ban.
                var start = Interlocked.Read(ref _windowStart);
                var now = Environment.TickCount64;
                if (now - start >= WindowMilliseconds && Interlocked.CompareExchange(ref _windowStart, now, start) == start)
                {
                    _byAddress.Clear();
                    ConsoleWriterService.Output($"{Interlocked.Exchange(ref _count, 0)} requests with bad or cut-off bodies in the last minute.");
                }
                Interlocked.Increment(ref _count);
                if (address != null && _byAddress.AddOrUpdate(address, 1, (_, n) => n + 1) == BanAfter)
                    BanService.BanPeer(address, $"{BanAfter} requests with bad or cut-off bodies within a minute.", "CutOffBodyQuieting");
            }
        });
    }
}
