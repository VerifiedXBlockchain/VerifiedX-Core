using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Data;
using VerifiedXCore.Models;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// From Globals.VrfMixHeight every block carries its producer's VRF proof (<see cref="Ecvrf.Secp256k1"/>, under the
    /// producer's own validator key) over its height's seed (<see cref="VrfSeed.SeedFor"/>), and the running mix of the
    /// chain's VRF outputs: the previous block's mix hashed with this block's output. Both are in the block's hash.
    /// An epoch whose anchor is at or above the height is seeded from the anchor's mix (<see cref="VrfSeed.SeedMaterialAt"/>).
    /// A VRF output is fixed by the key and the seed, so the producer of an anchor can publish its block or withhold it,
    /// and has no other choice over the next epoch: no block it builds differently changes the mix. The draw of block
    /// producers itself is unchanged, and nothing else is sent between nodes.
    /// </summary>
    public static class BlockVrf
    {
        public static bool Active(long height) => height >= Globals.VrfMixHeight;

        /// <summary>The message a producer proves for <paramref name="height"/>; null when the seed is not known.</summary>
        public static byte[]? Alpha(long height) =>
            VrfSeed.SeedFor(height) is { } seed ? Encoding.UTF8.GetBytes($"vfx-vrf-block|{seed}") : null;

        /// <summary>The mix after a block whose VRF output is <paramref name="beta"/>.</summary>
        public static string MixOf(string previousMix, byte[] beta) =>
            Sha256Hex($"vfx-vrf-mix|{previousMix}|{Convert.ToHexString(beta).ToLowerInvariant()}");

        /// <summary>The mix a block takes from the one before it: its mix, or, for the first block at the height, its hash.</summary>
        public static string? PreviousMix(Block? previous)
        {
            if (previous == null) return null;
            if (Active(previous.Height)) return string.IsNullOrEmpty(previous.VrfMix) ? null : previous.VrfMix;
            return string.IsNullOrEmpty(previous.Hash) ? null : Sha256Hex($"vfx-vrf-mix-start|{previous.Hash}");
        }

        /// <summary>The block before <paramref name="block"/> on this chain: the tip when it is that block, else from storage.</summary>
        public static Block? PreviousOf(Block block)
        {
            var tip = Globals.LastBlock;
            if (tip != null && tip.Height == block.Height - 1 && tip.Hash == block.PrevHash) return tip;
            var stored = BlockchainData.GetBlockByHeight(block.Height - 1);
            return stored != null && stored.Hash == block.PrevHash ? stored : null;
        }

        /// <summary>
        /// Sets the proof and the mix of a block this node produces, before the block is hashed and signed. False when the
        /// height's seed or the previous block's mix is not known, or the key is not usable: no block is then produced.
        /// </summary>
        public static bool Attach(Block block, Block? previous, string privateKeyHex)
        {
            if (!Active(block.Height)) { block.VrfProof = null; block.VrfMix = null; return true; }
            var alpha = Alpha(block.Height);
            var previousMix = PreviousMix(previous);
            if (alpha == null || previousMix == null || previous == null || previous.Height != block.Height - 1) return false;
            if (!BigInteger.TryParse("0" + privateKeyHex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var x)) return false;
            if (x <= 0) return false;
            var proof = Ecvrf.Secp256k1.Prove(x, alpha);
            block.VrfProof = Convert.ToHexString(proof).ToLowerInvariant();
            block.VrfMix = MixOf(previousMix, Ecvrf.Secp256k1.ProofToHash(proof)!);
            return true;
        }

        /// <summary>
        /// Why a block's VRF fields are not right; null when they are, or when the block is below the height. The proof must
        /// verify under the key that signed the block, for the block's height's seed, and the mix must follow from the
        /// previous block's.
        /// </summary>
        public static string? Refusal(Block block, Block? previous)
        {
            if (block == null || !Active(block.Height)) return null;
            if (string.IsNullOrEmpty(block.VrfProof) || string.IsNullOrEmpty(block.VrfMix))
                return "the block carries no VRF proof";
            if (previous == null || previous.Height != block.Height - 1 || previous.Hash != block.PrevHash)
                return "the block before it is not known";
            var previousMix = PreviousMix(previous);
            if (previousMix == null) return "the block before it carries no VRF mix";
            var alpha = Alpha(block.Height);
            if (alpha == null) return "the seed of its height is not known";
            var key = ProducerKey(block);
            if (key == null) return "the producer's key is not known";
            byte[] proof;
            try { proof = Convert.FromHexString(block.VrfProof); } catch (FormatException) { return "the VRF proof is not hex"; }
            byte[] beta;
            try
            {
                if (!Ecvrf.Secp256k1.Verify(key, proof, alpha, out beta))
                    return "the VRF proof does not verify";
            }
            catch (Exception) { return "the VRF proof does not verify"; } // a proof is judged, never thrown out of validation
            if (!string.Equals(block.VrfMix, MixOf(previousMix, beta), StringComparison.Ordinal))
                return "the VRF mix does not follow from the block before it";
            return null;
        }

        /// <summary>The uncompressed public key that signed the block, when the signature is the producer's; null otherwise.</summary>
        internal static byte[]? ProducerKey(Block block)
        {
            try
            {
                if (!Services.SignatureService.VerifySignature(block.Validator, block.Hash, block.ValidatorSignature)) return null;
                var parts = block.ValidatorSignature.Split('.', 2);
                var hex = HexByteUtility.ByteToHex(Base58Utility.Base58Decode(parts[1]));
                if (hex.Length / 2 == 63) hex = "00" + hex; // as the signature check reads it
                var key = EllipticCurve.PublicKey.fromString(HexByteUtility.HexToByte(hex));
                return Convert.FromHexString("04" + HexByteUtility.ByteToHex(key.toString()));
            }
            catch { return null; }
        }

        private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}
