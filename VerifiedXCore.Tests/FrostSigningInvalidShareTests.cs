using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A holder that answers Round 1 honestly and then gives a well-formed Round 2 share that is not its own made every
    /// signing ceremony fail at aggregation with nobody named, so the same holder was asked again on every attempt. The
    /// aggregation now checks every share against its holder's verifying share and names the holders whose share fails:
    /// the ceremony fails as Round2InvalidShares with them named, a failure that is retried (never charged to the
    /// contract) without them. Driven through FrostMPCService.CoordinateSigningCeremony with a real key and the real
    /// library, against holders answering over HTTP.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostSigningInvalidShareTests
    {
        private const string MessageHash = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
        private const string ScUid = "vault:invalid-share";

        /// <summary>Three of the DKG's holders at 10.0.0.1-3, signing for real. <paramref name="cheater"/> answers Round 2 with another holder's share.</summary>
        private sealed class Holders : HttpMessageHandler
        {
            public readonly Dictionary<string, string> AddressOf = new() { ["10.0.0.1"] = "xAlpha", ["10.0.0.2"] = "xBravo", ["10.0.0.3"] = "xCharlie" };
            private readonly Dictionary<string, string> _keyPackages;
            private readonly string? _cheater;
            private readonly Dictionary<string, string> _commitments = new();
            private readonly Dictionary<string, string> _nonceSecrets = new();
            private Dictionary<string, string>? _shares;

            public Holders(Dictionary<string, string> keyPackages, string? cheater)
            {
                _keyPackages = keyPackages;
                _cheater = cheater;
                foreach (var address in AddressOf.Values)
                {
                    var (commitment, secret, e) = FrostNative.SignRound1Nonces(keyPackages[address]);
                    Assert.Equal(FrostNative.SUCCESS, e);
                    (_commitments[address], _nonceSecrets[address]) = (commitment, secret);
                }
            }

            /// <summary>Every holder's share over the commitment set Round 2 sent, each with its DKG identifier.</summary>
            private Dictionary<string, string> Shares(Dictionary<string, string> sent)
            {
                lock (_commitments)
                {
                    if (_shares != null) return _shares;
                    var ids = FrostMPCService.BuildAddressToFrostIdentifierMap(FrostAggregateParticipantOrderTests.DkgParticipants);
                    var view = new JObject();
                    foreach (var (address, commitment) in sent) view[ids[address]] = JToken.Parse(commitment);
                    _shares = new Dictionary<string, string>();
                    foreach (var address in AddressOf.Values)
                    {
                        var (share, e) = FrostNative.SignRound2Signature(_keyPackages[address], _nonceSecrets[address], view.ToString(Formatting.None), MessageHash);
                        Assert.Equal(FrostNative.SUCCESS, e);
                        _shares[address] = share;
                    }
                    return _shares;
                }
            }

            private string ShareOf(string me, Dictionary<string, string> shares) =>
                me == _cheater ? shares[AddressOf.Values.First(a => a != me)] : shares[me];

            private Dictionary<string, string>? _lastSent;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var me = AddressOf[request.RequestUri!.Host];
                var path = request.RequestUri.AbsolutePath;
                var session = path.Split('/').Last();
                object body = new { Success = true };
                if (path.StartsWith("/frost/sign/round1/"))
                    body = new { Success = true, SessionId = session, Nonces = new Dictionary<string, string> { [me] = _commitments[me] } };
                else if (path.StartsWith("/frost/sign/round2/"))
                {
                    var sent = JsonConvert.DeserializeObject<Dictionary<string, string>>(await request.Content!.ReadAsStringAsync(ct))!;
                    _lastSent = sent;
                    body = new { Success = true, ShareGenerated = true, SignatureShare = ShareOf(me, Shares(sent)), SessionId = session };
                }
                else if (path.StartsWith("/frost/sign/share/") && _lastSent != null)
                    body = new { Success = true, SessionId = session, Shares = new Dictionary<string, string> { [me] = ShareOf(me, Shares(_lastSent)) } };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") };
            }
        }

        private static async Task<FrostCeremonyOutcome> Ceremony(string? cheater)
        {
            var (keyPackages, pubkeyPackage) = FrostAggregateParticipantOrderTests.RealDkg();
            var handler = new Holders(keyPackages, cheater);
            var validators = handler.AddressOf.Select(kv => new VBTCValidator { ValidatorAddress = kv.Value, IPAddress = kv.Key, IsActive = true }).ToList();
            var priorHttp = FrostMPCService.SigningHttpForTests;
            var priorFfi = Globals.FrostFFIAvailable;
            var priorPath = Globals.CustomPath;
            var priorValidator = Globals.ValidatorAddress;
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"invalidshare_{Guid.NewGuid():N}") + System.IO.Path.DirectorySeparatorChar;
            System.IO.Directory.CreateDirectory(root);
            Globals.CustomPath = root;
            VerifiedXCore.Data.DbContext.Initialize();
            FrostMPCService.SigningHttpForTests = new HttpClient(handler);
            Globals.FrostFFIAvailable = true;
            Globals.ValidatorAddress = "xAlpha";                                    // the coordinator, a holder of the key
            try
            {
                Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore
                {
                    SmartContractUID = ScUid, ValidatorAddress = "xAlpha", KeyPackage = keyPackages["xAlpha"], PubkeyPackage = pubkeyPackage,
                    ParticipantOrderJson = JsonConvert.SerializeObject(FrostAggregateParticipantOrderTests.DkgParticipants),
                    CreatedTimestamp = VerifiedXCore.Utilities.TimeUtil.GetTime(),
                }));
                return await FrostMPCService.CoordinateSigningCeremony(MessageHash, ScUid, validators, 51, coordinatorAddress: "xLeader");
            }
            finally
            {
                FrostMPCService.SigningHttpForTests = priorHttp;
                Globals.FrostFFIAvailable = priorFfi;
                Globals.ValidatorAddress = priorValidator;
                try { VerifiedXCore.Data.DbContext.CloseDB(); } catch { }
                Globals.CustomPath = priorPath;
                try { System.IO.Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task HonestHolders_Sign()
        {
            var outcome = await Ceremony(cheater: null);
            Assert.True(outcome.Success, outcome.Describe());
            Assert.Equal(128, outcome.Result!.SchnorrSignature.Length);
            Assert.Empty(outcome.InvalidShareHolders);
        }

        [Fact]
        public async Task AHolderWhoseShareDoesNotVerify_IsNamed_AndTheFailureIsRetryable()
        {
            var outcome = await Ceremony(cheater: "xBravo");
            Assert.True(outcome.FailureCode == FrostCeremonyFailureCode.Round2InvalidShares, outcome.Describe());
            Assert.Equal(new[] { "xBravo" }, outcome.InvalidShareHolders);
            Assert.True(outcome.IsRetryable);                                     // the contract is not blacklisted for it
        }
    }
}
