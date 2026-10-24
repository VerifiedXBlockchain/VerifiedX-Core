using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VerifiedXCore.Bitcoin.Controllers;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A cancel request reaches CancelWithdrawal without BTCTxHash and FailureProof: the action reads neither, and a watched
    /// vault's cancel has neither. Declared as plain strings under nullable reference types, model validation required
    /// both and answered 400 ("One or more validation errors occurred.") before the action ran. The same holds for the
    /// raw route's payload. The host registers controllers as the wallet API host does, so model validation runs as it
    /// does in production.
    /// </summary>
    public class CancelWithdrawalOptionalFieldsTests
    {
        private static TestServer Host() => new(new WebHostBuilder()
            .ConfigureServices(s => s.AddControllers().AddApplicationPart(typeof(VBTCController).Assembly))
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(e => e.MapControllers());
            }));

        private static async Task<(HttpStatusCode Code, string Body)> Post(TestServer server, string route, string json)
        {
            var response = await server.CreateClient().PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task ACancelWithoutBtcTxHashOrFailureProof_ReachesTheAction()
        {
            using var server = Host();

            // The body the desktop wallet sends for a watched vault.
            var (code, body) = await Post(server, "/vbtcapi/vbtc/CancelWithdrawal",
                "{\"SmartContractUID\":\"6c546818bb154579892e08f644438fc8:1792851413\",\"OwnerAddress\":\"xD5hREu1jsJ4tmDmfYZPYhgQNJMGmMqjP6\"," +
                "\"WithdrawalRequestHash\":\"952018975ebcb0f9200b9f49f81c87af9911a8f27eacaa77282a4eeab79f6c3b\"}");

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.DoesNotContain("field is required", body);
            Assert.Contains("\"Success\":false", body); // the action ran: this node holds no key for the requester
        }

        [Fact]
        public async Task ARequiredFieldTheActionReads_IsStillChecked_ByTheAction()
        {
            using var server = Host();

            var (code, body) = await Post(server, "/vbtcapi/vbtc/CancelWithdrawal", "{\"SmartContractUID\":\"\",\"OwnerAddress\":\"\",\"WithdrawalRequestHash\":\"\"}");

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Contains("Required fields cannot be null", body);
        }

        [Fact]
        public async Task TheRawCancelWithoutBtcTxHashOrFailureProof_ReachesTheAction()
        {
            using var server = Host();

            var (code, body) = await Post(server, "/vbtcapi/vbtc/CancelWithdrawalRaw",
                "{\"SmartContractUID\":\"6c546818bb154579892e08f644438fc8:1792851413\",\"OwnerAddress\":\"xD5hREu1jsJ4tmDmfYZPYhgQNJMGmMqjP6\"," +
                "\"WithdrawalRequestHash\":\"952018975ebcb0f9200b9f49f81c87af9911a8f27eacaa77282a4eeab79f6c3b\",\"Timestamp\":0,\"UniqueId\":\"u\",\"OwnerSignature\":\"\"}");

            Assert.Equal(HttpStatusCode.OK, code);
            Assert.DoesNotContain("field is required", body);
            Assert.Contains("Owner signature cannot be null", body);
        }
    }
}
