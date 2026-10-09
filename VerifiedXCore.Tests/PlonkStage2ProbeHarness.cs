using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1, stage 2 scoping probe. Inert unless FUNDLOSS_PLONK_PROBE names the VXPLNK03 params file.
    /// Loads the real params into the native library and reports, to FUNDLOSS_SCAN_OUT/plonk-probe.txt:
    ///  1. the capability bits;
    ///  2. a Shield round trip: prove, verify, and whether the prover's public inputs equal the VFXPI1 blob validators
    ///     reconstruct from a transaction;
    ///  3. an Unshield round trip with a witness built from the wallet's own Merkle tree (CommitmentMerkleTree) and
    ///     note data, with the dynamic-depth path zero-padded to the circuit's 32 levels: whether it proves, whether
    ///     it verifies, and whether the root the circuit computed equals the wallet tree's root.
    /// </summary>
    public class PlonkStage2ProbeHarness
    {
        /// <summary>32 random bytes that are below the BLS12-381 scalar modulus as a big-endian field element.</summary>
        private static byte[] FieldRandom()
        {
            var r = RandomNumberGenerator.GetBytes(32);
            r[31] &= 0x0f; // arkworks canonical Fr is little-endian: the LAST byte is the top byte; 0x0f keeps it below the modulus (0x73ed...)
            return r;
        }

        [Fact]
        public void Probe()
        {
            var paramsPath = Environment.GetEnvironmentVariable("FUNDLOSS_PLONK_PROBE");
            if (string.IsNullOrWhiteSpace(paramsPath)) return;
            var outDir = Environment.GetEnvironmentVariable("FUNDLOSS_SCAN_OUT") ?? Path.GetDirectoryName(paramsPath)!;
            var lines = new List<string>();
            void L(string s) => lines.Add(s);
            try
            {
                L($"load params: {PLONKSetup.TryLoadParamsFile(paramsPath)}");
                PLONKSetup.RefreshVerificationCapability();
                var caps = PlonkNative.plonk_capabilities();
                L($"caps={caps} verifyImplemented={PLONKSetup.IsProofVerificationImplemented} v1Circuits={PLONKSetup.IsV1CircuitsLoaded} v1Prove={PLONKSetup.IsV1ProvingAvailable}");

                // ── 2. Shield ─────────────────────────────────────────────────────────────────────────────────
                ulong amount = 100_000_000; // 1 VFX
                var raw = RandomNumberGenerator.GetBytes(32);
                var g1raw = new byte[PlonkNative.G1CompressedSize];
                L($"pedersen_commit(raw random): {PlonkNative.pedersen_commit(amount, raw, g1raw)}");
                var rand = FieldRandom();
                var g1f = new byte[PlonkNative.G1CompressedSize];
                L($"pedersen_commit(field random): {PlonkNative.pedersen_commit(amount, rand, g1f)}");
                L($"note hash (NoteHashService, falls back to poseidon_hash when poseidon_note_hash is absent): {Convert.ToHexString(NoteHashService.Compute(amount, rand))}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var code = PlonkProverV1.TryProveShield(amount, rand, out var proof, out var pi);
                L($"shield prove: code={code} proofLen={proof?.Length} piLen={pi?.Length} in {sw.ElapsedMilliseconds} ms");
                if (code == PlonkNative.Success && proof != null && pi != null)
                {
                    var vr = PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, pi);
                    L($"shield verify(prover PI): {vr}");
                    L($"shield prover PI hex: {Convert.ToHexString(pi)}");
                    // What validators reconstruct for a matching transaction.
                    var g1 = new byte[PlonkNative.G1CompressedSize];
                    var pc = PlonkNative.pedersen_commit(amount, rand, g1);
                    var noteHash = NoteHashService.Compute(amount, rand);
                    var payload = new PrivateTxPayload { Asset = "VFX", MerkleRootB64 = Convert.ToBase64String(new byte[32]), Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1), NoteHashB64 = Convert.ToBase64String(noteHash) } } };
                    var tx = new Transaction { TransactionType = TransactionType.VFX_SHIELD, Amount = 1M };
                    if (PlonkPublicInputsV1.TryBuild(tx, payload, out var rebuilt, out var err))
                    {
                        L($"shield VFXPI1 hex:    {Convert.ToHexString(rebuilt)}");
                        L($"shield PI equal to VFXPI1: {rebuilt.AsSpan().SequenceEqual(pi)} (pedersen code {pc}, noteHash {Convert.ToHexString(noteHash)})");
                        L($"shield verify(VFXPI1): {PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, rebuilt)}");
                    }
                    else L($"shield VFXPI1 build failed: {err}");
                    // Tamper: a different amount must not verify.
                    var bad = (byte[])pi.Clone();
                    bad[^1] ^= 0x01;
                    L($"shield verify(tampered PI): {PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, bad)}");
                }

                // ── 3. Unshield with the wallet's tree ────────────────────────────────────────────────────────
                var vk = FieldRandom();
                var notes = new List<(ulong Amount, byte[] Rand)> { (300_000_000, FieldRandom()), (200_000_000, FieldRandom()), (50_000_000, FieldRandom()) };
                var leaves = notes.Select(n => CommitmentMerkleTree.LeafDigest(NoteHashService.Compute(n.Amount, n.Rand))).ToList();
                // Variant A: the wallet's dynamic-depth tree, path zero-padded to 32 levels. Variant B: a fixed-depth-32
                // tree where every missing node is the "zero" subtree (Poseidon of two zero children, recursively).
                var zero = new byte[PlonkNative.TreeDepth + 1][];
                zero[0] = new byte[32];
                for (var l = 1; l <= PlonkNative.TreeDepth; l++) zero[l] = CommitmentMerkleTree.Combine(zero[l - 1], zero[l - 1]);
                (byte[] Path, byte[] Root) FixedDepthProof(int idx)
                {
                    var path = new byte[PlonkNative.ScalarSize * PlonkNative.TreeDepth];
                    var level = leaves.Select(x => (byte[])x.Clone()).ToList();
                    var cur = idx;
                    for (var l = 0; l < PlonkNative.TreeDepth; l++)
                    {
                        var sib = (cur ^ 1) < level.Count ? level[cur ^ 1] : zero[l];
                        Buffer.BlockCopy(sib, 0, path, l * 32, 32);
                        var next = new List<byte[]>();
                        for (var i = 0; i < level.Count; i += 2)
                            next.Add(CommitmentMerkleTree.Combine(level[i], i + 1 < level.Count ? level[i + 1] : zero[l]));
                        level = next; cur >>= 1;
                    }
                    return (path, level[0]);
                }
                var spend = new[] { 0, 1 };
                foreach (var variant in new[] { "A-dynamic", "B-fixed32" })
                {
                    var inputs = new PlonkProverV1.TransferInputWitness[2];
                    byte[]? treeRoot = null;
                    for (var k = 0; k < 2; k++)
                    {
                        var idx = spend[k];
                        byte[] padded; byte[] root;
                        if (variant == "A-dynamic")
                        {
                            Assert.True(CommitmentMerkleTree.TryBuildProof(leaves, idx, out var path));
                            Assert.True(CommitmentMerkleTree.TryComputeRootFromProof(leaves[idx], idx, leaves.Count, path, out var r));
                            root = r!; padded = new byte[PlonkNative.ScalarSize * PlonkNative.TreeDepth];
                            Buffer.BlockCopy(path!, 0, padded, 0, path!.Length);
                        }
                        else { (padded, root) = FixedDepthProof(idx); }
                        treeRoot ??= root;
                        var indices = new byte[PlonkNative.ScalarSize * PlonkNative.TreeDepth];
                        for (var lvl = 0; lvl < PlonkNative.TreeDepth; lvl++)
                            indices[lvl * PlonkNative.ScalarSize] = (byte)((idx >> lvl) & 1); // LE field element: low byte first
                        inputs[k] = new PlonkProverV1.TransferInputWitness { AmountScaled = notes[idx].Amount, Randomness32 = notes[idx].Rand, ViewingKey32 = vk, TreePosition = (ulong)idx, MerklePath = padded, MerkleIndices = indices };
                    }
                    L($"[{variant}] root={Convert.ToHexString(treeRoot!)}");
                ulong fee = 300; // 0.000003 VFX scaled
                ulong transparent = 400_000_000;
                ulong change = 300_000_000 + 200_000_000 - transparent - fee;
                var changeRand = FieldRandom();
                sw.Restart();
                code = PlonkProverV1.TryProveUnshield(inputs, transparent, change, changeRand, fee, treeRoot!, out proof, out pi);
                L($"[{variant}] unshield prove: code={code} proofLen={proof?.Length} piLen={pi?.Length} in {sw.ElapsedMilliseconds} ms");
                if (code == PlonkNative.Success && proof != null && pi != null)
                {
                    L($"unshield verify(prover PI): {PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi)}");
                    L($"unshield prover PI hex: {Convert.ToHexString(pi)}");
                    L($"wallet tree root:       {Convert.ToHexString(treeRoot!)}");
                    var n0 = NullifierService.DeriveFromNoteHash(vk, NoteHashService.Compute(notes[0].Amount, notes[0].Rand), 0);
                    var n1 = NullifierService.DeriveFromNoteHash(vk, NoteHashService.Compute(notes[1].Amount, notes[1].Rand), 1);
                    var changeG1 = new byte[PlonkNative.G1CompressedSize];
                    PlonkNative.pedersen_commit(change, changeRand, changeG1);
                    var payload = new PrivateTxPayload
                    {
                        Asset = "VFX", MerkleRootB64 = Convert.ToBase64String(treeRoot!), Fee = 0.000003M,
                        NullsB64 = { Convert.ToBase64String(n0), Convert.ToBase64String(n1) }, SpentCommitmentTreePositions = { 0, 1 },
                        Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(changeG1) } },
                    };
                    var tx = new Transaction { TransactionType = TransactionType.VFX_UNSHIELD, Amount = 4M };
                    if (PlonkPublicInputsV1.TryBuild(tx, payload, out var rebuilt, out var err))
                    {
                        L($"unshield VFXPI1 hex:    {Convert.ToHexString(rebuilt)}");
                        L($"unshield PI equal to VFXPI1: {rebuilt.AsSpan().SequenceEqual(pi)}");
                        L($"unshield verify(VFXPI1): {PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, rebuilt)}");
                        L($"nullifier0={Convert.ToHexString(n0)} nullifier1={Convert.ToHexString(n1)}");
                    }
                    else L($"unshield VFXPI1 build failed: {err}");
                }
                } // variant
            }
            catch (Exception ex)
            {
                L($"EXCEPTION: {ex}");
            }
            File.WriteAllLines(Path.Combine(outDir, "plonk-probe.txt"), lines);
        }
    }
}
