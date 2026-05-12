using System.Collections.Concurrent;
using Newtonsoft.Json;
using ReserveBlockCore;
using ReserveBlockCore.Controllers;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// CONSENSUS-V2 (Fix #4): ExchangeValidatorList endpoint behavior.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class ValidatorListSyncMergeTests
    {
        [Fact]
        public async Task ExchangeValidatorList_UntrustedCaster_ReturnsZero()
        {
            var originalCasters = Globals.BlockCasters;
            var originalVals = Globals.NetworkValidators;

            try
            {
                Globals.BlockCasters = new ConcurrentBag<Peers>();
                Globals.NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>();

                var controller = new ValidatorController();
                var req = new ValidatorListExchangeRequest
                {
                    BlockHeight = 100,
                    CasterAddress = "xUNKNOWN",
                    Validators = new List<ValidatorListEntry>()
                };

                var action = await controller.ExchangeValidatorList(req);
                var badReq = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(action.Result);

                Assert.Equal("0", badReq.Value?.ToString());
            }
            finally
            {
                Globals.BlockCasters = originalCasters;
                Globals.NetworkValidators = originalVals;
            }
        }

        [Fact]
        public async Task ExchangeValidatorList_TrustedCaster_ReturnsFullEntriesFromLocalPool()
        {
            var originalCasters = Globals.BlockCasters;
            var originalVals = Globals.NetworkValidators;
            var originalAddr = Globals.ValidatorAddress;

            try
            {
                Globals.ValidatorAddress = "xSELF";
                Globals.BlockCasters = new ConcurrentBag<Peers>(new[]
                {
                    new Peers { ValidatorAddress = "xCASTER", PeerIP = "127.0.0.1", IsValidator = true, ValidatorPublicKey = "PK1" }
                });
                Globals.NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>(
                    new Dictionary<string, NetworkValidator>
                    {
                        ["xVAL1"] = new NetworkValidator
                        {
                            Address = "xVAL1",
                            IPAddress = "10.1.1.1",
                            PublicKey = "PUB1",
                            IsFullyTrusted = true,
                            FirstSeenAtHeight = 10,
                            LastSeen = TimeUtil.GetTime(),
                        },
                        ["xVAL2"] = new NetworkValidator
                        {
                            Address = "xVAL2",
                            IPAddress = "10.1.1.2",
                            PublicKey = "PUB2",
                            IsFullyTrusted = true,
                            FirstSeenAtHeight = 20,
                            LastSeen = TimeUtil.GetTime(),
                        }
                    });

                var controller = new ValidatorController();
                var req = new ValidatorListExchangeRequest
                {
                    BlockHeight = 123,
                    CasterAddress = "xCASTER",
                    Validators = new List<ValidatorListEntry>()
                };

                var action = await controller.ExchangeValidatorList(req);
                var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);
                var payload = ok.Value?.ToString() ?? "";
                var resp = JsonConvert.DeserializeObject<ValidatorListExchangeResponse>(payload);

                Assert.NotNull(resp);
                Assert.Equal(123, resp!.BlockHeight);
                Assert.Equal("xSELF", resp.CasterAddress);
                Assert.True(resp.Validators.Count >= 2);
                Assert.Contains(resp.Validators, v => v.Address == "xVAL1" && v.IPAddress == "10.1.1.1" && v.PublicKey == "PUB1");
                Assert.Contains(resp.Validators, v => v.Address == "xVAL2" && v.IPAddress == "10.1.1.2" && v.PublicKey == "PUB2");
            }
            finally
            {
                Globals.ValidatorAddress = originalAddr;
                Globals.BlockCasters = originalCasters;
                Globals.NetworkValidators = originalVals;
            }
        }
    }
}
