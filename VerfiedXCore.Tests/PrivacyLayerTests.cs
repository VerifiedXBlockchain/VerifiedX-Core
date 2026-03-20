using LiteDB;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Privacy;

namespace VerfiedXCore.Tests
{
    [Collection("PrivacySequential")]
    public class PrivacyLayerTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly LiteDatabase _db;

        public PrivacyLayerTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "vfx_privacy_ut_" + Guid.NewGuid() + ".db");
            _db = new LiteDatabase(new ConnectionString { Filename = _dbPath, Connection = ConnectionType.Direct });
            PrivacyDbContext.EnsurePrivacyIndexes(_db);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { /* ignore */ }
        }

        [Fact]
        public void PrivacyDb_Indexes_AndCrud_Work()
        {
            var commitments = _db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
            var rec = new CommitmentRecord
            {
                Commitment = "dGVzdA==",
                AssetType = "VFX",
                TreePosition = 0,
                BlockHeight = 1,
                Timestamp = 100,
                IsSpent = false
            };
            commitments.Insert(rec);
            var found = commitments.FindOne(x => x.AssetType == "VFX" && x.TreePosition == 0);
            Assert.NotNull(found);
            Assert.Equal("dGVzdA==", found.Commitment);
        }

        [Fact]
        public void PlonkNative_Pedersen_RoundTrip()
        {
            var r = new byte[32];
            Array.Fill(r, (byte)9);
            var c = new byte[PlonkNative.G1CompressedSize];
            int code = PlonkNative.pedersen_commit(42, r, c);
            Assert.Equal(PlonkNative.Success, code);
            int v = PlonkNative.pedersen_verify(c, 42, r);
            Assert.Equal(1, v);
        }

        [Fact]
        public void PlonkNative_Nullifier_IsDeterministic()
        {
            var vk = new byte[32];
            Array.Fill(vk, (byte)3);
            var commitment = new byte[PlonkNative.G1CompressedSize];
            Array.Fill(commitment, (byte)5);
            var n1 = NullifierService.DeriveNullifier(vk, commitment, 7UL);
            var n2 = NullifierService.DeriveNullifier(vk, commitment, 7UL);
            Assert.Equal(n1, n2);
        }

        [Fact]
        public void ShieldedMerkleStore_PersistsNodesAndPoolState()
        {
            var store = new ShieldedMerkleStore("VFX", _db);
            var r = new byte[32];
            Array.Fill(r, (byte)11);
            var g1 = new byte[PlonkNative.G1CompressedSize];
            int pc = PlonkNative.pedersen_commit(100, r, g1);
            Assert.Equal(PlonkNative.Success, pc);

            store.AppendG1Commitment(g1, 1, 200);
            store.UpdatePoolStateRoot(1, 100m, 1);

            var nodes = _db.GetCollection<MerkleTreeNodeRecord>(PrivacyDbContext.PRIV_MERKLE_NODES);
            Assert.True(nodes.Count() >= 1);
            var pool = _db.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);
            var st = pool.FindOne(x => x.AssetType == "VFX");
            Assert.NotNull(st);
            Assert.False(string.IsNullOrEmpty(st.CurrentMerkleRoot));
        }

        [Fact]
        public void NullifierService_UniqueConstraint_Respected()
        {
            const string n = "bnVsbGlmLXRlc3Q=";
            Assert.True(NullifierService.TryRecordNullifier(n, "VFX", 1, 1, _db));
            Assert.False(NullifierService.TryRecordNullifier(n, "VFX", 1, 2, _db));
            Assert.True(NullifierService.IsNullifierSpentInDb(n, "VFX", _db));
        }

        [Fact]
        public void PlonkNative_Verify_IsStub()
        {
            int code = PlonkNative.plonk_verify(0, Array.Empty<byte>(), 0, Array.Empty<byte>(), 0);
            Assert.Equal(PlonkNative.ErrNotImplemented, code);
        }

        [Fact]
        public void PLONKSetup_IsProofVerificationNotImplemented()
        {
            Assert.False(PLONKSetup.IsProofVerificationImplemented);
        }

        [Fact]
        public void CommitmentMerkleTree_InclusionProof_RoundTrips()
        {
            var store = new ShieldedMerkleStore("VFX", _db);
            for (var i = 0; i < 3; i++)
            {
                var r = new byte[32];
                r[0] = (byte)(i + 1);
                var g1 = new byte[PlonkNative.G1CompressedSize];
                Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit((ulong)(10 + i), r, g1));
                store.AppendG1Commitment(g1, 1, 100 + i);
            }

            Assert.True(store.TryGetInclusionProof(1, out var proof, out var root));
            var rec1 = _db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS)
                .FindOne(x => x.AssetType == "VFX" && x.TreePosition == 1);
            Assert.NotNull(rec1);
            var leaf = CommitmentMerkleTree.LeafDigest(Convert.FromBase64String(rec1!.Commitment));

            Assert.True(CommitmentMerkleTree.VerifyInclusionProof(leaf, 1, 3, proof, root));
            Assert.Equal(0, PrivacyMerklePolicy.GetExpectedProofSizeBytes(1));
            Assert.Equal(32 * 2, PrivacyMerklePolicy.GetExpectedProofSizeBytes(3));
        }

        [Fact]
        public void PlonkNative_PedersenCommitmentAdd_Succeeds()
        {
            var r1 = new byte[32];
            r1[0] = 9;
            var r2 = new byte[32];
            r2[0] = 11;
            var c1 = new byte[PlonkNative.G1CompressedSize];
            var c2 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(5, r1, c1));
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(7, r2, c2));
            var sum = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commitment_add(c1, c2, sum));
        }

        [Fact]
        public async Task PrivacyDbRebuildService_RebuildMerkleFromDb_Works()
        {
            var store = new ShieldedMerkleStore("VFX", _db);
            var r = new byte[32];
            Array.Fill(r, (byte)2);
            var g1 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(1, r, g1));
            store.AppendG1Commitment(g1, 1, 1);

            var (ok, msg) = await PrivacyDbRebuildService.TryRebuildMerkleStateFromDbAsync("VFX", _db);
            Assert.True(ok, msg);
            Assert.Contains("commitments=1", msg);
        }
    }
}
