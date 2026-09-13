using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using VerifiedXCore.Bitcoin.FROST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: the unsuffixed POST /frost/sign/round1 and /frost/sign/round2 routes
    /// accepted nonce commitments and signature shares for any signer address with no signature
    /// check, and first-writer-wins storage let garbage block the real entry (ceremony poisoning).
    /// Nothing called them. They are removed; the authenticated suffixed routes remain.
    /// </summary>
    public class FrostRouteSurfaceTests
    {
        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());

        private static StringContent Json(string body) => new StringContent(body, Encoding.UTF8, "application/json");

        [Fact]
        public async Task Server_IsUp()
        {
            using var server = NewServer();
            var client = server.CreateClient();
            var r = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        [Fact]
        public async Task UnsuffixedRound1_NoLongerExists()
        {
            using var server = NewServer();
            var client = server.CreateClient();
            var r = await client.PostAsync("/frost/sign/round1", Json("{\"SessionId\":\"s\",\"ValidatorAddress\":\"x\",\"NonceCommitment\":\"n\"}"));
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        [Fact]
        public async Task UnsuffixedRound2_NoLongerExists()
        {
            using var server = NewServer();
            var client = server.CreateClient();
            var r = await client.PostAsync("/frost/sign/round2", Json("{\"SessionId\":\"s\",\"ValidatorAddress\":\"x\",\"SignatureShare\":\"z\"}"));
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        [Fact]
        public async Task SuffixedRound2_StillRouted_AndRejectsUnknownSession()
        {
            using var server = NewServer();
            var client = server.CreateClient();
            var r = await client.PostAsync("/frost/sign/round2/no-such-session", Json("{}"));
            // Routed (not a routing 404 with empty body): the handler answers with its own JSON.
            var body = await r.Content.ReadAsStringAsync();
            Assert.Contains("Signing session not found", body);
        }

        [Fact]
        public async Task SuffixedRound1Get_StillRouted()
        {
            using var server = NewServer();
            var client = server.CreateClient();
            var r = await client.GetAsync("/frost/sign/round1/no-such-session");
            var body = await r.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body)); // handler produced a response body
        }
    }
}
