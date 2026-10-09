using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1, stage 2: the proof-rules epoch's pool state. At PrivateTxProofRulesHeight every pool is
    /// reset to the circuits' fixed-depth tree with the dummy note at leaf 0; from then on the ledger appends to that
    /// tree, the dummy nullifier is never recorded, the rebuild reproduces the same roots, and the mainnet supply
    /// correction no longer applies. Before the height nothing changes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PrivacyStage2_EpochTreeTests : IDisposable
    {
        private const long Height = 5000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight;
        private readonly long _priorSupplyGate;

        public PrivacyStage2_EpochTreeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ps2e_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorHeight = Globals.PrivateTxProofRulesHeight;
            _priorSupplyGate = Globals.PrivateTxSupplyRulesHeight;
            Globals.PrivateTxProofRulesHeight = Height;
            Globals.PrivateTxSupplyRulesHeight = 1;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxProofRulesHeight = _priorHeight;
            Globals.PrivateTxSupplyRulesHeight = _priorSupplyGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static LiteDB.LiteDatabase Db => DbContext.DB_Privacy;

        private static (byte[] G1, byte[] NoteHash, byte[] Rand) Note(ulong amountScaled)
        {
            var r = PrivacyField.RandomCanonical();
            var g1 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(amountScaled, r, g1));
            return (g1, PoseidonV1.NoteHash(amountScaled, r), r);
        }

        private static Transaction Shield(decimal amount, byte[] g1, byte[] noteHash, string from = "RSHIELDER")
        {
            var payload = new PrivateTxPayload
            {
                Asset = "VFX", Kind = "shield",
                Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1), NoteHashB64 = Convert.ToBase64String(noteHash) } },
                TransparentInput = from, TransparentAmount = amount,
            };
            return new Transaction { TransactionType = TransactionType.VFX_SHIELD, FromAddress = from, ToAddress = PrivacyConstants.ShieldedPoolAddress, Amount = amount, Data = PrivateTxPayloadCodec.SerializeToJson(payload), Hash = Guid.NewGuid().ToString("N"), Timestamp = 1 };
        }

        private static Transaction Unshield(decimal amount, IEnumerable<byte[]> nullifiers, byte[] root, (byte[] G1, byte[] NoteHash)? change = null, params long[] positions)
        {
            var payload = new PrivateTxPayload
            {
                Asset = "VFX", Kind = "unshield",
                NullsB64 = nullifiers.Select(Convert.ToBase64String).ToList(),
                MerkleRootB64 = Convert.ToBase64String(root), Fee = Globals.PrivateTxFixedFee,
                TransparentOutput = "RRECIPIENT", TransparentAmount = amount,
            };
            payload.SpentCommitmentTreePositions = positions.Length > 0 ? positions.ToList() : payload.NullsB64.Select((_, i) => (long)i).ToList();
            if (change != null)
                payload.Outs.Add(new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(change.Value.G1), NoteHashB64 = Convert.ToBase64String(change.Value.NoteHash) });
            return new Transaction { TransactionType = TransactionType.VFX_UNSHIELD, FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = "RRECIPIENT", Amount = amount, Data = PrivateTxPayloadCodec.SerializeToJson(payload), Hash = Guid.NewGuid().ToString("N"), Timestamp = 1, Signature = PrivacyConstants.PlonkSignatureSentinel };
        }

        private static Block BlockAt(long height, params Transaction[] txs) => new Block { Height = height, StateRoot = "root", Timestamp = 1_700_000_000 + height, Transactions = txs.ToList() };

        private static async Task ApplyPrivacy(Block block)
        {
            foreach (var tx in block.Transactions)
                await PrivateTxLedgerService.ApplyBlockTransactionAsync(tx, block, Db);
        }

        // ── The reset ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AtTheHeight_EveryPoolIsReset_ToTheFixedDepthTreeWithTheDummyLeaf()
        {
            // Pre-epoch pool with two legacy-shaped notes and a non-zero supply.
            var n1 = Note(100_000_000); var n2 = Note(200_000_000);
            await ApplyPrivacy(BlockAt(Height - 10, Shield(1M, n1.G1, n1.NoteHash), Shield(2M, n2.G1, n2.NoteHash)));
            var before = ShieldedPoolService.GetState("VFX", Db)!;
            Assert.Equal(3M, before.TotalShieldedSupply);
            Assert.Equal(2, before.TotalCommitments);
            var oldRoot = before.CurrentMerkleRoot;

            PrivacyEpochService.ApplyIfResetBlock(BlockAt(Height - 1), Db);
            Assert.Equal(oldRoot, ShieldedPoolService.GetState("VFX", Db)!.CurrentMerkleRoot); // not yet

            PrivacyEpochService.ApplyIfResetBlock(BlockAt(Height), Db);
            var after = ShieldedPoolService.GetState("VFX", Db)!;
            Assert.Equal(0M, after.TotalShieldedSupply);
            Assert.Equal(1, after.TotalCommitments);
            Assert.Equal(Height, after.LastUpdateHeight);
            var expectedRoot = new FixedDepthMerkleTree(new[] { PrivacyEpoch.DummyNoteHash }).Root();
            Assert.Equal(Convert.ToBase64String(expectedRoot), after.CurrentMerkleRoot);
            var leaves = Db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).Query().Where(x => x.AssetType == "VFX").ToList();
            var dummy = Assert.Single(leaves);
            Assert.Equal(0, dummy.TreePosition);
            Assert.Equal(Convert.ToBase64String(PrivacyEpoch.DummyNoteHash), dummy.NoteHash);
            Assert.Equal(Convert.ToBase64String(PrivacyEpoch.DummyCommitment), dummy.Commitment);
            Assert.Equal(PrivacyEpoch.DummyCommitment.Length, PlonkNative.G1CompressedSize);
        }

        /// <summary>Re-audit (9 Oct 2026): the reset is the VFX pool's; a vBTC pool (privacy refused since VbtcPrivacyDisableHeight) is left as history.</summary>
        [Fact]
        public void AtTheHeight_OnlyTheVfxPoolIsReset_VbtcPoolRowsStay()
        {
            const string vbtc = "VBTC:test-vault";
            var store = new ShieldedMerkleStore(vbtc, Db, fixedDepth: false);
            var n = Note(50_000_000);
            store.AppendCommitment(n.G1, n.NoteHash, Height - 10, 1);
            var vbtcBefore = Db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).Query().Where(x => x.AssetType == vbtc).ToList();
            Assert.Single(vbtcBefore);
            Assert.Contains(vbtc, PrivacyEpochService.PoolAssets(Db));

            PrivacyEpochService.ApplyIfResetBlock(BlockAt(Height), Db);

            var vbtcAfter = Db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).Query().Where(x => x.AssetType == vbtc).ToList();
            Assert.Equal(vbtcBefore.Single().Commitment, Assert.Single(vbtcAfter).Commitment);
            var vfx = Db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).Query().Where(x => x.AssetType == "VFX").ToList();
            Assert.Equal(Convert.ToBase64String(PrivacyEpoch.DummyNoteHash), Assert.Single(vfx).NoteHash);
            Assert.Equal(new[] { "VFX" }, PrivacyEpochService.ResetAssets);
        }

        // ── Appending in the epoch ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task EpochAppends_UseTheFixedDepthTree_AndDummyNullifierIsNeverRecorded()
        {
            PrivacyEpochService.ApplyIfResetBlock(BlockAt(Height), Db);
            var a = Note(300_000_000); var b = Note(200_000_000);
            await ApplyPrivacy(BlockAt(Height + 1, Shield(3M, a.G1, a.NoteHash), Shield(2M, b.G1, b.NoteHash)));

            var tree = new FixedDepthMerkleTree(new[] { PrivacyEpoch.DummyNoteHash, a.NoteHash, b.NoteHash });
            var state = ShieldedPoolService.GetState("VFX", Db)!;
            Assert.Equal(Convert.ToBase64String(tree.Root()), state.CurrentMerkleRoot);
            Assert.Equal(5M, state.TotalShieldedSupply);
            Assert.Equal(3, state.TotalCommitments);

            // The inclusion proof is the circuits' flat 32-level path and recomputes the same root natively.
            Assert.True(ShieldedPoolService.TryGetInclusionProof("VFX", 1, Db, out var proof, out var root, Height + 2));
            Assert.Equal(32 * 32, proof!.Length);
            Assert.Equal(tree.Root(), root);
            Assert.Equal(tree.Root(), PoseidonV1.RootFromPath(a.NoteHash, 1, proof));

            // A single-note spend of `a` with the dummy as second input: the real nullifier is recorded, the dummy's is not.
            var vk = PrivacyField.RandomCanonical();
            var realNullifier = PoseidonV1.Nullifier(vk, a.NoteHash, 1);
            var change = Note(300_000_000 - 100_000_000 - 300);
            await ApplyPrivacy(BlockAt(Height + 2, Unshield(1M, new[] { realNullifier, PrivacyEpoch.DummyNullifier }, tree.Root(), (change.G1, change.NoteHash), 1, 0)));
            Assert.True(NullifierService.IsNullifierSpentInDb(Convert.ToBase64String(realNullifier), "VFX", Db));
            Assert.False(NullifierService.IsNullifierSpentInDb(PrivacyEpoch.DummyNullifierB64, "VFX", Db));
            var leaf0 = Db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).FindOne(x => x.AssetType == "VFX" && x.TreePosition == 0);
            Assert.False(leaf0.IsSpent); // the dummy is never marked spent
            // A second single-note spend by another wallet carries the same dummy nullifier and still applies.
            var vk2 = PrivacyField.RandomCanonical();
            await ApplyPrivacy(BlockAt(Height + 3, Unshield(2M, new[] { PoseidonV1.Nullifier(vk2, b.NoteHash, 2), PrivacyEpoch.DummyNullifier }, tree.Root(), null, 2, 0)));
            Assert.False(NullifierService.IsNullifierSpentInDb(PrivacyEpoch.DummyNullifierB64, "VFX", Db));
            Assert.Equal(5M - 1M - 0.000003M - 2M - 0.000003M, ShieldedPoolService.GetState("VFX", Db)!.TotalShieldedSupply);
        }

        [Fact]
        public async Task BeforeTheHeight_NothingChanges()
        {
            var a = Note(100_000_000);
            await ApplyPrivacy(BlockAt(Height - 2, Shield(1M, a.G1, a.NoteHash)));
            var legacy = new ShieldedMerkleStore("VFX", Db, fixedDepth: false);
            legacy.LoadLeavesFromCommitments();
            Assert.Equal(Convert.ToBase64String(legacy.GetRootBytes()!), ShieldedPoolService.GetState("VFX", Db)!.CurrentMerkleRoot);
            Assert.NotEqual(Convert.ToBase64String(new FixedDepthMerkleTree(new[] { a.NoteHash }).Root()), ShieldedPoolService.GetState("VFX", Db)!.CurrentMerkleRoot);
        }

        // ── Rebuild reproduces live application ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task Rebuild_ReproducesTheEpochRoot_AndResetsEvenWithoutLaterPrivateTransactions()
        {
            var pre = Note(100_000_000);
            var a = Note(300_000_000);
            var blocks = new List<Block> { BlockAt(Height - 5, Shield(1M, pre.G1, pre.NoteHash)), BlockAt(Height + 4, Shield(3M, a.G1, a.NoteHash)) };
            // Live application in chain order: the pre-epoch shield, the reset block (no private tx), the epoch shield.
            await ApplyPrivacy(blocks[0]);
            PrivacyEpochService.ApplyIfResetBlock(BlockAt(Height), Db);
            await ApplyPrivacy(blocks[1]);
            var live = ShieldedPoolService.GetState("VFX", Db)!;

            var (ok, msg) = await PrivacyDbRebuildService.TryReplayPrivateBlocksAsync(blocks, Db, default, tipHeight: Height + 10);
            Assert.True(ok, msg);
            var rebuilt = ShieldedPoolService.GetState("VFX", Db)!;
            Assert.Equal(live.CurrentMerkleRoot, rebuilt.CurrentMerkleRoot);
            Assert.Equal(new FixedDepthMerkleTree(new[] { PrivacyEpoch.DummyNoteHash, a.NoteHash }).Root(), Convert.FromBase64String(rebuilt.CurrentMerkleRoot));
            Assert.Equal(3M, rebuilt.TotalShieldedSupply);

            // Only pre-epoch history but a tip past the height: the rebuild still starts the epoch.
            var (ok2, msg2) = await PrivacyDbRebuildService.TryReplayPrivateBlocksAsync(new[] { blocks[0] }, Db, default, tipHeight: Height + 1);
            Assert.True(ok2, msg2);
            var resetOnly = ShieldedPoolService.GetState("VFX", Db)!;
            Assert.Equal(Convert.ToBase64String(new FixedDepthMerkleTree(new[] { PrivacyEpoch.DummyNoteHash }).Root()), resetOnly.CurrentMerkleRoot);
            Assert.Equal(0M, resetOnly.TotalShieldedSupply);

            // Pre-epoch only, tip below the height: the legacy tree, untouched.
            var (ok3, msg3) = await PrivacyDbRebuildService.TryReplayPrivateBlocksAsync(new[] { blocks[0] }, Db, default, tipHeight: Height - 1);
            Assert.True(ok3, msg3);
            Assert.Equal(1M, ShieldedPoolService.GetState("VFX", Db)!.TotalShieldedSupply);
            Assert.Equal(1, ShieldedPoolService.GetState("VFX", Db)!.TotalCommitments);
        }

        // ── Supply floor in the epoch ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void SupplyCorrection_AppliesBeforeTheHeightOnly()
        {
            var prior = new Dictionary<string, decimal>(Globals.ShieldedSupplyCorrections, StringComparer.Ordinal);
            try
            {
                Globals.ShieldedSupplyCorrections["VFX"] = 100M;
                var pool = ShieldedPoolService.GetOrCreateState("VFX", Db);
                pool.TotalShieldedSupply = -40M;
                PrivacyDbContext.PoolState().Update(pool);
                Assert.Equal(60M, PrivateTxSupplyRules.CommittedSupply("VFX", Height - 1));
                Assert.Equal(-40M, PrivateTxSupplyRules.CommittedSupply("VFX", Height));
            }
            finally
            {
                Globals.ShieldedSupplyCorrections.Clear();
                foreach (var (k, v) in prior) Globals.ShieldedSupplyCorrections[k] = v;
            }
        }
    }
}
