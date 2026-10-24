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
    /// On a synced node the next block needs its committee certificate on every path, the download path included, however
    /// long the chain has stalled. Mainnet 7,562,232 (23 October 2026): the download exemption returned once the tip was
    /// 120 s old, and after a stall nodes downloaded an uncertified round draft over the certified block. Startup catch-up
    /// keeps the exemption.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SyncedTipCertificateTests : IDisposable
    {
        private const long Tip = 100;
        private const string CertStep = "BlockValidatorService.ValidateBlock()-cert";

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorCaster = Globals.IsBlockCaster;
        private readonly long _priorCertEnforceHeight = Globals.CertEnforceHeight;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;

        public SyncedTipCertificateTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"synced_tip_cert_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            BlockchainData.ChainRef = "test-chain";
            Globals.IsBlockCaster = false;
            Globals.CertEnforceHeight = 1;
            // The tip is ten minutes old: the chain has stalled.
            Globals.LastBlock = new Block { Height = Tip, Hash = "tip-100", Timestamp = TimeUtil.GetTime() - 600 };
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.IsChainSynced = _priorSynced;
            Globals.IsBlockCaster = _priorCaster;
            Globals.CertEnforceHeight = _priorCertEnforceHeight;
            BlockchainData.ChainRef = _priorChainRef;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>A block at Tip + 1 that passes the producer signature and proof checks and carries no certificate.</summary>
        private static Block UncertifiedNextBlock()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var height = Tip + 1;
            var block = new Block
            {
                Height = height, Version = BlockVersionUtility.GetBlockVersion(height), PrevHash = "tip-100",
                Timestamp = TimeUtil.GetTime(), NumOfTx = 0, Validator = AccountData.GetHumanAddress(pub),
                ValidatorAnswer = ProofUtility.ComputeVrf(pub, height, "tip-100").ProofHash, ChainRefId = "test-chain",
                Transactions = new List<Transaction>(),
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, key, pub);
            return block;
        }

        /// <summary>The rollback steps one validation recorded.</summary>
        private static async Task<List<string>> StepsRefusedAt(Func<Task<bool>> validate)
        {
            var since = DateTime.UtcNow;
            Assert.False(await validate());
            return BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location).ToList();
        }

        [Fact]
        public async Task OnASyncedNode_ADownloadedNextBlockWithoutACertificate_IsRefusedAtTheCertificateStep_AfterAStall()
        {
            Globals.IsChainSynced = true;
            var block = UncertifiedNextBlock();

            var steps = await StepsRefusedAt(() => BlockValidatorService.ValidateBlock(block, false, blockDownloads: true));

            Assert.Equal(CertStep, steps.Last());
            Assert.Equal(Tip, Globals.LastBlock.Height);
        }

        [Fact]
        public async Task DuringStartupCatchUp_TheDownloadExemptionHolds()
        {
            Globals.IsChainSynced = false;
            var block = UncertifiedNextBlock();

            var since = DateTime.UtcNow;
            await BlockValidatorService.ValidateBlock(block, false, blockDownloads: true);

            Assert.DoesNotContain(CertStep, BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location));
        }

        [Fact]
        public void TheNextBlockOnASyncedNode_IsAtTheSyncedTip_HoweverOldTheTip()
        {
            Assert.True(BlockValidatorService.AtSyncedTip(Tip + 1, Tip, chainSynced: true));
            Assert.False(BlockValidatorService.AtSyncedTip(Tip + 1, Tip, chainSynced: false));
            Assert.False(BlockValidatorService.AtSyncedTip(Tip + 2, Tip, chainSynced: true));
            Assert.False(BlockValidatorService.AtSyncedTip(Tip, Tip, chainSynced: true));
        }
    }
}
