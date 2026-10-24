using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Replace-by-fee is only offered once nonces are enforced (Globals.TXHeightRule4); before that the original stays
    /// pending everywhere and a replacement would be mined beside it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ReplaceByFeeGateTests
    {
        private const long Rule = 5000;

        private static void WithTip(long tip, Action check)
        {
            var priorRule = Globals.TXHeightRule4;
            var priorTip = Globals.LastBlock;
            try
            {
                Globals.TXHeightRule4 = Rule;
                Globals.LastBlock = new Block { Height = tip };
                check();
            }
            finally
            {
                Globals.TXHeightRule4 = priorRule;
                Globals.LastBlock = priorTip;
            }
        }

        [Fact]
        public void BeforeNonceRules_ReplaceByFeeIsRefused() => WithTip(Rule, () =>
        {
            Assert.False(WalletService.ReplaceByFeeIsActive());
            var result = JObject.Parse(WalletService.SendTXOutRBF("xFrom", "xTo", 1M, 1M, "hash").GetAwaiter().GetResult());
            Assert.Equal("Fail", (string?)result["Result"]);
            Assert.Contains("nonce rules", (string?)result["Message"]);
        });

        [Fact]
        public void OnceNonceRulesAreActive_ReplaceByFeeIsOffered() => WithTip(Rule + 1, () =>
        {
            Assert.True(WalletService.ReplaceByFeeIsActive());
        });

        [Fact]
        public void NonceRulesShipInactive_SoReplaceByFeeIsOffNow()
        {
            Assert.Equal(999999999999, Globals.TXHeightRule4);
        }
    }
}
