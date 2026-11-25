using System.Net;
using System.Net.Sockets;
using System.Text;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A host holds a bounded amount of request body at once: one network holds at most its share, the host at most
    /// its total, and a body that does not fit is answered 503 before any of it is read. Small bodies are not counted.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BodiesInProgressTests
    {
        private const long Mb = 1024 * 1024;

        [Fact]
        public void One_network_holds_its_share_and_the_host_its_total()
        {
            var bodies = new BoundedJsonBodies.BodiesInProgress(perSource: 16 * Mb, total: 24 * Mb);
            using var a1 = bodies.Hold("203.0.113", 5 * Mb, 5 * Mb);
            using var a2 = bodies.Hold("203.0.113", 5 * Mb, 5 * Mb);
            using var a3 = bodies.Hold("203.0.113", 5 * Mb, 5 * Mb);
            Assert.True(a1.Granted && a2.Granted && a3.Granted);
            Assert.False(bodies.Hold("203.0.113", 5 * Mb, 5 * Mb).Granted); // its share is used

            using var b1 = bodies.Hold("198.51.100", 5 * Mb, 5 * Mb);
            Assert.True(b1.Granted);
            Assert.False(bodies.Hold("192.0.2", 5 * Mb, 5 * Mb).Granted); // the host's total is used

            a1.Dispose();
            using var again = bodies.Hold("192.0.2", 5 * Mb, 5 * Mb);
            Assert.True(again.Granted); // a body's place is given back when its request is answered
            Assert.Equal(20 * Mb, bodies.Held);
        }

        [Fact]
        public void Small_bodies_and_bodies_the_server_refuses_are_not_counted()
        {
            var bodies = new BoundedJsonBodies.BodiesInProgress(perSource: 1 * Mb, total: 1 * Mb);
            using var small = bodies.Hold("203.0.113", BoundedJsonBodies.BodiesInProgress.UncountedBytes, 5 * Mb);
            using var overLimit = bodies.Hold("203.0.113", 6 * Mb, 5 * Mb);
            Assert.True(small.Granted && overLimit.Granted);
            Assert.Equal(0, bodies.Held);
        }

        [Fact]
        public async Task The_validator_api_answers_a_body_with_no_room_503_while_slow_bodies_from_one_network_are_held()
        {
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = "xTestValidator";
            var capture = new CutOffRequestBodyTests.Capture();
            var (host, port) = await CutOffRequestBodyTests.StartValidatorApi(capture);
            var slow = new List<TcpClient>();
            try
            {
                // Three bodies of nearly 5 MB each, announced and only begun, from this machine: 15 MB of its 16 MB share.
                for (var i = 0; i < 3; i++) slow.Add(await BeginAsync(port, 5 * 1024 * 1024 - 1));
                await WaitUntil(() => BoundedJson.ValidatorApiBodies.Held >= 15 * Mb - 3);

                var reply = await CutOffRequestBodyTests.Post(port, 4 * 1024 * 1024, "{\"a\":", stopSending: false, route: "ProbeTip");
                Assert.StartsWith("HTTP/1.1 503", reply);
                Assert.Contains("Busy with other requests", reply);

                // A small body is not counted, and is still served.
                var small = await CutOffRequestBodyTests.Post(port, 2, "{}", stopSending: false, route: "ProbeTip");
                Assert.DoesNotContain("Busy with other requests", small);
            }
            finally
            {
                foreach (var client in slow) client.Dispose();
                await host.StopAsync();
                host.Dispose();
                Globals.ValidatorAddress = priorValidator;
            }
            await WaitUntil(() => BoundedJson.ValidatorApiBodies.Held == 0);
        }

        private static async Task<TcpClient> BeginAsync(int port, long contentLength)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var head = $"POST /valapi/Validator/ProbeTip HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" +
                       $"Content-Length: {contentLength}\r\n\r\n{{\"Height\":1";
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
            return client;
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
            Assert.True(condition());
        }
    }
}
