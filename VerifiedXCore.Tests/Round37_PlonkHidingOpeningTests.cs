using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: the node's verifier refuses a PLONK proof whose opening carries a hiding term. Honest provers never make
    /// one, and the term is read from the proof outside the transcript. An honest shield proof verifies; the same proof
    /// with a zero hiding term added to either opening does not. Needs the params with prover keys (VFX_PLONK_PARAMS or
    /// the repository's VerifiedXCore/DBs/PlonkParams); without them the test returns early.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_PlonkHidingOpeningTests
    {
        // A proof is 17 compressed G1 commitments (48 bytes each), then the aw opening (w, 48 bytes; an Option flag;
        // 32 bytes when set), then the saw opening the same way.
        private const int AwFlag = 17 * 48 + 48;
        private const int SawFlagWhenAwHasNone = AwFlag + 1 + 48;

        private static string? FindParams()
        {
            var env = Environment.GetEnvironmentVariable("VFX_PLONK_PARAMS");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "VerifiedXCore", "DBs", "PlonkParams", PLONKParamsDownloader.ParamsFileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static byte[] WithZeroHidingTerm(byte[] proof, int flagOffset)
        {
            Assert.Equal(0, proof[flagOffset]); // honest: no hiding term
            return proof.Take(flagOffset).Append((byte)1).Concat(new byte[32]).Concat(proof.Skip(flagOffset + 1)).ToArray();
        }

        [Fact]
        public void AProofWhoseOpeningCarriesAHidingTerm_IsRefused()
        {
            var path = FindParams();
            if (path != null && PLONKSetup.TryLoadParamsFile(path)) PLONKSetup.RefreshVerificationCapability();
            if (path == null || !PlonkProverV1.IsAvailable)
            {
                Console.WriteLine("Round37_PlonkHidingOpeningTests: no params with prover keys; skipped.");
                return;
            }
            var randomness = PrivacyField.RandomCanonical();
            var code = PlonkProverV1.TryProveShield(100_000_000, randomness, PoseidonV1.OwnerPk(PrivacyField.Zero32), out var proof, out var pi);
            Assert.Equal(PlonkNative.Success, code);
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof!, pi!));

            Assert.NotEqual(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, WithZeroHidingTerm(proof!, AwFlag), pi!));
            Assert.NotEqual(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, WithZeroHidingTerm(proof!, SawFlagWhenAwHasNone), pi!));
        }
    }
}
