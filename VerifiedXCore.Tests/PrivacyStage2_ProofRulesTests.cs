using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1, stage 2: from PrivateTxProofRulesHeight a private transaction must have the circuits'
    /// shape and carry a proof; a node that cannot verify refuses it; the reset block refuses everything private; the
    /// public-input blob matches the native library's version-2 layout byte for byte. Below the height nothing changes.
    /// Real proof verification (prove on one side, verify from the reconstructed blob on the other) is in the
    /// round-trip tests that need the params file.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PrivacyStage2_ProofRulesTests : IDisposable
    {
        private const long Height = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight;
        private readonly long _priorSupplyGate;
        private readonly Func<bool> _priorAvailable;
        private readonly Dictionary<string, decimal> _priorCorrections;
        private readonly byte[] _root = new FixedDepthMerkleTree(new[] { PrivacyEpoch.DummyNoteHash }).Root();

        public PrivacyStage2_ProofRulesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ps2p_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorHeight = Globals.PrivateTxProofRulesHeight;
            _priorSupplyGate = Globals.PrivateTxSupplyRulesHeight;
            _priorAvailable = PlonkProofVerifier.VerifierAvailable;
            _priorCorrections = new(Globals.ShieldedSupplyCorrections, StringComparer.Ordinal);
            Globals.ShieldedSupplyCorrections.Clear();
            Globals.PrivateTxProofRulesHeight = Height;
            Globals.PrivateTxSupplyRulesHeight = 1;
            DbContext.Initialize();
            var pool = ShieldedPoolService.GetOrCreateState("VFX");
            pool.TotalShieldedSupply = 50M;
            pool.CurrentMerkleRoot = Convert.ToBase64String(_root);
            PrivacyDbContext.PoolState().Update(pool);
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            PlonkProofVerifier.VerifierAvailable = _priorAvailable;
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxProofRulesHeight = _priorHeight;
            Globals.PrivateTxSupplyRulesHeight = _priorSupplyGate;
            Globals.ShieldedSupplyCorrections.Clear();
            foreach (var (k, v) in _priorCorrections) Globals.ShieldedSupplyCorrections[k] = v;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string NewAddress()
        {
            var key = new PrivateKey("secp256k1");
            return AccountData.GetHumanAddress("04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
        }

        private static PrivateShieldedOutput Output(int index, ulong amount, bool withNoteHash = true)
        {
            var r = PrivacyField.RandomCanonical();
            var g1 = new byte[PlonkNative.G1CompressedSize];
            PlonkNative.pedersen_commit(amount, r, g1);
            return new PrivateShieldedOutput { Index = index, CommitmentB64 = Convert.ToBase64String(g1), NoteHashB64 = withNoteHash ? Convert.ToBase64String(PoseidonV1.NoteHash(amount, r)) : null };
        }

        private static byte[] RealNullifier() => PoseidonV1.Nullifier(PrivacyField.RandomCanonical(), PoseidonV1.NoteHash(5, PrivacyField.RandomCanonical()), 1);

        private Transaction Unshield(decimal amount, IEnumerable<byte[]>? nullifiers = null, IEnumerable<PrivateShieldedOutput>? outs = null, string? proof = null)
        {
            var nulls = (nullifiers ?? new[] { RealNullifier(), PrivacyEpoch.DummyNullifier }).Select(Convert.ToBase64String).ToList();
            var payload = new PrivateTxPayload
            {
                Version = 1, Kind = "unshield", Asset = "VFX",
                NullsB64 = nulls, SpentCommitmentTreePositions = nulls.Select((_, i) => (long)(i == 0 ? 1 : 0)).ToList(),
                MerkleRootB64 = Convert.ToBase64String(_root), Fee = Globals.PrivateTxFixedFee, ProofB64 = proof,
            };
            foreach (var o in outs ?? new[] { Output(0, 0) }) payload.Outs.Add(o);
            var to = NewAddress();
            payload.TransparentOutput = to;
            payload.TransparentAmount = amount;
            var tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = to, Amount = amount, Fee = 0M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VFX_UNSHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload), Signature = PrivacyConstants.PlonkSignatureSentinel,
            };
            tx.BuildPrivate();
            return tx;
        }

        private Transaction Shield(int outputs = 1)
        {
            var payload = new PrivateTxPayload { Version = 1, Kind = "shield", Asset = "VFX", TransparentAmount = 1M, TransparentInput = "RSHIELDER" };
            for (var i = 0; i < outputs; i++) payload.Outs.Add(Output(i, 100_000_000));
            var tx = new Transaction
            {
                FromAddress = "RSHIELDER", ToAddress = PrivacyConstants.ShieldedPoolAddress, Amount = 1M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VFX_SHIELD, Data = PrivateTxPayloadCodec.SerializeToJson(payload),
            };
            tx.BuildPrivate();
            return tx;
        }

        private static void TipBelow() => Globals.LastBlock = new Block { Height = Height - 2 }; // next block 999
        private static void TipInEpoch() => Globals.LastBlock = new Block { Height = Height };    // next block 1001

        // ── Public inputs v2 layout ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void PublicInputsV2_LayoutMatchesTheNativeLibrary()
        {
            Assert.Equal(112, PlonkPublicInputsV2.TotalLength(PlonkCircuitType.Shield));
            Assert.Equal(184, PlonkPublicInputsV2.TotalLength(PlonkCircuitType.Unshield));
            Assert.Equal(208, PlonkPublicInputsV2.TotalLength(PlonkCircuitType.Transfer));
            Assert.Equal(144, PlonkPublicInputsV2.TotalLength(PlonkCircuitType.Fee));

            var n0 = RealNullifier();
            var change = Output(0, 123);
            var tx = Unshield(4M, new[] { n0, PrivacyEpoch.DummyNullifier }, new[] { change });
            Assert.True(PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _));
            Assert.True(PlonkPublicInputsV2.TryBuild(tx, payload!, out var blob, out var err), err);
            Assert.Equal(184, blob.Length);
            Assert.Equal("VFXPI1", System.Text.Encoding.ASCII.GetString(blob, 0, 6));
            Assert.Equal(2, blob[6]);
            Assert.Equal((byte)PlonkCircuitType.Unshield, blob[7]);
            Assert.Equal(PlonkPublicInputsV2.AssetTag32("VFX"), blob[8..40]);
            Assert.Equal(_root, blob[40..72]);
            Assert.Equal(300UL, BitConverter.ToUInt64(blob, 72));          // fee 0.000003 scaled
            Assert.Equal(400_000_000UL, BitConverter.ToUInt64(blob, 80)); // transparent 4 VFX scaled
            Assert.Equal(n0, blob[88..120]);
            Assert.Equal(PrivacyEpoch.DummyNullifier, blob[120..152]);
            Assert.Equal(Convert.FromBase64String(change.NoteHashB64!), blob[152..184]);

            var shield = Shield();
            PrivateTxPayloadCodec.TryDecode(shield.Data, out var sp, out _);
            Assert.True(PlonkPublicInputsV2.TryBuild(shield, sp!, out var sblob, out err), err);
            Assert.Equal(112, sblob.Length);
            Assert.Equal(100_000_000UL, BitConverter.ToUInt64(sblob, 72));
            Assert.Equal(Convert.FromBase64String(sp!.Outs[0].NoteHashB64!), sblob[80..112]);

            // A transfer with two outputs.
            var tp = new PrivateTxPayload { Asset = "VFX", MerkleRootB64 = Convert.ToBase64String(_root), Fee = 0.000003M, NullsB64 = { Convert.ToBase64String(n0), PrivacyEpoch.DummyNullifierB64 }, SpentCommitmentTreePositions = { 1, 0 }, Outs = { Output(0, 1), Output(1, 2) } };
            var ttx = new Transaction { TransactionType = TransactionType.VFX_PRIVATE_TRANSFER, Data = PrivateTxPayloadCodec.SerializeToJson(tp) };
            Assert.True(PlonkPublicInputsV2.TryBuild(ttx, tp, out var tblob, out err), err);
            Assert.Equal(208, tblob.Length);
            Assert.Equal(Convert.FromBase64String(tp.Outs[1].NoteHashB64!), tblob[176..208]);

            // What the circuits bind must be present.
            var noHash = Unshield(1M, outs: new[] { Output(0, 0, withNoteHash: false) });
            PrivateTxPayloadCodec.TryDecode(noHash.Data, out var np, out _);
            Assert.False(PlonkPublicInputsV2.TryBuild(noHash, np!, out _, out err));
            Assert.Contains("note_hash", err);
            var oneNull = Unshield(1M, new[] { n0 });
            PrivateTxPayloadCodec.TryDecode(oneNull.Data, out var op, out _);
            Assert.False(PlonkPublicInputsV2.TryBuild(oneNull, op!, out _, out err));
            Assert.Contains("exactly two notes", err);
        }

        // ── Reset block and the height ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheResetBlock_RefusesEveryPrivateTransaction()
        {
            Globals.LastBlock = new Block { Height = Height - 1 }; // next block = the reset block
            var (okShield, msgShield) = await TransactionValidatorService.VerifyTX(Shield());
            Assert.False(okShield);
            Assert.Equal(PrivateTxProofRules.ResetBlockReason, msgShield);
            var (okUnshield, msgUnshield) = await TransactionValidatorService.VerifyTX(Unshield(1M));
            Assert.False(okUnshield);
            Assert.Equal(PrivateTxProofRules.ResetBlockReason, msgUnshield);
            // Block path at the reset height, whatever the tip.
            Globals.LastBlock = new Block { Height = 10 };
            var (okBlock, msgBlock) = await TransactionValidatorService.VerifyTX(Unshield(1M), blockVerify: true, blockHeight: Height);
            Assert.False(okBlock);
            Assert.Equal(PrivateTxProofRules.ResetBlockReason, msgBlock);
        }

        [Fact]
        public async Task BelowTheHeight_AProoflessUnshieldStillPasses_AndTheShapeRulesAreInert()
        {
            TipBelow();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(1M, new[] { RealNullifier() }, Array.Empty<PrivateShieldedOutput>()));
            Assert.True(ok, msg); // stage 1 only: one note, no change output, no proof
            Assert.Null(PrivateTxProofRules.Check(Unshield(1M, new[] { RealNullifier() }, Array.Empty<PrivateShieldedOutput>()), new PrivateTxPayload { Asset = "VFX" }, Height - 1));
        }

        // ── In the epoch ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task InTheEpoch_AProofIsRequired()
        {
            TipInEpoch();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(1M));
            Assert.False(ok);
            Assert.Contains("must carry a PLONK proof", msg);
            // The shield circuit binds the amount to the note hash, so a shield needs its proof too (checked at the verifier,
            // which sits after the shield's transparent-side checks in VerifyTX).
            var shield = Shield();
            PrivateTxPayloadCodec.TryDecode(shield.Data, out var sp, out _);
            var (okShield, msgShield) = PlonkProofVerifier.TryValidatePrivateProofs(shield, sp!, false, Height + 1);
            Assert.False(okShield);
            Assert.Contains("must carry a PLONK proof", msgShield);
            // Below the height the same shield needs none.
            Assert.True(PlonkProofVerifier.TryValidatePrivateProofs(shield, sp!, false, Height - 1).ok);
        }

        [Fact]
        public async Task InTheEpoch_ANodeThatCannotVerify_FailsClosed()
        {
            TipInEpoch();
            PlonkProofVerifier.VerifierAvailable = () => false;
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(1M, proof: Convert.ToBase64String(new byte[64])));
            Assert.False(ok);
            Assert.Equal(PlonkProofVerifier.VerifierUnavailableReason, msg);
        }

        [Fact]
        public async Task InTheEpoch_AGarbageProofIsRefused_WhateverTheVerifierSays()
        {
            TipInEpoch();
            PlonkProofVerifier.VerifierAvailable = () => true; // the native call then answers Invalid, NotImplemented or an error: all refusals
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(1M, proof: Convert.ToBase64String(new byte[64])));
            Assert.False(ok);
            Assert.Contains("PLONK", msg);
        }

        [Fact]
        public void InTheEpoch_TheShapeRulesHold()
        {
            string? Check(Transaction tx)
            {
                PrivateTxPayloadCodec.TryDecode(tx.Data, out var p, out _);
                return PrivateTxProofRules.Check(tx, p!, Height + 1);
            }
            Assert.Null(Check(Unshield(1M)));
            Assert.Contains("exactly two notes", Check(Unshield(1M, new[] { RealNullifier() })));
            Assert.Contains("only once", Check(Unshield(1M, new[] { PrivacyEpoch.DummyNullifier, PrivacyEpoch.DummyNullifier })));
            Assert.Contains("note_hash", Check(Unshield(1M, outs: new[] { Output(0, 0, withNoteHash: false) })));
            Assert.Contains("exactly one change output", Check(Unshield(1M, outs: Array.Empty<PrivateShieldedOutput>())));
            Assert.Contains("exactly one output", Check(Shield(outputs: 2)));
            var transfer = new Transaction { TransactionType = TransactionType.VFX_PRIVATE_TRANSFER, Data = PrivateTxPayloadCodec.SerializeToJson(new PrivateTxPayload { Asset = "VFX", NullsB64 = { Convert.ToBase64String(RealNullifier()), PrivacyEpoch.DummyNullifierB64 }, SpentCommitmentTreePositions = { 1, 0 }, Outs = { Output(0, 1) } }) };
            Assert.Contains("exactly two outputs", Check(transfer));
        }

        // ── Mempool and block-scoped nullifier sets ───────────────────────────────────────────────────────────

        [Fact]
        public void NullifierTrackers_IgnoreTheDummy_AndStillCatchRealDoubleSpends()
        {
            var real = Convert.ToBase64String(RealNullifier());
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("tx-a", "VFX", new[] { real, PrivacyEpoch.DummyNullifierB64 }, out var e1), e1);
            Assert.True(MempoolNullifierTracker.TryRegisterForMempool("tx-b", "VFX", new[] { Convert.ToBase64String(RealNullifier()), PrivacyEpoch.DummyNullifierB64 }, out var e2), e2);
            Assert.False(MempoolNullifierTracker.TryRegisterForMempool("tx-c", "VFX", new[] { real, PrivacyEpoch.DummyNullifierB64 }, out var e3));
            Assert.Contains("already spends", e3);
            MempoolNullifierTracker.ReleaseClaimsForTxHash("tx-a");
            MempoolNullifierTracker.ReleaseClaimsForTxHash("tx-b");

            var set = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(1M, new[] { RealNullifier(), PrivacyEpoch.DummyNullifier }), set, out _));
            Assert.True(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(1M, new[] { RealNullifier(), PrivacyEpoch.DummyNullifier }), set, out _));
            var same = RealNullifier();
            Assert.True(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(1M, new[] { same, PrivacyEpoch.DummyNullifier }), set, out _));
            Assert.False(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(1M, new[] { same, PrivacyEpoch.DummyNullifier }), set, out var dup));
            Assert.Contains("Duplicate nullifier", dup);
        }
    }
}
