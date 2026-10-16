using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A proof the native verifier accepted is not verified again for the same circuit and public inputs; anything else
    /// still goes to the verifier.
    /// </summary>
    [Collection("PrivacySequential")]
    public class PlonkVerifiedProofCacheTests : IDisposable
    {
        private readonly Func<byte, byte[], byte[], int> _priorNative = PlonkProofVerifier.NativeVerify;
        private int _calls;

        public PlonkVerifiedProofCacheTests() => PlonkProofVerifier.ClearVerifiedProofCache();

        public void Dispose()
        {
            PlonkProofVerifier.NativeVerify = _priorNative;
            PlonkProofVerifier.ClearVerifiedProofCache();
        }

        private void NativeReturns(Func<byte[], byte[], int> verdict) =>
            PlonkProofVerifier.NativeVerify = (_, p, pi) => { _calls++; return verdict(p, pi); };

        [Fact]
        public void AcceptedProof_IsNotReverifiedForTheSameCircuitAndInputs()
        {
            NativeReturns((_, _) => 1);
            var proof = new byte[] { 1, 2, 3 };
            var pi = new byte[] { 9, 9 };

            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Transfer, proof, pi));
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Transfer, proof, pi));
            Assert.Equal(1, _calls);
        }

        [Fact]
        public void AcceptedProof_DoesNotCoverOtherPublicInputsOrAnotherCircuit()
        {
            var proof = new byte[] { 1, 2, 3 };
            var good = new byte[] { 9, 9 };
            NativeReturns((_, pi) => pi.SequenceEqual(good) ? 1 : 0);
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Transfer, proof, good));

            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Transfer, proof, new byte[] { 9, 8 }));
            // Same bytes moved across the proof/input boundary must not collide.
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Transfer, new byte[] { 1, 2 }, new byte[] { 3, 9, 9 }));

            NativeReturns((_, _) => 0);
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, good));
        }

        [Fact]
        public void RejectedOrFailedVerification_IsNotCached()
        {
            var proof = new byte[] { 4 };
            var pi = new byte[] { 5 };
            NativeReturns((_, _) => 0);
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, pi));
            NativeReturns((_, _) => -1);
            Assert.Equal(PlonkVerifyResult.NativeError, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, pi));
            NativeReturns((_, _) => 0);
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, pi));
            Assert.Equal(3, _calls);
        }
    }
}
