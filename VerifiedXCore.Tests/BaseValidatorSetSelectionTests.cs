using System;
using System.Collections.Generic;
using System.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: the Base (vBTC.b) minting validator set mirrored the raw vBTC validator
    /// registry. Registration is a free self-transaction gated only by a wallet balance, and the
    /// mint threshold is two thirds of the set, so enough free registrations would control minting.
    /// The minting set is now the intersection of registered validators and the caster committee.
    /// </summary>
    public class BaseValidatorSetSelectionTests
    {
        private static VBTCValidator V(string addr) => new VBTCValidator { ValidatorAddress = addr, IsActive = true, IPAddress = "1.2.3.4" };
        private static HashSet<string> C(params string[] a) => new HashSet<string>(a, StringComparer.Ordinal);

        [Fact]
        public void CastersOnly_KeepsOnlyRegisteredValidatorsInCommittee()
        {
            var registered = new List<VBTCValidator> { V("xA"), V("xB"), V("xSybil1"), V("xSybil2"), V("xSybil3") };
            var committee = C("xA", "xB", "xC"); // xC is a caster but not a registered vBTC validator

            var set = BaseValidatorSyncService.SelectBaseValidatorSet(registered, committee, castersOnly: true);

            Assert.Equal(new[] { "xA", "xB" }, set.Select(v => v.ValidatorAddress).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void CastersOnly_EmptyOrMissingCommittee_FailsClosed()
        {
            var registered = new List<VBTCValidator> { V("xA"), V("xB") };
            Assert.Empty(BaseValidatorSyncService.SelectBaseValidatorSet(registered, new HashSet<string>(), castersOnly: true));
            Assert.Empty(BaseValidatorSyncService.SelectBaseValidatorSet(registered, null, castersOnly: true));
        }

        [Fact]
        public void LegacyMode_ReturnsAllRegistered()
        {
            var registered = new List<VBTCValidator> { V("xA"), V("xSybil1") };
            var set = BaseValidatorSyncService.SelectBaseValidatorSet(registered, C("xA"), castersOnly: false);
            Assert.Equal(2, set.Count);
        }

        [Fact]
        public void IgnoresNullAndAddresslessEntries()
        {
            var registered = new List<VBTCValidator> { V("xA"), null!, new VBTCValidator { ValidatorAddress = "" } };
            var set = BaseValidatorSyncService.SelectBaseValidatorSet(registered, C("xA"), castersOnly: true);
            Assert.Single(set);
            Assert.Equal("xA", set[0].ValidatorAddress);
        }

        [Fact]
        public void SybilRegistrationsCannotReachTwoThirds()
        {
            // 3 honest casters registered, attacker registers 6 free validators.
            var registered = new List<VBTCValidator> { V("xC1"), V("xC2"), V("xC3") };
            registered.AddRange(Enumerable.Range(1, 6).Select(i => V($"xSybil{i}")));

            var set = BaseValidatorSyncService.SelectBaseValidatorSet(registered, C("xC1", "xC2", "xC3"), castersOnly: true);

            Assert.Equal(3, set.Count);
            Assert.DoesNotContain(set, v => v.ValidatorAddress.StartsWith("xSybil"));
        }
    }
}
