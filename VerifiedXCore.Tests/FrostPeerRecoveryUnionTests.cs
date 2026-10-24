using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A validator restoring its key shares asks every peer and joins the answers: each peer holds the backups that
    /// reached it, and the first peer to answer may not hold them all. A peer that does not answer is counted, and the
    /// recovery asks every peer once more.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostPeerRecoveryUnionTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _me;

        public FrostPeerRecoveryUnionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"peerrecover_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _me = AccountData.GetHumanAddress(pub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = _me, PublicKey = pub, PrivateKey = "0" + key.secret.ToString("x") });
        }

        public void Dispose()
        {
            FrostKeyBackupService.FetchFromPeerForTests = null;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>A backup of this validator's share of <paramref name="contract"/>, as a peer would return it.</summary>
        private FrostKeyBackupService.BackupRecoveryItem BackupOf(string contract)
        {
            var share = new FrostValidatorKeyStore
            {
                SmartContractUID = contract, ValidatorAddress = _me, KeyPackage = "kp-" + contract, PubkeyPackage = "pkp-" + contract, GroupPublicKey = "g-" + contract,
            };
            var (blob, hash) = FrostKeyBackupService.EncryptKeyPackage(_me, contract, share);
            Assert.NotNull(blob);
            return new FrostKeyBackupService.BackupRecoveryItem { SmartContractUID = contract, EncryptedBlob = blob!, PlaintextHash = hash! };
        }

        private static VBTCValidator Peer(string address) => new() { ValidatorAddress = address, IPAddress = "10.0.0.9" };

        [Fact]
        public async Task TwoPeersWithDisjointBackups_RestoreBoth_AndAPeerThatDoesNotAnswerIsAskedAgain()
        {
            var one = BackupOf("vault-1:1");
            var two = BackupOf("vault-2:2");
            var asked = new Dictionary<string, int>();
            FrostKeyBackupService.FetchFromPeerForTests = peer =>
            {
                asked[peer.ValidatorAddress] = asked.GetValueOrDefault(peer.ValidatorAddress) + 1;
                return Task.FromResult(peer.ValidatorAddress switch
                {
                    "xPeerA" => new List<FrostKeyBackupService.BackupRecoveryItem> { one },
                    "xPeerB" => new List<FrostKeyBackupService.BackupRecoveryItem> { two, one },
                    _ => (List<FrostKeyBackupService.BackupRecoveryItem>?)null,   // no answer
                });
            };

            var result = await FrostKeyBackupService.RecoverKeysFromPeersWithRetry(_me, new List<VBTCValidator> { Peer("xPeerA"), Peer("xPeerB"), Peer("xPeerDown"), Peer(_me) });

            Assert.Equal(2, result.Restored);
            Assert.Equal("kp-vault-1:1", FrostValidatorKeyStore.GetKeyPackage("vault-1:1", _me)!.KeyPackage);
            Assert.Equal("kp-vault-2:2", FrostValidatorKeyStore.GetKeyPackage("vault-2:2", _me)!.KeyPackage);
            Assert.Equal(3, result.PeersAsked);
            Assert.Equal(1, result.PeersFailed);
            Assert.Equal(1, result.FromPeer["xPeerA"]);
            Assert.Equal(2, result.FromPeer["xPeerB"]);
            Assert.Equal(2, asked["xPeerDown"]);   // asked once more after it did not answer
            Assert.False(asked.ContainsKey(_me));
        }

        [Fact]
        public async Task WhenEveryPeerAnswers_ThereIsNoSecondRound()
        {
            var one = BackupOf("vault-3:3");
            var calls = 0;
            FrostKeyBackupService.FetchFromPeerForTests = _ => { calls++; return Task.FromResult<List<FrostKeyBackupService.BackupRecoveryItem>?>(new() { one }); };

            var result = await FrostKeyBackupService.RecoverKeysFromPeersWithRetry(_me, new List<VBTCValidator> { Peer("xPeerA"), Peer("xPeerB") });

            Assert.Equal(1, result.Restored);
            Assert.Equal(0, result.PeersFailed);
            Assert.Equal(2, calls);
        }
    }
}
