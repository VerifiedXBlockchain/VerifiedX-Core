using System;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: a confirmation's agreed handler was unsigned and last-writer-wins, so a
    /// network observer could replay a caster's genuine bound vote under a junk handler, overwrite
    /// the real confirmation, and drop it from the count. The handler is now covered by a second
    /// signature with a freshness window.
    /// </summary>
    public class BurnConfirmationHandlerBindingTests
    {
        private static (PrivateKey key, string pub, string addr) NewCaster()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        [Fact]
        public void SignedHandler_Accepted()
        {
            var (key, pub, addr) = NewCaster();
            long ts = 1_700_000_000;
            var sig = VerifiedXCore.Services.SignatureService.CreateSignature(BridgeCasterConsensus.BuildConfirmationHandlerMessage("0xb", addr, "xHandler", ts), key, pub);
            Assert.True(BridgeCasterConsensus.VerifyConfirmationHandler(addr, "0xb", "xHandler", ts, sig, ts + 1).Ok);
        }

        [Fact]
        public void HandlerSwapped_Refused()
        {
            var (key, pub, addr) = NewCaster();
            long ts = 1_700_000_000;
            var sig = VerifiedXCore.Services.SignatureService.CreateSignature(BridgeCasterConsensus.BuildConfirmationHandlerMessage("0xb", addr, "xHandler", ts), key, pub);
            var (ok, reason) = BridgeCasterConsensus.VerifyConfirmationHandler(addr, "0xb", "xJunk", ts, sig, ts);
            Assert.False(ok);
            Assert.Contains("invalid handler signature", reason);
        }

        [Fact]
        public void MissingOrStale_Refused()
        {
            var (key, pub, addr) = NewCaster();
            long ts = 1_700_000_000;
            var sig = VerifiedXCore.Services.SignatureService.CreateSignature(BridgeCasterConsensus.BuildConfirmationHandlerMessage("0xb", addr, "xHandler", ts), key, pub);
            Assert.False(BridgeCasterConsensus.VerifyConfirmationHandler(addr, "0xb", "xHandler", ts, "", ts).Ok);
            Assert.False(BridgeCasterConsensus.VerifyConfirmationHandler(addr, "0xb", "", ts, sig, ts).Ok);
            Assert.False(BridgeCasterConsensus.VerifyConfirmationHandler(addr, "0xb", "xHandler", ts, sig, ts + BridgeCasterConsensus.ALERT_MAX_SKEW_SECONDS + 1).Ok);
        }
    }
}
