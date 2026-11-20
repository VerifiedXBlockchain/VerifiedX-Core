using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A validator reads a share batch's entries only for the session's participants. A name the leader lists that is
    /// not one is neither read nor reported, so a batch cannot make the validator sign a report for each name in it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostDkgShareReportTargetsTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string? _priorValidator = Globals.ValidatorAddress;
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly long _now = TimeUtil.GetTime();
        private readonly Account _me;

        public FrostDkgShareReportTargetsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"dkgreports_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.FrostFFIAvailable = true;
            // A validator, as production runs one: its key is in its wallet.
            _me = AccountData.CreateNewAccount(skipSave: true);
            _me.IsValidating = true;
            AccountData.GetAccounts().Insert(_me);
            Globals.ValidatorAddress = _me.Address;
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.ValidatorAddress = _priorValidator;
            Globals.FrostFFIAvailable = _priorLibrary;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Fact]
        public async Task ABatchEntryForANameThatIsNotAParticipant_IsNeitherReadNorReported()
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var leader = new EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(leader.publicKey().toString()).ToLowerInvariant();
            var leaderAddress = AccountData.GetHumanAddress(pub);
            var session = new DKGSession
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = "sc", LeaderAddress = leaderAddress, StartTimestamp = _now, LastProgressTimestamp = _now,
                ParticipantAddresses = new List<string> { _me.Address, "xV2", "xV3" }, RequiredThreshold = 67, MyParticipantIndex = 1, Round2Secret = "secret",
            };
            var myId = FrostStartup.BuildAddressToIdentifierMap(session.ParticipantAddresses)[_me.Address];
            // A participant's share that will not open, and names that are not participants.
            var shares = new JObject { ["xV2"] = new JObject { [myId] = FrostShareCrypto.Prefix + "AAAA" }.ToString(Formatting.None) };
            for (var i = 0; i < 2_000; i++) shares["xNotAParticipant" + i] = "{}";
            Assert.True(FrostSessionStorage.DKGSessions.TryAdd(session.SessionId, session));
            try
            {
                var body = JsonConvert.SerializeObject(new
                {
                    LeaderAddress = leaderAddress, Timestamp = _now,
                    LeaderSignature = VerifiedXCore.Services.SignatureService.CreateSignature($"{session.SessionId}.{leaderAddress}.{_now}", leader, pub),
                    AllGeneratedShares = shares,
                });
                var response = await server.CreateClient().PostAsync($"/frost/dkg/shares/{session.SessionId}", new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var answer = JObject.Parse(await response.Content.ReadAsStringAsync());

                // The participant is reported and the report signed, as before; nobody else is.
                Assert.Equal(new[] { "xV2" }, ((JObject)answer["Accusations"]!).Properties().Select(p => p.Name).ToArray());
                var signatures = (JObject)answer["AccusationSignatures"]!;
                Assert.Equal(new[] { "xV2" }, signatures.Properties().Select(p => p.Name).ToArray());
                Assert.False(string.IsNullOrEmpty((string?)signatures["xV2"]));
                Assert.True(session.Reported);
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(session.SessionId, out _); }
        }
    }
}
