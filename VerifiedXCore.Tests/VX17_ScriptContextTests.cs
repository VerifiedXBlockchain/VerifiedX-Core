using System;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-17 (follow-up; found by the independent review): the same-origin pages that can reach /wallet/api.
    ///  - The browser wallet put names inside inline handlers as '...'+esc(name)+'...'. The HTML parser decodes
    ///    &amp;#39; back to ' before the handler runs, so an NFT/token name containing a quote ran script on click.
    ///  - The explorer rendered privacy-payload fields (sub_type, kind, asset, outputs, notes) and truncated addresses
    ///    into innerHTML without encoding.
    /// No JS engine runs in the test suite, so these tests pin the served page code.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX17_ScriptContextTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;

        public VX17_ScriptContextTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx17b_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Startup.APIEnabled = true;
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static async Task<string> Page(string path)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<Startup>());
            return await server.CreateClient().GetStringAsync(path);
        }

        // '...\''+esc(x)+'\'...' inside an inline handler: HTML-escaping in a JavaScript string context.
        private static readonly Regex EscInsideHandlerString = new(@"\\''\+esc\(");

        [Theory]
        [InlineData("/wallet")]
        [InlineData("/explorer")]
        public async Task VX17_InlineHandlers_PassValuesAsJsonStringLiterals(string path)
        {
            var html = await Page(path);
            Assert.Contains("function jsa(", html);
            Assert.False(EscInsideHandlerString.IsMatch(html), "an inline handler still embeds esc() output in a quoted JS string");
        }

        [Fact]
        public async Task VX17_Explorer_PrivacyPayloadFieldsAreEncoded()
        {
            var html = await Page("/explorer");
            Assert.Contains("function pi(lbl,val){return piRaw(lbl,esc(val));}", html);
            Assert.Contains("esc(o.i)", html);
            Assert.Contains("var fs=esc(shn(tx.fromAddress,20))", html);
        }

        [Theory]
        [InlineData("/wallet")]
        [InlineData("/explorer")]
        public async Task VX17_FollowUp_EscRendersZero(string path)
        {
            // Second review: esc() returned '' for every falsy value, so after pi() started encoding, counts and indexes
            // of 0 rendered blank. Only null/undefined are empty now.
            var html = await Page(path);
            Assert.Contains("function esc(s){return s!=null?String(s)", html);
        }

        [Fact]
        public async Task VX17_FollowUp_WalletBridgeAndPeerCellsAreEncoded()
        {
            // Second review: the bridge history (lock id, EVM destination, Base tx hash) and the history peer cell were
            // inserted as raw HTML.
            var html = await Page("/wallet");
            Assert.Contains("'+esc(lid)+'</code>", html);
            Assert.Contains("esc(shn(lk.EvmDestination||lk.evmDestination||'',14))", html);
            Assert.Contains("esc(shn(lk.BaseTxHash||lk.baseTxHash,12))", html);
            Assert.Contains("esc(shn(peer||'--',18))", html);
        }
    }
}
