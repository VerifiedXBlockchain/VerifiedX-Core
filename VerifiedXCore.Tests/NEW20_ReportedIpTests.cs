using VerifiedXCore;
using VerifiedXCore.P2P;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-20 (found by the sixth independent review): a single peer reporting ("IP", "6.6.6.6") twice fixed this node's
    /// Globals.ReportedIP (used in the vBTC validator registration and heartbeat); any string was accepted and never pruned.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW20_ReportedIpTests
    {
        private static void Reset()
        {
            Globals.ReportedIP = "";
            Globals.ReportedIPManuallySet = false;
            Globals.ReportedIPs.Clear();
            P2PClient.ResetReportedIPTracking();
        }

        [Fact]
        public void NEW20_PoC_OnePeerRepeatingAnAddressDoesNotFixTheReportedIP()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                P2PClient.RecordReportedIP("6.6.6.6", "203.0.113.9");
                P2PClient.RecordReportedIP("6.6.6.6", "203.0.113.9");
                Assert.True(string.IsNullOrEmpty(Globals.ReportedIP));

                P2PClient.RecordReportedIP("6.6.6.6", "198.51.100.7"); // control: a second peer confirms
                Assert.Equal("6.6.6.6", Globals.ReportedIP);
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void NEW20_NonAddressReportsAreIgnored()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                P2PClient.RecordReportedIP(new string('x', 100_000), "203.0.113.9");
                P2PClient.RecordReportedIP("not-an-ip", "203.0.113.9");
                Assert.Empty(Globals.ReportedIPs);
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void OnePeerHoldsOneVote_SoItCannotFillReportedIPs()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                for (var i = 1; i <= 256; i++)
                    P2PClient.RecordReportedIP($"10.20.{i / 256}.{i % 256}", "203.0.113.9");
                Assert.Single(Globals.ReportedIPs);
                Assert.True(Globals.ReportedIPs.ContainsKey("10.20.1.0"));

                // The table still has room for real reports from other peers.
                P2PClient.RecordReportedIP("6.6.6.6", "198.51.100.7");
                P2PClient.RecordReportedIP("6.6.6.6", "198.51.100.8");
                Assert.Equal(2, Globals.ReportedIPs["6.6.6.6"]);
                Assert.Equal("6.6.6.6", Globals.ReportedIP);
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void APeerWhoseViewChanges_MovesItsVote_AndAManualAddressIsKept()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                P2PClient.RecordReportedIP("6.6.6.6", "198.51.100.7");
                P2PClient.RecordReportedIP("6.6.6.6", "198.51.100.8");
                P2PClient.RecordReportedIP("7.7.7.7", "198.51.100.8");
                Assert.Equal(1, Globals.ReportedIPs["6.6.6.6"]);
                Assert.Equal(1, Globals.ReportedIPs["7.7.7.7"]);

                P2PClient.RecordReportedIP("8.8.8.8", "198.51.100.7");
                Assert.False(Globals.ReportedIPs.ContainsKey("6.6.6.6"));

                Globals.ReportedIPs["9.9.9.9"] = 99999; // seeded by a manually configured address
                P2PClient.RecordReportedIP("9.9.9.9", "198.51.100.9");
                P2PClient.RecordReportedIP("8.8.8.8", "198.51.100.9");
                Assert.Equal(99999, Globals.ReportedIPs["9.9.9.9"]);
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void DialsSkipOnlyTheConfirmedOwnAddress_NotEveryReportedCandidate()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                P2PClient.RecordReportedIP("198.51.100.20", "203.0.113.1"); // one peer's candidate, a real peer's address
                P2PClient.RecordReportedIP("198.51.100.21", "203.0.113.2");
                Assert.Empty(P2PClient.OwnAddressesToSkip());

                P2PClient.RecordReportedIP("198.51.100.21", "203.0.113.3"); // a second peer confirms our address
                Assert.Equal(new[] { "198.51.100.21" }, P2PClient.OwnAddressesToSkip());
                Assert.Contains("198.51.100.20", Globals.ReportedIPs.Keys); // still a candidate, but no longer skipped
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void TwoSpellingsOfOneReporterAddress_CastOneVote()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                P2PClient.RecordReportedIP("198.51.100.30", "203.0.113.9");
                P2PClient.RecordReportedIP("198.51.100.30", "::ffff:203.0.113.9");
                Assert.True(string.IsNullOrEmpty(Globals.ReportedIP), "one endpoint must not confirm an address on its own");
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }
    }
}
