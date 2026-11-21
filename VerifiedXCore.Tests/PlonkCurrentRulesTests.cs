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
    /// Globals.PlonkCurrentRulesHeight: a block below it has its private proofs judged under the library's original rules
    /// (plonk_verify), a block from it under the current ones (plonk_verify2), which are what this library's prover makes.
    /// Until the height this node admits no private transaction carrying a proof, so it never proposes one a block below
    /// the height would refuse. A proof accepted under one set of rules is never taken from the cache for the other.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PlonkCurrentRulesTests : IDisposable
    {
        private const long EpochHeight = 1000;
        private const long Gate = 2000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorEpoch, _priorGate, _priorSupplyGate;
        private readonly Func<bool> _priorAvailable, _priorCurrentAvailable;
        private readonly Func<byte, byte[], byte[], bool, int> _priorNative;
        private readonly byte[] _root = new FixedDepthMerkleTree(new[] { PrivacyEpoch.DummyNoteHash }).Root();
        private readonly List<bool> _modes = new();

        public PlonkCurrentRulesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pcr_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorEpoch = Globals.PrivateTxProofRulesHeight;
            _priorGate = Globals.PlonkCurrentRulesHeight;
            _priorSupplyGate = Globals.PrivateTxSupplyRulesHeight;
            _priorAvailable = PlonkProofVerifier.VerifierAvailable;
            _priorCurrentAvailable = PlonkProofVerifier.CurrentRulesAvailable;
            _priorNative = PlonkProofVerifier.NativeVerify;
            Globals.PrivateTxProofRulesHeight = EpochHeight;
            Globals.PlonkCurrentRulesHeight = Gate;
            Globals.PrivateTxSupplyRulesHeight = 1;
            PlonkProofVerifier.ClearVerifiedProofCache();
            PlonkProofVerifier.VerifierAvailable = () => true;
            PlonkProofVerifier.CurrentRulesAvailable = () => true;
            PlonkProofVerifier.NativeVerify = (_, _, _, current) => { _modes.Add(current); return 0; };
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
            PlonkProofVerifier.ClearVerifiedProofCache();
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxProofRulesHeight = _priorEpoch;
            Globals.PlonkCurrentRulesHeight = _priorGate;
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
            Globals.LastBlock = new Block { Height = Gate + 10 };
            var tx = UnshieldWithProof();

            var (below, whyBelow) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: Gate - 1);
            Assert.False(below);
            Assert.Contains(PlonkProofVerifier.FailedProofPrefix, whyBelow);
            Assert.Equal(new[] { false }, _modes);

            _modes.Clear();
            var (at, whyAt) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: Gate);
            Assert.False(at);
            Assert.Contains(PlonkProofVerifier.FailedProofPrefix, whyAt);
            Assert.Equal(new[] { true }, _modes);
        }

        [Fact]
        public async Task BelowTheHeight_AProofIsNotAdmitted_ButABlockCarryingOneIsJudgedUnderTheOriginalRules()
        {
            Globals.LastBlock = new Block { Height = Gate - 5 };
            var tx = UnshieldWithProof();
            var (admitted, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admitted);
            Assert.Equal(PrivateTransactionValidatorService.CurrentRulesPendingReason, why);
            Assert.Empty(_modes); // refused before any proof is judged

            var (inBlock, whyBlock) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: Gate - 4);
            Assert.False(inBlock);
            Assert.Contains(PlonkProofVerifier.FailedProofPrefix, whyBlock); // judged, not refused as paused
            Assert.Equal(new[] { false }, _modes);

            // From the height admission judges the proof, under the current rules.
            _modes.Clear();
            Globals.LastBlock = new Block { Height = Gate - 1 };
            var (atGate, whyGate) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(atGate);
            Assert.Contains(PlonkProofVerifier.FailedProofPrefix, whyGate);
            Assert.Equal(new[] { true }, _modes);
        }

        [Fact]
        public void AProofAcceptedUnderOneSetOfRules_IsNotTakenFromTheCacheForTheOther()
        {
            PlonkProofVerifier.NativeVerify = (_, _, _, current) => { _modes.Add(current); return current ? 0 : 1; };
            var proof = new byte[] { 1, 2, 3 };
            var pi = new byte[] { 4, 5 };
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi, currentRules: false));
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi, currentRules: true));
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Unshield, proof, pi, currentRules: false));
            Assert.Equal(new[] { false, true }, _modes); // the second legacy call is the cached answer
        }

        [Fact]
        public async Task FromTheHeight_ANodeWhoseLibraryLacksTheCurrentRules_FailsClosed()
        {
            PlonkProofVerifier.CurrentRulesAvailable = () => false;
            Globals.LastBlock = new Block { Height = Gate + 10 };
            var tx = UnshieldWithProof();
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, blockVerify: true, blockHeight: Gate);
            Assert.False(ok);
            Assert.Equal(PlonkProofVerifier.VerifierUnavailableReason, why);
            Assert.Empty(_modes);
            Assert.False(Globals.ProofVerifierReady);

            // Below the height such a node still judges blocks as before.
            Globals.LastBlock = new Block { Height = Gate - 10 };
            Assert.True(Globals.ProofVerifierReady);
        }

        /// <summary>
        /// With the real library and the VXPLNK06 params (VFX_PLONK_PARAMS): a proof the prover makes verifies under the
        /// current rules and not under the original ones. Without the file it returns early.
        /// </summary>
        [Fact]
        public void AProofThisLibraryMakes_PassesTheCurrentRulesOnly()
        {
            var path = Environment.GetEnvironmentVariable("VFX_PLONK_PARAMS");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !PLONKSetup.TryLoadParamsFile(path) || !PrivateTxProverV1.CanProve)
            {
                Console.WriteLine("PlonkCurrentRulesTests: no VXPLNK06 params with prover keys (VFX_PLONK_PARAMS); skipped.");
                return;
            }
            PlonkProofVerifier.NativeVerify = _priorNative;
            PlonkProofVerifier.CurrentRulesAvailable = _priorCurrentAvailable;
            Assert.True(PlonkProofVerifier.CurrentRulesAvailable());
            var proof = new byte[65536]; nuint proofLen = (nuint)proof.Length;
            var pi = new byte[65536]; nuint piLen = (nuint)pi.Length;
            Assert.Equal(PlonkNative.Success, PlonkNative.plonk_prove_shield(500_000_000UL, PrivacyField.FromUInt64(7), PrivacyField.FromUInt64(11), proof, ref proofLen, pi, ref piLen));
            proof = proof.Take((int)proofLen).ToArray();
            pi = pi.Take((int)piLen).ToArray();
            Assert.Equal(1, PlonkNative.plonk_verify2(1, proof, (nuint)proof.Length, pi, (nuint)pi.Length));
            Assert.Equal(0, PlonkNative.plonk_verify(1, proof, (nuint)proof.Length, pi, (nuint)pi.Length));
            Assert.Equal(PlonkVerifyResult.Valid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, pi, currentRules: true));
            Assert.Equal(PlonkVerifyResult.Invalid, PlonkProofVerifier.VerifyRaw(PlonkCircuitType.Shield, proof, pi, currentRules: false));
        }
    }
}
