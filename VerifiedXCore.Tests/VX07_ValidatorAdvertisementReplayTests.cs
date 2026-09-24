using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-07 (HIGH): "A replayed signature rewrites a validator's registry entry".
    ///
    /// Audit PoC: a genuine heartbeat signature harvested from the public ValidatorPool route was replayed
    /// verbatim to /valapi/Validator/Status with AdvertisementTimestamp 0 and an attacker-chosen PublicKey;
    /// the caster's trusted entry was rewritten to the attacker's IP and key. Control: the same request with
    /// a stale non-zero AdvertisementTimestamp was ignored. A byte-identical second replay was accepted too.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX07_ValidatorAdvertisementReplayTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly ConcurrentDictionary<string, NetworkValidator> _priorNetVals;
        private readonly int _priorValPort;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly TcpListener _listener;
        private readonly (PrivateKey Key, string Pub, string Address) _victim;

        public VX07_ValidatorAdvertisementReplayTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx07_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            _priorNetVals = Globals.NetworkValidators;
            Globals.NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>();
            Globals.BannedIPs ??= new ConcurrentDictionary<string, Peers>();

            // The Status route checks the caller's validator port is open.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _priorValPort = Globals.ValAPIPort;
            Globals.ValAPIPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _victim = (key, pub, AccountData.GetHumanAddress(pub));
            _priorValidatorAddress = Globals.ValidatorAddress;
            Globals.ValidatorAddress = Me; // this caster: direct Status must be addressed to it

            // The trusted entry the caster already holds for the victim.
            Globals.NetworkValidators[_victim.Address] = new NetworkValidator
            {
                Address = _victim.Address, IPAddress = "172.28.0.11", PublicKey = _victim.Pub, UniqueName = "victim",
                IsFullyTrusted = true, FirstSeenAtHeight = 0,
            };
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            Globals.ValAPIPort = _priorValPort;
            Globals.ValidatorAddress = _priorValidatorAddress;
            Globals.LastBlock = _priorLastBlock;
            Globals.NetworkValidators = _priorNetVals;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private const string Me = "xVX07_THIS_CASTER";
        private readonly string _priorValidatorAddress;

        /// <summary>VX-07 (follow-up): a validator's direct Status, signed for one recipient caster.</summary>
        private NetworkValidator DirectStatus(long signedAt, string recipient = Me)
        {
            var msg = ConsensusMessageFormatter.FormatValidatorStatusV2(_victim.Address, signedAt, _victim.Pub, recipient);
            return new NetworkValidator
            {
                Address = _victim.Address, IPAddress = "0.0.0.0", PublicKey = _victim.Pub, UniqueName = "victim",
                SignatureMessage = msg, Signature = SignatureService.CreateSignature(msg, _victim.Key, _victim.Pub),
            };
        }

        private NetworkValidator Advertisement(long signedAt, string? claimedPub = null, string ip = "0.0.0.0")
        {
            var msg = $"{_victim.Address}:{signedAt}:{_victim.Pub}";
            return new NetworkValidator
            {
                Address = _victim.Address, IPAddress = ip, PublicKey = claimedPub ?? _victim.Pub, UniqueName = "victim",
                SignatureMessage = msg, Signature = SignatureService.CreateSignature(msg, _victim.Key, _victim.Pub),
            };
        }

        private static ValidatorController From(string ip)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            return new ValidatorController { ControllerContext = new ControllerContext { HttpContext = ctx } };
        }

        private NetworkValidator Entry => Globals.NetworkValidators[_victim.Address];

        // ── Audit PoC through the real route ───────────────────────────────────────────────

        [Fact]
        public async Task VX07_AuditPoC_HarvestedOldSignature_Replayed_EntryUnchanged()
        {
            // A heartbeat the victim signed long ago (harvested), replayed from the attacker's host with an
            // attacker-chosen key and AdvertisementTimestamp 0.
            var harvested = Advertisement(TimeUtil.GetTime() - 3600);
            harvested.PublicKey = "04deadbeef1111";
            harvested.AdvertisementTimestamp = 0;
            harvested.UniqueName = "hijacked";

            var r = await From("127.0.0.1").Status(harvested);

            Assert.IsType<UnauthorizedResult>(r.Result);
            Assert.Equal("172.28.0.11", Entry.IPAddress);
            Assert.Equal(_victim.Pub, Entry.PublicKey);
            Assert.Equal("victim", Entry.UniqueName);
            Assert.True(Entry.IsFullyTrusted);
        }

        [Fact]
        public async Task VX07_OldSignature_WithOriginalKey_StillRefused_ItIsStale()
        {
            var harvested = Advertisement(TimeUtil.GetTime() - 3600);
            var r = await From("127.0.0.1").Status(harvested);
            Assert.IsType<UnauthorizedResult>(r.Result);
            Assert.Equal("172.28.0.11", Entry.IPAddress);
        }

        [Fact]
        public async Task VX07_ByteIdenticalReplay_OfAFreshAdvertisement_RefusedTheSecondTime()
        {
            var fresh = DirectStatus(TimeUtil.GetTime());
            var first = await From("127.0.0.1").Status(fresh);
            Assert.IsType<OkResult>(first.Result);

            var replay = Advertisement(0); // placeholder then copy exact fields
            replay.SignatureMessage = fresh.SignatureMessage; replay.Signature = fresh.Signature;
            var second = await From("127.0.0.1").Status(replay);
            Assert.IsType<UnauthorizedResult>(second.Result);
        }

        [Fact]
        public async Task VX07_Control_FreshAdvertisementFromTheValidator_UpdatesItsIp()
        {
            // A legitimate IP change: the validator's own fresh, single-use advertisement.
            var r = await From("127.0.0.1").Status(DirectStatus(TimeUtil.GetTime()));
            Assert.IsType<OkResult>(r.Result);
            Assert.Equal("127.0.0.1", Entry.IPAddress);
            Assert.True(Entry.IsFullyTrusted);
        }

        // ── Follow-up (independent review): cross-node replay and re-encoded signatures ────

        [Fact]
        public async Task VX07_FreshStatusAddressedToAnotherCaster_Refused()
        {
            // A fresh Status the victim sent caster A, harvested from A's registry gossip, replayed to this caster.
            var r = await From("127.0.0.1").Status(DirectStatus(TimeUtil.GetTime(), recipient: "xSOME_OTHER_CASTER"));
            Assert.IsType<UnauthorizedResult>(r.Result);
            Assert.Equal("172.28.0.11", Entry.IPAddress);
        }

        [Fact]
        public async Task VX07_LegacyUnboundMessage_CannotMoveTheEntry()
        {
            var r = await From("127.0.0.1").Status(Advertisement(TimeUtil.GetTime()));
            Assert.IsType<UnauthorizedResult>(r.Result);
            Assert.Equal("172.28.0.11", Entry.IPAddress);
        }

        [Fact]
        public async Task VX07_ReEncodedSignature_CountsAsTheSameSignature()
        {
            var fresh = DirectStatus(TimeUtil.GetTime());
            Assert.IsType<OkResult>((await From("127.0.0.1").Status(fresh)).Result);

            var parts = fresh.Signature.Split('.', 2);
            // 1. whitespace inside the base64 (Convert.FromBase64String ignores it)
            var spaced = DirectStatus(0);
            spaced.SignatureMessage = fresh.SignatureMessage;
            spaced.Signature = parts[0].Insert(4, " ") + "." + parts[1];
            Assert.IsType<UnauthorizedResult>((await From("127.0.0.1").Status(spaced)).Result);

            // 2. the (r, n - s) twin, which ECDSA also accepts
            var sig = VerifiedXCore.EllipticCurve.Signature.fromBase64(parts[0]);
            var twin = new VerifiedXCore.EllipticCurve.Signature(sig.r, VerifiedXCore.EllipticCurve.Curves.secp256k1.N - sig.s);
            var malleated = DirectStatus(0);
            malleated.SignatureMessage = fresh.SignatureMessage;
            malleated.Signature = twin.toBase64() + "." + parts[1];
            Assert.True(SignatureService.VerifySignature(_victim.Address, fresh.SignatureMessage, malleated.Signature)); // it IS valid
            Assert.IsType<UnauthorizedResult>((await From("127.0.0.1").Status(malleated)).Result);
        }

        [Fact]
        public void VX07_HandshakeSignature_IsNotStoredForGossip()
        {
            var hs = $"{_victim.Address}:{TimeUtil.GetTime()}:{_victim.Pub}:nonce123"; // the SignalR handshake shape
            NetworkValidator.UpsertTrustedOnDirectConnect(new NetworkValidator
            {
                Address = _victim.Address, IPAddress = "172.28.0.11", PublicKey = _victim.Pub,
                SignatureMessage = hs, Signature = SignatureService.CreateSignature(hs, _victim.Key, _victim.Pub),
            });
            Assert.NotEqual(hs, Entry.SignatureMessage);
            Assert.DoesNotContain("nonce123", Entry.SignatureMessage ?? "");
        }

        // ── Binding rules (shared by every path) ───────────────────────────────────────────

        [Fact]
        public async Task VX07_ClaimedKeyDiffersFromSignedMessage_Refused()
        {
            var ad = Advertisement(TimeUtil.GetTime(), claimedPub: "04" + new string('a', 128));
            Assert.False(await NetworkValidator.AddValidatorToPool(ad, directFromValidator: true));
        }

        [Fact]
        public async Task VX07_FreeTextMessage_Refused()
        {
            var msg = "anything the validator ever signed";
            var ad = new NetworkValidator
            {
                Address = _victim.Address, PublicKey = _victim.Pub, IPAddress = "9.9.9.9", SignatureMessage = msg,
                Signature = SignatureService.CreateSignature(msg, _victim.Key, _victim.Pub),
            };
            Assert.False(await NetworkValidator.AddValidatorToPool(ad, "9.9.9.9"));
        }

        [Fact]
        public async Task VX07_KeyThatDoesNotOwnTheAddress_Refused()
        {
            // A second key signs "victimAddress:time:otherPub": signature verifies for otherPub's own
            // address only, and otherPub does not derive the victim address.
            var other = new PrivateKey("secp256k1");
            var otherPub = "04" + Convert.ToHexString(other.publicKey().toString()).ToLowerInvariant();
            var msg = $"{_victim.Address}:{TimeUtil.GetTime()}:{otherPub}";
            var ad = new NetworkValidator
            {
                Address = _victim.Address, PublicKey = otherPub, IPAddress = "9.9.9.9", SignatureMessage = msg,
                Signature = SignatureService.CreateSignature(msg, other, otherPub),
            };
            Assert.False(await NetworkValidator.AddValidatorToPool(ad, directFromValidator: true));
        }

        // ── Gossip may confirm or refresh, never move ──────────────────────────────────────

        [Fact]
        public async Task VX07_GossipedCopyWithAnotherIp_RefreshesButDoesNotMoveTheEntry()
        {
            var before = Entry.LastSeen;
            var relayed = Advertisement(TimeUtil.GetTime() - 7200, ip: "172.28.0.20"); // old signature, attacker IP
            Assert.True(await NetworkValidator.AddValidatorToPool(relayed, "10.1.1.1"));
            Assert.Equal("172.28.0.11", Entry.IPAddress);
            Assert.True(Entry.LastSeen >= before);
            Assert.True(Entry.IsFullyTrusted);
        }

        [Fact]
        public async Task VX07_NewValidatorFromGossip_FirstSeenHeightIsLocal_NotFromTheWire()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var addr = AccountData.GetHumanAddress(pub);
            var msg = $"{addr}:{TimeUtil.GetTime()}:{pub}";
            Globals.LastBlock = new Block { Height = 5000 };
            var ad = new NetworkValidator
            {
                Address = addr, PublicKey = pub, IPAddress = "8.8.4.4", SignatureMessage = msg, FirstSeenAtHeight = 1,
                Signature = SignatureService.CreateSignature(msg, key, pub),
            };
            Assert.True(await NetworkValidator.AddValidatorToPool(ad, "10.2.2.2"));
            Assert.Equal(5000, ad.FirstSeenAtHeight);
        }
    }
}
