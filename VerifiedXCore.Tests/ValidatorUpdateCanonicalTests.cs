using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: the ETH message every validator signs for a Base validator-set update
    /// must be built from the normalized action and a canonical target list, so all signers hash
    /// identical bytes and the requester cannot obtain many differently-labelled/ordered variants.
    /// </summary>
    public class ValidatorUpdateCanonicalTests
    {
        [Fact]
        public void CanonicalTargets_TrimLowerDistinctSorted()
        {
            var canon = BaseValidatorSyncService.CanonicalTargets(new[] { " 0xBBBB ", "0xaaaa", "0xBBBB", "", null! });
            Assert.Equal(new[] { "0xaaaa", "0xbbbb" }, canon);
        }

        [Fact]
        public void CanonicalTargets_OrderIndependent()
        {
            var a = BaseValidatorSyncService.CanonicalTargets(new[] { "0x1", "0x2", "0x3" });
            var b = BaseValidatorSyncService.CanonicalTargets(new[] { "0x3", "0x1", "0x2" });
            Assert.Equal(a, b);
        }

        [Fact]
        public void SignRequestMessage_SameForAnyTargetOrderOrCase()
        {
            var m1 = BaseValidatorSyncService.BuildSignRequestMessage("add", new[] { "0xBB", "0xaa" }, 10, "xReq", 5);
            var m2 = BaseValidatorSyncService.BuildSignRequestMessage("ADD_BATCH", new[] { "0xaa", "0xbb" }, 10, "xReq", 5);
            Assert.Equal(m1, m2);
        }
    }
}
