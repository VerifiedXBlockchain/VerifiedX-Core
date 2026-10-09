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
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1, stage 2: the whole path with real proofs. The wallet builder proves a shield, an
    /// unshield of one note (with the dummy), an unshield of two notes and a private transfer; the ledger applies each
    /// to the epoch tree; consensus verifies every proof against public inputs it rebuilds from the transaction alone;
    /// a tampered transaction is refused. Needs the VXPLNK03 params with prover keys: VFX_PLONK_PARAMS, the node's
    /// default folder (%LOCALAPPDATA%/RBX/PlonkParams), or the repository's VerifiedXCore/DBs/PlonkParams. Without
    /// them the tests return early and say so.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PrivacyStage2_RoundTripTests : IDisposable
    {
        private const long Height = 1000;
        private const string SeedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
        private static readonly object ParamsLock = new();
        private static bool? _paramsLoaded;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight;
        private readonly long _priorSupplyGate;
        private readonly Dictionary<string, decimal> _priorCorrections;
        private readonly ShieldedKeyMaterial _alice;
        private readonly ShieldedKeyMaterial _bob;

        public PrivacyStage2_RoundTripTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ps2r_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorHeight = Globals.PrivateTxProofRulesHeight;
            _priorSupplyGate = Globals.PrivateTxSupplyRulesHeight;
            _priorCorrections = new(Globals.ShieldedSupplyCorrections, StringComparer.Ordinal);
            Globals.ShieldedSupplyCorrections.Clear();
            Globals.PrivateTxProofRulesHeight = Height;
            Globals.PrivateTxSupplyRulesHeight = 1;
            Globals.LastBlock = new Block { Height = Height }; // the wallet builds for block Height + 1
            DbContext.Initialize();
            PrivacyEpochService.ResetPools(DbContext.DB_Privacy, Height, 1_700_000_000);
            _alice = ShieldedHdDerivation.DeriveShieldedKeyMaterial(SeedHex, ShieldedAddressConstants.DefaultBip44CoinType, 11);
            _bob = ShieldedHdDerivation.DeriveShieldedKeyMaterial(SeedHex, ShieldedAddressConstants.DefaultBip44CoinType, 12);
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxProofRulesHeight = _priorHeight;
            Globals.PrivateTxSupplyRulesHeight = _priorSupplyGate;
            Globals.ShieldedSupplyCorrections.Clear();
            foreach (var (k, v) in _priorCorrections) Globals.ShieldedSupplyCorrections[k] = v;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        // ── Params ────────────────────────────────────────────────────────────────────────────────────────────

        private static string? FindParams()
        {
            var env = Environment.GetEnvironmentVariable("VFX_PLONK_PARAMS");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RBX", "PlonkParams", PLONKParamsDownloader.ParamsFileName);
            if (File.Exists(local)) return local;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "VerifiedXCore", "DBs", "PlonkParams", PLONKParamsDownloader.ParamsFileName);
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir.FullName, "DBs", "PlonkParams", PLONKParamsDownloader.ParamsFileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Loads the params once per process; false (with a console note) when they are not available.</summary>
        private static bool ParamsLoaded()
        {
            lock (ParamsLock)
            {
                if (_paramsLoaded.HasValue) return _paramsLoaded.Value;
                var path = FindParams();
                if (path == null)
                {
                    Console.WriteLine("PrivacyStage2_RoundTripTests: no VXPLNK03 params found (set VFX_PLONK_PARAMS); round-trip tests skipped.");
                    _paramsLoaded = false;
                    return false;
                }
                _paramsLoaded = PLONKSetup.TryLoadParamsFile(path) && PrivateTxProverV1.CanProve && PlonkProofVerifier.VerifierAvailable();
                if (!_paramsLoaded.Value) Console.WriteLine($"PrivacyStage2_RoundTripTests: params at {path} did not give proving + v2 verification; tests skipped.");
                return _paramsLoaded.Value;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────

        private static string NewAddress()
        {
            var key = new PrivateKey("secp256k1");
            return AccountData.GetHumanAddress("04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
        }

        private static LiteDB.LiteDatabase Db => DbContext.DB_Privacy;

        private static (bool ok, string message) Consensus(Transaction tx, long height)
        {
            Assert.True(PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out var err), err);
            var shape = PrivateTxProofRules.Check(tx, payload!, height);
            if (shape != null) return (false, shape);
            return PlonkProofVerifier.TryValidatePrivateProofs(tx, payload!, false, height);
        }

        private static async Task Apply(Transaction tx, long height)
        {
            var block = new Block { Height = height, StateRoot = "root", Timestamp = 1_700_000_000 + height, Transactions = new List<Transaction> { tx } };
            await PrivateTxLedgerService.ApplyBlockTransactionAsync(tx, block, Db);
        }

        /// <summary>The wallet's view of an output it owns: opens the sealed note and looks the leaf up in the tree.</summary>
        private static UnspentCommitment NoteOf(Transaction tx, int outputIndex, ShieldedKeyMaterial owner)
        {
            PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _);
            var output = payload!.Outs.Single(o => o.Index == outputIndex);
            Assert.True(ShieldedNoteEncryption.TryOpen(Convert.FromBase64String(output.EncryptedNoteB64!), owner.EncryptionPrivateKey32, out var plain, out var err), err);
            Assert.True(ShieldedPlainNoteCodec.TryDeserializeUtf8(plain!, out var note, out err), err);
            var record = Db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).FindOne(x => x.AssetType == "VFX" && x.Commitment == output.CommitmentB64);
            Assert.NotNull(record);
            return new UnspentCommitment { Commitment = output.CommitmentB64, AssetType = "VFX", Amount = note!.Amount, Randomness = Convert.FromBase64String(note.RandomnessB64), TreePosition = record!.TreePosition, IsSpent = false };
        }

        private Transaction ShieldFor(ShieldedKeyMaterial recipient, decimal amount)
        {
            Assert.True(VfxPrivateTransactionBuilder.TryBuildShield(NewAddress(), amount, 0.00000100M, 0, 1_700_000_000, recipient.ZfxAddress, "round trip", out var tx, out var err, Db, Height + 1), err);
            return tx!;
        }

        private static Transaction Tampered(Transaction tx, Action<PrivateTxPayload, Transaction> mutate)
        {
            var copy = new Transaction { FromAddress = tx.FromAddress, ToAddress = tx.ToAddress, Amount = tx.Amount, Fee = tx.Fee, Nonce = tx.Nonce, Timestamp = tx.Timestamp, TransactionType = tx.TransactionType, Data = tx.Data, Signature = tx.Signature };
            PrivateTxPayloadCodec.TryDecode(copy.Data, out var payload, out _);
            mutate(payload!, copy);
            copy.Data = PrivateTxPayloadCodec.SerializeToJson(payload!);
            copy.BuildPrivate();
            return copy;
        }

        // ── Shield ────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Shield_ProvesAndVerifies_AndLandsInTheEpochTree()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            Assert.True(Consensus(shield, Height + 1).ok, Consensus(shield, Height + 1).message);

            // The proof binds the amount to the note hash: changing either is refused.
            var moreAmount = Tampered(shield, (p, t) => { t.Amount = 6M; p.TransparentAmount = 6M; });
            Assert.False(Consensus(moreAmount, Height + 1).ok);
            var otherNote = Tampered(shield, (p, _) => p.Outs[0].NoteHashB64 = Convert.ToBase64String(PoseidonV1.NoteHashV2(500_000_000, PrivacyField.RandomCanonical(), _alice.OwnerPk32)));
            Assert.False(Consensus(otherNote, Height + 1).ok);
            // Re-addressing the note to Bob (same amount and randomness, his owner key) is refused: the proof binds the owner.
            PrivateTxPayloadCodec.TryDecode(shield.Data, out var sp0, out _);
            ShieldedNoteEncryption.TryOpen(Convert.FromBase64String(sp0!.Outs[0].EncryptedNoteB64!), _alice.EncryptionPrivateKey32, out var plain0, out _);
            ShieldedPlainNoteCodec.TryDeserializeUtf8(plain0!, out var note0, out _);
            var toBob = Tampered(shield, (p, _) => p.Outs[0].NoteHashB64 = Convert.ToBase64String(PoseidonV1.NoteHashV2(500_000_000, Convert.FromBase64String(note0!.RandomnessB64), _bob.OwnerPk32)));
            Assert.False(Consensus(toBob, Height + 1).ok);
            var unchanged = otherNote;
            Assert.False(Consensus(otherNote, Height + 1).ok);
            // Without the proof it is refused outright.
            var noProof = Tampered(shield, (p, _) => p.ProofB64 = null);
            Assert.Contains("must carry a PLONK proof", Consensus(noProof, Height + 1).message);

            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice);
            Assert.Equal(1, note.TreePosition); // leaf 0 is the dummy
            Assert.Equal(5M, note.Amount);
            var state = ShieldedPoolService.GetState("VFX", Db)!;
            Assert.Equal(5M, state.TotalShieldedSupply);
            Assert.Equal(2, state.TotalCommitments);
        }

        // ── Unshield ──────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task UnshieldOneNote_UsesTheDummy_ProvesAndVerifies_AndTamperingIsRefused()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice);

            Assert.True(VfxPrivateTransactionBuilder.TryBuildUnshield(new[] { note }, 3M, NewAddress(), _alice, 1_700_000_001, out var unshield, out var err, Db, Height + 2), err);
            PrivateTxPayloadCodec.TryDecode(unshield!.Data, out var payload, out _);
            Assert.Equal(2, payload!.NullsB64.Count);
            Assert.True(PrivacyEpoch.IsDummyNullifier(payload.NullsB64[1]));
            Assert.Single(payload.Outs); // the change (5 - 3 - fee)
            Assert.NotNull(payload.ProofB64);

            var verdict = Consensus(unshield, Height + 2);
            Assert.True(verdict.ok, verdict.message);

            // Tampering: a larger payout, another recipient, a changed root, a swapped nullifier - all refused by the proof.
            Assert.False(Consensus(Tampered(unshield, (p, t) => { t.Amount = 4M; p.TransparentAmount = 4M; }), Height + 2).ok);
            Assert.False(Consensus(Tampered(unshield, (p, _) => p.MerkleRootB64 = Convert.ToBase64String(PoseidonV1.MerkleZero(32))), Height + 2).ok);
            Assert.False(Consensus(Tampered(unshield, (p, _) => p.NullsB64[0] = Convert.ToBase64String(PoseidonV1.Nullifier(PrivacyField.RandomCanonical(), PoseidonV1.NoteHashV2(1, PrivacyField.Zero32, _alice.OwnerPk32), 1))), Height + 2).ok);
            Assert.False(Consensus(Tampered(unshield, (p, _) => p.Outs[0].NoteHashB64 = Convert.ToBase64String(PoseidonV1.NoteHashV2(1, PrivacyField.RandomCanonical(), _alice.OwnerPk32))), Height + 2).ok);
            // The recipient is bound by the tx hash (outer == payload, stage 1) AND by the proof (stage 3): a changed outer
            // address alone fails stage 1's binding, and a consistent redirect (outer and payload) fails the proof.
            var other = Tampered(unshield, (p, t) => t.ToAddress = NewAddress());
            PrivateTxPayloadCodec.TryDecode(other.Data, out var op, out _);
            Assert.NotNull(PrivateTxSupplyRules.StructureError(other, op!));
            var thief = NewAddress();
            var redirected = Tampered(unshield, (p, t) => { t.ToAddress = thief; p.TransparentOutput = thief; });
            PrivateTxPayloadCodec.TryDecode(redirected.Data, out var rp, out _);
            Assert.Null(PrivateTxSupplyRules.StructureError(redirected, rp!)); // consistent, so stage 1 lets it through...
            var redirectVerdict = Consensus(redirected, Height + 2);
            Assert.False(redirectVerdict.ok, "a redirected unshield verified");                 // ...and the proof refuses it

            await Apply(unshield, Height + 2);
            var state = ShieldedPoolService.GetState("VFX", Db)!;
            Assert.Equal(5M - 3M - Globals.PrivateTxFixedFee, state.TotalShieldedSupply);
            Assert.True(NullifierService.IsNullifierSpentInDb(payload.NullsB64[0], "VFX", Db));
            Assert.False(NullifierService.IsNullifierSpentInDb(payload.NullsB64[1], "VFX", Db));

            // The change note is spendable in turn (a second single-note spend reusing the dummy).
            var change = NoteOf(unshield, 0, _alice);
            Assert.Equal(5M - 3M - Globals.PrivateTxFixedFee, change.Amount);
            Assert.True(VfxPrivateTransactionBuilder.TryBuildUnshield(new[] { change }, 1M, NewAddress(), _alice, 1_700_000_002, out var again, out err, Db, Height + 3), err);
            Assert.True(Consensus(again!, Height + 3).ok, Consensus(again!, Height + 3).message);
        }

        [Fact]
        public async Task UnshieldTwoNotes_ProvesAndVerifies()
        {
            if (!ParamsLoaded()) return;
            var s1 = ShieldFor(_alice, 2M);
            var s2 = ShieldFor(_alice, 3M);
            await Apply(s1, Height + 1);
            await Apply(s2, Height + 1);
            var inputs = new[] { NoteOf(s1, 0, _alice), NoteOf(s2, 0, _alice) };
            Assert.True(VfxPrivateTransactionBuilder.TryBuildUnshield(inputs, 4.5M, NewAddress(), _alice, 1_700_000_001, out var unshield, out var err, Db, Height + 2), err);
            PrivateTxPayloadCodec.TryDecode(unshield!.Data, out var payload, out _);
            Assert.Equal(2, payload!.NullsB64.Count);
            Assert.DoesNotContain(payload.NullsB64, PrivacyEpoch.IsDummyNullifier);
            var verdict = Consensus(unshield, Height + 2);
            Assert.True(verdict.ok, verdict.message);
        }

        // ── Private transfer ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task PrivateTransfer_ProvesAndVerifies_AndTheRecipientCanUnshield()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice);

            Assert.True(VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(new[] { note }, 2M, _bob.ZfxAddress, _alice, 1_700_000_001, out var transfer, out var err, Db, Height + 2), err);
            PrivateTxPayloadCodec.TryDecode(transfer!.Data, out var payload, out _);
            Assert.Equal(2, payload!.NullsB64.Count);
            Assert.Equal(2, payload.Outs.Count);
            var verdict = Consensus(transfer, Height + 2);
            Assert.True(verdict.ok, verdict.message);
            Assert.False(Consensus(Tampered(transfer, (p, _) => (p.Outs[0].NoteHashB64, p.Outs[1].NoteHashB64) = (p.Outs[1].NoteHashB64, p.Outs[0].NoteHashB64)), Height + 2).ok);
            await Apply(transfer, Height + 2);

            // Bob received 2 VFX and can unshield it; Alice holds the change.
            var bobsNote = NoteOf(transfer, 0, _bob);
            Assert.Equal(2M, bobsNote.Amount);
            Assert.True(VfxPrivateTransactionBuilder.TryBuildUnshield(new[] { bobsNote }, 1.5M, NewAddress(), _bob, 1_700_000_002, out var bobUnshield, out err, Db, Height + 3), err);
            Assert.True(Consensus(bobUnshield!, Height + 3).ok, Consensus(bobUnshield!, Height + 3).message);
            var alicesChange = NoteOf(transfer, 1, _alice);
            Assert.Equal(5M - 2M - Globals.PrivateTxFixedFee, alicesChange.Amount);
            // Alice built Bob's note, so she knows its amount and randomness. With the v1 circuits that was enough to spend it
            // with her own key (re-audit finding 1). With the owner-bound notes the builder refuses: the leaf is Bob's.
            var bobsNoteSeenByAlice = new UnspentCommitment { Commitment = bobsNote.Commitment, AssetType = "VFX", Amount = bobsNote.Amount, Randomness = bobsNote.Randomness, TreePosition = bobsNote.TreePosition };
            Assert.False(VfxPrivateTransactionBuilder.TryBuildUnshield(new[] { bobsNoteSeenByAlice }, 1M, NewAddress(), _alice, 1_700_000_003, out _, out var stealErr, Db, Height + 3));
            Assert.Contains("only the note's owner can spend it", stealErr);
            Assert.Equal(4, ShieldedPoolService.GetState("VFX", Db)!.TotalCommitments); // dummy, shield, two transfer outputs
        }

        // ── Re-audit finding 1: a note is its owner's ───────────────────────────────────────────────────────────

        /// <summary>
        /// The drain the re-audit reproduced against the stage-2 circuits: one known note spent twice with two keys. With the
        /// owner-bound circuits a stranger who knows the note (its sender always does) cannot produce a proof consensus
        /// accepts, whether through the builder or by forcing a witness with their own key.
        /// </summary>
        [Fact]
        public async Task AStrangerWhoKnowsTheNote_CannotSpendIt_WithAnotherKey()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 10M);
            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice); // amount, randomness, position: what Mallory (the sender) knows
            var payout = NewAddress();

            // 1. The builder with Mallory's (Bob's) keys refuses: the stored leaf is Alice's.
            Assert.False(VfxPrivateTransactionBuilder.TryBuildUnshield(new[] { note }, 9M, payout, _bob, 1_700_000_001, out _, out var err, Db, Height + 2));
            Assert.Contains("only the note's owner can spend it", err);

            // 2. Forcing it: a witness with Bob's nullifier key over Alice's note, proven directly. Whatever the prover
            //    returns, consensus refuses the transaction (the proof cannot satisfy the Merkle membership of a leaf that
            //    names Alice while the circuit recomputes it with Bob's owner key).
            var store = new ShieldedMerkleStore("VFX", Db, fixedDepth: true);
            store.LoadLeavesFromCommitments();
            Assert.True(store.TryGetInclusionProof(note.TreePosition, out var path, out var root));
            PrivacyPedersenAmount.TryToScaledU64(note.Amount, out var scaled, out _);
            var forged = new PlonkProverV1.TransferInputWitness { AmountScaled = scaled, Randomness32 = note.Randomness, NullifierKey32 = _bob.NullifierKey32, TreePosition = (ulong)note.TreePosition, MerklePath = path, MerkleIndices = FixedDepthMerkleTree.IndicesFlat(note.TreePosition) };
            Assert.True(PrivateTxProverV1.TryBuildInput("VFX", PrivateTxProverV1.Dummy, PrivacyField.Zero32, Db, out var dummy, out _, out var dErr), dErr);
            PrivacyPedersenAmount.TryToScaledU64(9M, out var transparent, out _);
            PrivacyPedersenAmount.TryToScaledU64(Globals.PrivateTxFixedFee, out var fee, out _);
            var changeAmount = scaled - transparent - fee;
            var changeRand = PrivacyField.RandomCanonical();
            var changeHash = PoseidonV1.NoteHashV2(changeAmount, changeRand, _bob.OwnerPk32);
            var bobNullifier = PoseidonV1.Nullifier(_bob.NullifierKey32, PoseidonV1.NoteHashV2(scaled, note.Randomness, _bob.OwnerPk32), (ulong)note.TreePosition);
            var code = PlonkProverV1.TryProveUnshield(new[] { forged, dummy! }, transparent, changeAmount, changeRand, _bob.OwnerPk32, fee, root, PlonkPublicInputsV2.RecipientTag32(payout), out var proof, out _);
            if (code == PlonkNative.Success && proof != null)
            {
                var g1 = new byte[PlonkNative.G1CompressedSize];
                PlonkNative.pedersen_commit(changeAmount, changeRand, g1);
                var payload = new PrivateTxPayload
                {
                    Version = 1, Kind = "unshield", SubType = "Unshield", Asset = "VFX",
                    NullsB64 = { Convert.ToBase64String(bobNullifier), PrivacyEpoch.DummyNullifierB64 },
                    SpentCommitmentTreePositions = { note.TreePosition, 0 }, SpentCommitmentB64s = new List<string> { note.Commitment },
                    Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1), NoteHashB64 = Convert.ToBase64String(changeHash) } },
                    TransparentOutput = payout, TransparentAmount = 9M, Fee = Globals.PrivateTxFixedFee, MerkleRootB64 = Convert.ToBase64String(root), ProofB64 = Convert.ToBase64String(proof),
                };
                var tx = new Transaction { FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = payout, Amount = 9M, Fee = 0M, Nonce = 0, Timestamp = 1_700_000_001, TransactionType = TransactionType.VFX_UNSHIELD, Data = PrivateTxPayloadCodec.SerializeToJson(payload), Signature = PrivacyConstants.PlonkSignatureSentinel };
                tx.BuildPrivate();
                var verdict = Consensus(tx, Height + 2);
                Assert.False(verdict.ok, "a stranger's spend of Alice's note verified");
            }

            // 3. Alice herself spends it, once; the pool ends where it should.
            Assert.True(VfxPrivateTransactionBuilder.TryBuildUnshield(new[] { note }, 9M, payout, _alice, 1_700_000_002, out var honest, out err, Db, Height + 2), err);
            Assert.True(Consensus(honest!, Height + 2).ok);
            await Apply(honest!, Height + 2);
            Assert.Equal(10M - 9M - Globals.PrivateTxFixedFee, ShieldedPoolService.GetState("VFX", Db)!.TotalShieldedSupply);
        }

        /// <summary>Stage 3: a v1 recipient address carries no owner key, so nothing can be sent to it in the epoch.</summary>
        [Fact]
        public async Task AVersionOneAddress_CannotReceive_InTheEpoch()
        {
            if (!ParamsLoaded()) return;
            var v1 = ShieldedAddressCodec.EncodeEncryptionKey(_bob.EncryptionPublicKey33);
            Assert.False(VfxPrivateTransactionBuilder.TryBuildShield(NewAddress(), 1M, 0.00000100M, 0, 1_700_000_000, v1, null, out _, out var err, Db, Height + 1));
            Assert.Contains("owner key", err);
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            Assert.False(VfxPrivateTransactionBuilder.TryBuildPrivateTransfer(new[] { NoteOf(shield, 0, _alice) }, 1M, v1, _alice, 1_700_000_001, out _, out err, Db, Height + 2));
            Assert.Contains("owner key", err);
            // Before the epoch a v1 address still receives (legacy regime).
            Assert.True(VfxPrivateTransactionBuilder.TryBuildShield(NewAddress(), 1M, 0.00000100M, 0, 1_700_000_000, v1, null, out _, out err, Db, Height - 1), err);
        }

        // ── Before the height ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void BeforeTheHeight_TheBuilderStillMakesLegacyTransactions()
        {
            if (!PoseidonV1.IsAvailable) return;
            Assert.True(VfxPrivateTransactionBuilder.TryBuildShield(NewAddress(), 1M, 0.00000100M, 0, 1_700_000_000, _alice.ZfxAddress, null, out var tx, out var err, Db, Height - 1), err);
            PrivateTxPayloadCodec.TryDecode(tx!.Data, out var payload, out _);
            Assert.Null(payload!.ProofB64); // no proof before the height (the v0 prover never existed in the field)
        }
    }
}
