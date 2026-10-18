using VerifiedXCore;
using VerifiedXCore.P2P;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A validator's registration and heartbeat publish its address on chain, so they carry an auto-discovered address
    /// only once three peers confirm it; a configured address (<c>ipaddress=</c>) always. Two confirmations still set
    /// Globals.ReportedIP for the node's other uses.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ValidatorReportedIpTests
    {
        private static void Reset()
        {
            Globals.ReportedIP = "";
            Globals.ReportedIPManuallySet = false;
            Globals.ReportedIPs.Clear();
            P2PClient.ResetReportedIPTracking();
        }

        [Fact]
        public void TwoConfirmations_SetTheReportedIP_ButRegisterNothing()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                P2PClient.RecordReportedIP("6.6.6.6", "203.0.113.9");
                P2PClient.RecordReportedIP("6.6.6.6", "198.51.100.7");
                Assert.Equal("6.6.6.6", Globals.ReportedIP);
                Assert.Null(P2PClient.ValidatorReportedIP());
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void ThreeConfirmations_Register()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                P2PClient.RecordReportedIP("6.6.6.6", "203.0.113.9");
                P2PClient.RecordReportedIP("6.6.6.6", "198.51.100.7");
                P2PClient.RecordReportedIP("6.6.6.6", "192.0.2.44");
                Assert.Equal("6.6.6.6", P2PClient.ValidatorReportedIP());

                P2PClient.RetireReport("192.0.2.44"); // one confirmation withdrawn: below three again
                Assert.Null(P2PClient.ValidatorReportedIP());
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void AConfiguredAddress_AlwaysRegisters()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                Globals.ReportedIP = "7.7.7.7";
                Globals.ReportedIPManuallySet = true;
                Assert.Equal("7.7.7.7", P2PClient.ValidatorReportedIP());
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }

        [Fact]
        public void NoAddress_RegistersNothing()
        {
            var (ip, manual) = (Globals.ReportedIP, Globals.ReportedIPManuallySet);
            try
            {
                Reset();
                Assert.Null(P2PClient.ValidatorReportedIP());
            }
            finally { Reset(); Globals.ReportedIP = ip; Globals.ReportedIPManuallySet = manual; }
        }
    }
}
