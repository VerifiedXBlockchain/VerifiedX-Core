using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A key ceremony or signing start takes as many participants as a vault's ceremony may have under consensus
    /// (FrostDkgAttestation.MaxParticipants). The cap used to be 200, below the participation consensus requires once
    /// about 223 validators are eligible, so no public vault could be created from then on.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostParticipantCapTests : IDisposable
    {
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;

        public FrostParticipantCapTests() => Globals.FrostFFIAvailable = true;

        public void Dispose() => Globals.FrostFFIAvailable = _priorLibrary;

        private static string[] Addresses(int n) => Enumerable.Range(0, n).Select(i => $"xParticipant{i:D5}").ToArray();

        private static async Task<string> Start(string route, object body)
        {
            using var server = new Microsoft.AspNetCore.TestHost.TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var response = await server.CreateClient().PostAsync(route, new System.Net.Http.StringContent(JsonConvert.SerializeObject(body), System.Text.Encoding.UTF8, "application/json"));
            return await response.Content.ReadAsStringAsync();
        }

        [Fact]
        public void TheStartCap_IsTheConsensusMaximum()
        {
            Assert.Equal(FrostDkgAttestation.MaxParticipants, FrostSessionStorage.MAX_PARTICIPANTS);
            Assert.True(FrostDkgAttestation.RequiredParticipants(248) <= FrostSessionStorage.MAX_PARTICIPANTS);   // 223 of 248 eligible
        }

        [Theory]
        [InlineData(223, true)]
        [InlineData(512, true)]
        [InlineData(513, false)]
        public async Task AKeyCeremonyStart_TakesUpToTheConsensusMaximum(int participants, bool withinCap)
        {
            var body = await Start("/frost/dkg/start", new { SessionId = Guid.NewGuid().ToString("N"), ParticipantAddresses = Addresses(participants) });
            Assert.Equal(!withinCap, body.Contains("ParticipantAddresses must be 1-512"));
        }

        [Theory]
        [InlineData(223, true)]
        [InlineData(512, true)]
        [InlineData(513, false)]
        public async Task ASigningStart_TakesUpToTheConsensusMaximum(int signers, bool withinCap)
        {
            var body = await Start("/frost/sign/start", new { SessionId = Guid.NewGuid().ToString("N"), SignerAddresses = Addresses(signers) });
            Assert.Equal(!withinCap, body.Contains("SignerAddresses must be 1-512"));
        }
    }
}
