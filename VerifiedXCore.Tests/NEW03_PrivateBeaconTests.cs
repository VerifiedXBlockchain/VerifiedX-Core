using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-03 (follow-up; fourth review): a private beacon authorized uploads by BeaconSendData.CurrentOwnerAddress,
    /// which the caller supplies and nothing bound to the contract or the signature. Naming any address held by the
    /// beacon's own wallet (e.g. its validator address) and signing one's own contract registered uploads.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW03_PrivateBeaconTests : IDisposable
    {
        private const string Uid = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd:1790500050";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Beacons? _priorSelfBeacon = Globals.SelfBeacon;

        public NEW03_PrivateBeaconTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new03p_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.SelfBeacon = new Beacons { SelfBeaconActive = true, IsPrivateBeacon = true };
        }

        public void Dispose()
        {
            Globals.SelfBeacon = _priorSelfBeacon;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private sealed class FakeContext : HubCallerContext
        {
            private readonly FeatureCollection _features = new();
            public FakeContext() { _features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { RemoteIpAddress = IPAddress.Parse("10.1.2.3") }); }
            public override string ConnectionId => "c1";
            public override string? UserIdentifier => null;
            public override ClaimsPrincipal? User => null;
            public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
            public override IFeatureCollection Features => _features;
            public override CancellationToken ConnectionAborted => CancellationToken.None;
            public override void Abort() { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private async Task<bool> Upload(string currentOwnerAddress, (PrivateKey Key, string Pub, string Address) owner)
        {
            var hub = new P2PBeaconServer { Context = new FakeContext() };
            return await hub.ReceiveUploadRequest(new BeaconData.BeaconSendData
            {
                CurrentOwnerAddress = currentOwnerAddress, SmartContractUID = Uid, Assets = new List<string> { "art.png" },
                Signature = SignatureService.CreateSignature(Uid, owner.Key, owner.Pub), NextAssetOwnerAddress = "xNext", Reference = "r", MD5List = "m",
            });
        }

        [Fact]
        public async Task NEW03_FollowUp_PrivateBeacon_CallerSuppliedLocalAddressDoesNotAuthorize()
        {
            var outsider = NewKey();                 // owns the contract, not a local account of this beacon
            var local = NewKey();                    // an address held by the beacon's wallet
            AccountData.GetAccounts().InsertSafe(new Account { Address = local.Address, PublicKey = local.Pub, PrivateKey = "x" });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Uid, OwnerAddress = outsider.Address, MinterAddress = outsider.Address, ContractData = VbtcTestContracts.PlainNftContractData });

            Assert.False(await Upload(local.Address, outsider));
        }

        [Fact]
        public async Task NEW03_FollowUp_PrivateBeacon_LocalOwnerIsAuthorized()
        {
            var local = NewKey();                    // control: the beacon's own account owns the contract
            AccountData.GetAccounts().InsertSafe(new Account { Address = local.Address, PublicKey = local.Pub, PrivateKey = "x" });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Uid, OwnerAddress = local.Address, MinterAddress = local.Address, ContractData = VbtcTestContracts.PlainNftContractData });

            Assert.True(await Upload(local.Address, local));
        }

        [Fact]
        public async Task NEW15_BeaconPoolEntryIsRemovedWhenItsConnectionCloses()
        {
            // Fifth review: OnDisconnectedAsync called TryGetFromKey1 (a lookup), so pool entries were never removed.
            var ctx = new FakeContext();
            Globals.BeaconPool[("10.1.2.3", "ref-new15")] = new BeaconPool { ConnectionId = ctx.ConnectionId, IpAddress = "10.1.2.3", Reference = "ref-new15" };
            try
            {
                await new P2PBeaconServer { Context = ctx }.OnDisconnectedAsync(null);
                Assert.False(Globals.BeaconPool.TryGetFromKey1("10.1.2.3", out _));
            }
            finally { Globals.BeaconPool.TryRemoveFromKey1("10.1.2.3", out _); }
        }
    }
}
