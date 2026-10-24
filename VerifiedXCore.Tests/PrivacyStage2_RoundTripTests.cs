using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
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

        /// <summary>
        /// A wallet marked its notes spent from the commitments and tree positions a mined payload lists, which no proof
        /// covers: any private transaction listing other people's notes had every node's wallet mark them spent (fifth
        /// review, round 21). A note is spent by a transaction exposing its own nullifier.
        /// </summary>
        [Fact]
        public async Task AWalletNoteIsSpent_OnlyByATransactionExposingItsNullifier()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice);

            Assert.True(VfxPrivateTransactionBuilder.TryBuildUnshield(new[] { note }, 3M, NewAddress(), _alice, 1_700_000_001, out var unshield, out var err, Db, Height + 2), err);
            PrivateTxPayloadCodec.TryDecode(unshield!.Data, out var real, out _);
            Assert.True(PrivacyApiHelper.SpendsWalletNote(unshield, real!, note, _alice, Height + 2));

            var listing = new PrivateTxPayload
            {
                Asset = "VFX", Kind = "unshield", NullsB64 = { Convert.ToBase64String(PrivacyField.RandomCanonical()) },
                SpentCommitmentB64s = new List<string> { note.Commitment }, SpentCommitmentTreePositions = { note.TreePosition },
            };
            Assert.False(PrivacyApiHelper.SpendsWalletNote(unshield, listing, note, _alice, Height + 2));
        }

        /// <summary>
        /// Wallets credited the fee change note and honoured the fee nullifier of any private transaction, while the ledger
        /// applies a fee leg only for a vBTC unshield or private transfer carrying all of it: a VFX shield carrying a fee
        /// note sealed to a victim showed a balance that did not exist (fifth review, round 23).
        /// </summary>
        [Fact]
        public async Task AFeeLeg_CountsInAWallet_OnlyWhereTheLedgerAppliesIt()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice);
            PrivateTxPayloadCodec.TryDecode(shield.Data, out var real, out _);
            var nullifier = VfxPrivateTransactionBuilder.NullifierOf(note, _alice, true)!;
            var plain = new ShieldedPlainNote { Version = 1, Amount = 5M, RandomnessB64 = Convert.ToBase64String(note.Randomness), AssetType = "VFX" };

            // The fee fields on a VFX shield: applied by nothing.
            var carried = new PrivateTxPayload
            {
                Asset = "VFX", Kind = "shield", FeeOutputCommitmentB64 = real!.Outs[0].CommitmentB64, FeeInputNullifierB64 = nullifier,
                FeeTreeMerkleRoot = Convert.ToBase64String(PrivacyField.RandomCanonical()), FeeInputSpentTreePosition = 0, Fee = Globals.PrivateTxFixedFee,
            };
            var vfxShield = new Transaction { TransactionType = TransactionType.VFX_SHIELD };
            Assert.False(PrivacyApiHelper.FeeNoteMatchesLeg(vfxShield, carried, plain, _alice));
            Assert.False(PrivacyApiHelper.SpendsWalletNote(vfxShield, carried, note, _alice, Height + 2));

            // The same fields on a vBTC private transfer: its fee leg, whose change note is checked against its leaf.
            var vbtcTransfer = new Transaction { TransactionType = TransactionType.VBTC_V2_PRIVATE_TRANSFER };
            Assert.True(PrivacyApiHelper.FeeNoteMatchesLeg(vbtcTransfer, carried, plain, _alice));
            Assert.True(PrivacyApiHelper.SpendsWalletNote(vbtcTransfer, carried, note, _alice, Height + 2));
            plain.Amount = 500M;
            Assert.False(PrivacyApiHelper.FeeNoteMatchesLeg(vbtcTransfer, carried, plain, _alice));   // a claimed amount
        }

        /// <summary>
        /// A wallet credited the amount a sealed note claims, from any transaction: a fake note showed a balance that did
        /// not exist (fifth review, round 22). A note is credited only as the leaf the chain applied for its output.
        /// </summary>
        [Fact]
        public async Task AWalletCreditsANote_OnlyAsTheLeafTheChainApplied()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice);
            PrivateTxPayloadCodec.TryDecode(shield.Data, out var payload, out _);
            var output = payload!.Outs[0];
            Assert.True(PrivacyApiHelper.NoteMatchesOutput(output, 5M, note.Randomness, _alice, "VFX"));
            Assert.False(PrivacyApiHelper.NoteMatchesOutput(output, 500M, note.Randomness, _alice, "VFX"));    // a claimed amount
            Assert.False(PrivacyApiHelper.NoteMatchesOutput(output, 5M, note.Randomness, _bob, "VFX"));        // someone else's key
            var unapplied = new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(PrivacyField.RandomCanonical()) };
            Assert.False(PrivacyApiHelper.NoteMatchesOutput(unapplied, 5M, note.Randomness, _alice, "VFX"));   // not a leaf of the chain
            // A VFX note sealed as vBTC (round 23): no note hash covers the asset; the leaf's pool does.
            Assert.False(PrivacyApiHelper.NoteMatchesOutput(output, 5M, note.Randomness, _alice, "VBTC:" + Guid.NewGuid().ToString("N")));
            Assert.False(PrivacyApiHelper.NoteMatchesOutput(output, 5M, note.Randomness, _alice, "vfx"));
        }

        /// <summary>
        /// The resync and scan routes marked an output's commitment seen before checking its note: a copy of the
        /// commitment ahead of the real output, carrying a note sealed to the owner that does not match, hid the real
        /// note from every resync (fifth review, round 24).
        /// </summary>
        [Fact]
        public async Task AResync_CreditsTheRealNote_BehindACopyThatDoesNotMatch()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            var note = NoteOf(shield, 0, _alice);
            var claim = ShieldedNoteEncryption.SealPlainNote(new ShieldedPlainNote { Version = 1, Amount = 500M, RandomnessB64 = Convert.ToBase64String(note.Randomness), AssetType = "VFX" }, _alice.ZfxAddress);
            var copy = Tampered(shield, (p, _) => p.Outs[0].EncryptedNoteB64 = Convert.ToBase64String(claim));
            var block = new Block { Height = Height + 1, StateRoot = "root", Timestamp = 1_700_000_000 + Height + 1, Hash = "resync", Transactions = new List<Transaction> { copy, shield } };
            BlockchainData.GetBlocks().InsertSafe(block);
            ShieldedWalletService.Upsert(new ShieldedWallet { ShieldedAddress = _alice.ZfxAddress, ViewingKey = _alice.ViewingKey32, IsViewOnly = true });

            var result = PrivacyApiHelper.ResyncShieldedWallet(_alice.ZfxAddress, Height + 1, Height + 1);
            Assert.True(string.IsNullOrEmpty(result.Error), result.Error);
            var wallet = ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!;
            Assert.Equal(5M, Assert.Single(wallet.UnspentCommitments).Amount);
            Assert.Equal(5M, wallet.ShieldedBalances["VFX"]);

            // A resync over blocks where nothing matches leaves nothing: an empty rebuild took the stored notes back
            // (round 25), so a forged note survived the resync run to clear it.
            wallet.UnspentCommitments[0].Amount = 500M; wallet.ShieldedBalances["VFX"] = 500M;
            ShieldedWalletService.Upsert(wallet);
            result = PrivacyApiHelper.ResyncShieldedWallet(_alice.ZfxAddress, Height + 2, Height + 2);
            Assert.True(string.IsNullOrEmpty(result.Error), result.Error);
            wallet = ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!;
            Assert.Empty(wallet.UnspentCommitments);
            Assert.Empty(wallet.ShieldedBalances);
        }

        /// <summary>
        /// A real note sent to the wallet, applied first with the commitment of another note on its way to the wallet,
        /// made that note "already known": every scan and resync skipped it, and marking the first spent marked both
        /// (fifth review, round 32). Each note is its own (commitment, amount, randomness).
        /// </summary>
        [Fact]
        public async Task ANoteCarryingAnothersCommitment_DoesNotHideIt()
        {
            if (!ParamsLoaded()) return;
            var real = ShieldFor(_alice, 5M);
            PrivateTxPayloadCodec.TryDecode(real.Data, out var realPayload, out _);
            var small = ShieldFor(_alice, 1M);
            var copy = Tampered(small, (p, _) => p.Outs[0].CommitmentB64 = realPayload!.Outs[0].CommitmentB64);
            await Apply(copy, Height + 1);
            await Apply(real, Height + 1);
            var block = new Block { Height = Height + 1, StateRoot = "root", Timestamp = 1_700_000_000 + Height + 1, Hash = "copied-commitment", Transactions = new List<Transaction> { copy, real } };
            BlockchainData.GetBlocks().InsertSafe(block);
            ShieldedWalletService.Upsert(new ShieldedWallet { ShieldedAddress = _alice.ZfxAddress, ViewingKey = _alice.ViewingKey32, IsViewOnly = true });

            var result = PrivacyApiHelper.ResyncShieldedWallet(_alice.ZfxAddress, Height + 1, Height + 1);
            Assert.True(string.IsNullOrEmpty(result.Error), result.Error);
            var wallet = ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!;
            Assert.Equal(new[] { 1M, 5M }, wallet.UnspentCommitments.Select(n => n.Amount).OrderBy(a => a));
            Assert.Equal(6M, wallet.ShieldedBalances["VFX"]);

            // Marking the small note spent leaves the real one.
            PrivacyApiHelper.MarkInputsSpentLocally(_alice.ZfxAddress, new[] { wallet.UnspentCommitments.Single(n => n.Amount == 1M) }, "VFX");
            wallet = ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!;
            Assert.False(wallet.UnspentCommitments.Single(n => n.Amount == 5M).IsSpent);
            Assert.Equal(5M, wallet.ShieldedBalances["VFX"]);
        }

        /// <summary>
        /// Wallets took a note's tree position from the first leaf with its commitment: a copy applied before it gave the
        /// note a position it cannot be spent from, and the browser scans took 0 (fifth review, round 25). The matched
        /// leaf's position is taken.
        /// </summary>
        [Fact]
        public async Task AWalletNote_TakesItsOwnLeafsPosition_NotACopysBeforeIt()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            var copy = Tampered(shield, (p, _) => p.Outs[0].NoteHashB64 = Convert.ToBase64String(PrivacyField.RandomCanonical()));
            await Apply(copy, Height + 1);
            await Apply(shield, Height + 1);
            PrivateTxPayloadCodec.TryDecode(shield.Data, out var payload, out _);
            var output = payload!.Outs[0];
            Assert.True(ShieldedNoteEncryption.TryOpen(Convert.FromBase64String(output.EncryptedNoteB64!), _alice.EncryptionPrivateKey32, out var plain, out _));
            Assert.True(ShieldedPlainNoteCodec.TryDeserializeUtf8(plain!, out var note, out _));
            var own = Db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).Find(x => x.Commitment == output.CommitmentB64).Single(r => r.NoteHash == output.NoteHashB64);

            Assert.True(PrivacyApiHelper.NoteMatchesOutput(output, 5M, Convert.FromBase64String(note!.RandomnessB64), _alice, "VFX", out var position));
            Assert.Equal(own.TreePosition, position); // the first leaf with the commitment is either, as LiteDB returns them
        }

        /// <summary>
        /// Block processing called the wallet scanner directly and waited on every local wallet's lock: a long manual
        /// scan, or a lock left held, stopped the node's block processing (fifth review, round 26). The scanner takes
        /// blocks on its own thread.
        /// </summary>
        [Fact]
        public async Task BlockProcessing_DoesNotWaitOnAWalletsLock()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            ShieldedWalletService.Upsert(new ShieldedWallet { ShieldedAddress = _alice.ZfxAddress, ViewingKey = _alice.ViewingKey32, IsViewOnly = true });
            var block = new Block { Height = Height + 1, StateRoot = "root", Timestamp = 1_700_000_000 + Height + 1, Hash = "auto", Transactions = new List<Transaction> { shield } };

            var held = ShieldedWalletLock.Acquire(_alice.ZfxAddress);   // a long scan holds the wallet
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ShieldedNoteAutoScanner.Enqueue(block);
            Assert.True(watch.ElapsedMilliseconds < 1_000, "block processing waited on the wallet's lock");
            Assert.Empty(ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!.UnspentCommitments);
            ShieldedWalletLock.Release(held);

            for (var i = 0; i < 100 && ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!.UnspentCommitments.Count == 0; i++)
                await Task.Delay(100);
            Assert.Equal(5M, Assert.Single(ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!.UnspentCommitments).Amount);
            // The scanner records how far it has scanned, to catch up after a restart (round 27).
            for (var i = 0; i < 50 && ShieldedNoteAutoScanner.ProcessedThrough() < block.Height; i++)
                await Task.Delay(100);
            Assert.True(ShieldedNoteAutoScanner.ProcessedThrough() >= block.Height);
        }

        /// <summary>
        /// A wallet marks a spend's notes spent when it sends it; one never mined (its root went stale, it was dropped)
        /// left them spent until a manual resync (fifth review, round 32). The scanner restores them once the nullifier
        /// is not spent on chain, nothing in the mempool claims it and enough blocks have passed; a mined one stays spent.
        /// </summary>
        [Fact]
        public async Task ANoteMarkedSpentForASpendNeverMined_IsRestored()
        {
            if (!ParamsLoaded()) return;
            var shield = ShieldFor(_alice, 5M);
            await Apply(shield, Height + 1);
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = Height + 1, StateRoot = "root", Timestamp = 1_700_000_000 + Height + 1, Hash = "unmined", Transactions = new List<Transaction> { shield } });
            ShieldedWalletService.Upsert(new ShieldedWallet { ShieldedAddress = _alice.ZfxAddress, ViewingKey = _alice.ViewingKey32, IsViewOnly = true });
            Assert.True(string.IsNullOrEmpty(PrivacyApiHelper.ResyncShieldedWallet(_alice.ZfxAddress, Height + 1, Height + 1).Error));
            UnspentCommitment Note() => Assert.Single(ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!.UnspentCommitments);
            decimal Balance() => ShieldedWalletService.FindByZfxAddress(_alice.ZfxAddress)!.ShieldedBalances["VFX"];
            async Task Scanned(long height)
            {
                ShieldedNoteAutoScanner.Enqueue(new Block { Height = height, Transactions = new List<Transaction>() });
                for (var i = 0; i < 100 && ShieldedNoteAutoScanner.ProcessedThrough() < height; i++)
                    await Task.Delay(100);
                Assert.True(ShieldedNoteAutoScanner.ProcessedThrough() >= height);
            }

            Globals.LastBlock = new Block { Height = Height + 1 };
            PrivacyApiHelper.MarkInputsSpentLocally(_alice.ZfxAddress, new[] { Note() }, "VFX");   // as a spend route does
            Assert.True(Note().IsSpent);
            Assert.Equal(0M, Balance());
            var nullifier = VfxPrivateTransactionBuilder.NullifierOf(Note(), _alice, PrivacyEpoch.ProofRulesActive(Height + 2))!;
            var claim = MempoolNullifierTracker.MakeKey("VFX", nullifier);

            await Scanned(Height + 10);                                           // too soon
            Assert.True(Note().IsSpent);
            // A claim counts while its spend is in the mempool (a claim whose holder has left it is stale).
            TransactionData.GetPool().InsertSafe(new Transaction { Hash = "still-pooled", FromAddress = "xPooled", ToAddress = "xPooled", Timestamp = VerifiedXCore.Utilities.TimeUtil.GetTime(), TransactionType = TransactionType.VFX_PRIVATE_TRANSFER });
            Globals.MempoolNullifiers[claim] = "still-pooled";
            await Scanned(Height + 1 + PrivacyApiHelper.UnminedSpendBlocks + 5);  // still claimed by a pooled spend
            Assert.True(Note().IsSpent);
            Globals.MempoolNullifiers.TryRemove(claim, out _);
            TransactionData.GetPool().DeleteManySafe(x => x.Hash == "still-pooled");
            await Scanned(Height + 1 + PrivacyApiHelper.UnminedSpendBlocks + 6);  // never mined: restored
            Assert.False(Note().IsSpent);
            Assert.Null(Note().SpendPendingSince);
            Assert.Equal(5M, Balance());

            // Mined: stays spent, and is settled.
            PrivacyApiHelper.MarkInputsSpentLocally(_alice.ZfxAddress, new[] { Note() }, "VFX");
            Assert.True(NullifierService.TryRecordNullifier(nullifier, "VFX", Height + 2, 1_700_000_000 + Height + 2));
            await Scanned(Height + 1 + PrivacyApiHelper.UnminedSpendBlocks + 60);
            Assert.True(Note().IsSpent);
            Assert.Null(Note().SpendPendingSince);
            Assert.Equal(0M, Balance());
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
