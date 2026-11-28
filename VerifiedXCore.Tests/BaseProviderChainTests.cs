using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethereum.ABI;
using Nethereum.Util;
using Newtonsoft.Json.Linq;
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
    /// A Base provider is read only while it serves this network's Base chain, by its own eth_chainId: asked when the
    /// node starts and before a provider is read, a provider on another chain is left out, and is asked again later
    /// rather than left out for good. Driven through the node's own readers (the casters' burn watch, the operator's
    /// signing of a contract change) against providers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseProviderChainTests : IDisposable
    {
        private const string Contract = "0x3669EE313f9Aa14b9eBb787DfdB0E97Ca67A0510";
        private const long OtherChain = 1;

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
        private long _now = 1_800_000_000;

        public BaseProviderChainTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"base_chain_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100, Timestamp = TimeUtil.GetTime() };
            Globals.IsBlockCaster = false;
            BaseBridgeService.ContractAddress = Contract;
            BaseBridgeService.BaseRpcUrl2 = "";
            BaseBridgeService.BaseRpcUrl3 = "";
            BaseBridgeService.BaseChainId = (int)Globals.BaseEvmChainId;
            BaseRpc.Clock = () => _now;
            BaseRpc.ForgetChainChecksForTests();
            BaseBridgeExitWatchService.ResetSpanForTests();
            BaseBridgeExitWatchService.ResetRewindForTests();
        }

        public void Dispose()
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

        private FakeBaseRpc Provider(long? chainId = null)
        {
            var p = new FakeBaseRpc { ChainId = chainId };
            _providers.Add(p);
            return p;
        }

        private static void Configure(params FakeBaseRpc[] providers)
        {
            BaseBridgeService.BaseRpcUrl = providers.Length > 0 ? providers[0].Url : "";
            BaseBridgeService.BaseRpcUrl2 = providers.Length > 1 ? providers[1].Url : "";
            BaseBridgeService.BaseRpcUrl3 = providers.Length > 2 ? providers[2].Url : "";
        }

        // ── When the node starts ─────────────────────────────────────────────────────────

        [Fact]
        public async Task AtStart_EachProviderIsAskedItsChainId_AndOneOnAnotherChainIsNotReadAfterwards()
        {
            var onBase = Provider();
            var elsewhere = Provider(OtherChain);
            var down = Provider();
            down.Down = true;
            Configure(elsewhere, down, onBase);

            var checks = await BaseRpc.CheckProvidersAtStartAsync();

            Assert.Equal(new[] { BaseProviderCheck.OtherChain, BaseProviderCheck.NoAnswer, BaseProviderCheck.OnBase }, checks.Select(c => c.Check));
            Assert.Equal(1, elsewhere.CallsTo("eth_chainId"));

            // The first read after it passes the provider on another chain over without asking it anything.
            var burn = "0x" + new string('a', 64);
            elsewhere.AddBurn(burn, Contract, vfxExit: true, 500_000, "xDestination");
            var (ok, _, reason) = await BaseBridgeService.TryGetBurnEventAsync(burn, vfxExit: true);
            Assert.False(ok);
            Assert.Contains("receipt not found", reason);
            Assert.Equal(0, elsewhere.CallsTo("eth_getTransactionReceipt"));
            Assert.Equal(1, elsewhere.CallsTo("eth_chainId"));
            Assert.Equal(1, onBase.CallsTo("eth_getTransactionReceipt"));
        }

        // ── The casters' burn watch ──────────────────────────────────────────────────────

        [Fact]
        public async Task TheBurnWatch_DoesNotReadAProviderOnAnotherChain_AndReadsItAgainOnceItServesBase()
        {
            var provider = Provider(OtherChain);
            provider.Head = 5_000;
            Configure(provider);

            // The poll the loop runs: the provider is asked its chain id and nothing else, and the cursor does not move.
            await Assert.ThrowsAnyAsync<Exception>(() => BaseBridgeExitWatchService.ReadUntilCaughtUpAsync());
            Assert.Equal(1, provider.CallsTo("eth_chainId"));
            Assert.Equal(0, provider.CallsTo("eth_blockNumber"));
            Assert.Equal(0, provider.CallsTo("eth_getLogs"));
            Assert.Equal(0, BridgeExitSyncState.GetOrCreate().LastScannedBlock);

            // It serves Base again. Inside the set-aside time it is not asked; after it, it is asked and read.
            provider.ChainId = null;
            _now += BaseRpc.SetAsideSeconds - 1;
            await Assert.ThrowsAnyAsync<Exception>(() => BaseBridgeExitWatchService.ReadUntilCaughtUpAsync());
            Assert.Equal(1, provider.CallsTo("eth_chainId"));
            Assert.Equal(0, provider.CallsTo("eth_blockNumber"));

            _now += 2;
            await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
            Assert.Equal(2, provider.CallsTo("eth_chainId"));
            Assert.Equal(1, provider.CallsTo("eth_blockNumber"));
            Assert.Equal(4_999, BridgeExitSyncState.GetOrCreate().LastScannedBlock); // the cursor's first position, one below the head
        }

        [Fact]
        public async Task AProviderFoundOnBase_IsAskedItsChainIdAgainLater_AndLeftOutIfItChanged()
        {
            var provider = Provider();
            provider.Head = 5_000;
            Configure(provider);

            await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
            await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
            Assert.Equal(1, provider.CallsTo("eth_chainId")); // kept between reads
            Assert.Equal(2, provider.CallsTo("eth_blockNumber"));

            provider.ChainId = OtherChain;
            _now += BaseRpc.VerifiedForSeconds + 1;
            await Assert.ThrowsAnyAsync<Exception>(() => BaseBridgeExitWatchService.ReadUntilCaughtUpAsync());
            Assert.Equal(2, provider.CallsTo("eth_chainId"));
            Assert.Equal(2, provider.CallsTo("eth_blockNumber"));
        }

        /// <summary>
        /// A block holding a burn log that names another chain was not read from Base: it waits, however many polls find
        /// it so, and is never passed over the way a block the provider cannot serve is. A burn of Base's own in the same
        /// block is taken once the provider is set right.
        /// </summary>
        [Fact]
        public async Task TheBurnWatch_NeverPassesABlockWhoseBurnLogNamesAnotherChain_HoweverOftenItIsRead()
        {
            var provider = Provider();
            provider.Head = 5_000;
            Configure(provider);
            var state = BridgeExitSyncState.GetOrCreate();
            state.LastScannedBlock = 4_990;
            BridgeExitSyncState.Save(state);
            // Block 4,995 holds a burn of Base's and a burn log that names another chain.
            var real = "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var foreign = "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            provider.AddBurn(real, Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_995);
            provider.AddBurn(foreign, Contract, vfxExit: false, 400_000, "bc1qdestination", chainId: OtherChain, blockNumber: 4_995);

            // Polled far past the count at which a block that cannot be read is passed over (the range narrows to the
            // block first, and the provider reads the block after it without trouble).
            var taken = 0;
            for (var i = 0; i < 4 * BaseBridgeExitWatchService.SingleBlockFailuresBeforeSkip; i++)
                taken += (await BaseBridgeExitWatchService.PollOnce()).Processed;

            Assert.Equal(1, BaseBridgeExitWatchService.CurrentSpan);                          // narrowed to the one block
            Assert.Equal(4_994, BridgeExitSyncState.GetOrCreate().LastScannedBlock);          // which waits; the blocks before it were read
            Assert.Equal(0, taken);
            Assert.True(provider.CallsTo("eth_getLogs") > 2 * BaseBridgeExitWatchService.SingleBlockFailuresBeforeSkip);

            // The provider is set right: the block is read, and the burn that waited in it is taken.
            lock (provider.Logs) provider.Logs.RemoveAll(l => l["transactionHash"]!.ToString() == foreign);
            for (var i = 0; i < 12 && BridgeExitSyncState.GetOrCreate().LastScannedBlock < 5_000; i++)
                taken += (await BaseBridgeExitWatchService.PollOnce()).Processed;
            Assert.Equal(5_000, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
            Assert.Equal(1, taken);
        }

        // ── A burn's receipt ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task ABurnWhoseEventNamesAnotherChain_IsNotTakenFromThatProvider()
        {
            var first = Provider();
            var second = Provider();
            Configure(first, second);
            var burn = "0x" + new string('c', 64);
            first.AddBurn(burn, Contract, vfxExit: true, 500_000, "xDestination", chainId: OtherChain);

            var (ok, info, reason) = await BaseBridgeService.TryGetBurnEventAsync(burn, vfxExit: true);
            Assert.False(ok);
            Assert.Null(info);
            // No answer about the burn, as when Base could not be read: it is asked again, not failed.
            Assert.True(BurnExitConsensusService.BaseCouldNotBeRead(reason), reason);

            // The next provider's receipt, which names Base's chain, is the one taken.
            second.AddBurn(burn, Contract, vfxExit: true, 500_000, "xDestination");
            (ok, info, reason) = await BaseBridgeService.TryGetBurnEventAsync(burn, vfxExit: true);
            Assert.True(ok, reason);
            Assert.Equal(500_000, info!.AmountSats);
            Assert.Equal("xDestination", info.Destination);
        }

        // ── The contract's views and code, as the operator's signing reads them ────────────

        private static string Selector(string signature) => "0x" + Sha3Keccack.Current.CalculateHash(signature)[..8];

        private static string Encoded(string type, object value) =>
            "0x" + Convert.ToHexString(new ABIEncode().GetABIEncoded(new ABIValue(type, value))).ToLowerInvariant();

        /// <summary>A provider that serves a version 4 contract with these members and admin nonce, and code at any address.</summary>
        private FakeBaseRpc ContractProvider(long? chainId, string member, int nonce, string codeHex)
        {
            var p = Provider(chainId);
            var views = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Selector("getValidators()")] = Encoded("address[]", new List<string> { member, "0x00000000000000000000000000000000000000aa", "0x00000000000000000000000000000000000000ab" }),
                [Selector("requiredMintSignatures()")] = Encoded("uint256", new BigInteger(2)),
                [Selector("adminNonce()")] = Encoded("uint256", new BigInteger(nonce)),
                [Selector("MESSAGE_VERSION()")] = Encoded("uint256", new BigInteger(4)),
                [Selector("getCasters()")] = Encoded("address[]", new List<string> { member, "0x00000000000000000000000000000000000000ba", "0x00000000000000000000000000000000000000bb" }),
                [Selector("requiredCasterSignatures()")] = Encoded("uint256", new BigInteger(2)),
            };
            p.On = (method, args) =>
            {
                if (method == "eth_getCode") return codeHex;
                if (method != "eth_call") return null;
                var call = (JObject)args[0]!;
                var data = (call["data"] ?? call["input"])!.ToString();
                return views.TryGetValue(data[..10], out var answer) ? answer : null;
            };
            return p;
        }

        [Fact]
        public async Task TheOperatorsSigning_ReadsTheContractFromAProviderOnBase_NotFromOneOnAnotherChain()
        {
            // This node, a validator with its key in the wallet.
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var me = AccountData.GetHumanAddress(pub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = me, PublicKey = pub, PrivateKey = "0" + key.secret.ToString("x") });
            Globals.ValidatorAddress = me;
            var myBase = ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(pub);
            const string implementation = "0xd2bB4BdA89A23c63eaaD464F15658845a2114Ffa";

            // A provider on another chain serves a contract at admin nonce 99 and other code; the one on Base, nonce 5.
            var elsewhere = ContractProvider(OtherChain, myBase, nonce: 99, codeHex: "0xdeadbeef");
            var onBase = ContractProvider(null, myBase, nonce: 5, codeHex: "0x6080604052");

            Configure(elsewhere);
            var alone = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
            Assert.False(alone.Success);
            Assert.Equal("Could not read the Base contract", alone.Error);
            Assert.Equal(0, elsewhere.CallsTo("eth_call"));

            Configure(elsewhere, onBase);
            var cancel = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
            Assert.True(cancel.Success, cancel.Error);
            Assert.Equal("5", cancel.AdminNonce);
            Assert.Equal(4, cancel.MessageVersion);

            var upgrade = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.ProposeUpgrade, Implementation = implementation });
            Assert.True(upgrade.Success, upgrade.Error);
            Assert.Equal("0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash(Convert.FromHexString("6080604052"))).ToLowerInvariant(), upgrade.ImplementationCodeHash);
            Assert.Equal(0, elsewhere.CallsTo("eth_call"));
            Assert.Equal(0, elsewhere.CallsTo("eth_getCode"));
            Assert.True(onBase.CallsTo("eth_call") >= 6);
            Assert.Equal(1, onBase.CallsTo("eth_getCode"));
        }
    }
}
