using System;
using System.IO;
using System.Linq;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A proof that makes the native verifier panic is refused as an invalid proof: the library catches the panic at its
    /// boundary (ErrPanic) instead of aborting the node, and the node reads that, like a proof it cannot read, as a failed
    /// proof, not as a sign of local state corruption. The end-to-end test crafts the proof (custom evaluations removed)
    /// and needs the VXPLNK06 params with prover keys (VFX_PLONK_PARAMS); without them it returns early.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PlonkPanicRefusedTests
    {
        [Theory]
        [InlineData(PlonkNative.ErrPanic)]
        [InlineData(PlonkNative.ErrCrypto)]
        public void ARefusedProof_IsAnInvalidProof_NotALocalFault(int code)
        {
            var prior = PlonkProofVerifier.NativeVerify;
            try
            {
                PlonkProofVerifier.ClearVerifiedProofCache();
                PlonkProofVerifier.NativeVerify = (_, _, _) => code;
                Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, new byte[] { 1, 2, 3 }, new byte[] { 4 }));
                Assert.False(BlockValidatorService.IsStateCorruptionSignal($"{PlonkProofVerifier.FailedProofPrefix} (primary circuit)."));
            }
            finally { PlonkProofVerifier.NativeVerify = prior; }
        }

        /// <summary>The proof with its custom-evaluation list (u64 count, then per entry u64 length, label, 32 bytes) emptied.</summary>
        private static byte[] WithoutCustomEvaluations(byte[] proof)
        {
            var marker = System.Text.Encoding.ASCII.GetBytes("_eval");
            var labelAt = Enumerable.Range(0, proof.Length - marker.Length).First(i => proof.AsSpan(i, marker.Length).SequenceEqual(marker));
            var start = -1;
            for (var s = labelAt; s >= 16; s--)
            {
                var len = (int)BitConverter.ToUInt64(proof, s - 8);
                if (len > 0 && len < 64 && s + len > labelAt && proof.Skip(s).Take(len).All(b => b > 0x20 && b < 0x7F)) { start = s - 16; break; }
            }
            Assert.True(start >= 0);
            var count = (int)BitConverter.ToUInt64(proof, start);
            var end = start + 8;
            for (var i = 0; i < count; i++)
                end += 8 + (int)BitConverter.ToUInt64(proof, end) + 32;
            return proof.Take(start).Concat(BitConverter.GetBytes(0UL)).Concat(proof.Skip(end)).ToArray();
        }

        [Fact]
        public void ACraftedProofThatPanicsTheVerifier_IsRefused_AndTheNodeKeepsVerifying()
        {
            var path = Environment.GetEnvironmentVariable("VFX_PLONK_PARAMS");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !PLONKSetup.TryLoadParamsFile(path) || !PrivateTxProverV1.CanProve)
            {
                Console.WriteLine("PlonkPanicRefusedTests: no VXPLNK06 params with prover keys (VFX_PLONK_PARAMS); end-to-end test skipped.");
                return;
            }
            var randomness = PrivacyField.FromUInt64(7);
            var ownerPk = PrivacyField.FromUInt64(11);
            var proof = new byte[65536]; nuint proofLen = (nuint)proof.Length;
            var pi = new byte[65536]; nuint piLen = (nuint)pi.Length;
            Assert.Equal(PlonkNative.Success, PlonkNative.plonk_prove_shield(500_000_000UL, randomness, ownerPk, proof, ref proofLen, pi, ref piLen));
            proof = proof.Take((int)proofLen).ToArray();
            pi = pi.Take((int)piLen).ToArray();
            Assert.Equal(1, PlonkNative.plonk_verify(1, proof, (nuint)proof.Length, pi, (nuint)pi.Length));

            var crafted = WithoutCustomEvaluations(proof);
            Assert.Equal(PlonkNative.ErrPanic, PlonkNative.plonk_verify(1, crafted, (nuint)crafted.Length, pi, (nuint)pi.Length)); // the process is still here

            Assert.Equal(1, PlonkNative.plonk_verify(1, proof, (nuint)proof.Length, pi, (nuint)pi.Length));
        }
    }
}
