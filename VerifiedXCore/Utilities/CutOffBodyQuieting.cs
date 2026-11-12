using Microsoft.AspNetCore.Http;
using VerifiedXCore.Services;

namespace VerifiedXCore
{
    /// <summary>
    /// A request whose body is malformed or ends before its Content-Length is the client's doing (Kestrel throws
    /// BadHttpRequestException). Placed right after UseExceptionHandler, this ends such a request with 400 and without
    /// the error-and-stack-trace log entry the exception handler writes, so a sender cannot flood the console. One line a
    /// minute says how many there were. Every other exception still reaches the exception handler and is logged.
    /// </summary>
    public static class CutOffBodyQuieting
    {
        private static long _count;
        private static long _windowStart = Environment.TickCount64;

        public static IApplicationBuilder UseQuietCutOffBodies(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (BadHttpRequestException) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                Interlocked.Increment(ref _count);
                var start = Interlocked.Read(ref _windowStart);
                var now = Environment.TickCount64;
                if (now - start >= 60_000 && Interlocked.CompareExchange(ref _windowStart, now, start) == start)
                    ConsoleWriterService.Output($"{Interlocked.Exchange(ref _count, 0)} requests with bad or cut-off bodies in the last minute.");
            }
        });
    }
}
