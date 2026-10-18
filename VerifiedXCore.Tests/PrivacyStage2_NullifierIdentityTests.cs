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
    /// From PrivateTxProofRulesHeight a nullifier is its 32 bytes, as the proof binds it: every nullifier a private
    /// transaction names is the one Base64 spelling of a canonical field element, no note is named twice in one
    /// transaction, and the spent set, the mempool claims and the block-scoped set key one note one way however it is
    /// spelled. Below the height a nullifier is its text, as earlier releases judged it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PrivacyStage2_NullifierIdentityTests : IDisposable
    {
        private const long Gate = 1000;
        private const long InEpoch = Gate + 1;
        private const long BelowEpoch = Gate - 1;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorSupplyGate;
        private readonly long _priorProofGate;
        private readonly string _root;

        public PrivacyStage2_NullifierIdentityTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ps2n_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorSupplyGate = Globals.PrivateTxSupplyRulesHeight;
            _priorProofGate = Globals.PrivateTxProofRulesHeight;
            Globals.PrivateTxSupplyRulesHeight = Gate;
            Globals.PrivateTxProofRulesHeight = Gate;
            Globals.LastBlock = new Block { Height = InEpoch - 1 };
            DbContext.Initialize();

            _root = Convert.ToBase64String(PrivacyField.RandomCanonical());
            var pool = ShieldedPoolService.GetOrCreateState("VFX");
            pool.TotalShieldedSupply = 0.5M;
            pool.CurrentMerkleRoot = _root;
            PrivacyDbContext.PoolState().Update(pool);
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxSupplyRulesHeight = _priorSupplyGate;
            Globals.PrivateTxProofRulesHeight = _priorProofGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string NewAddress()
        {
            var key = new PrivateKey("secp256k1");
            return AccountData.GetHumanAddress("04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
        }

        private static string Nullifier() => Convert.ToBase64String(PrivacyField.RandomCanonical());

        private static string Spaced(string canonical) => canonical.Substring(0, 20) + " " + canonical.Substring(20);

        private Transaction Unshield(string null0, string null1)
        {
            var to = NewAddress();
            var payload = new PrivateTxPayload
            {
                Version = 1, Kind = "unshield", SubType = "Unshield", Asset = "VFX",
                NullsB64 = new() { null0, null1 }, SpentCommitmentTreePositions = new() { 0L, 1L },
                Outs = new()
                {
                    new PrivateShieldedOutput
                    {
                        Index = 0,
                        CommitmentB64 = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(PlonkNative.G1CompressedSize)),
                        NoteHashB64 = Convert.ToBase64String(PrivacyField.RandomCanonical()),
                    }
                },
                MerkleRootB64 = _root, TransparentOutput = to, TransparentAmount = 0.1M, Fee = Globals.PrivateTxFixedFee,
            };
            var tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = to, Amount = 0.1M, Fee = 0M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VFX_UNSHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload), Signature = PrivacyConstants.PlonkSignatureSentinel,
            };
            tx.BuildPrivate();
            return tx;
        }

        private static PrivateTxPayload Payload(Transaction tx)
        {
            Assert.True(PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out var err), err);
            return payload!;
        }

        [Fact]
        public void IsCanonicalNullifier_AcceptsOnlyTheOneSpellingOfAFieldElement()
        {
            var n = Nullifier();
            Assert.True(NullifierService.IsCanonicalNullifier(n));
            Assert.False(NullifierService.IsCanonicalNullifier(n + " "));
            Assert.False(NullifierService.IsCanonicalNullifier(" " + n));
            Assert.False(NullifierService.IsCanonicalNullifier(Spaced(n)));
            Assert.False(NullifierService.IsCanonicalNullifier(n.Insert(10, "\n")));
            Assert.False(NullifierService.IsCanonicalNullifier(Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 32).ToArray()))); // above the modulus
            Assert.False(NullifierService.IsCanonicalNullifier(Convert.ToBase64String(new byte[31])));
            Assert.False(NullifierService.IsCanonicalNullifier(""));
        }

        [Fact]
        public void SpentSet_InEpoch_SeesEverySpellingOfASpentNote()
        {
            var n = Nullifier();
            Assert.True(NullifierService.TryRecordNullifier(n, "VFX", InEpoch, 1));
            Assert.True(NullifierService.IsNullifierSpentInDb(Spaced(n), "VFX", height: InEpoch));
            Assert.True(NullifierService.IsNullifierSpentInDb(n + " ", "VFX", height: InEpoch));
            Assert.False(NullifierService.TryRecordNullifier(" " + n, "VFX", InEpoch + 1, 2));
        }

        [Fact]
        public void SpentSet_BelowEpoch_KeysTheTextAsBefore()
        {
            var n = Nullifier();
            Assert.True(NullifierService.TryRecordNullifier(n, "VFX", BelowEpoch, 1));
            Assert.True(NullifierService.IsNullifierSpentInDb(n, "VFX", height: BelowEpoch));
            Assert.False(NullifierService.IsNullifierSpentInDb(n + " ", "VFX", height: BelowEpoch));
            Assert.True(NullifierService.TryRecordNullifier(n + " ", "VFX", BelowEpoch, 2));
        }

        [Fact]
        public void MempoolKey_InEpoch_IsOneKeyPerNote_AndBelowIsTheText()
        {
            var n = Nullifier();
            Assert.Equal(MempoolNullifierTracker.MakeKey("VFX", n, InEpoch), MempoolNullifierTracker.MakeKey("VFX", Spaced(n), InEpoch));
            Assert.NotEqual(MempoolNullifierTracker.MakeKey("VFX", n, BelowEpoch), MempoolNullifierTracker.MakeKey("VFX", Spaced(n), BelowEpoch));
        }

        [Fact]
        public async Task VerifyTX_InEpoch_RefusesASpentNoteRespelled()
        {
            var spent = Nullifier();
            Assert.True(NullifierService.TryRecordNullifier(spent, "VFX", InEpoch, 1));
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(spent + " ", Nullifier()), skipPrivatePlonkProofVerification: true);
            Assert.False(ok);
            Assert.Equal(PrivateTxProofRules.NonCanonicalNullifierReason, msg);
        }

        [Fact]
        public async Task VerifyTX_InEpoch_RefusesOneNoteSpentAsBothInputs()
        {
            var n = Nullifier();
            var (okAlias, msgAlias) = await TransactionValidatorService.VerifyTX(Unshield(n, n + " "), skipPrivatePlonkProofVerification: true);
            Assert.False(okAlias);
            Assert.Equal(PrivateTxProofRules.NonCanonicalNullifierReason, msgAlias);

            var (okSame, msgSame) = await TransactionValidatorService.VerifyTX(Unshield(n, n), skipPrivatePlonkProofVerification: true);
            Assert.False(okSame);
            Assert.Equal(PrivateTxProofRules.RepeatedNullifierReason, msgSame);
        }

        [Fact]
        public async Task VerifyTX_InEpoch_AcceptsTwoDistinctCanonicalNotes_AndARealNoteBesideTheDummy()
        {
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(Nullifier(), Nullifier()), skipPrivatePlonkProofVerification: true);
            Assert.True(ok, msg);
            var (okDummy, msgDummy) = await TransactionValidatorService.VerifyTX(Unshield(Nullifier(), PrivacyEpoch.DummyNullifierB64), skipPrivatePlonkProofVerification: true);
            Assert.True(okDummy, msgDummy);
        }

        [Fact]
        public void ProofRules_FeeLegNullifier_MustBeCanonicalAndDistinctFromTheSpends()
        {
            var n = Nullifier();
            var payload = Payload(Unshield(n, Nullifier()));
            payload.FeeInputNullifierB64 = n;
            Assert.Equal(PrivateTxProofRules.RepeatedNullifierReason, PrivateTxProofRules.NullifierIdentityError(payload));
            payload.FeeInputNullifierB64 = Nullifier() + " ";
            Assert.Equal(PrivateTxProofRules.NonCanonicalNullifierReason, PrivateTxProofRules.NullifierIdentityError(payload));
            payload.FeeInputNullifierB64 = Nullifier();
            Assert.Null(PrivateTxProofRules.NullifierIdentityError(payload));
        }

        [Fact]
        public void BlockScopedSet_InEpoch_RefusesARespelledNullifier_BelowKeepsTheText()
        {
            var n = Nullifier();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(n, Nullifier()), keys, out var err, InEpoch), err);
            Assert.False(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(Spaced(n), Nullifier()), keys, out _, InEpoch));

            var below = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(n, Nullifier()), below, out var e1, BelowEpoch), e1);
            Assert.True(MempoolNullifierTracker.TryAddBlockScopedNullifiers(Unshield(Spaced(n), Nullifier()), below, out var e2, BelowEpoch), e2);
        }
    }
}
