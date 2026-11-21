using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A node verifies proofs only with the circuit generation its build pins: a native library that reports a capability
    /// bit the build does not know is refused, and the node refuses private transactions rather than judge with it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PlonkCircuitVersionGuardTests
    {
        private const uint PinnedVerifier = PlonkNative.CapParsePublicInputsV1 | PlonkNative.CapV1Circuits
            | PlonkNative.CapVfxPi2Verify | PlonkNative.CapV2Circuits;
        private const uint LaterGeneration = 512;

        private static bool VerifierAvailableWith(uint caps)
        {
            var prior = PLONKSetup.Capabilities;
            try
            {
                PLONKSetup.Capabilities = () => caps;
                return PlonkProofVerifier.VerifierAvailable();
            }
            finally
            {
                PLONKSetup.Capabilities = prior;
                PLONKSetup.RefreshVerificationCapability();
            }
        }

        [Fact]
        public void TheReleasedLibrary_IsThePinnedGeneration()
        {
            Assert.Null(PLONKSetup.CircuitVersionMismatch(PinnedVerifier));
            Assert.Null(PLONKSetup.CircuitVersionMismatch(PinnedVerifier | PlonkNative.CapV1Prove));
            Assert.Null(PLONKSetup.CircuitVersionMismatch(PLONKSetup.KnownCapabilities));
            Assert.True(VerifierAvailableWith(PinnedVerifier | PlonkNative.CapV1Prove));
        }

        [Fact]
        public void ALibraryFromALaterCircuitGeneration_DoesNotVerify()
        {
            Assert.NotNull(PLONKSetup.CircuitVersionMismatch(PinnedVerifier | LaterGeneration));
            Assert.NotNull(PLONKSetup.CircuitVersionMismatch(0x8000_0000u));
            Assert.False(VerifierAvailableWith(PinnedVerifier | LaterGeneration));
        }

        /// <summary>
        /// From PlonkCurrentRulesHeight a node judges proofs with plonk_verify2 on the circuits whose hashes fix their
        /// capacity: a library with plonk_verify2 but the earlier circuits (bit 7 without bit 8) cannot judge those blocks.
        /// </summary>
        [Fact]
        public void TheCurrentRules_NeedTheCircuitsWithAFixedHashCapacity()
        {
            static bool CurrentWith(uint caps)
            {
                var prior = PLONKSetup.Capabilities;
                try
                {
                    PLONKSetup.Capabilities = () => caps;
                    return PlonkProofVerifier.CurrentRulesAvailable();
                }
                finally
                {
                    PLONKSetup.Capabilities = prior;
                    PLONKSetup.RefreshVerificationCapability();
                }
            }
            Assert.True(CurrentWith(PinnedVerifier | PlonkNative.CapVerify2 | PlonkNative.CapBoundHashCapacity));
            Assert.False(CurrentWith(PinnedVerifier | PlonkNative.CapVerify2));
            Assert.False(CurrentWith(PinnedVerifier | PlonkNative.CapBoundHashCapacity));
        }

        [Fact]
        public void ALibraryWithoutTheV2Circuits_StillDoesNotVerify()
        {
            Assert.False(VerifierAvailableWith(PlonkNative.CapParsePublicInputsV1 | PlonkNative.CapV1Circuits | PlonkNative.CapVfxPi2Verify));
        }
    }
}
