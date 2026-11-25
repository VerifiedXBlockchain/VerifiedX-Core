using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From VrfMixHeight a block carries its producer's VRF proof over its height's seed and the chain's running mix, both
    /// in its hash, and an epoch is seeded from its anchor's mix. A producer that builds its anchor block again, with other
    /// transactions or other header fields, gets another block hash but the same mix: the next epoch's producers, the
    /// emergency committee and the Base signer order no longer move with what the anchor's producer puts in its block.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BlockVrfTests : IDisposable
    {
        private const long Tip = 100;
        private const string VrfStep = "BlockValidatorService.ValidateBlock()-vrf";

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorCaster = Globals.IsBlockCaster;
        private readonly long _priorCertEnforceHeight = Globals.CertEnforceHeight;
        private readonly long _priorVrfSeedHeight = Globals.VrfSeedHeight;
        private readonly long _priorVrfMixHeight = Globals.VrfMixHeight;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Func<long, string?> _priorHashAt = VrfSeed.BlockHashAt;
        private readonly Func<long, string?> _priorMixAt = VrfSeed.MixAt;
        private readonly string _tempRoot;

        public BlockVrfTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"block_vrf_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            BlockchainData.ChainRef = "test-chain";
            Globals.IsBlockCaster = false;
            Globals.IsChainSynced = false;
            Globals.CertEnforceHeight = long.MaxValue;
            Globals.VrfSeedHeight = 1;
            Globals.VrfMixHeight = Tip + 1;
            VrfSeed.BlockHashAt = h => $"hash-{h}";
            VrfSeed.MixAt = h => $"mix-{h}";
            Globals.LastBlock = new Block { Height = Tip, Hash = "tip-100", Timestamp = TimeUtil.GetTime() - 10 };
        }

        public void Dispose()
        {
            VrfSeed.BlockHashAt = _priorHashAt;
            VrfSeed.MixAt = _priorMixAt;
            Globals.VrfSeedHeight = _priorVrfSeedHeight;
            Globals.VrfMixHeight = _priorVrfMixHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.IsChainSynced = _priorSynced;
            Globals.IsBlockCaster = _priorCaster;
            Globals.CertEnforceHeight = _priorCertEnforceHeight;
            BlockchainData.ChainRef = _priorChainRef;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private sealed record Producer(PrivateKey Key, string Pub, string Address)
        {
            public string SecretHex => Key.secret.ToString("x");
        }

        private static Producer NewProducer()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return new Producer(key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>The next block, its VRF fields set as a producer sets them (or as <paramref name="bend"/> changes them), hashed and signed.</summary>
        private static Block NextBlock(Producer producer, Action<Block>? bend = null, Producer? prover = null, long timestampOffset = 0)
        {
            var height = Tip + 1;
            var block = new Block
            {
                Height = height, Version = BlockVersionUtility.GetBlockVersion(height), PrevHash = "tip-100",
                Timestamp = TimeUtil.GetTime() + timestampOffset, NumOfTx = 0, Validator = producer.Address,
                ValidatorAnswer = ProofUtility.ComputeVrf(producer.Pub, height, "tip-100").ProofHash, ChainRefId = "test-chain",
                Transactions = new List<Transaction>(),
            };
            Assert.True(BlockVrf.Attach(block, Globals.LastBlock, (prover ?? producer).SecretHex));
            bend?.Invoke(block);
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, producer.Key, producer.Pub);
            return block;
        }

        private static async Task<List<string>> StepsOf(Block block)
        {
            var since = DateTime.UtcNow;
            await BlockValidatorService.ValidateBlock(block, false, blockDownloads: true);
            return BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location).ToList();
        }

        [Fact]
        public async Task A_block_with_its_producers_proof_and_mix_passes_the_vrf_step()
        {
            var block = NextBlock(NewProducer());
            Assert.Null(BlockVrf.Refusal(block, BlockVrf.PreviousOf(block)));
            Assert.DoesNotContain(VrfStep, await StepsOf(block));
            Assert.Equal(Ecvrf.ProofLength * 2, block.VrfProof!.Length);
        }

        public static TheoryData<string> Bends => new() { "no proof", "no mix", "another key's proof", "a mix that does not follow", "a proof whose checks are the identity" };

        [Theory]
        [MemberData(nameof(Bends))]
        public async Task A_block_whose_vrf_fields_are_not_its_producers_is_refused_at_the_vrf_step(string bend)
        {
            var producer = NewProducer();
            var block = bend switch
            {
                "no proof" => NextBlock(producer, b => b.VrfProof = null),
                "no mix" => NextBlock(producer, b => b.VrfMix = null),
                "another key's proof" => NextBlock(producer, prover: NewProducer()),
                "a proof whose checks are the identity" => NextBlock(producer, b => b.VrfProof = b.VrfProof![..66] + new string('0', (Ecvrf.ProofLength - 33) * 2)),
                _ => NextBlock(producer, b => b.VrfMix = new string('0', 64)),
            };
            Assert.NotNull(BlockVrf.Refusal(block, BlockVrf.PreviousOf(block)));
            Assert.Equal(VrfStep, (await StepsOf(block)).Last());
            Assert.Equal(Tip, Globals.LastBlock.Height);
        }

        [Fact]
        public void A_copy_with_its_proof_changed_or_left_out_is_another_block()
        {
            var block = NextBlock(NewProducer());
            var hash = block.GetBlockHash();
            var proof = block.VrfProof;
            block.VrfProof = null;
            Assert.NotEqual(hash, block.GetBlockHash());
            block.VrfProof = proof;
            block.VrfMix = new string('1', 64);
            Assert.NotEqual(hash, block.GetBlockHash());
        }

        [Fact]
        public async Task Below_the_height_nothing_changes()
        {
            Globals.VrfMixHeight = Tip + 2;
            var block = NextBlock(NewProducer());
            Assert.Null(block.VrfProof);
            Assert.Null(block.VrfMix);
            Assert.Null(BlockVrf.Refusal(block, BlockVrf.PreviousOf(block)));
            Assert.DoesNotContain(VrfStep, await StepsOf(block));
            var hash = block.GetBlockHash();
            block.VrfProof = "ff"; // not part of a block below the height
            Assert.Equal(hash, block.GetBlockHash());
        }

        [Fact]
        public void A_producer_that_builds_its_anchor_again_moves_its_hash_but_not_the_next_epochs_seed()
        {
            var producer = NewProducer();
            var first = NextBlock(producer);
            var again = NextBlock(producer, timestampOffset: 1); // other contents: another block
            Assert.NotEqual(first.Hash, again.Hash);
            Assert.Equal(first.VrfMix, again.VrfMix);

            // Anchored on either copy, the epoch's seed is the same from the VRF height, where before it the hash decided it.
            // Epochs start at 201 here, so the block is the anchor of the epoch from 201.
            var anchor = first.Height;
            Globals.VrfSeedHeight = anchor + Globals.VrfAnchorLagBlocks;
            var epochHeight = Globals.VrfSeedHeight;
            Assert.Equal(anchor, VrfSeed.AnchorHeight(epochHeight));
            string? SeedOn(Block anchored) { VrfSeed.BlockHashAt = h => h == anchor ? anchored.Hash : $"hash-{h}"; VrfSeed.MixAt = h => h == anchor ? anchored.VrfMix : $"mix-{h}"; return VrfSeed.SeedFor(epochHeight); }
            Assert.Equal(SeedOn(first), SeedOn(again));

            Globals.VrfMixHeight = long.MaxValue;
            Assert.NotEqual(SeedOn(first), SeedOn(again));
        }

        [Fact]
        public void Every_draw_from_a_block_reads_its_seed_material_not_its_hash()
        {
            // The emergency committee, the rotation, the reach set and the Base signer order: none ranks by a block hash.
            var root = Path.Combine(RepoRoot(), "VerifiedXCore");
            var vree = File.ReadAllText(Path.Combine(root, "Services", "VreeElection.cs"));
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(vree, @"BuildEpoch\([^;]*VrfSeed\.SeedMaterialAt\(").Count);
            Assert.DoesNotMatch(@"BuildEpoch\([^;]*VrfSeed\.BlockHashAt\(", vree);
            var policy = File.ReadAllText(Path.Combine(root, "Bitcoin", "Services", "BaseSignerSetPolicy.cs"));
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(policy, @"VrfSeed\.SeedMaterialAt\(epochHeight\)").Count);
            Assert.DoesNotMatch(@"Compute\w*\(epochHeight, block\.Hash", policy);
            var sync = File.ReadAllText(Path.Combine(root, "Bitcoin", "Services", "BaseValidatorSyncService.cs"));
            Assert.Contains("var epochHash = VerifiedXCore.Utilities.VrfSeed.SeedMaterialAt(epoch);", sync);
        }

        [Fact]
        public void Both_blocks_a_producer_makes_carry_the_proof_before_they_are_hashed()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "VerifiedXCore", "Data", "BlockchainData.cs"));
            var craft = source.IndexOf("public static async Task<Block?> CraftBlock_V5(", StringComparison.Ordinal);
            Assert.True(craft > 0);
            var body = source[craft..];
            var at = 0;
            for (var i = 0; i < 2; i++)
            {
                var attach = body.IndexOf("BlockVrf.Attach(block, Globals.LastBlock", at, StringComparison.Ordinal);
                var build = body.IndexOf("block.Build();", at, StringComparison.Ordinal);
                Assert.True(attach > 0 && attach < build, $"block {i + 1}: the proof is attached before the block is hashed");
                at = build + 1;
            }
            // The production loop makes its blocks here and nowhere else.
            var node = File.ReadAllText(Path.Combine(RepoRoot(), "VerifiedXCore", "Nodes", "ValidatorNode.cs"));
            Assert.Contains("BlockchainData.CraftBlock_V5(", node);
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
    }
}
