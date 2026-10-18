using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A node whose FROST library has not passed its startup check (still running, or failed) neither leads nor joins a
    /// key ceremony or a signing, and sends no vBTC validator heartbeat, so peers do not pick it and stall on it. Once the
    /// check passes, nothing here stands in the way.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostLibraryGateTests : IDisposable
    {
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly string _priorValidator = Globals.ValidatorAddress;

        public void Dispose()
        {
            Globals.FrostFFIAvailable = _priorLibrary;
            Globals.ValidatorAddress = _priorValidator;
        }

        private static async Task<(HttpStatusCode Code, string Body)> Post(string route)
        {
            using var server = new Microsoft.AspNetCore.TestHost.TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var response = await server.CreateClient().PostAsync(route, new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("/frost/dkg/start")]
        [InlineData("/frost/sign/start")]
        public async Task WithoutAWorkingLibrary_TheNodeJoinsNoCeremony(string route)
        {
            Globals.FrostFFIAvailable = false;
            var (code, body) = await Post(route);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
            Assert.Contains(FrostLibraryGate.Unavailable, body);

            // With the check passed, the request is judged on its own merits (here: refused for what it carries, not 503).
            Globals.FrostFFIAvailable = true;
            (code, body) = await Post(route);
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, code);
            Assert.DoesNotContain(FrostLibraryGate.Unavailable, body);
        }

        [Fact]
        public async Task WithoutAWorkingLibrary_TheNodeLeadsNoCeremony()
        {
            Globals.FrostFFIAvailable = false;
            var validators = new List<VBTCValidator> { new() { ValidatorAddress = "xA", IPAddress = "10.0.0.1" }, new() { ValidatorAddress = "xB", IPAddress = "10.0.0.2" } };

            var signing = await FrostMPCService.CoordinateSigningCeremony(new string('a', 64), "vault:1", validators, 51);
            Assert.False(signing.Success);
            Assert.Equal(FrostCeremonyFailureCode.LibraryUnavailable, signing.FailureCode);
            Assert.True(signing.IsRetryable);   // a local condition: nothing is held against the vault

            var dkg = await FrostMPCService.CoordinateDKGCeremony("ceremony-1", "xOwner", validators, 51);
            Assert.Null(dkg.Result);
            Assert.Equal(FrostLibraryGate.Unavailable, dkg.Error);
        }

        [Fact]
        public async Task WithoutAWorkingLibrary_AValidatorDoesNotRegister()
        {
            var priorPorts = Globals.PortsOpened;
            try
            {
                Globals.FrostFFIAvailable = false;
                FrostLibraryGate.CheckFinished();          // the startup check has run, and failed
                Globals.ValidatorAddress = "xValidatorWithoutFrost";
                Globals.PortsOpened = false;               // the registration would otherwise wait for its ports first
                var register = (Task)typeof(ValidatorService).GetMethod("SendVBTCV2RegistrationTx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
                Assert.Same(register, await Task.WhenAny(register, Task.Delay(TimeSpan.FromSeconds(20))));   // refused at once
            }
            finally { Globals.PortsOpened = priorPorts; }
        }

        [Fact]
        public async Task WithoutAWorkingLibrary_NoValidatorHeartbeatIsSent()
        {
            Globals.FrostFFIAvailable = false;
            Globals.ValidatorAddress = "xValidatorWithoutFrost";
            Assert.False(await VBTCValidatorHeartbeatService.SendHeartbeatTransaction());
        }
    }
}
