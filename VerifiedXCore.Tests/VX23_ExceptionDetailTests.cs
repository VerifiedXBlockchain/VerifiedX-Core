using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-23 (LOW): "The validator API returns full exception detail to unauthenticated callers".
    ///
    /// Audit PoC: unauthenticated POST valapi/Validator/RequestBlock with Timestamp = long.MinValue → HTTP 400 with
    /// "System.OverflowException: Negating the minimum value of a twos complement number is invalid. at
    /// ValidatorController.RequestBlock … ValidatorController.cs:line 619". Controls: benign / stale / −2^62 timestamps
    /// → 401 "timestamp"; so only the overflow path leaked, before membership and signature checks.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class VX23_ExceptionDetailTests : IDisposable
    {
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private const string Caster = "xVX23_CASTER";

        public VX23_ExceptionDetailTests()
        {
            Globals.BlockCasters = new ConcurrentBag<Peers> { new Peers { ValidatorAddress = Caster, PeerIP = "10.0.0.23" } };
            Globals.BannedIPs ??= new ConcurrentDictionary<string, Peers>();
        }

        public void Dispose() => Globals.BlockCasters = _priorCasters;

        private static ValidatorController Controller()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("172.28.0.20");
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = ctx } };
        }

        private static RequestBlockRequest Req(long ts) => new RequestBlockRequest
        {
            BlockHeight = 1, CasterAddress = Caster, WinnerAddress = "xWINNER", Signature = "sig", Timestamp = ts,
        };

        private static string Body(IActionResult? r) => r switch
        {
            ObjectResult o => o.Value?.ToString() ?? "",
            _ => "",
        };

        [Fact]
        public async Task VX23_AuditPoC_OverflowingTimestamp_NoExceptionDetail()
        {
            // The overflow needs now - Timestamp == long.MinValue, i.e. Timestamp = now + long.MinValue (unchecked) for
            // the server's current second; retried in case the clock ticks between here and the route.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var ts = unchecked(TimeUtil.GetTime() + long.MinValue);
                var r = await Controller().RequestBlock(Req(ts));
                var body = Body(r.Result);

                Assert.DoesNotContain("OverflowException", body);
                Assert.DoesNotContain(" at VerifiedXCore", body);
                Assert.True(r.Result is UnauthorizedObjectResult or UnauthorizedResult, $"got {r.Result?.GetType().Name}: {body}");
            }
        }

        [Theory]
        [InlineData(-4611686018427387904L)] // −2^62: large negative, no overflow (the audit's decisive control)
        [InlineData(1L)]                    // stale
        public async Task VX23_AuditControls_BadTimestamps_Are401Timestamp(long ts)
        {
            var r = await Controller().RequestBlock(Req(ts));
            Assert.Equal("timestamp", Body(r.Result));
        }

        [Fact]
        public void VX23_ErrorText_CarriesNoStackTrace()
        {
            Exception ex;
            try { throw new InvalidOperationException("first line\nsecond line with C:\\src\\secret\\path.cs"); }
            catch (Exception e) { ex = e; }

            Assert.Equal("Request failed", ApiErrorText.Generic(ex));
            var forOperator = ApiErrorText.For(ex);
            Assert.Equal("InvalidOperationException: first line", forOperator);
            Assert.DoesNotContain(" at ", forOperator);
            Assert.DoesNotContain("secret", forOperator);
        }
    }
}
