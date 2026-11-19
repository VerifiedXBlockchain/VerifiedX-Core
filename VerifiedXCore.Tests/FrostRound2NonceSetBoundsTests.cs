using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A leader's Round 2 nonce set is bounded by its session before any entry is parsed: no more entries than the
    /// session's signers, only signers' addresses, and no entry longer than a commitment. A refused set leaves the
    /// nonce unconsumed, and a set of the honest shape is judged as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostRound2NonceSetBoundsTests : IDisposable
    {
        private const string Peer = "xValidatorPeer";
        private const string MyCommitment = "{\"header\":{\"version\":0,\"ciphersuite\":\"FROST-secp256k1-SHA256-TR-v1\"},\"hiding\":\"aa\",\"binding\":\"bb\"}";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly string? _priorValidator = Globals.ValidatorAddress;
        private readonly string _me = "xValidatorMe" + Guid.NewGuid().ToString("N")[..6];

        public FrostRound2NonceSetBoundsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r2bounds_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            Globals.LastBlock = new Block { Height = 500 };
            Globals.FrostFFIAvailable = true;   // a validator whose FROST library passed its check
            Globals.ValidatorAddress = _me;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.FrostFFIAvailable = _priorLibrary;
            Globals.ValidatorAddress = _priorValidator;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>Posts <paramref name="body"/> as Round 2 of a session this validator holds, signed by its leader.</summary>
        private async Task<(System.Net.HttpStatusCode Code, string Body, SigningSession Session)> Round2(string body)
        {
            var key = new VerifiedXCore.EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var leader = AccountData.GetHumanAddress(pub);
            var session = new SigningSession
            {
                SessionId = Guid.NewGuid().ToString("N"), LeaderAddress = leader, LeaderStartTimestamp = TimeUtil.GetTime(), StartTimestamp = TimeUtil.GetTime(),
                SmartContractUID = "", MessageHash = new string('a', 64), MyKeyPackage = "key-package", NonceSecret = "secret-nonce",
                SignerAddresses = new List<string> { _me, Peer }, RequiredThreshold = 100,
            };
            session.Round1Nonces.TryAdd(_me, MyCommitment);
            Assert.True(FrostSessionStorage.SigningSessions.TryAdd(session.SessionId, session));
            try
            {
                using var server = new Microsoft.AspNetCore.TestHost.TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"/frost/sign/round2/{session.SessionId}")
                { Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("X-Frost-Leader", leader);
                req.Headers.TryAddWithoutValidation("X-Frost-Timestamp", session.LeaderStartTimestamp.ToString());
                req.Headers.TryAddWithoutValidation("X-Frost-Signature", VerifiedXCore.Services.SignatureService.CreateSignature($"{session.SessionId}.{leader}.{session.LeaderStartTimestamp}", key, pub));
                var response = await server.CreateClient().SendAsync(req);
                var text = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Leader auth failed", text);
                return (response.StatusCode, text, session);
            }
            finally { FrostSessionStorage.SigningSessions.TryRemove(session.SessionId, out _); }
        }

        [Fact]
        public async Task AWideEntryForThisValidator_IsRefusedUnparsed_AndTheNonceStaysUnconsumed()
        {
            var wide = "[" + string.Join(",", Enumerable.Repeat("{}", 200_000)) + "]";
            var (code, body, session) = await Round2(JsonConvert.SerializeObject(new Dictionary<string, string> { [_me] = wide, [Peer] = MyCommitment }));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, code);
            Assert.Contains("larger than this session's signers can send", body);
            Assert.False(session.NonceConsumed);

            // Under the body cap, one entry over a commitment's length is refused the same way.
            (code, body, session) = await Round2(JsonConvert.SerializeObject(new Dictionary<string, string> { [_me] = new string(' ', 1_100) + MyCommitment }));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, code);
            Assert.Contains("longer than", body);
            Assert.False(session.NonceConsumed);
        }

        [Fact]
        public async Task MoreEntriesThanSigners_OrAnAddressThatIsNotOne_IsRefused()
        {
            var (code, body, _) = await Round2(JsonConvert.SerializeObject(new Dictionary<string, string> { [_me] = MyCommitment, [Peer] = MyCommitment, ["xStranger"] = MyCommitment }));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, code);
            Assert.Contains("3 entries for 2 signers", body);

            (code, body, _) = await Round2(JsonConvert.SerializeObject(new Dictionary<string, string> { [_me] = MyCommitment, ["xStranger"] = MyCommitment }));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, code);
            Assert.Contains("not a signer", body);
        }

        [Fact]
        public async Task ASetOfTheHonestShape_IsJudgedAsBefore()
        {
            // Its own commitment substituted: refused by the existing check, not by the bound.
            var (code, body, session) = await Round2(JsonConvert.SerializeObject(new Dictionary<string, string> { [_me] = MyCommitment.Replace("aa", "ee"), [Peer] = MyCommitment }));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, code);
            Assert.Contains("own commitment unchanged", body);
            Assert.False(session.NonceConsumed);
        }
    }
}
