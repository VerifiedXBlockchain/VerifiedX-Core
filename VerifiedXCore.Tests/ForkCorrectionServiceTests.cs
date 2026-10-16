using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A fork correction rolls back only for a block that is the one the message names, hashes to its own hash and
    /// carries a valid committee certificate, and it goes through the shared bounded reorg, which applies the block.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ForkCorrectionServiceTests : IDisposable
    {
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly Func<Block, bool> _priorCert = ForkCorrectionService.HasValidCertificate;
        private readonly Func<long, string, string, string, Block?, Task<bool>> _priorReorg = ForkCorrectionService.Reorg;
        private readonly List<(long Height, string Hash, Block? Known)> _reorgs = new();
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ForkCorrectionServiceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fork_correction_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            BlockchainData.ChainRef = "test-chain";
            Globals.LastBlock = new Block { Height = 100, Hash = "our-tip-100" };
            ForkCorrectionService.Reorg = (height, hash, ip, caller, known) => { _reorgs.Add((height, hash, known)); return Task.FromResult(true); };
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            BlockchainData.ChainRef = _priorChainRef;
            ForkCorrectionService.HasValidCertificate = _priorCert;
            ForkCorrectionService.Reorg = _priorReorg;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block SignedBlock(long height)
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var block = new Block
            {
                Height = height, Version = 4, PrevHash = $"prev-{height - 1}", Timestamp = 1_700_000_000 + height,
                NumOfTx = 0, Validator = AccountData.GetHumanAddress(pub), ValidatorAnswer = "a", ChainRefId = "test-chain",
                Transactions = new List<Transaction>(),
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, key, pub);
            return block;
        }

        [Fact]
        public async Task ASelfSignedBlockWithoutACertificate_CausesNoRollback()
        {
            ForkCorrectionService.HasValidCertificate = _ => false;

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(SignedBlock(100)), "1.2.3.4");
            await ForkCorrectionService.HandleForkCorrectionAsync(91, JsonConvert.SerializeObject(SignedBlock(91)), "1.2.3.4");

            Assert.Empty(_reorgs);
            Assert.Equal("our-tip-100", Globals.LastBlock.Hash);
        }

        [Fact]
        public async Task AMessageHeightOtherThanTheBlocks_OrAHashTheBlockDoesNotHaveTo_IsRefused()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;

            // The block is for 100, the message claims 91 (a deeper rollback).
            await ForkCorrectionService.HandleForkCorrectionAsync(91, JsonConvert.SerializeObject(SignedBlock(100)), "1.2.3.4");

            // A signed hash the block's content does not produce.
            var tampered = SignedBlock(100);
            tampered.Timestamp += 1;
            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(tampered), "1.2.3.4");

            Assert.Empty(_reorgs);
        }

        [Fact]
        public async Task ACertifiedHeaderWithOtherTransactions_IsRefusedBeforeAnyRollback()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var altered = SignedBlock(100);
            altered.Transactions = new List<Transaction> { new Transaction { Hash = "inserted", FromAddress = "xA", ToAddress = "xB", Amount = 1M } };

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(altered), "1.2.3.4");

            Assert.Empty(_reorgs);
        }

        [Fact]
        public async Task ACertifiedCorrection_ReplacesAStaleCasterApprovedHash()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var prior = new System.Collections.Generic.Dictionary<long, string>(Globals.CasterApprovedBlockHashDict);
            try
            {
                Globals.CasterApprovedBlockHashDict[95] = "stale-95";
                Globals.CasterApprovedBlockHashDict[97] = "stale-97";
                var below = SignedBlock(95);

                await ForkCorrectionService.HandleForkCorrectionAsync(95, JsonConvert.SerializeObject(below), "1.2.3.4");

                Assert.Equal(below.Hash, Globals.CasterApprovedBlockHashDict[95]);
                Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(97));
            }
            finally
            {
                Globals.CasterApprovedBlockHashDict.Clear();
                foreach (var kv in prior) Globals.CasterApprovedBlockHashDict[kv.Key] = kv.Value;
            }
        }

        [Fact]
        public async Task ACertifiedCorrection_GoesThroughTheReorg_WithTheBlockItCarries()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var atTip = SignedBlock(100);
            var below = SignedBlock(95);

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(atTip), "1.2.3.4");
            await ForkCorrectionService.HandleForkCorrectionAsync(95, JsonConvert.SerializeObject(below), "1.2.3.4");

            Assert.Equal(2, _reorgs.Count);
            Assert.Equal((100L, atTip.Hash), (_reorgs[0].Height, _reorgs[0].Hash));
            Assert.Equal(atTip.Hash, _reorgs[0].Known!.Hash);
            Assert.Equal((95L, below.Hash), (_reorgs[1].Height, _reorgs[1].Hash));
        }

        [Fact]
        public async Task TheSameBlockWeHold_IsANoOp()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var ours = SignedBlock(100);
            Globals.LastBlock = ours;

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(ours), "1.2.3.4");

            Assert.Empty(_reorgs);
        }
    }
}
