using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A bridge lock debits its holder on VFX at once, and only a mint on Base gives that back. The Base contract mints with
    /// 2/3 of the validators it lists and a majority of the casters it lists, whoever sits in the VFX committee by now. This
    /// node makes a lock (the wallet's BridgeToBase route, as the node serves it) only while enough of the contract's own
    /// signers answer and report that they would sign; otherwise nothing is locked, sent or reserved.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseMintReadinessTests : IDisposable
    {
        private const long Height = 5_000;
        private const string Vault = "mintready:1";
        private const string Proxy = "0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D";

        private sealed record K(PrivateKey Key, string Pub, string Address, string Base, string Ip);

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly HttpMessageHandler? _priorHandler = NodeHttp.PeerHandlerForTests;

        private readonly K[] _validators = Enumerable.Range(1, 3).Select(i => NewKey($"10.0.7.{i}")).ToArray();
        private readonly K[] _casters = Enumerable.Range(1, 3).Select(i => NewKey($"10.0.8.{i}")).ToArray();
        private readonly K _holder = NewKey("");
        private readonly SignerNodes _nodes = new();
        private bool _contractReadable = true;
        /// <summary>A caster on the contract that this node has no address for, in the place of the contract's third.</summary>
        private readonly K _stranger = NewKey("10.0.8.9");
        private bool _thirdCasterIsAStranger;

        public BaseMintReadinessTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"mintready_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();

            // A vault with 1 vBTC of the holder's, and the holder's key in this node's wallet.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault,
                OwnerAddress = "xContractOwnerNotTheHolder00000002",
                ContractData = VbtcTestContracts.VbtcV2ContractData,
                SCStateTreiTokenizationTXes = new() { new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _holder.Address, Amount = 1.0M } },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
            AccountData.GetAccounts().InsertSafe(new Account { Address = _holder.Address, PublicKey = _holder.Pub, PrivateKey = "0" + _holder.Key.secret.ToString("x") });

            // The contract's validators are registered vBTC validators; its casters are in the caster record.
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = Height - 10, Hash = "registrations",
                Transactions = _validators.Select((v, i) => new Transaction
                {
                    TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = v.Address, ToAddress = v.Address, Hash = "r" + i,
                    Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = v.Ip, FrostPublicKey = v.Pub, IsS3C = false }),
                }).ToList(),
            });
            var record = new CasterMembershipRecord
            {
                RecordSeq = 1, EffectiveFromHeight = 0,
                Casters = _casters.Select(c => new CasterInfo { Address = c.Address, PeerIP = c.Ip, PublicKey = c.Pub }).ToList(),
            };
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Upsert(record.RecordSeq, record);
            VerifiedXCore.Services.CasterMembershipStore.InvalidateCache();

            Globals.LastBlock = new Block { Height = Height };
            Globals.IsChainSynced = true;
            VBTCValidatorRegistry.InvalidateCache();
            BaseBridgeService.ContractAddress = Proxy;
            BaseBridgeService.ReadSignerViewForTests = () => Task.FromResult<BaseBridgeService.SignerView?>(_contractReadable
                ? new BaseBridgeService.SignerView(_validators.Select(v => v.Base).ToList(), 4, 2, 7,
                    (_thirdCasterIsAStranger ? _casters.Take(2).Append(_stranger) : _casters).Select(c => c.Base).ToList(), 2)
                : null);
            NodeHttp.PeerHandlerForTests = _nodes;
        }

        public void Dispose()
        {
            NodeHttp.PeerHandlerForTests = _priorHandler;
            BaseBridgeService.ReadSignerViewForTests = null;
            BaseBridgeService.ContractAddress = _priorContract;
            Globals.IsChainSynced = _priorSynced;
            Globals.LastBlock = _priorLastBlock;
            VBTCValidatorRegistry.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            VerifiedXCore.Services.CasterMembershipStore.InvalidateCache();
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static K NewKey(string ip)
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return new K(key, pub, AccountData.GetHumanAddress(pub), ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(pub).ToLowerInvariant(), ip);
        }

        /// <summary>The signers' nodes as this node reaches them: by IP, down (no answer) or answering Health as told.</summary>
        private sealed class SignerNodes : HttpMessageHandler
        {
            public readonly HashSet<string> Down = new();
            public readonly Dictionary<string, object> Reports = new();
            public int Asked;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Asked);
                var ip = request.RequestUri!.Host;
                if (Down.Contains(ip) || !request.RequestUri.AbsolutePath.EndsWith("/valapi/validator/Health", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("unreachable");
                var report = Reports.TryGetValue(ip, out var told) ? told
                    : new { IsValidator = true, IsChainSynced = true, SyncState = "Healthy", ForkStatus = "None", Height };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(report)) });
            }
        }

        private int Pooled() =>
            TransactionData.GetPool().Find(x => x.FromAddress == _holder.Address).Count(x => x.TransactionType == TransactionType.VBTC_V2_BRIDGE_LOCK);

        /// <summary>The wallet's own request to lock vBTC for Base, through the route.</summary>
        private async Task<JObject> BridgeToBase() => JObject.Parse(await new VBTCController().BridgeToBase(new VBTCBridgeLockPayload
        {
            SmartContractUID = Vault, OwnerAddress = _holder.Address, Amount = VBTCService.MinBridgeLockSats / 100_000_000M, EvmDestination = "0x" + new string('3', 40),
        }));

        private async Task Refused(string because)
        {
            var before = Pooled();
            var answer = await BridgeToBase();
            Assert.False(answer.Value<bool>("Success"), answer.ToString());
            Assert.Contains(because, answer.Value<string>("Message"));
            Assert.Contains("Nothing was locked", answer.Value<string>("Message"));
            Assert.Equal(before, Pooled());                                        // no lock was sent
            Assert.Equal(0M, BridgeLockRecord.GetLockedAmount(_holder.Address, Vault) - before * (VBTCService.MinBridgeLockSats / 100_000_000M)); // and none is reserved
        }

        [Fact]
        public async Task ALockIsMade_OnlyWhileEnoughOfTheContractsOwnSignersAnswerToMintIt()
        {
            // Every signer the contract lists answers: the lock is made.
            var made = await BridgeToBase();
            Assert.True(made.Value<bool>("Success"), made.ToString());
            Assert.Equal(1, Pooled());
            Assert.Equal(6, _nodes.Asked);                                         // each listed signer was asked once

            // Two of the contract's three casters are gone (the validators all answer): Base needs two casters.
            _nodes.Down.UnionWith(_casters.Take(2).Select(c => c.Ip));
            await Refused("3 of the contract's 3 validators and 1 of its 3 casters answer, and it needs 2 and 2");
            _nodes.Down.Clear();

            // Two of its three validators answer but are stalled or not synced: they would not sign.
            _nodes.Reports[_validators[0].Ip] = new { IsValidator = true, IsChainSynced = true, SyncState = "Stalled", ForkStatus = "None" };
            _nodes.Reports[_validators[1].Ip] = new { IsValidator = true, IsChainSynced = false, SyncState = "Healthy", ForkStatus = "None" };
            await Refused("1 of the contract's 3 validators and 3 of its 3 casters answer");
            _nodes.Reports[_validators[1].Ip] = new { IsValidator = true, IsChainSynced = true, SyncState = "Healthy", ForkStatus = "Unresolved" };
            await Refused("1 of the contract's 3 validators");
            _nodes.Reports.Clear();

            // The contract cannot be read: not known, so not locked.
            _contractReadable = false;
            await Refused("Could not read the Base contract's signers");
            _contractReadable = true;

            // One validator and one caster down leaves 2/3 and a majority: enough.
            _nodes.Down.UnionWith(new[] { _validators[2].Ip, _casters[2].Ip });
            var again = await BridgeToBase();
            Assert.True(again.Value<bool>("Success"), again.ToString());
            Assert.Equal(2, Pooled());
            _nodes.Down.Clear();

            // A caster the contract lists that this node has no address for was not asked: it is not held against the lock
            // (one caster answers, one is down), but it does not stand in for two that are known and gone.
            _thirdCasterIsAStranger = true;
            _nodes.Down.Add(_casters[1].Ip);
            var third = await BridgeToBase();
            Assert.True(third.Value<bool>("Success"), third.ToString());
            Assert.Equal(3, Pooled());
            _nodes.Down.Add(_casters[0].Ip);
            await Refused("0 of its 3 casters answer, and it needs 2 and 2 (1 more could not be asked");
        }

        /// <summary>A node with no bridge contract configured has nothing to ask: its lock is made as before.</summary>
        [Fact]
        public async Task ANodeWithNoBridgeContract_LocksAsBefore()
        {
            BaseBridgeService.ContractAddress = "";
            _nodes.Down.UnionWith(_validators.Concat(_casters).Select(k => k.Ip));
            var made = await BridgeToBase();
            Assert.True(made.Value<bool>("Success"), made.ToString());
            Assert.Equal(1, Pooled());
            Assert.Equal(0, _nodes.Asked);
        }
    }
}
