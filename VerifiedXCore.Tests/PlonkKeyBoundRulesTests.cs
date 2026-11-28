using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    /// Globals.PlonkKeyBoundRulesHeight: from it a block's private proofs are judged under the key-bound rules
    /// (plonk_verify3), whose transcript binds the whole verifier key; below it under the current rules (plonk_verify2) as
    /// before. The wallet proves under the rules of the block it would mine into. A proof accepted under one set of rules
    /// is never taken from the cache for another, and a node whose library lacks the key-bound rules fails closed from the
    /// height.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PlonkKeyBoundRulesTests : IDisposable
    {
        private const long EpochHeight = 1000;
        private const long CurrentGate = 2000;
        private const long KeyBoundGate = 3000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorEpoch, _priorCurrentGate, _priorKeyBoundGate, _priorSupplyGate;
        private readonly Func<bool> _priorAvailable, _priorCurrentAvailable, _priorKeyBoundAvailable;
        private readonly Func<byte, byte[], byte[], PlonkRules, int> _priorNative;
        private readonly byte[] _root = new FixedDepthMerkleTree(new[] { PrivacyEpoch.DummyNoteHash }).Root();
        private readonly List<PlonkRules> _rules = new();

        public PlonkKeyBoundRulesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pkb_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorEpoch = Globals.PrivateTxProofRulesHeight;
            _priorCurrentGate = Globals.PlonkCurrentRulesHeight;
            _priorKeyBoundGate = Globals.PlonkKeyBoundRulesHeight;
            _priorSupplyGate = Globals.PrivateTxSupplyRulesHeight;
            _priorAvailable = PlonkProofVerifier.VerifierAvailable;
            _priorCurrentAvailable = PlonkProofVerifier.CurrentRulesAvailable;
            _priorKeyBoundAvailable = PlonkProofVerifier.KeyBoundRulesAvailable;
            _priorNative = PlonkProofVerifier.NativeVerify;
            Globals.PrivateTxProofRulesHeight = EpochHeight;
            Globals.PlonkCurrentRulesHeight = CurrentGate;
            Globals.PlonkKeyBoundRulesHeight = KeyBoundGate;
            Globals.PrivateTxSupplyRulesHeight = 1;
            PlonkProofVerifier.ClearVerifiedProofCache();
            PlonkProofVerifier.VerifierAvailable = () => true;
            PlonkProofVerifier.CurrentRulesAvailable = () => true;
            PlonkProofVerifier.KeyBoundRulesAvailable = () => true;
            PlonkProofVerifier.NativeVerify = (_, _, _, rules) => { _rules.Add(rules); return 0; };
            DbContext.Initialize();
            var pool = ShieldedPoolService.GetOrCreateState("VFX");
            pool.TotalShieldedSupply = 50M;
            pool.CurrentMerkleRoot = Convert.ToBase64String(_root);
            PrivacyDbContext.PoolState().Update(pool);
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            PlonkProofVerifier.NativeVerify = _priorNative;
            PlonkProofVerifier.VerifierAvailable = _priorAvailable;
            PlonkProofVerifier.CurrentRulesAvailable = _priorCurrentAvailable;
            PlonkProofVerifier.KeyBoundRulesAvailable = _priorKeyBoundAvailable;
            PlonkProofVerifier.ClearVerifiedProofCache();
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxProofRulesHeight = _priorEpoch;
            Globals.PlonkCurrentRulesHeight = _priorCurrentGate;
            Globals.PlonkKeyBoundRulesHeight = _priorKeyBoundGate;
            Globals.PrivateTxSupplyRulesHeight = _priorSupplyGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string NewAddress()
        {
            var key = new PrivateKey("secp256k1");
            return AccountData.GetHumanAddress("04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
        }

        private static PrivateShieldedOutput Output(int index, ulong amount)
        {
            var r = PrivacyField.RandomCanonical();
            var g1 = new byte[PlonkNative.G1CompressedSize];
            PlonkNative.pedersen_commit(amount, r, g1);
            return new PrivateShieldedOutput { Index = index, CommitmentB64 = Convert.ToBase64String(g1), NoteHashB64 = Convert.ToBase64String(PoseidonV1.NoteHash(amount, r)) };
        }

        private Transaction UnshieldWithProof()
        {
            var realNullifier = PoseidonV1.Nullifier(PrivacyField.RandomCanonical(), PoseidonV1.NoteHash(5, PrivacyField.RandomCanonical()), 1);
            var nulls = new[] { realNullifier, PrivacyEpoch.DummyNullifier }.Select(Convert.ToBase64String).ToList();
            var payload = new PrivateTxPayload
            {
                Version = 1, Kind = "unshield", Asset = "VFX",
                NullsB64 = nulls, SpentCommitmentTreePositions = new List<long> { 1, 0 },
                MerkleRootB64 = Convert.ToBase64String(_root), Fee = Globals.PrivateTxFixedFee, ProofB64 = Convert.ToBase64String(new byte[64]),
            };
            payload.Outs.Add(Output(0, 0));
            var to = NewAddress();
            payload.TransparentOutput = to;
            payload.TransparentAmount = 1M;
            var tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = to, Amount = 1M, Fee = 0M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VFX_UNSHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload), Signature = PrivacyConstants.PlonkSignatureSentinel,
            };
            tx.BuildPrivate();
            return tx;
        }

        [Fact]
        public async Task ABlock_HasItsProofsJudgedUnderTheRulesOfItsHeight()
        {
            Globals.LastBlock = new Block { Height = KeyBoundGate + 10 };
            var tx = UnshieldWithProof();
            foreach (var (height, rules) in new[] { (CurrentGate - 1, PlonkRules.Original), (KeyBoundGate - 1, PlonkRules.Current), (KeyBoundGate, PlonkRules.KeyBound) })
            {
                _rules.Clear();
                var (ok, why) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: height);
                Assert.False(ok);
                Assert.Contains(PlonkProofVerifier.FailedProofPrefix, why);
                Assert.Equal(new[] { rules }, _rules);
            }
        }

        [Fact]
        public async Task Admission_JudgesAProofUnderTheRulesOfTheBlockItWouldMineInto()
        {
            var tx = UnshieldWithProof();
            Globals.LastBlock = new Block { Height = KeyBoundGate - 2 };
            await TransactionValidatorService.VerifyTX(tx);
            Assert.Equal(new[] { PlonkRules.Current }, _rules);

            // At the last block before the height a proof is admitted only if it passes the key-bound rules: one made
            // under the current rules stays out of the pool, so no producer proposes it into a block that refuses it.
            _rules.Clear();
            Globals.LastBlock = new Block { Height = KeyBoundGate - 1 };
            var (admitted, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admitted);
            Assert.Contains(PlonkProofVerifier.FailedProofPrefix, why);
            Assert.Equal(new[] { PlonkRules.KeyBound }, _rules);
        }

        [Fact]
        public void AProofAcceptedUnderTheCurrentRules_IsNotTakenFromTheCacheForTheKeyBoundOnes()
        {
            PlonkProofVerifier.NativeVerify = (_, _, _, rules) => { _rules.Add(rules); return rules == PlonkRules.Current ? 1 : 0; };
            var proof = new byte[] { 1, 2, 3 };
            var pi = new byte[] { 4, 5 };
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi, PlonkRules.Current));
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi, PlonkRules.KeyBound));
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi, PlonkRules.Original));
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi, PlonkRules.Current));
            Assert.Equal(new[] { PlonkRules.Current, PlonkRules.KeyBound, PlonkRules.Original }, _rules); // the last call is the cached answer
        }

        [Fact]
        public async Task FromTheHeight_ANodeWhoseLibraryLacksTheKeyBoundRules_FailsClosed()
        {
            PlonkProofVerifier.KeyBoundRulesAvailable = () => false;
            Globals.LastBlock = new Block { Height = KeyBoundGate + 10 };
            var tx = UnshieldWithProof();
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: KeyBoundGate);
            Assert.False(ok);
            Assert.Equal(PlonkProofVerifier.VerifierUnavailableReason, why);
            Assert.Empty(_rules);
            Assert.False(Globals.ProofVerifierReady);

            // Below the height such a node still judges blocks as before.
            Globals.LastBlock = new Block { Height = KeyBoundGate - 10 };
            Assert.True(Globals.ProofVerifierReady);
            var (below, whyBelow) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: KeyBoundGate - 1);
            Assert.False(below);
            Assert.Contains(PlonkProofVerifier.FailedProofPrefix, whyBelow);
            Assert.Equal(new[] { PlonkRules.Current }, _rules);
        }

        /// <summary>
        /// With the real library and the VXPLNK06 params (VFX_PLONK_PARAMS): the wallet's prover makes key-bound proofs from
        /// the block before the height, which pass the key-bound rules only, and current-rules proofs before that, which
        /// pass the current rules only; the wallet's own check before broadcasting judges each under the rules it was made
        /// under. Without the file it returns early.
        /// </summary>
        [Fact]
        public void TheWalletProvesUnderTheRulesOfTheNextBlock()
        {
            var path = Environment.GetEnvironmentVariable("VFX_PLONK_PARAMS");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.WriteLine("PlonkKeyBoundRulesTests: no VXPLNK06 params file (VFX_PLONK_PARAMS); skipped.");
                return;
            }
            Assert.True(PLONKSetup.TryLoadParamsFile(path), "the supplied params file must load");
            Assert.True(PrivateTxProverV1.CanProve, "the supplied params must carry prover keys");
            PlonkProofVerifier.NativeVerify = _priorNative;
            PlonkProofVerifier.CurrentRulesAvailable = _priorCurrentAvailable;
            PlonkProofVerifier.KeyBoundRulesAvailable = _priorKeyBoundAvailable;
            Assert.True(PlonkProofVerifier.KeyBoundRulesAvailable());

            foreach (var (tip, made, other) in new[] { (KeyBoundGate - 1, PlonkRules.KeyBound, PlonkRules.Current), (KeyBoundGate - 2, PlonkRules.Current, PlonkRules.KeyBound) })
            {
                Globals.LastBlock = new Block { Height = tip };
                Assert.Equal(made, PlonkProverV1.Rules);
                Assert.Equal(PlonkNative.Success, PlonkProverV1.TryProveShield(500_000_000UL, PrivacyField.FromUInt64(7), PrivacyField.FromUInt64(11), out var proof, out var pi));
                Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof!, pi!, made));
                Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof!, pi!, other));
                Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof!, pi!, PlonkRules.Original));
            }

            // The native calls directly: a key-bound proof passes plonk_verify3 and neither earlier verifier.
            var p3 = new byte[65536]; nuint p3Len = (nuint)p3.Length;
            var pi3 = new byte[65536]; nuint pi3Len = (nuint)pi3.Length;
            Assert.Equal(PlonkNative.Success, PlonkNative.plonk_prove_shield3(500_000_000UL, PrivacyField.FromUInt64(7), PrivacyField.FromUInt64(11), p3, ref p3Len, pi3, ref pi3Len));
            p3 = p3.Take((int)p3Len).ToArray();
            pi3 = pi3.Take((int)pi3Len).ToArray();
            Assert.Equal(1, PlonkNative.plonk_verify3(1, p3, (nuint)p3.Length, pi3, (nuint)pi3.Length));
            Assert.Equal(0, PlonkNative.plonk_verify2(1, p3, (nuint)p3.Length, pi3, (nuint)pi3.Length));
            Assert.Equal(0, PlonkNative.plonk_verify(1, p3, (nuint)p3.Length, pi3, (nuint)pi3.Length));

            // A library without the key-bound rules: from the block before the height the wallet's prover reports itself
            // unavailable rather than calling exports that library does not have.
            PlonkProofVerifier.KeyBoundRulesAvailable = () => false;
            Globals.LastBlock = new Block { Height = KeyBoundGate - 1 };
            Assert.Equal(PlonkNative.ErrNotImplemented, PlonkProverV1.TryProveShield(500_000_000UL, PrivacyField.FromUInt64(7), PrivacyField.FromUInt64(11), out var none, out _));
            Assert.Null(none);
            Globals.LastBlock = new Block { Height = KeyBoundGate - 2 };
            Assert.Equal(PlonkNative.Success, PlonkProverV1.TryProveShield(500_000_000UL, PrivacyField.FromUInt64(7), PrivacyField.FromUInt64(11), out _, out _));
        }
    }
}
