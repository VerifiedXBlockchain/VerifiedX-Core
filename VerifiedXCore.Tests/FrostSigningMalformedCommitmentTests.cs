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
    /// A signing coordinator takes a holder's Round 1 commitment only in the form the FROST library writes one: its
    /// header, and two points on the curve. A holder that sends anything else has made no commitment: Round 2 goes to
    /// the others without it, so they are not made to refuse the set and named for withholding.
    /// Driven through FrostMPCService.CoordinateSigningCeremony against holders answering over HTTP.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostSigningMalformedCommitmentTests
    {
        /// <summary>A commitment in the library's form, with points of a fresh key.</summary>
        internal static string Commitment() =>
            JsonConvert.SerializeObject(new
            {
                header = new { version = 0, ciphersuite = "FROST-secp256k1-SHA256-TR-v1" },
                hiding = new NBitcoin.Key().PubKey.ToHex(),
                binding = new NBitcoin.Key().PubKey.ToHex(),
            });

        /// <summary>
        /// Three holders at 10.0.0.1-3. xB sends a commitment that is valid JSON but not a point; it gives a share
        /// anyway. The others, as the library does, give none over a set holding a commitment they cannot read.
        /// </summary>
        private sealed class Holders : HttpMessageHandler
        {
            public readonly Dictionary<string, string> AddressOf = new() { ["10.0.0.1"] = "xA", ["10.0.0.2"] = "xB", ["10.0.0.3"] = "xC" };
            public readonly Dictionary<string, string> Sent = new();
            public readonly List<string> Round2Bodies = new();
            public static readonly string NotAPoint = "{\"header\":{\"version\":0,\"ciphersuite\":\"FROST-secp256k1-SHA256-TR-v1\"},\"hiding\":\"02" + new string('f', 64) + "\",\"binding\":\"02" + new string('f', 64) + "\"}";

            public Holders()
            {
                Sent["xA"] = Commitment();
                Sent["xB"] = NotAPoint;
                Sent["xC"] = Commitment();
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var me = AddressOf[request.RequestUri!.Host];
                var path = request.RequestUri.AbsolutePath;
                var session = path.Split('/').Last();
                object body = new { Success = true };
                if (path.StartsWith("/frost/sign/round1/"))
                    body = new { Success = true, SessionId = session, Nonces = new Dictionary<string, string> { [me] = Sent[me] } };
                else if (path.StartsWith("/frost/sign/round2/"))
                {
                    var set = await request.Content!.ReadAsStringAsync(ct);
                    lock (Round2Bodies) Round2Bodies.Add(set);
                    var readable = JsonConvert.DeserializeObject<Dictionary<string, string>>(set)!.Values.All(v => v != NotAPoint);
                    body = me == "xB" || readable
                        ? new { Success = true, ShareGenerated = true, SignatureShare = "share-" + me, SessionId = session }
                        : (object)new { Success = false };
                }
                else if (path.StartsWith("/frost/sign/share/"))
                    body = new { Success = true, SessionId = session, Shares = new Dictionary<string, string> { [me] = "share-" + me } };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") };
            }
        }

        [Fact]
        public async Task ACommitmentThatIsNotAPoint_IsNotSentOn_AndTheHoldersWhoCouldNotUseItAreNotNamed()
        {
            var handler = new Holders();
            var validators = handler.AddressOf.Select(kv => new VBTCValidator { ValidatorAddress = kv.Value, IPAddress = kv.Key, IsActive = true }).ToList();
            var priorHttp = FrostMPCService.SigningHttpForTests;
            var priorFfi = Globals.FrostFFIAvailable;
            var priorPath = Globals.CustomPath;
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"badcommit_{Guid.NewGuid():N}") + System.IO.Path.DirectorySeparatorChar;
            System.IO.Directory.CreateDirectory(root);
            Globals.CustomPath = root;
            VerifiedXCore.Data.DbContext.Initialize();                                    // the leader signs its start with its account
            FrostMPCService.SigningHttpForTests = new HttpClient(handler);
            Globals.FrostFFIAvailable = true;
            try
            {
                var outcome = await FrostMPCService.CoordinateSigningCeremony(new string('a', 64), "vault:1", validators, 51);

                // Round 2 carried the two commitments in the library's form, and not xB's.
                var sent = JsonConvert.DeserializeObject<Dictionary<string, string>>(Assert.Single(handler.Round2Bodies.Distinct()))!;
                Assert.Equal(new[] { "xA", "xC" }, sent.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
                Assert.Equal(handler.Sent["xA"], sent["xA"]);
                // The holders that would have had to refuse it are not named as withholding their share.
                Assert.False(outcome.FailureCode == FrostCeremonyFailureCode.Round2SharesWithheld, outcome.Describe());
                Assert.DoesNotContain("xA", outcome.Withholders ?? new List<string>());
                Assert.DoesNotContain("xC", outcome.Withholders ?? new List<string>());
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

        [Fact]
        public void ACommitmentTheLibraryMakes_IsTaken()
        {
            var prior = Globals.FrostFFIAvailable;
            Globals.FrostFFIAvailable = true;
            try
            {
                var (keyPackages, _) = FrostAggregateParticipantOrderTests.RealDkg();
                foreach (var keyPackage in keyPackages.Values)
                {
                    var (commitment, _, error) = VerifiedXCore.Bitcoin.FROST.FrostNative.SignRound1Nonces(keyPackage);
                    Assert.Equal(VerifiedXCore.Bitcoin.FROST.FrostNative.SUCCESS, error);
                    Assert.True(FrostMPCService.IsSigningCommitment(commitment), commitment);
                }
            }
            finally { Globals.FrostFFIAvailable = prior; }
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("nonce-xA", false)]
        [InlineData("{}", false)]
        public void OnlyTheLibrarysFormIsACommitment(string text, bool expected)
        {
            Assert.Equal(expected, FrostMPCService.IsSigningCommitment(text));
            Assert.True(FrostMPCService.IsSigningCommitment(Commitment()));
            Assert.False(FrostMPCService.IsSigningCommitment(Holders.NotAPoint));
            Assert.False(FrostMPCService.IsSigningCommitment(Commitment().Replace("TR-v1", "TR-v2")));
            Assert.False(FrostMPCService.IsSigningCommitment(Commitment().Replace("}", ",\"extra\":1}")));
        }
    }
}
