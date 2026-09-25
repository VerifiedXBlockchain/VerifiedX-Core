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
    }
}
