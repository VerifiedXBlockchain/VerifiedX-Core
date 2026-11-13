using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Unauthenticated bridge requests cannot make a node hold their fields in memory or spend seconds on them: a log line
    /// quoting a caller's field is cut to LogUtility.MaxMessageChars and the log queue is bounded, and a burn alert's
    /// signature is refused at once however long its key part.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeRequestBoundsTests
    {
        private static ConcurrentQueue<(string Message, string Location, string FileName, DateTime Time)> Queue() =>
            (ConcurrentQueue<(string Message, string Location, string FileName, DateTime Time)>)typeof(LogUtility)
                .GetField("FileQueue", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static IEnumerable<string> QueuedMessages() => Queue().Select(e => e.Message ?? "");

        [Fact]
        public async Task MintAttestationRequestsWithHugeFields_LeaveOnlyShortLogLines()
        {
            var priorValidator = Globals.ValidatorAddress;
            Globals.ValidatorAddress = "xTestValidator";
            var (host, port) = await CutOffRequestBodyTests.StartValidatorApi(new CutOffRequestBodyTests.Capture());
            try
            {
                var body = JsonConvert.SerializeObject(new { LockId = new string('A', 4 * 1024 * 1024), EvmDestination = "0x1", AmountSats = 1, Nonce = 1, ChainId = 8453, ContractAddress = "0x1" });
                for (var i = 0; i < 20; i++)
                    await CutOffRequestBodyTests.Post(port, body.Length, body, stopSending: false, route: "SignMintAttestation");
                await host.StopAsync();

                var messages = QueuedMessages().ToList();
                Assert.Contains(messages, m => m.Contains("AAAA")); // the requests were logged
                Assert.All(messages, m => Assert.True(m.Length <= LogUtility.MaxMessageChars + 64, $"a queued line has {m.Length} characters"));
            }
            finally
            {
                host.Dispose();
                Globals.ValidatorAddress = priorValidator;
            }
        }

        [Fact]
        public void TheLogQueue_StopsGrowingAtItsBound()
        {
            try
            {
                for (var i = 0; i < LogUtility.MaxQueued + 10; i++)
                    LogUtility.LogQueue("line", "BridgeRequestBoundsTests", "test.txt", true);
                Assert.True(Queue().Count <= LogUtility.MaxQueued);
            }
            finally
            {
                Queue().Clear(); // nothing drains it in tests
            }
        }

        [Fact]
        public void ABurnAlertSignedWithAHugeKeyPart_IsRefusedAtOnce()
        {
            const string Caster = "xCommitteeCaster";
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var watch = Stopwatch.StartNew();
            var (ok, reason) = BridgeCasterConsensus.VerifyBurnAlert("0xburn", "exit", 1000, "bc1qdest", Caster, now,
                "MEUCIQ." + new string('z', 4 * 1024 * 1024), new HashSet<string> { Caster }, now);
            watch.Stop();
            Assert.False(ok);
            Assert.Equal("invalid alert signature", reason);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        }
    }
}
