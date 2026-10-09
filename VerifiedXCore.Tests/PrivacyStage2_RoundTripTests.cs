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
            var otherNote = Tampered(shield, (p, _) => p.Outs[0].NoteHashB64 = Convert.ToBase64String(PoseidonV1.NoteHash(500_000_000, PrivacyField.RandomCanonical())));
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
            Assert.False(Consensus(Tampered(unshield, (p, _) => p.NullsB64[0] = Convert.ToBase64String(PoseidonV1.Nullifier(PrivacyField.RandomCanonical(), PoseidonV1.NoteHash(1, PrivacyField.Zero32), 1))), Height + 2).ok);
            Assert.False(Consensus(Tampered(unshield, (p, _) => p.Outs[0].NoteHashB64 = Convert.ToBase64String(PoseidonV1.NoteHash(1, PrivacyField.RandomCanonical()))), Height + 2).ok);
            // The recipient is bound by the tx hash (outer == payload, stage 1), not by the circuit: a changed outer address alone fails stage 1's binding.
            var other = Tampered(unshield, (p, t) => t.ToAddress = NewAddress());
            PrivateTxPayloadCodec.TryDecode(other.Data, out var op, out _);
            Assert.NotNull(PrivateTxSupplyRules.StructureError(other, op!));

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
            // Alice cannot spend Bob's note: her viewing key gives a different nullifier, and the proof is made with her key -
            // the builder would still produce a proof, but she does not know Bob's randomness in practice; the ledger/trees
            // are indifferent. What consensus enforces is the circuit: the spent note must be in the tree with that amount.
            Assert.Equal(4, ShieldedPoolService.GetState("VFX", Db)!.TotalCommitments); // dummy, shield, two transfer outputs
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
