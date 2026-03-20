using LiteDB;
using Newtonsoft.Json;
using System.Security.Cryptography;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Utilities;

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

        [Fact]
        public void ShieldedAddressCodec_RoundTrip_EncodeDecode()
        {
            var key = new byte[ShieldedAddressConstants.EncryptionKeyLength];
            for (var i = 0; i < key.Length; i++)
                key[i] = (byte)(i + 1);

            var addr = ShieldedAddressCodec.EncodeEncryptionKey(key);
            Assert.StartsWith(ShieldedAddressConstants.Prefix, addr, StringComparison.Ordinal);

            Assert.True(ShieldedAddressCodec.TryDecodeEncryptionKey(addr, out var decoded, out var err), err);
            Assert.NotNull(decoded);
            Assert.Equal(key, decoded);
            Assert.True(ShieldedAddressCodec.IsWellFormed(addr));
        }

        [Fact]
        public void ShieldedAddressCodec_TryDecode_RejectsWrongPrefix()
        {
            var key = new byte[ShieldedAddressConstants.EncryptionKeyLength];
            Array.Fill(key, (byte)7);
            var good = ShieldedAddressCodec.EncodeEncryptionKey(key);
            var bad = "zbx_" + good.Substring(ShieldedAddressConstants.Prefix.Length);
            Assert.False(ShieldedAddressCodec.TryDecodeEncryptionKey(bad, out _, out var e));
            Assert.Contains("zfx_", e, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShieldedAddressCodec_TryDecode_RejectsBadChecksum()
        {
            var key = new byte[ShieldedAddressConstants.EncryptionKeyLength];
            Array.Fill(key, (byte)42);
            var good = ShieldedAddressCodec.EncodeEncryptionKey(key);
            var last = good[^1];
            var tampered = good[..^1] + (char)(last == '1' ? '2' : '1');
            Assert.False(ShieldedAddressCodec.TryDecodeEncryptionKey(tampered, out _, out var e));
            Assert.NotNull(e);
        }

        [Fact]
        public void ShieldedAddressCodec_Encode_ThrowsOnWrongKeyLength()
        {
            Assert.Throws<ArgumentException>(() => ShieldedAddressCodec.EncodeEncryptionKey(new byte[32]));
        }

        [Fact]
        public void AddressValidateUtility_AcceptsWellFormedZfx()
        {
            var key = new byte[ShieldedAddressConstants.EncryptionKeyLength];
            Array.Fill(key, (byte)0xab);
            var addr = ShieldedAddressCodec.EncodeEncryptionKey(key);
            Assert.True(AddressValidateUtility.ValidateAddress(addr));
        }

        [Fact]
        public void AddressValidateUtility_RejectsMalformedZfx()
        {
            Assert.False(AddressValidateUtility.ValidateAddress("zfx_notbase58!!!"));
            Assert.False(AddressValidateUtility.ValidateAddress("zfx_"));
        }

        [Fact]
        public void ShieldedHdDerivation_Path_AndAddress_AreDeterministic()
        {
            const string seedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
            var path = ShieldedHdDerivation.FormatDerivationPath(ShieldedAddressConstants.DefaultBip44CoinType, 0);
            Assert.Equal("m/44'/889'/0'/1'/0'", path);

            var addr1 = ShieldedHdDerivation.DeriveZfxAddress(seedHex, ShieldedAddressConstants.DefaultBip44CoinType, 0);
            var addr2 = ShieldedHdDerivation.DeriveZfxAddress(seedHex, ShieldedAddressConstants.DefaultBip44CoinType, 0);
            Assert.Equal(addr1, addr2);
            Assert.True(ShieldedAddressCodec.TryDecodeEncryptionKey(addr1, out var ek, out _), ek!.Length.ToString());
            Assert.Equal(ShieldedAddressConstants.EncryptionKeyLength, ek!.Length);
            Assert.NotEqual(addr1, ShieldedHdDerivation.DeriveZfxAddress(seedHex, ShieldedAddressConstants.DefaultBip44CoinType, 1));
        }

        [Fact]
        public void ShieldedNoteEncryption_SealAndOpen_RoundTrip()
        {
            const string seedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
            var m = ShieldedHdDerivation.DeriveShieldedKeyMaterial(seedHex, ShieldedAddressConstants.DefaultBip44CoinType, 2);
            var sealedBytes = ShieldedNoteEncryption.SealUtf8("hello-shielded", m.ZfxAddress);
            Assert.True(ShieldedNoteEncryption.TryOpenUtf8(sealedBytes, m.EncryptionPrivateKey32, out var text, out var err), err);
            Assert.Equal("hello-shielded", text);
        }

        [Fact]
        public void PrivateTxPayload_TryValidateStructure_RejectsNullsWithoutSpentPositions()
        {
            var p = new PrivateTxPayload
            {
                Asset = "VFX",
                Kind = "z2z",
                NullsB64 = { Convert.ToBase64String(new byte[32]) }
            };
            Assert.False(p.TryValidateStructure(out var err));
            Assert.Contains("spent_tree_positions", err ?? "");
        }

        [Fact]
        public void PrivateTxPayload_TryValidateStructure_RejectsShortNote()
        {
            var r = new byte[32];
            Array.Fill(r, (byte)1);
            var g1 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(1, r, g1));
            var p = new PrivateTxPayload
            {
                Asset = "VFX",
                Outs =
                {
                    new PrivateShieldedOutput
                    {
                        Index = 0,
                        CommitmentB64 = Convert.ToBase64String(g1),
                        EncryptedNoteB64 = Convert.ToBase64String(new byte[20])
                    }
                }
            };
            Assert.False(p.TryValidateStructure(out _));
        }

        [Fact]
        public void ShieldedRecoveryScanService_FindsNoteInBlock()
        {
            const string seedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
            var m = ShieldedHdDerivation.DeriveShieldedKeyMaterial(seedHex, ShieldedAddressConstants.DefaultBip44CoinType, 3);
            var note = ShieldedNoteEncryption.SealUtf8("scan-me", m.ZfxAddress);

            var r = new byte[32];
            Array.Fill(r, (byte)2);
            var g1 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(2, r, g1));

            var payload = new PrivateTxPayload
            {
                Asset = "VFX",
                Kind = "z2z",
                Outs =
                {
                    new PrivateShieldedOutput
                    {
                        Index = 0,
                        CommitmentB64 = Convert.ToBase64String(g1),
                        EncryptedNoteB64 = Convert.ToBase64String(note)
                    }
                },
                NullsB64 = { Convert.ToBase64String(new byte[32]) },
                SpentCommitmentTreePositions = { 0L }
            };
            Assert.True(payload.TryValidateStructure(out var ve), ve);
            var tx = new Transaction { Data = JsonConvert.SerializeObject(payload) };
            var block = new Block { Height = 1, Transactions = new List<Transaction> { tx } };
            var hits = ShieldedRecoveryScanService.ScanBlocksForNotes(new[] { block }, m.EncryptionPrivateKey32).ToList();
            Assert.Single(hits);
            Assert.Equal("scan-me", System.Text.Encoding.UTF8.GetString(hits[0].Plaintext));
        }

        [Fact]
        public void ShieldedSpendingKeyProtector_RoundTrip()
        {
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var blob = ShieldedSpendingKeyProtector.Protect(key, "testpassword123");
            Assert.True(ShieldedSpendingKeyProtector.TryUnprotect(blob, "testpassword123", out var back, out _), "unwrap");
            Assert.Equal(key, back);
        }

        [Fact]
        public void ShieldedWalletService_UpsertAndFind_WorksOnPrivacyDb()
        {
            const string seedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
            var m = ShieldedHdDerivation.DeriveShieldedKeyMaterial(seedHex, ShieldedAddressConstants.DefaultBip44CoinType, 4);
            var w = ShieldedWalletService.CreateFromKeyMaterial(m, "VFX_test_transparent", "walletpass123456");
            ShieldedWalletService.Upsert(w, _db);
            var loaded = ShieldedWalletService.FindByZfxAddress(m.ZfxAddress, _db);
            Assert.NotNull(loaded);
            Assert.True(ShieldedWalletService.TryUnwrapSpendingBundle(loaded!, "walletpass123456", out var spend, out var enc, out var uerr), uerr);
            Assert.Equal(m.SpendingKey32, spend);
            Assert.Equal(m.EncryptionPrivateKey32, enc);
        }

        [Fact]
        public void PrivateTxPayloadCodec_DecodesJsonAndBase64()
        {
            var inner = "{\"v\":1,\"asset\":\"VFX\",\"outs\":[{\"i\":0,\"c\":\"dGVzdA==\"}]}";
            Assert.True(PrivateTxPayloadCodec.TryDecode(inner, out var p1, out _), "json");
            Assert.NotNull(p1);
            Assert.Equal("VFX", p1!.Asset);

            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(inner));
            Assert.True(PrivateTxPayloadCodec.TryDecode(b64, out var p2, out _), "b64");
            Assert.Equal("VFX", p2!.Asset);
        }

        [Fact]
        public async Task PrivacyDbRebuildService_ReplayFromSyntheticBlocks_Works()
        {
            var r = new byte[32];
            Array.Fill(r, (byte)2);
            var g1 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(1, r, g1));

            var payload = new PrivateTxPayload
            {
                Asset = "VFX",
                Kind = "z2z",
                Outs =
                {
                    new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1) }
                },
                NullsB64 = { Convert.ToBase64String(new byte[32]) },
                SpentCommitmentTreePositions = { 0L }
            };
            var json = JsonConvert.SerializeObject(payload);

            var tx = new Transaction
            {
                Timestamp = 100,
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0,
                Fee = 0,
                Nonce = 0,
                TransactionType = TransactionType.VFX_PRIVATE_TRANSFER,
                Signature = PrivacyConstants.PlonkSignatureSentinel,
                Data = json
            };
            tx.BuildPrivate();

            var block = new Block { Height = 3, Transactions = new List<Transaction> { tx } };

            var (ok, msg) = await PrivacyDbRebuildService.TryReplayPrivateBlocksAsync(new[] { block }, _db);
            Assert.True(ok, msg);

            var commitments = _db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
            Assert.Equal(1, commitments.Count(x => x.AssetType == "VFX"));

            var nulls = _db.GetCollection<NullifierRecord>(PrivacyDbContext.PRIV_NULLIFIERS);
            Assert.Equal(1, nulls.Count(x => x.AssetType == "VFX"));
        }

        [Fact]
        public async Task PrivacyDbRebuildService_Replay_MarksCommitmentSpentWhenNullifierWithPosition()
        {
            var r1 = new byte[32];
            Array.Fill(r1, (byte)2);
            var g1Shield = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(1, r1, g1Shield));

            var shieldPayload = new PrivateTxPayload
            {
                Asset = "VFX",
                Kind = "t2z",
                Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1Shield) } }
            };
            var shieldTx = new Transaction
            {
                Timestamp = 100,
                FromAddress = "VFX_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 1.0m,
                Fee = 0.000003m,
                Nonce = 0,
                TransactionType = TransactionType.VFX_SHIELD,
                Signature = "sig",
                Data = JsonConvert.SerializeObject(shieldPayload)
            };
            shieldTx.BuildPrivate();

            var r2 = new byte[32];
            Array.Fill(r2, (byte)3);
            var g1Out = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(2, r2, g1Out));

            var spendPayload = new PrivateTxPayload
            {
                Asset = "VFX",
                Kind = "z2z",
                Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1Out) } },
                NullsB64 = { Convert.ToBase64String(new byte[32]) },
                SpentCommitmentTreePositions = { 0L }
            };
            var spendTx = new Transaction
            {
                Timestamp = 101,
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0,
                Fee = 0,
                Nonce = 0,
                TransactionType = TransactionType.VFX_PRIVATE_TRANSFER,
                Signature = PrivacyConstants.PlonkSignatureSentinel,
                Data = JsonConvert.SerializeObject(spendPayload)
            };
            spendTx.BuildPrivate();

            var blockShield = new Block { Height = 3, Transactions = new List<Transaction> { shieldTx } };
            var blockSpend = new Block { Height = 4, Transactions = new List<Transaction> { spendTx } };

            var (ok, msg) = await PrivacyDbRebuildService.TryReplayPrivateBlocksAsync(new[] { blockShield, blockSpend }, _db);
            Assert.True(ok, msg);

            var commitments = _db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
            var leaf0 = commitments.FindOne(x => x.AssetType == "VFX" && x.TreePosition == 0);
            Assert.NotNull(leaf0);
            Assert.True(leaf0!.IsSpent, "commitment at position 0 should be marked spent after nullifier + spent_tree_positions");
            var leaf1 = commitments.FindOne(x => x.AssetType == "VFX" && x.TreePosition == 1);
            Assert.NotNull(leaf1);
            Assert.False(leaf1!.IsSpent);
        }

        [Fact]
        public async Task PrivateTxLedgerService_AppendsCommitmentsToPrivacyDb()
        {
            var r = new byte[32];
            Array.Fill(r, (byte)7);
            var g1 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(3, r, g1));

            var payload = new PrivateTxPayload
            {
                Asset = "VFX",
                Kind = "t2z",
                Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1) } }
            };
            var json = JsonConvert.SerializeObject(payload);
            var tx = new Transaction
            {
                Timestamp = 50,
                FromAddress = "VFX_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 1.0m,
                Fee = 0.000003m,
                Nonce = 0,
                TransactionType = TransactionType.VFX_SHIELD,
                Signature = "sig",
                Data = json
            };
            tx.BuildPrivate();

            var block = new Block { Height = 9, StateRoot = "sr", Transactions = new List<Transaction>() };
            await PrivateTxLedgerService.ApplyBlockTransactionAsync(tx, block, _db);

            var commitments = _db.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS);
            Assert.Equal(1, commitments.Count(x => x.AssetType == "VFX"));
        }

        [Fact]
        public void MempoolNullifierTracker_BlocksDoubleSpendInMempool()
        {
            var n = Convert.ToBase64String(new byte[32]);
            var nulls = new List<string> { n };
            try
            {
                Assert.True(MempoolNullifierTracker.TryRegisterForMempool("tx-a", "VFX", nulls, out var e1), e1);
                Assert.False(MempoolNullifierTracker.TryRegisterForMempool("tx-b", "VFX", nulls, out _), "second tx same nullifier");
                MempoolNullifierTracker.ReleaseClaimsForTxHash("tx-a");
                Assert.True(MempoolNullifierTracker.TryRegisterForMempool("tx-b", "VFX", nulls, out var e2), e2);
            }
            finally
            {
                MempoolNullifierTracker.ReleaseClaimsForTxHash("tx-a");
                MempoolNullifierTracker.ReleaseClaimsForTxHash("tx-b");
            }
        }

        [Fact]
        public void MempoolNullifierTracker_BlockScopedSet_RejectsDuplicate()
        {
            var r = new byte[32];
            r[0] = 9;
            var g1 = new byte[PlonkNative.G1CompressedSize];
            Assert.Equal(PlonkNative.Success, PlonkNative.pedersen_commit(1, r, g1));
            var payload = new PrivateTxPayload
            {
                Asset = "VFX",
                Kind = "z2z",
                Outs = { new PrivateShieldedOutput { Index = 0, CommitmentB64 = Convert.ToBase64String(g1) } },
                NullsB64 = { Convert.ToBase64String(new byte[32]) },
                SpentCommitmentTreePositions = { 0L }
            };
            var json = JsonConvert.SerializeObject(payload);
            var tx = new Transaction
            {
                Timestamp = 1,
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0,
                Fee = 0,
                Nonce = 0,
                TransactionType = TransactionType.VFX_PRIVATE_TRANSFER,
                Signature = PrivacyConstants.PlonkSignatureSentinel,
                Data = json
            };
            tx.BuildPrivate();

            var set = new HashSet<string>();
            Assert.True(MempoolNullifierTracker.TryAddBlockScopedNullifiers(tx, set, out _));
            Assert.False(MempoolNullifierTracker.TryAddBlockScopedNullifiers(tx, set, out _), "same nullifier twice in block");
        }
    }
}
