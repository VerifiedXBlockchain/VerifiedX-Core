using System;
using System.Linq;
using System.Numerics;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A vBTCb contract with typed messages (V4 and later) that holds fewer than 3 casters, as a V3 proxy upgraded in place
    /// does until the casters are added, approves nothing but adding them: from V5 the contract refuses everything else,
    /// so Core gathers no mint or validator-change signatures for it, and gathers the caster bootstrap from 2/3 of its
    /// validators alone. V3 (no casters at all) and a contract with its casters are judged as before.
    /// </summary>
    public class BaseCastersNotSetUpTests
    {
        private static readonly string[] Validators = { "0x00000000000000000000000000000000000000a1", "0x00000000000000000000000000000000000000a2", "0x00000000000000000000000000000000000000a3" };
        private static readonly string[] Casters = { "0x00000000000000000000000000000000000000c1", "0x00000000000000000000000000000000000000c2", "0x00000000000000000000000000000000000000c3" };

        private static BaseBridgeService.SignerView View(int version, string[]? casters) =>
            new(Validators, version, 2, BigInteger.One, casters, casters == null || casters.Length == 0 ? 0 : casters.Length / 2 + 1);

        [Fact]
        public void A_typed_contract_without_casters_is_enough_for_nothing_but_the_caster_bootstrap()
        {
            var casterless = View(VbtcbTypedData.MessageVersion, Array.Empty<string>());
            Assert.False(UserBridgeMintService.Enough(casterless, 2, Validators));               // every validator: still no mint
            Assert.True(UserBridgeMintService.EnoughForCasterChange(casterless, Validators[..2])); // 2/3 of the validators add casters
            Assert.False(UserBridgeMintService.EnoughForCasterChange(casterless, Validators[..1]));
            Assert.False(UserBridgeMintService.Enough(View(VbtcbTypedData.MessageVersion, Casters[..2]), 2, Validators.Concat(Casters)));
        }

        [Fact]
        public void V3_and_a_contract_with_its_casters_are_judged_as_before()
        {
            var v3 = View(3, null);
            Assert.True(UserBridgeMintService.Enough(v3, 2, Validators[..2]));

            var withCasters = View(VbtcbTypedData.MessageVersion, Casters);
            Assert.False(UserBridgeMintService.Enough(withCasters, 2, Validators));
            Assert.True(UserBridgeMintService.Enough(withCasters, 2, Validators[..2].Concat(Casters[..2])));
            Assert.False(UserBridgeMintService.EnoughForCasterChange(withCasters, Validators));    // a caster change needs the casters too
            Assert.True(UserBridgeMintService.EnoughForCasterChange(withCasters, Validators[..2].Concat(Casters[..2])));
        }
    }
}
