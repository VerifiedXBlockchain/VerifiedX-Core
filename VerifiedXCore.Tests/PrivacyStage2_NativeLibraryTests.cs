using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using VerifiedXCore;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1, stage 2: the repackaged native library (plonk_ffi with CapVfxPi2Verify) and the Core
    /// helpers built on it. These tests need no params file: they pin the circuit-compatible Poseidon exports, the
    /// canonical field encoding, the fixed-depth-32 tree against the native root computation, and the dummy-note
    /// constants, all of which every node computes identically from the library alone.
    /// </summary>
    public class PrivacyStage2_NativeLibraryTests
    {
        private static byte[] Fr(ulong n) => PrivacyField.FromUInt64(n);

        [Fact]
        public void TheLibraryCarriesTheStage2Exports()
        {
            Assert.True(PoseidonV1.IsAvailable, "plonk_ffi lacks poseidon_hash_2: the stage-2 library is not the one loaded");
            var caps = PlonkNative.plonk_capabilities();
            Assert.Equal(PlonkNative.CapParsePublicInputsV1, caps & PlonkNative.CapParsePublicInputsV1);
            // Bit 32 appears only once VXPLNK03 params are loaded; without them the library must not claim it.
            if ((caps & PlonkNative.CapV1Circuits) == 0)
                Assert.Equal(0u, caps & PlonkNative.CapVfxPi2Verify);
            else
                Assert.Equal(PlonkNative.CapVfxPi2Verify, caps & PlonkNative.CapVfxPi2Verify);
        }

        // ── Field encoding ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Field_ModulusAndEncodingRoundTrip()
        {
            Assert.Equal(BigInteger.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513"), PrivacyField.Modulus);
            var x = BigInteger.Parse("123456789012345678901234567890");
            Assert.Equal(x, PrivacyField.FromLe(PrivacyField.ToLe32(x)));
            Assert.True(PrivacyField.IsCanonicalLe(PrivacyField.ToLe32(PrivacyField.Modulus - 1)));
            var modulusLe = PrivacyField.ToLe32(PrivacyField.Modulus - 1);
            modulusLe[0] += 1; // == r
            Assert.False(PrivacyField.IsCanonicalLe(modulusLe));
            Assert.Equal(PrivacyField.Zero32, PrivacyField.ReduceLe(modulusLe)); // r mod r = 0
            Assert.Equal(7UL, (ulong)PrivacyField.FromLe(Fr(7)));
        }

        [Fact]
        public void Field_RandomCanonical_IsAlwaysBelowTheModulus_AndRawRandomOftenIsNot()
        {
            for (var i = 0; i < 200; i++)
                Assert.True(PrivacyField.IsCanonicalLe(PrivacyField.RandomCanonical()));
            var raw = Enumerable.Range(0, 200).Select(_ => RandomNumberGenerator.GetBytes(32)).Count(r => !PrivacyField.IsCanonicalLe(r));
            Assert.InRange(raw, 40, 160); // about half of raw 32-byte values are above r: this is why reduction is mandatory
        }

        // ── Poseidon exports ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Poseidon_IsDeterministic_OrderSensitive_AndNotTheChainedLegacyHash()
        {
            var a = PrivacyField.RandomCanonical();
            var b = PrivacyField.RandomCanonical();
            var c = PrivacyField.RandomCanonical();
            Assert.Equal(PoseidonV1.Hash2(a, b), PoseidonV1.Hash2(a, b));
            Assert.NotEqual(PoseidonV1.Hash2(a, b), PoseidonV1.Hash2(b, a));
            Assert.Equal(PoseidonV1.Hash3(a, b, c), PoseidonV1.Hash3(a, b, c));
            // hash_3 is hash_2 chained from zero, exactly as the circuits' poseidon_hash_3 gadget.
            Assert.Equal(PoseidonV1.Hash3(a, b, c), PoseidonV1.Hash2(PoseidonV1.Hash2(PoseidonV1.Hash2(PrivacyField.Zero32, a), b), c));
            // The legacy chained big-endian export is a different function: nothing it produced is circuit-compatible.
            var legacy = new byte[32];
            var input = a.Concat(b).ToArray();
            Assert.Equal(PlonkNative.Success, PlonkNative.poseidon_hash(input, (nuint)input.Length, legacy));
            Assert.NotEqual(legacy, PoseidonV1.Hash2(a, b));
            // The legacy nullifier derivation (used before the epoch) differs from the circuits' too.
            var legacyNullifier = NullifierService.DeriveFromNoteHash(a, b, 5);
            Assert.NotEqual(legacyNullifier, PoseidonV1.Nullifier(a, b, 5));
        }

        [Fact]
        public void Poseidon_RefusesNonCanonicalInput()
        {
            var tooBig = Enumerable.Repeat((byte)0xff, 32).ToArray();
            Assert.Throws<ArgumentException>(() => PoseidonV1.Hash2(tooBig, PrivacyField.Zero32));
            var o = new byte[32];
            Assert.Equal(PlonkNative.ErrCrypto, PlonkNative.poseidon_hash_2(tooBig, PrivacyField.Zero32, o)); // and so does the library itself
            Assert.Throws<ArgumentException>(() => PoseidonV1.NoteHash(1, new byte[31]));
        }

        [Fact]
        public void NoteHash_BindsAmountAndRandomness()
        {
            var r = PrivacyField.RandomCanonical();
            Assert.Equal(PoseidonV1.NoteHash(100_000_000, r), PoseidonV1.Hash2(Fr(100_000_000), r));
            Assert.NotEqual(PoseidonV1.NoteHash(100_000_000, r), PoseidonV1.NoteHash(100_000_001, r));
            Assert.NotEqual(PoseidonV1.NoteHash(100_000_000, r), PoseidonV1.NoteHash(100_000_000, PrivacyField.RandomCanonical()));
        }

        /// <summary>Stage 3: the v2 note names its owner; the owner key is a domain-separated Poseidon of the nullifier key.</summary>
        [Fact]
        public void OwnerKeyAndNoteHashV2_AreWhatTheCircuitsCompute()
        {
            var nk = PrivacyField.RandomCanonical();
            var r = PrivacyField.RandomCanonical();
            var owner = PoseidonV1.OwnerPk(nk);
            Assert.Equal(PoseidonV1.Hash2(PrivacyField.FromUInt64(PoseidonV1.OwnerPkDomain), nk), owner);
            Assert.Equal(0x524E574F5F584656UL, PoseidonV1.OwnerPkDomain);
            Assert.Equal("VFX_OWNR", System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(PoseidonV1.OwnerPkDomain)));
            Assert.Equal(PoseidonV1.Hash3(Fr(100_000_000), r, owner), PoseidonV1.NoteHashV2(100_000_000, r, owner));
            Assert.NotEqual(PoseidonV1.NoteHashV2(100_000_000, r, owner), PoseidonV1.NoteHashV2(100_000_000, r, PoseidonV1.OwnerPk(PrivacyField.RandomCanonical())));
            Assert.NotEqual(PoseidonV1.NoteHashV2(100_000_000, r, owner), PoseidonV1.NoteHash(100_000_000, r));
            Assert.Throws<ArgumentException>(() => PoseidonV1.OwnerPk(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff }));
            // Key material: nullifier key = viewing key reduced into the field; owner key from it.
            var m = ShieldedHdDerivation.DeriveShieldedKeyMaterial("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40", ShieldedAddressConstants.DefaultBip44CoinType, 3);
            Assert.Equal(PrivacyField.ReduceLe(m.ViewingKey32), m.NullifierKey32);
            Assert.Equal(PoseidonV1.OwnerPk(m.NullifierKey32), m.OwnerPk32);
            // The v2 address carries encryption key and owner key; the v1 encoding of the same keys decodes with no owner key.
            Assert.True(ShieldedAddressCodec.TryDecode(m.ZfxAddress, out var enc, out var owner2, out _));
            Assert.Equal(m.EncryptionPublicKey33, enc);
            Assert.Equal(m.OwnerPk32, owner2);
            var v1 = ShieldedAddressCodec.EncodeEncryptionKey(m.EncryptionPublicKey33);
            Assert.NotEqual(v1, m.ZfxAddress);
            Assert.True(ShieldedAddressCodec.TryDecode(v1, out var enc1, out var owner1, out _));
            Assert.Equal(m.EncryptionPublicKey33, enc1);
            Assert.Null(owner1);
            Assert.False(ShieldedAddressCodec.TryDecodeOwnerKey(v1, out _, out var why));
            Assert.Contains("owner key", why);
            Assert.True(ShieldedAddressCodec.HasOwnerKey(m.ZfxAddress));
            Assert.False(ShieldedAddressCodec.HasOwnerKey(v1));
            Assert.False(ShieldedAddressCodec.TryDecode("zfx_nonsense", out _, out _, out _));
        }

        // ── Fixed-depth tree ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void MerkleZero_ChainIsHash2OfTwoZeros()
        {
            Assert.Equal(PrivacyField.Zero32, PoseidonV1.MerkleZero(0));
            for (var l = 0; l < 32; l++)
                Assert.Equal(PoseidonV1.Hash2(PoseidonV1.MerkleZero(l), PoseidonV1.MerkleZero(l)), PoseidonV1.MerkleZero(l + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => PoseidonV1.MerkleZero(33));
        }

        [Fact]
        public void FixedDepthTree_PathsRecomputeTheRoot_InManagedAndNativeCode()
        {
            var leaves = Enumerable.Range(1, 5).Select(i => PoseidonV1.NoteHash((ulong)i * 1_000_000, PrivacyField.RandomCanonical())).ToList();
            var tree = new FixedDepthMerkleTree(leaves);
            var root = tree.Root();
            for (long pos = 0; pos < leaves.Count; pos++)
            {
                var path = tree.Path(pos);
                Assert.Equal(root, FixedDepthMerkleTree.RootFromPath(leaves[(int)pos], pos, path));
                Assert.Equal(root, PoseidonV1.RootFromPath(leaves[(int)pos], (ulong)pos, FixedDepthMerkleTree.Flatten(path)));
                Assert.Equal(FixedDepthMerkleTree.Flatten(path), tree.PathFlat(pos));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => tree.Path(5));
            // A wrong position or a different leaf gives a different root.
            Assert.NotEqual(root, FixedDepthMerkleTree.RootFromPath(leaves[1], 0, tree.Path(1)));
            Assert.NotEqual(root, FixedDepthMerkleTree.RootFromPath(leaves[0], 1, tree.Path(1)));
        }

        [Fact]
        public void FixedDepthTree_EmptyAndSingleLeafRoots()
        {
            Assert.Equal(PoseidonV1.MerkleZero(32), new FixedDepthMerkleTree(Array.Empty<byte[]>()).Root());
            var leaf = PoseidonV1.NoteHash(5, PrivacyField.RandomCanonical());
            var one = new FixedDepthMerkleTree(new[] { leaf });
            // Leaf 0 with an empty sibling at every level.
            var expected = leaf;
            for (var l = 0; l < 32; l++) expected = PoseidonV1.Hash2(expected, PoseidonV1.MerkleZero(l));
            Assert.Equal(expected, one.Root());
            Assert.All(one.Path(0).Select((p, l) => (p, l)), x => Assert.Equal(PoseidonV1.MerkleZero(x.l), x.p));
        }

        [Fact]
        public void Indices_AreThePositionBitsAsLittleEndianFieldElements()
        {
            var idx = FixedDepthMerkleTree.IndicesFlat(5); // 101b
            Assert.Equal(1, idx[0 * 32]);
            Assert.Equal(0, idx[1 * 32]);
            Assert.Equal(1, idx[2 * 32]);
            Assert.All(Enumerable.Range(3, 29), l => Assert.Equal(0, idx[l * 32]));
            Assert.All(Enumerable.Range(0, 32), l => Assert.All(Enumerable.Range(1, 31), k => Assert.Equal(0, idx[l * 32 + k])));
        }

        // ── Dummy note ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void DummyNote_ConstantsAreWhatTheyClaim()
        {
            Assert.Equal(PoseidonV1.OwnerPk(PrivacyField.Zero32), PrivacyEpoch.DummyOwnerPk);
            Assert.Equal(PoseidonV1.NoteHashV2(0, PrivacyField.Zero32, PrivacyEpoch.DummyOwnerPk), PrivacyEpoch.DummyNoteHash);
            Assert.Equal(PoseidonV1.Hash3(PrivacyField.Zero32, PrivacyEpoch.DummyNoteHash, PrivacyField.Zero32), PrivacyEpoch.DummyNullifier);
            Assert.True(PrivacyEpoch.IsDummyNullifier(PrivacyEpoch.DummyNullifierB64));
            Assert.False(PrivacyEpoch.IsDummyNullifier(Convert.ToBase64String(PoseidonV1.Nullifier(PrivacyField.RandomCanonical(), PrivacyEpoch.DummyNoteHash, 0))));
            Assert.False(PrivacyEpoch.IsDummyNullifier(null));
            Assert.Equal(PlonkNative.G1CompressedSize, PrivacyEpoch.DummyCommitment.Length);
            Assert.Equal(1, PlonkNative.pedersen_verify(PrivacyEpoch.DummyCommitment, 0, PrivacyField.Zero32)); // 1 = valid, as the Pedersen round-trip test pins
        }

        [Fact]
        public void Epoch_HeightSemantics()
        {
            var prior = Globals.PrivateTxProofRulesHeight;
            try
            {
                Globals.PrivateTxProofRulesHeight = 1000;
                Assert.False(PrivacyEpoch.ProofRulesActive(999));
                Assert.True(PrivacyEpoch.ProofRulesActive(1000));
                Assert.True(PrivacyEpoch.IsResetBlock(1000));
                Assert.False(PrivacyEpoch.IsResetBlock(1001));
            }
            finally { Globals.PrivateTxProofRulesHeight = prior; }
        }
    }
}
