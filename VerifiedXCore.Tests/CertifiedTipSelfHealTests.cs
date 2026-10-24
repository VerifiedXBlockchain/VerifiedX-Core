using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A blind self-heal (roll the tip back, download it again) does not roll back a tip that carries a valid committee
    /// certificate. Mainnet 7,562,232 (23 October 2026): a caster's self-heal rolled its certified block back again and
    /// again on a "majority" of uncommitted drafts. A certified competitor is fork choice's to weigh, not a self-heal's.
    /// </summary>
    [Collection("DbContextSequential")]
    public class CertifiedTipSelfHealTests : IDisposable
    {
        private const long Tip = 100;

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly long _priorCertEnforceHeight = Globals.CertEnforceHeight;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _casters = new();

        public CertifiedTipSelfHealTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"certified_tip_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            BlockchainData.ChainRef = "test-chain";
            Globals.CertEnforceHeight = 1;
            for (var i = 0; i < 3; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                _casters.Add((key, pub, AccountData.GetHumanAddress(pub)));
            }
            Globals.BlockCasters = new ConcurrentBag<Peers>(_casters.Select((c, i) => new Peers { ValidatorAddress = c.Address, PeerIP = $"10.8.0.{i + 1}" }));
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.BlockCasters = _priorCasters;
            Globals.CertEnforceHeight = _priorCertEnforceHeight;
            BlockchainData.ChainRef = _priorChainRef;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block SignedBlock(long height, string prevHash)
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var block = new Block
            {
                Height = height, Version = 4, PrevHash = prevHash, Timestamp = TimeUtil.GetTime() - 30, NumOfTx = 0,
                Validator = AccountData.GetHumanAddress(pub), ValidatorAnswer = "a", ChainRefId = "test-chain",
                Transactions = new List<Transaction>(),
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, key, pub);
            return block;
        }

        /// <summary>All three casters (a quorum of the five-seat legacy committee) sign the block.</summary>
        private void Certify(Block block)
        {
            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            block.ConsensusCertificate = new ConsensusCertificate
            {
                BlockHeight = block.Height, BlockHash = block.Hash, WinnerAddress = block.Validator, PrevHash = block.PrevHash,
                Attestations = _casters.Select(c => new CasterAttestation
                {
                    CasterAddress = c.Address, Signature = SignatureService.CreateSignature(msg, c.Key, c.Pub), Timestamp = TimeUtil.GetTime(),
                }).ToList(),
            };
        }

        /// <summary>Blocks 99 and 100 stored; the tip (100) certified or not.</summary>
        private Block StoredChain(bool certifiedTip)
        {
            var parent = SignedBlock(Tip - 1, "prev-98");
            var tip = SignedBlock(Tip, parent.Hash);
            if (certifiedTip) Certify(tip);
            BlockchainData.GetBlocks().InsertSafe(parent);
            BlockchainData.GetBlocks().InsertSafe(tip);
            Globals.LastBlock = tip;
            return tip;
        }

        private static Task CasterSelfHealAsync(long height) =>
            (Task)typeof(BlockcasterNode).GetMethod("ForkRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { height })!;

        [Fact(Timeout = 60_000)]
        public async Task TheSharedRecovery_DoesNotRollBackACertifiedTip()
        {
            var tip = StoredChain(certifiedTip: true);

            Assert.False(await ForkRecoveryUtility.RecoverAsync(Tip, "DesyncReconcile"));

            Assert.Equal(tip.Hash, Globals.LastBlock.Hash);
            Assert.Equal(tip.Hash, BlockchainData.GetBlockByHeight(Tip)?.Hash);
        }

        [Fact(Timeout = 60_000)]
        public async Task TheCasterSelfHeal_DoesNotRollBackACertifiedTip()
        {
            var tip = StoredChain(certifiedTip: true);

            await CasterSelfHealAsync(Tip);

            Assert.Equal(tip.Hash, Globals.LastBlock.Hash);
            Assert.Equal(tip.Hash, BlockchainData.GetBlockByHeight(Tip)?.Hash);
        }

        [Fact]
        public void TheRefusal_ReadsTheStoredCertificate_AndLeavesAnUncertifiedTipToTheSelfHeal()
        {
            var tip = StoredChain(certifiedTip: true);
            var withoutCertInMemory = BlockchainData.GetBlockByHeight(Tip)!;
            Assert.NotNull(ForkRecoveryUtility.CertifiedTipRefusal(new Block { Height = Tip, Hash = tip.Hash }));
            Assert.NotNull(ForkRecoveryUtility.CertifiedTipRefusal(withoutCertInMemory));

            Globals.CertEnforceHeight = long.MaxValue; // no record: no certificate counts
            Assert.Null(ForkRecoveryUtility.CertifiedTipRefusal(tip));
        }

        [Fact]
        public void AnUncertifiedTip_IsNotRefused()
        {
            var tip = StoredChain(certifiedTip: false);
            Assert.Null(ForkRecoveryUtility.CertifiedTipRefusal(tip));
        }
    }
}
