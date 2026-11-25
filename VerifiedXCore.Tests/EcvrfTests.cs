using System;
using System.Numerics;
using System.Text;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The ECVRF of RFC 9381: on P-256 it reproduces the RFC's own test vectors for ECVRF-P256-SHA256-TAI step by step
    /// (encode-to-curve, nonce, proof, output); on secp256k1 a proof verifies under its key only, for its message only, and
    /// every valid proof of a message has the same output.
    /// </summary>
    public class EcvrfTests
    {
        public static TheoryData<string, string, string, string, string, string, string> Rfc9381P256Tai => new()
        {
            // Example 10
            { "c9afa9d845ba75166b5c215767b1d6934e50c3db36e89b127b8a622b120f6721", "0360fed4ba255a9d31c961eb74c6356d68c049b8923b61fa6ce669622e60f29fb6",
              "73616d706c65", "0272a877532e9ac193aff4401234266f59900a4a9e3fc3cfc6a4b7e467a15d06d4", "0d90591273453d2dc67312d39914e3a93e194ab47a58cd598886897076986f77",
              "035b5c726e8c0e2c488a107c600578ee75cb702343c153cb1eb8dec77f4b5071b4a53f0a46f018bc2c56e58d383f2305e0975972c26feea0eb122fe7893c15af376b33edf7de17c6ea056d4d82de6bc02f",
              "a3ad7b0ef73d8fc6655053ea22f9bede8c743f08bbed3d38821f0e16474b505e" },
            // Example 11
            { "c9afa9d845ba75166b5c215767b1d6934e50c3db36e89b127b8a622b120f6721", "0360fed4ba255a9d31c961eb74c6356d68c049b8923b61fa6ce669622e60f29fb6",
              "74657374", "02173119b4fff5e6f8afed4868a29fe8920f1b54c2cf89cc7b301d0d473de6b974", "5852353a868bdce26938cde1826723e58bf8cb06dd2fed475213ea6f3b12e961",
              "034dac60aba508ba0c01aa9be80377ebd7562c4a52d74722e0abae7dc3080ddb56c19e067b15a8a8174905b13617804534214f935b94c2287f797e393eb0816969d864f37625b443f30f1a5a33f2b3c854",
              "a284f94ceec2ff4b3794629da7cbafa49121972671b466cab4ce170aa365f26d" },
            // Example 12
            { "2ca1411a41b17b24cc8c3b089cfd033f1920202a6c0de8abb97df1498d50d2c8", "03596375e6ce57e0f20294fc46bdfcfd19a39f8161b58695b3ec5b3d16427c274d",
              "4578616d706c65207573696e67204543445341206b65792066726f6d20417070656e646978204c2e342e32206f6620414e53492e58392d36322d32303035",
              "0258055c26c4b01d01c00fb57567955f7d39cd6f6e85fd37c58f696cc6b7aa761d", "5689e2e08e1110b4dda293ac21667eac6db5de4a46a519c73d533f69be2f4da3",
              "03d03398bf53aa23831d7d1b2937e005fb0062cbefa06796579f2a1fc7e7b8c667d091c00b0f5c3619d10ecea44363b5a599cadc5b2957e223fec62e81f7b4825fc799a771a3d7334b9186bdbee87316b1",
              "90871e06da5caa39a3c61578ebb844de8635e27ac0b13e829997d0d95dd98c19" },
        };

        private static byte[] Hex(string s) => Convert.FromHexString(s);
        private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
        private static BigInteger Int(string hex) => new(Hex(hex), isUnsigned: true, isBigEndian: true);

        [Theory]
        [MemberData(nameof(Rfc9381P256Tai))]
        public void P256_reproduces_the_rfc_vectors(string sk, string pk, string alpha, string h, string k, string pi, string beta)
        {
            var vrf = Ecvrf.P256;
            Assert.Equal(pk, Hex(vrf.PublicKeyOf(Int(sk))));
            var hPoint = vrf.EncodeToCurve(Hex(pk), Hex(alpha));
            Assert.Equal(h, Hex(vrf.Encode(hPoint)));
            Assert.Equal(k, Hex(Ecvrf.IntToString(vrf.Nonce(Int(sk), Hex(h)), 32)));

            var proof = vrf.Prove(Int(sk), Hex(alpha));
            Assert.Equal(pi, Hex(proof));
            Assert.True(vrf.Verify(Hex(pk), proof, Hex(alpha), out var output));
            Assert.Equal(beta, Hex(output));
            Assert.Equal(beta, Hex(vrf.ProofToHash(proof)!));
        }

        private static readonly BigInteger KeyA = Int("1b2c3d4e5f60718293a4b5c6d7e8f90112233445566778899aabbccddeeff001");
        private static readonly BigInteger KeyB = Int("7e57c0de7e57c0de7e57c0de7e57c0de7e57c0de7e57c0de7e57c0de7e57c0de");

        [Fact]
        public void A_secp256k1_proof_verifies_for_its_key_and_message_only()
        {
            var vrf = Ecvrf.Secp256k1;
            var alpha = Encoding.UTF8.GetBytes("vfx-vrf-block|seed|1000");
            var proof = vrf.Prove(KeyA, alpha);
            Assert.Equal(Ecvrf.ProofLength, proof.Length);

            Assert.True(vrf.Verify(vrf.PublicKeyOf(KeyA), proof, alpha, out var beta));
            Assert.Equal(Ecvrf.OutputLength, beta.Length);
            Assert.False(vrf.Verify(vrf.PublicKeyOf(KeyB), proof, alpha, out _));                                   // another key
            Assert.False(vrf.Verify(vrf.PublicKeyOf(KeyA), proof, Encoding.UTF8.GetBytes("vfx-vrf-block|seed|1001"), out _)); // another message
            for (var i = 0; i < proof.Length; i += 7)
            {
                var bent = (byte[])proof.Clone();
                bent[i] ^= 0x01;
                Assert.False(vrf.Verify(vrf.PublicKeyOf(KeyA), bent, alpha, out _));
            }
            Assert.False(vrf.Verify(vrf.PublicKeyOf(KeyA), proof[..^1], alpha, out _));
            // A proof of one suite is no proof in another.
            Assert.False(Ecvrf.P256.Verify(Ecvrf.P256.PublicKeyOf(KeyA), proof, alpha, out _));
        }

        [Fact]
        public void The_output_is_fixed_by_key_and_message_whatever_proof_carries_it()
        {
            var vrf = Ecvrf.Secp256k1;
            var alpha = Encoding.UTF8.GetBytes("message");
            var first = vrf.Prove(KeyA, alpha);
            Assert.Equal(Hex(first), Hex(vrf.Prove(KeyA, alpha))); // the prover's nonce is deterministic
            Assert.True(vrf.Verify(vrf.PublicKeyOf(KeyA), first, alpha, out var beta));
            Assert.NotEqual(Hex(beta), Hex(vrf.ProofToHash(vrf.Prove(KeyB, alpha))!));
            Assert.NotEqual(Hex(beta), Hex(vrf.ProofToHash(vrf.Prove(KeyA, Encoding.UTF8.GetBytes("other")))!));
        }

        [Fact]
        public void An_uncompressed_key_reads_as_its_compressed_form()
        {
            var vrf = Ecvrf.Secp256k1;
            var key = new VerifiedXCore.EllipticCurve.PrivateKey("secp256k1", KeyA);
            var uncompressed = Hex("04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
            var alpha = Encoding.UTF8.GetBytes("m");
            var proof = vrf.Prove(KeyA, alpha);
            Assert.True(vrf.Verify(uncompressed, proof, alpha, out var a));
            Assert.True(vrf.Verify(vrf.PublicKeyOf(KeyA), proof, alpha, out var b));
            Assert.Equal(Hex(a), Hex(b));
            Assert.False(vrf.Verify(new byte[65], proof, alpha, out _));
            Assert.False(vrf.Verify(Array.Empty<byte>(), proof, alpha, out _));
        }
    }
}
