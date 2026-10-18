using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// C-197: a signing ceremony aggregated every Round 1 commitment against only the shares that came back, so one holder
    /// that committed and withheld its share made every attempt fail (the shares are made over the whole set). It also
    /// took any commitment a validator sent under any holder's name, and fetched the commitments again to aggregate. The
    /// coordinator now takes each validator's own entries only, aggregates over the set Round 2 sent, and names the
    /// holders that withheld their share - a failure that is retried, never charged to the contract.
    /// Driven through FrostMPCService.CoordinateSigningCeremony against validators answering over HTTP.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostSigningWithheldShareTests
    {
        /// <summary>Three holders at 10.0.0.1-3. The second commits, also sends a commitment under the first's name, and withholds its share.</summary>
        private sealed class Holders : HttpMessageHandler
        {
            public readonly Dictionary<string, string> AddressOf = new() { ["10.0.0.1"] = "xA", ["10.0.0.2"] = "xW", ["10.0.0.3"] = "xC" };
            public readonly List<string> Round2Bodies = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var me = AddressOf[request.RequestUri!.Host];
                var path = request.RequestUri.AbsolutePath;
                var session = path.Split('/').Last();
                object body = new { Success = true };
                if (path.StartsWith("/frost/sign/round1/"))
                {
                    var nonces = new Dictionary<string, string> { [me] = "nonce-" + me };
                    if (me == "xW") nonces["xA"] = "forged-by-xW";
                    body = new { Success = true, SessionId = session, Nonces = nonces };
                }
                else if (path.StartsWith("/frost/sign/round2/"))
                {
                    var sent = await request.Content!.ReadAsStringAsync(ct);
                    lock (Round2Bodies) Round2Bodies.Add(sent);
                    body = me == "xW" ? new { Success = false } : new { Success = true, ShareGenerated = true, SignatureShare = "share-" + me, SessionId = session };
                }
                else if (path.StartsWith("/frost/sign/share/"))
                    body = new { Success = true, SessionId = session, Shares = me == "xW" ? new Dictionary<string, string>() : new Dictionary<string, string> { [me] = "share-" + me } };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") };
            }
        }

        [Fact]
        public async Task AHolderThatCommitsAndWithholdsItsShare_IsNamed_AndTheFailureIsRetryable()
        {
            var handler = new Holders();
            var validators = handler.AddressOf.Select(kv => new VBTCValidator { ValidatorAddress = kv.Value, IPAddress = kv.Key, IsActive = true }).ToList();
            var priorHttp = FrostMPCService.SigningHttpForTests;
            var priorFfi = Globals.FrostFFIAvailable;
            var priorPath = Globals.CustomPath;
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"withheld_{Guid.NewGuid():N}") + System.IO.Path.DirectorySeparatorChar;
            System.IO.Directory.CreateDirectory(root);
            Globals.CustomPath = root;
            VerifiedXCore.Data.DbContext.Initialize();                                    // the leader signs its start with its account
            FrostMPCService.SigningHttpForTests = new HttpClient(handler);
            Globals.FrostFFIAvailable = true;
            try
            {
                var outcome = await FrostMPCService.CoordinateSigningCeremony(new string('a', 64), "vault:1", validators, 51);

                Assert.True(outcome.FailureCode == FrostCeremonyFailureCode.Round2SharesWithheld, outcome.Describe());
                Assert.Equal(new[] { "xW" }, outcome.Withholders);
                Assert.True(outcome.IsRetryable);                                     // the contract is not blacklisted for it

                // Round 2 carried each holder's own commitment: the one sent under xA's name by xW was not taken.
                var sent = JsonConvert.DeserializeObject<Dictionary<string, string>>(handler.Round2Bodies.First())!;
                Assert.Equal("nonce-xA", sent["xA"]);
                Assert.Equal("nonce-xW", sent["xW"]);
            }
            finally
            {
                FrostMPCService.SigningHttpForTests = priorHttp;
                Globals.FrostFFIAvailable = priorFfi;
                try { VerifiedXCore.Data.DbContext.CloseDB(); } catch { }
                Globals.CustomPath = priorPath;
                try { System.IO.Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
