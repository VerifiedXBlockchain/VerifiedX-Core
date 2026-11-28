using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Nethereum.ABI;
using Nethereum.Util;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A node with a bridge contract configured and Base providers on loopback (<see cref="FakeBaseRpc"/>): its own
    /// databases, the layer's clock in the test's hands, and everything set back afterwards.
    /// </summary>
    public abstract class BaseProviderTestBase : IDisposable
    {
        protected const string Contract = "0x3669EE313f9Aa14b9eBb787DfdB0E97Ca67A0510";
        protected const long OtherChain = 1;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly bool _priorIsBlockCaster = Globals.IsBlockCaster;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly string _priorRpc = BaseBridgeService.BaseRpcUrl;
        private readonly string _priorRpc2 = BaseBridgeService.BaseRpcUrl2;
        private readonly string _priorRpc3 = BaseBridgeService.BaseRpcUrl3;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly int _priorChainId = BaseBridgeService.BaseChainId;
        private readonly Func<long> _priorClock = BaseRpc.Clock;
        private readonly List<FakeBaseRpc> _providers = new();

        /// <summary>The layer's clock, in seconds.</summary>
        protected long Now = 1_800_000_000;

        protected BaseProviderTestBase()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"base_provider_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100, Timestamp = TimeUtil.GetTime() };
            Globals.IsBlockCaster = false;
            BaseBridgeService.ContractAddress = Contract;
            BaseBridgeService.BaseRpcUrl2 = "";
            BaseBridgeService.BaseRpcUrl3 = "";
            BaseBridgeService.BaseChainId = (int)Globals.BaseEvmChainId;
            BaseRpc.Clock = () => Now;
            BaseRpc.ForgetChainChecksForTests();
            BaseBridgeExitWatchService.ResetSpanForTests();
            BaseBridgeExitWatchService.ResetRewindForTests();
        }

        public virtual void Dispose()
        {
            foreach (var p in _providers) p.Dispose();
            BaseRpc.Clock = _priorClock;
            BaseRpc.ForgetChainChecksForTests();
            BaseBridgeService.BaseRpcUrl = _priorRpc;
            BaseBridgeService.BaseRpcUrl2 = _priorRpc2;
            BaseBridgeService.BaseRpcUrl3 = _priorRpc3;
            BaseBridgeService.ContractAddress = _priorContract;
            BaseBridgeService.BaseChainId = _priorChainId;
            BaseBridgeExitWatchService.ResetSpanForTests();
            BaseBridgeExitWatchService.ResetRewindForTests();
            Globals.ValidatorAddress = _priorValidator;
            Globals.IsBlockCaster = _priorIsBlockCaster;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        protected FakeBaseRpc Provider(long? chainId = null)
        {
            var p = new FakeBaseRpc { ChainId = chainId };
            _providers.Add(p);
            return p;
        }

        /// <summary>The node's providers, in the order it asks them.</summary>
        protected static void Configure(params FakeBaseRpc[] providers)
        {
            BaseBridgeService.BaseRpcUrl = providers.Length > 0 ? providers[0].Url : "";
            BaseBridgeService.BaseRpcUrl2 = providers.Length > 1 ? providers[1].Url : "";
            BaseBridgeService.BaseRpcUrl3 = providers.Length > 2 ? providers[2].Url : "";
        }

        protected static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        /// <summary>Makes this node a validator with its key in the wallet, and returns its Base address.</summary>
        protected static string ThisNodeIsAValidator()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var me = AccountData.GetHumanAddress(pub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = me, PublicKey = pub, PrivateKey = "0" + key.secret.ToString("x") });
            Globals.ValidatorAddress = me;
            return ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(pub);
        }

        protected static string Selector(string signature) => "0x" + Sha3Keccack.Current.CalculateHash(signature)[..8];

        protected static string Encoded(string type, object value) =>
            "0x" + Convert.ToHexString(new ABIEncode().GetABIEncoded(new ABIValue(type, value))).ToLowerInvariant();

        /// <summary>
        /// A provider that serves a version 4 contract with <paramref name="member"/> among its validators and casters, and
        /// code at any address. The admin nonce and the code are what <paramref name="nonceAt"/> and
        /// <paramref name="codeAt"/> give for the block a call was asked at (as sent: "latest", or a number in hex).
        /// </summary>
        protected FakeBaseRpc ContractProvider(long? chainId, string member, Func<string, int> nonceAt, Func<string, string> codeAt)
        {
            var p = Provider(chainId);
            var views = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Selector("getValidators()")] = Encoded("address[]", new List<string> { member, "0x00000000000000000000000000000000000000aa", "0x00000000000000000000000000000000000000ab" }),
                [Selector("requiredMintSignatures()")] = Encoded("uint256", new BigInteger(2)),
                [Selector("MESSAGE_VERSION()")] = Encoded("uint256", new BigInteger(4)),
                [Selector("getCasters()")] = Encoded("address[]", new List<string> { member, "0x00000000000000000000000000000000000000ba", "0x00000000000000000000000000000000000000bb" }),
                [Selector("requiredCasterSignatures()")] = Encoded("uint256", new BigInteger(2)),
            };
            var adminNonce = Selector("adminNonce()");
            p.On = (method, args) =>
            {
                var at = args.Count > 1 ? args[1]!.ToString() : "latest";
                if (method == "eth_getCode") return codeAt(at);
                if (method != "eth_call") return null;
                var call = (JObject)args[0]!;
                var data = (call["data"] ?? call["input"])!.ToString()[..10];
                if (string.Equals(data, adminNonce, StringComparison.OrdinalIgnoreCase)) return Encoded("uint256", new BigInteger(nonceAt(at)));
                return views.TryGetValue(data, out var answer) ? answer : null;
            };
            return p;
        }

        protected FakeBaseRpc ContractProvider(long? chainId, string member, int nonce, string codeHex) =>
            ContractProvider(chainId, member, _ => nonce, _ => codeHex);
    }
}
