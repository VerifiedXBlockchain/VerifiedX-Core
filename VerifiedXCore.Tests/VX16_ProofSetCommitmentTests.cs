using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-16 (MEDIUM): "Proof set commitments are unsigned and unbounded".
    ///
    /// Audit PoC: ExchangeProofSet stored commitments for caller-chosen caster addresses a1..a5 at height 42 against
    /// a tip of 0, escalated to 205 fabricated identities in one attacker-chosen hash group, with no genuine validator
    /// in it; accumulation persisted across requests. Controls: a mismatched CommitmentHash got 400 "0"; a different
    /// height had its own dictionary. On a multi-caster committee every distinct address string counted as a vote.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX16_ProofSetCommitmentTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, ProofSetCommitment>> _priorCommits = Globals.CasterProofSetCommitDict;

        private readonly (PrivateKey Key, string Pub, string Address) _casterX = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _casterY = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _outsider = NewKey();

        public VX16_ProofSetCommitmentTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx16_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();

            Globals.LastBlock = new Block { Height = 41 };
            Globals.ValidatorAddress = "";
            Globals.BlockCasters = new ConcurrentBag<Peers>
            {
                new Peers { ValidatorAddress = _casterX.Address, PeerIP = "" },
                new Peers { ValidatorAddress = _casterY.Address, PeerIP = "" },
            };
            Globals.BannedIPs ??= new ConcurrentDictionary<string, Peers>();
            Globals.CasterProofSetCommitDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, ProofSetCommitment>>();
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.BlockCasters = _priorCasters;
            Globals.ValidatorAddress = _priorValidator;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.CasterProofSetCommitDict = _priorCommits;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static ValidatorController Controller()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("172.28.0.20");
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = ctx } };
        }

        private static ProofSetCommitment Unsigned(long height, string caster, params string[] addresses)
        {
            var sorted = addresses.OrderBy(a => a, StringComparer.Ordinal).ToList();
            return new ProofSetCommitment { BlockHeight = height, CasterAddress = caster, ProofAddressesSorted = sorted, CommitmentHash = BlockcasterNode.ComputeProofSetCommitmentHash(sorted) };
        }

        private static ProofSetCommitment Signed((PrivateKey Key, string Pub, string Address) signer, string claimedCaster, long height, long seq, params string[] addresses)
        {
            var c = Unsigned(height, claimedCaster, addresses);
            c.Sequence = seq;
            c.Signature = SignatureService.CreateSignature(ProofSetCommitmentStore.SigningMessage(c), signer.Key, signer.Pub);
            return c;
        }

        private static ProofSetCommitment? Held(long height, string caster) =>
            Globals.CasterProofSetCommitDict.TryGetValue(height, out var d) && d.TryGetValue(caster, out var c) ? c : null;

        // ── Audit PoC ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void VX16_AuditPoC_205FabricatedIdentitiesAtFutureHeight_NoneStored()
        {
            Globals.LastBlock = new Block { Height = 0 }; // the audit's tip
            int accepted = 0;
            for (int i = 1; i <= 205; i++)
            {
                var r = Controller().ExchangeProofSet(Unsigned(42, "a" + i, "xATTACKER_ONLY_WINNER"));
                if (r.Result is not BadRequestObjectResult) accepted++;
            }
            var stored = Globals.CasterProofSetCommitDict.TryGetValue(42, out var d) ? d.Count : 0;
            Assert.True(accepted == 0 && stored == 0, $"accepted={accepted} stored at h=42 (tip 0)={stored}");
        }

        [Fact]
        public void VX16_AuditPoC_UnsignedCommitmentNamingARealCaster_Refused()
        {
            var r = Controller().ExchangeProofSet(Unsigned(42, _casterX.Address, "xATTACKER_ONLY_WINNER"));
            Assert.IsType<BadRequestObjectResult>(r.Result);
            Assert.Null(Held(42, _casterX.Address));
        }

        [Fact]
        public void VX16_AuditControl_MismatchedHash_Refused()
        {
            var c = Signed(_casterX, _casterX.Address, 42, 1, "xV1", "xV2");
            c.CommitmentHash = new string('0', 64);
            Assert.IsType<BadRequestObjectResult>(Controller().ExchangeProofSet(c).Result);
        }

        // ── Accepted path and its limits ───────────────────────────────────────────────────

        [Fact]
        public void VX16_Control_SignedCommitmentFromACaster_StoredAndReturnedSigned()
        {
            var r = Controller().ExchangeProofSet(Signed(_casterX, _casterX.Address, 42, 1, "xV1", "xV2"));
            var ok = Assert.IsType<OkObjectResult>(r.Result);
            var resp = JsonConvert.DeserializeObject<ProofSetExchangeResponse>((string)ok.Value!)!;
            var returned = resp.Commitments[_casterX.Address];
            Assert.False(string.IsNullOrEmpty(returned.Signature));
            Assert.True(ConsensusRequestAuth.VerifySigner(_casterX.Address, ProofSetCommitmentStore.SigningMessage(returned), returned.Signature));
        }

        [Fact]
        public void VX16_NonCasterSigner_Refused()
        {
            Assert.IsType<BadRequestObjectResult>(Controller().ExchangeProofSet(Signed(_outsider, _outsider.Address, 42, 1, "xV1")).Result);
        }

        [Fact]
        public void VX16_OutsiderSigningAsACaster_Refused()
        {
            Assert.IsType<BadRequestObjectResult>(Controller().ExchangeProofSet(Signed(_outsider, _casterX.Address, 42, 1, "xV1")).Result);
            Assert.Null(Held(42, _casterX.Address));
        }

        [Theory]
        [InlineData(41)]   // the tip itself
        [InlineData(44)]   // tip + 3
        [InlineData(9999)] // pre-seeding far ahead
        public void VX16_HeightOutsideTheRoundWindow_Refused(long height)
        {
            Assert.IsType<BadRequestObjectResult>(Controller().ExchangeProofSet(Signed(_casterX, _casterX.Address, height, 1, "xV1")).Result);
            Assert.Null(Held(height, _casterX.Address));
        }

        [Fact]
        public void VX16_UnsignedOverwriteOfAnHonestCommitment_Refused()
        {
            Controller().ExchangeProofSet(Signed(_casterX, _casterX.Address, 42, 1, "xHONEST"));
            Controller().ExchangeProofSet(Unsigned(42, _casterX.Address, "xATTACKER"));
            Assert.Equal(new List<string> { "xHONEST" }, Held(42, _casterX.Address)!.ProofAddressesSorted);
        }

        [Fact]
        public void VX16_ReplayOfAnOlderSignedCommitment_DoesNotReplaceTheNewerOne()
        {
            var first = Signed(_casterX, _casterX.Address, 42, 1, "xFIRST");
            Controller().ExchangeProofSet(first);
            Controller().ExchangeProofSet(Signed(_casterX, _casterX.Address, 42, 2, "xSECOND")); // re-commit on retry
            Controller().ExchangeProofSet(first);                                              // replay
            Assert.Equal(new List<string> { "xSECOND" }, Held(42, _casterX.Address)!.ProofAddressesSorted);
        }

        [Fact]
        public void VX16_RelayedCommitment_MustBeSignedByItsOwnCaster()
        {
            // What the agreement client does with every commitment a peer relays.
            bool committee(string a) => a == _casterX.Address || a == _casterY.Address;
            Assert.Equal(ProofSetRecordResult.Invalid, ProofSetCommitmentStore.TryRecord(Unsigned(42, _casterY.Address, "xV1"), committee));
            Assert.Equal(ProofSetRecordResult.Invalid, ProofSetCommitmentStore.TryRecord(Signed(_casterX, _casterY.Address, 42, 1, "xV1"), committee));
            Assert.Equal(ProofSetRecordResult.Invalid, ProofSetCommitmentStore.TryRecord(Signed(_outsider, _outsider.Address, 42, 1, "xV1"), committee));
            Assert.Equal(ProofSetRecordResult.Stored, ProofSetCommitmentStore.TryRecord(Signed(_casterY, _casterY.Address, 42, 1, "xV1"), committee));
        }

        [Fact]
        public void VX16_Storage_IsBoundedToAcceptedCasters()
        {
            for (int i = 0; i < 50; i++)
            {
                var k = NewKey();
                Controller().ExchangeProofSet(Signed(k, k.Item3, 42, 1, "xV" + i)); // valid signatures, not casters
            }
            Controller().ExchangeProofSet(Signed(_casterX, _casterX.Address, 42, 1, "xV1"));
            Controller().ExchangeProofSet(Signed(_casterY, _casterY.Address, 42, 1, "xV1"));
            Assert.Equal(2, Globals.CasterProofSetCommitDict[42].Count);
        }

        // ── Tally counts committee members only ───────────────────────────────────────────

        [Fact]
        public async Task VX16_Tally_IgnoresNonCommitteeEntries_EvenIfPresentInTheMap()
        {
            var self = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(self);
            Globals.ValidatorAddress = self.Address;
            Globals.BlockCasters.Add(new Peers { ValidatorAddress = self.Address, PeerIP = "" });

            // The honest committee (self, X, Y) commits {xV1, xV2}.
            bool committee(string a) => a == _casterX.Address || a == _casterY.Address || a == self.Address;
            ProofSetCommitmentStore.TryRecord(Signed(_casterX, _casterX.Address, 42, 1, "xV1", "xV2"), committee);
            ProofSetCommitmentStore.TryRecord(Signed(_casterY, _casterY.Address, 42, 1, "xV1", "xV2"), committee);

            // Five non-committee entries in one attacker hash group, placed straight into the map (as the unsigned
            // route used to allow, or as another accepted-but-not-committee caster could).
            var perHeight = Globals.CasterProofSetCommitDict.GetOrAdd(42, _ => new ConcurrentDictionary<string, ProofSetCommitment>());
            for (int i = 1; i <= 5; i++)
                perHeight["a" + i] = Unsigned(42, "a" + i, "xATTACKER_ONLY_WINNER");

            var mine = BlockcasterNode.BuildLocalProofSetCommitment(42, new[] { new Proof { Address = "xV1", BlockHeight = 42 }, new Proof { Address = "xV2", BlockHeight = 42 } });
            var agreed = await BlockcasterNode.ReachProofSetAgreementAsync(42, mine);

            Assert.NotNull(agreed);
            Assert.Equal(new List<string> { "xV1", "xV2" }, agreed);
        }
    }
}
