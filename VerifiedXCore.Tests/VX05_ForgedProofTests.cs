using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-05 (HIGH): "Forged proofs determine the block producer and stall the chain".
    ///
    /// Audit PoC: an unauthenticated host POSTed proofs to /valapi/Validator/ReceiveWinningProof with a
    /// base64 ProofHash, PublicKey literally "aa" and VRFNumber 0; casters finalized
    /// "xATTACKERFORGEDWINNERADDR" as winner and the chain stalled (unreachable winner). The audit's
    /// control (hex-encoded hash) was accepted with 200 but had no effect.
    /// VerifyProof recomputed a hash over (PublicKey, height, prevHash) only; Address and VRFNumber
    /// were unbound, and no sender check existed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX05_ForgedProofTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters;
        private readonly ConcurrentDictionary<string, Proof> _priorProofDict;
        private readonly ConcurrentDictionary<string, NetworkValidator> _priorNetVals;

        private const string CasterIp = "10.0.0.5";
        private readonly (PrivateKey Key, string Pub, string Address) _validator;

        public VX05_ForgedProofTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx05_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            _priorLastBlock = Globals.LastBlock;
            _priorCasters = Globals.BlockCasters;
            _priorProofDict = Globals.CasterProofDict;
            _priorNetVals = Globals.NetworkValidators;

            Globals.LastBlock = new Block { Height = 99, Hash = "abcdef0123456789" };
            Globals.BlockCasters = new ConcurrentBag<Peers> { new Peers { PeerIP = CasterIp, ValidatorAddress = "xCasterA" } };
            Globals.CasterProofDict = new ConcurrentDictionary<string, Proof>();
            Globals.NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>();
            Globals.BannedIPs ??= new ConcurrentDictionary<string, Peers>(); // initialized at node startup

            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _validator = (key, pub, AccountData.GetHumanAddress(pub));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _validator.Address, Balance = ValidatorService.ValidatorRequiredAmount() + 1, Nonce = 0 });
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.BlockCasters = _priorCasters;
            Globals.CasterProofDict = _priorProofDict;
            Globals.NetworkValidators = _priorNetVals;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Proof Genuine(string ip = "1.2.3.4")
        {
            var (vrf, hash) = ProofUtility.ComputeVrf(_validator.Pub, 100, Globals.LastBlock.Hash);
            return new Proof { Address = _validator.Address, PublicKey = _validator.Pub, BlockHeight = 100, PreviousBlockHash = Globals.LastBlock.Hash, VRFNumber = vrf, ProofHash = hash, IPAddress = ip };
        }

        /// <summary>The audit's "B-valid" forgery: hash computed the way VerifyProof checked it, PublicKey "aa", VRF 0.</summary>
        private static Proof AuditForgery()
        {
            var seed = "aa" + 100 + Globals.LastBlock.Hash;
            var (realVrf, _) = ProofUtility.ComputeVrf("aa", 100, Globals.LastBlock.Hash);
            return new Proof
            {
                Address = "xATTACKERFORGEDWINNERADDR", PublicKey = "aa", BlockHeight = 100, PreviousBlockHash = Globals.LastBlock.Hash,
                VRFNumber = 0, ProofHash = ProofUtility.CalculateSHA256Hash(seed + realVrf), IPAddress = "172.28.0.20",
            };
        }

        private static ValidatorController ControllerFrom(string ip)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = ctx } };
        }

        // ── Content binding ────────────────────────────────────────────────────────────────

        [Fact]
        public void VX05_AuditForgery_PassedTheOldCheck_ButNotTheNewOne()
        {
            var forged = AuditForgery();
            // What the old VerifyProof checked (hash over PublicKey/height/prevHash) — this is why it was accepted.
            Assert.True(ProofUtility.VerifyProof(forged.PublicKey, forged.BlockHeight, forged.PreviousBlockHash, forged.ProofHash));
            Assert.False(forged.VerifyProof());
            Assert.False(ProofUtility.ValidateIncomingProofForNextRound(forged, out _));
        }

        [Fact]
        public void VX05_GenuineProof_WithVrfSetToZero_Rejected()
        {
            var p = Genuine();
            p.VRFNumber = 0;
            Assert.False(p.VerifyProof());
        }

        [Fact]
        public void VX05_GenuineProof_WithAddressSwapped_Rejected()
        {
            var p = Genuine();
            p.Address = "xATTACKERFORGEDWINNERADDR";
            Assert.False(p.VerifyProof());
        }

        [Fact]
        public void VX05_Control_GenuineProof_Accepted()
        {
            var p = Genuine();
            Assert.True(p.VerifyProof());
            Assert.True(ProofUtility.ValidateIncomingProofForNextRound(p, out var reason), reason);
        }

        // ── Round binding and eligibility ──────────────────────────────────────────────────

        [Fact]
        public void VX05_ProofForAnotherRound_Rejected()
        {
            var (vrf, hash) = ProofUtility.ComputeVrf(_validator.Pub, 150, Globals.LastBlock.Hash);
            var future = new Proof { Address = _validator.Address, PublicKey = _validator.Pub, BlockHeight = 150, PreviousBlockHash = Globals.LastBlock.Hash, VRFNumber = vrf, ProofHash = hash };
            Assert.True(future.VerifyProof());
            Assert.False(ProofUtility.ValidateIncomingProofForNextRound(future, out var r1));
            Assert.Equal("height", r1);

            var (vrf2, hash2) = ProofUtility.ComputeVrf(_validator.Pub, 100, "otherparent");
            var wrongParent = new Proof { Address = _validator.Address, PublicKey = _validator.Pub, BlockHeight = 100, PreviousBlockHash = "otherparent", VRFNumber = vrf2, ProofHash = hash2 };
            Assert.False(ProofUtility.ValidateIncomingProofForNextRound(wrongParent, out var r2));
            Assert.Equal("prevhash", r2);
        }

        [Fact]
        public void VX05_KeyWithNoValidatorBalance_NotEligible()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var (vrf, hash) = ProofUtility.ComputeVrf(pub, 100, Globals.LastBlock.Hash);
            var p = new Proof { Address = AccountData.GetHumanAddress(pub), PublicKey = pub, BlockHeight = 100, PreviousBlockHash = Globals.LastBlock.Hash, VRFNumber = vrf, ProofHash = hash };

            Assert.True(p.VerifyProof()); // well-formed…
            Assert.False(ProofUtility.ValidateIncomingProofForNextRound(p, out var reason)); // …but cannot produce
            Assert.Equal("not-eligible", reason);
        }

        [Fact]
        public void VX05_RelayedProof_IpIsTakenFromTheRegistry()
        {
            Globals.NetworkValidators[_validator.Address] = new NetworkValidator { Address = _validator.Address, IPAddress = "5.6.7.8" };
            var p = Genuine(ip: "172.28.0.20"); // attacker-chosen IP on a genuine proof
            Assert.True(ProofUtility.ValidateIncomingProofForNextRound(p, out _));
            Assert.Equal("5.6.7.8", p.IPAddress);
        }

        // ── The HTTP route (audit ingress) ─────────────────────────────────────────────────

        [Fact]
        public async Task VX05_Route_NonCasterSender_Refused_EvenWithAGenuineProof()
        {
            var r = await ControllerFrom("172.28.0.20").ReceiveWinningProof(Genuine());
            Assert.IsType<UnauthorizedResult>(r.Result);
            Assert.Empty(Globals.CasterProofDict);
        }

        [Fact]
        public async Task VX05_Route_CasterSender_ForgedProof_NotStored()
        {
            await ControllerFrom(CasterIp).ReceiveWinningProof(AuditForgery());
            Assert.Empty(Globals.CasterProofDict);
        }

        [Fact]
        public async Task VX05_Route_Control_CasterSender_GenuineProof_Stored()
        {
            await ControllerFrom(CasterIp).ReceiveWinningProof(Genuine());
            Assert.True(Globals.CasterProofDict.ContainsKey(CasterIp));
        }
    }
}
