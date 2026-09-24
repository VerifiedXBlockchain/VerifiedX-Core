using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-24 (LOW): "A missing return statement produces a deterministic unhandled null reference".
    ///
    /// Audit PoC: GET btcapi/BTCV2/GetvBTCBalance/{address}/{unknown scUID} → the "SC State Missing" response was built
    /// but not returned; scState.OwnerAddress then threw NullReferenceException and the catch returned $"Error: {ex}"
    /// (full exception text). Control: the sibling GetAllvBTCBalances has the return.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX24_MissingReturnTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VX24_MissingReturnTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx24_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task VX24_AuditPoC_UnknownContract_ReturnsTheMissingStateMessage()
        {
            var body = await new BTCV2Controller().GetvBTCBalance("xANY", "unknown-sc-uid");

            Assert.Contains("SC State Missing: unknown-sc-uid", body);
            Assert.DoesNotContain("NullReferenceException", body);
            Assert.DoesNotContain(" at VerifiedXCore", body);
        }
    }
}
