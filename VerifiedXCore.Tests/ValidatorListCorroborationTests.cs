using System.Collections.Concurrent;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A validator-list entry needs reports from two other committee casters, or from every other caster when there are
    /// fewer: one in a two-caster committee. Self and blank addresses never count as a voice.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ValidatorListCorroborationTests
    {
        private const string Self = "xSelfCaster";

        private static int RequiredWith(params string?[] casterAddresses)
        {
            var priorBag = Globals.BlockCasters;
            var priorSelf = Globals.ValidatorAddress;
            try
            {
                Globals.ValidatorAddress = Self;
                Globals.BlockCasters = new ConcurrentBag<Peers>(casterAddresses.Select(a => new Peers { ValidatorAddress = a, PeerIP = "10.0.0.1" }));
                return ValidatorListExchange.RequiredCorroboration();
            }
            finally
            {
                Globals.BlockCasters = priorBag;
                Globals.ValidatorAddress = priorSelf;
            }
        }

        [Fact]
        public void ThreeOrMoreOtherCasters_RequireTwoReports()
        {
            Assert.Equal(2, RequiredWith(Self, "xA", "xB", "xC"));
            Assert.Equal(2, RequiredWith(Self, "xA", "xB", "xC", "xD", "xE"));
        }

        [Fact]
        public void TwoOtherCasters_RequireBoth()
        {
            Assert.Equal(2, RequiredWith(Self, "xA", "xB"));
        }

        [Fact]
        public void ATwoCasterCommittee_RequiresTheOneOtherVoice()
        {
            Assert.Equal(1, RequiredWith(Self, "xA"));
        }

        [Fact]
        public void SelfDuplicatesAndBlankAddresses_AreNotVoices()
        {
            Assert.Equal(1, RequiredWith(Self, Self, "xA", "xA", "", null));
            Assert.Equal(1, RequiredWith(Self)); // never zero: an entry always needs a report
        }
    }
}
