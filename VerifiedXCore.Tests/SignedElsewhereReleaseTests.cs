using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
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
    /// The "signed elsewhere" hold (this caster signed another block at its tip height) releases once the block it holds
    /// carries a valid committee certificate. Mainnet 7,562,232 (23 October 2026): every caster that signed the losing
    /// block sat out until it was restarted, and with the signed-height claim stored on disk a restart no longer clears it.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SignedElsewhereReleaseTests : IDisposable
    {
        private const long Tip = 300;

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _priorAddress = Globals.ValidatorAddress;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly IHttpClientFactory _priorFactory = Globals.HttpClientFactory;
        private readonly long _priorCertEnforceHeight = Globals.CertEnforceHeight;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;
        private readonly List<(PrivateKey Key, string Pub, string Address, string Ip)> _casters = new();

        public SignedElsewhereReleaseTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"signed_elsewhere_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            AttestationGuard.ResetForTests();
            BlockchainData.ChainRef = "test-chain";
            Globals.CertEnforceHeight = 1;
            Globals.ValidatorAddress = "self";
            for (var i = 0; i < 3; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                _casters.Add((key, pub, AccountData.GetHumanAddress(pub), $"10.7.0.{i + 1}"));
            }
            Globals.BlockCasters = new ConcurrentBag<Peers>(_casters.Select(c => new Peers { ValidatorAddress = c.Address, PeerIP = c.Ip }));
        }

        public void Dispose()
        {
            AttestationGuard.ResetForTests();
            Globals.HttpClientFactory = _priorFactory;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorAddress;
            Globals.LastBlock = _priorLastBlock;
            Globals.CertEnforceHeight = _priorCertEnforceHeight;
            BlockchainData.ChainRef = _priorChainRef;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>
        /// Our committed tip, stored, certified by all three casters or not at all. Every peer caster committed the same
        /// block; only our own signature, for another block at the height, says otherwise.
        /// </summary>
        private Block CommittedTip(bool certified)
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var tip = new Block
            {
                Height = Tip, Version = 4, PrevHash = "prev-299", Timestamp = TimeUtil.GetTime() - 30, NumOfTx = 0,
                Validator = AccountData.GetHumanAddress(pub), ValidatorAnswer = "a", ChainRefId = "test-chain",
                Transactions = new List<Transaction>(),
            };
            tip.MerkleRoot = tip.MerkleRootOf();
            tip.Hash = tip.GetBlockHash();
            tip.ValidatorSignature = SignatureService.CreateSignature(tip.Hash, key, pub);
            if (certified)
            {
                var msg = ConsensusMessageFormatter.FormatAttestationV1(tip.Height, tip.Hash, tip.Validator, tip.PrevHash);
                tip.ConsensusCertificate = new ConsensusCertificate
                {
                    BlockHeight = tip.Height, BlockHash = tip.Hash, WinnerAddress = tip.Validator, PrevHash = tip.PrevHash,
                    Attestations = _casters.Select(c => new CasterAttestation { CasterAddress = c.Address, Signature = SignatureService.CreateSignature(msg, c.Key, c.Pub) }).ToList(),
                };
            }
            BlockchainData.GetBlocks().InsertSafe(tip);
            Globals.LastBlock = tip;
            var answer = "{\"Hash\":\"" + tip.Hash + "\",\"Validator\":\"" + tip.Validator + "\",\"Height\":" + Tip + ",\"Committed\":true}";
            Globals.HttpClientFactory = new FixedAnswerFactory(_casters.ToDictionary(c => c.Ip, c => answer));
            Assert.True(AttestationGuard.TryClaim(Tip, "losing-block-this-caster-signed"));
            return tip;
        }

        private static Task SyncBlockHashWithPeersAsync() =>
            (Task)typeof(BlockcasterNode).GetMethod("SyncBlockHashWithPeersAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        [Fact]
        public async Task ACasterThatSignedAnotherBlock_CastsAgain_OnceTheBlockItHoldsIsCertified()
        {
            CommittedTip(certified: true);

            await SyncBlockHashWithPeersAsync();

            Assert.False(BlockcasterNode.IsHoldingOnMinorityFork());
        }

        [Fact]
        public async Task OnAnUncertifiedTip_TheHoldStays()
        {
            CommittedTip(certified: false);

            await SyncBlockHashWithPeersAsync();

            Assert.True(BlockcasterNode.IsHoldingOnMinorityFork());
        }

        /// <summary>Answers GetBlockHash by host; every other host or route answers "0".</summary>
        private sealed class FixedAnswerFactory : IHttpClientFactory
        {
            private readonly Dictionary<string, string> _byHost;
            public FixedAnswerFactory(Dictionary<string, string> byHost) => _byHost = byHost;
            public HttpClient CreateClient(string name) => new HttpClient(new Handler(_byHost));

            private sealed class Handler : HttpMessageHandler
            {
                private readonly Dictionary<string, string> _byHost;
                public Handler(Dictionary<string, string> byHost) => _byHost = byHost;
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    var body = request.RequestUri!.AbsolutePath.Contains("/GetBlockHash/") && _byHost.TryGetValue(request.RequestUri.Host, out var a) ? a : "0";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
                }
            }
        }
    }
}
