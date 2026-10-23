using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The multi-contract withdrawal route allocates across every contract its requestor holds, so a body without a
    /// SmartContractUID is a complete request. ASP.NET's model validation must let it through to the route's own checks;
    /// the single-contract routes, which do need a contract, still refuse its absence themselves.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VbtcWithdrawalPayloadBindingTests : IDisposable
    {
        private const string Requestor = "xW67WwQLQyBqEqP6gc3sQ5GBBLfN1EWMTg";
        private const string BtcAddress = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly TestServer _server;
        private readonly HttpClient _client;

        public VbtcWithdrawalPayloadBindingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtc_payload_binding_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();

            _server = new TestServer(new WebHostBuilder().UseStartup<ControllersOnly>());
            _client = _server.CreateClient();
        }

        public void Dispose()
        {
            _client.Dispose();
            _server.Dispose();
            DbContext.CloseDB();
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>The wallet host's MVC setup (Startup.AddControllers), with the controllers of the node assembly.</summary>
        private sealed class ControllersOnly
        {
            public void ConfigureServices(IServiceCollection services) =>
                services.AddControllers().AddApplicationPart(typeof(VBTCController).Assembly);

            public void Configure(IApplicationBuilder app)
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }
        }

        private async Task<(HttpStatusCode Status, string Body)> Post(string action, string json)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync($"/vbtcapi/VBTC/{action}", content);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static string Body(string? contractField) =>
            "{" + (contractField == null ? "" : contractField + ",") +
            $"\"RequestorAddress\":\"{Requestor}\",\"BTCAddress\":\"{BtcAddress}\",\"Amount\":0.001,\"FeeRate\":3}}";

        [Theory]
        [InlineData(null)]                              // the field left out, as the route's doc says it may be
        [InlineData("\"SmartContractUID\":null")]
        [InlineData("\"SmartContractUID\":\"\"")]
        public async Task TheMultiRequest_ReachesItsOwnChecks_WithoutASmartContractUID(string? contractField)
        {
            var (status, body) = await Post("RequestWithdrawalMulti", Body(contractField));

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain("SmartContractUID", body);
            var answer = JObject.Parse(body);
            Assert.False((bool)answer["Success"]!);           // this requestor holds no vBTC: refused by the route itself
            Assert.False(string.IsNullOrEmpty((string?)answer["Message"]));
        }

        [Theory]
        [InlineData("RequestWithdrawal")]
        [InlineData("GetRawRequestWithdrawalTxData")]
        public async Task TheSingleContractRoutes_StillRefuseARequestWithoutItsContract(string action)
        {
            var (status, body) = await Post(action, Body(null));

            Assert.Equal(HttpStatusCode.OK, status);
            var answer = JObject.Parse(body);
            Assert.False((bool)answer["Success"]!);
            Assert.Equal("Required fields cannot be null", (string)answer["Message"]!);
        }
    }
}
