using System.Numerics;
using ReserveBlockCore.Models.Privacy;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Maps human VFX amounts to <see cref="PlonkNative.pedersen_commit"/> scalar domain (<see cref="Globals.PrivacyAmountScalingFactor"/>).
    /// </summary>
    public static class PrivacyPedersenAmount
    {
        /// <remarks>
        /// Rejects values above <c>u64</c> after 10^18 scaling. Fine for current on-chain amounts; public-input / circuit
        /// v2 may need <c>u128</c> if the spec’s full range must be represented (see <see cref="PlonkPublicInputsV1"/> remarks).
        /// </remarks>
        public static bool TryToScaledU64(decimal amount, out ulong scaled, out string? error)
        {
            scaled = 0;
            error = null;
            if (amount < 0)
            {
                error = "Amount cannot be negative.";
                return false;
            }
            var bi = Globals.ToCircuitAmount(amount);
            if (bi < 0 || bi > (BigInteger)ulong.MaxValue)
            {
                error = "Amount out of range for Pedersen encoding.";
                return false;
            }
            scaled = (ulong)bi;
            return true;
        }

        /// <summary>Commits <paramref name="amount"/> with fresh 32-byte randomness.</summary>
        public static bool TryCommitAmount(decimal amount, out byte[] randomness32, out byte[] commitmentG1, out string? error)
        {
            randomness32 = Array.Empty<byte>();
            commitmentG1 = Array.Empty<byte>();
            if (!TryToScaledU64(amount, out var scaled, out error))
                return false;
            randomness32 = new byte[PlonkNative.ScalarSize];
            System.Security.Cryptography.RandomNumberGenerator.Fill(randomness32);
            commitmentG1 = new byte[PlonkNative.G1CompressedSize];
            var code = PlonkNative.pedersen_commit(scaled, randomness32, commitmentG1);
            if (code != PlonkNative.Success)
            {
                error = $"pedersen_commit failed: {code}";
                return false;
            }
            return true;
        }

        public static ShieldedPlainNote CreatePlainNote(decimal amount, ReadOnlySpan<byte> randomness32, string assetType, string? memo = null)
        {
            if (randomness32.Length != PlonkNative.ScalarSize)
                throw new ArgumentException($"Randomness must be {PlonkNative.ScalarSize} bytes.", nameof(randomness32));
            return new ShieldedPlainNote
            {
                Version = 1,
                Amount = amount,
                RandomnessB64 = Convert.ToBase64String(randomness32.ToArray()),
                AssetType = assetType,
                Memo = memo
            };
        }
    }
}
