using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// What decides something of value on Base needs two providers that agree, so a node has three to ask by default. A
    /// new config.txt names three providers of the network's Base chain, run by different parties. A config.txt that is
    /// already there is not rewritten: the provider settings it leaves out are filled from the same defaults when the
    /// node starts, and the ones its operator wrote are kept. Driven the way a node starts: the config file established,
    /// read and processed, then the bridge's configuration loaded.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class BaseBridgeDefaultProvidersTests : IDisposable
    {
        private const string MainnetContract = "0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D";

        private readonly Dictionary<FieldInfo, object?> _priorFields;
        private readonly List<Bitcoin.ElectrumX.ClientSettings> _priorClientSettings = Globals.ClientSettings;
        private readonly StunServer[] _priorStun = Globals.STUNServers.ToArray();
        private readonly string _priorRpc = BaseBridgeService.BaseRpcUrl;
        private readonly string _priorRpc2 = BaseBridgeService.BaseRpcUrl2;
        private readonly string _priorRpc3 = BaseBridgeService.BaseRpcUrl3;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly int _priorChainId = BaseBridgeService.BaseChainId;
        private readonly bool[] _priorLeftOut = BaseBridgeService.ProvidersLeftOut;
        private readonly string _tempRoot;

        public BaseBridgeDefaultProvidersTests()
        {
            _priorFields = typeof(Globals).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsLiteral && !f.IsInitOnly)
                .ToDictionary(f => f, f => f.GetValue(null));
            _tempRoot = Path.Combine(Path.GetTempPath(), $"base_defaults_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            Globals.IsCustomTestNet = false;
        }

        public void Dispose()
        {
            foreach (var (field, value) in _priorFields)
                field.SetValue(null, value);
            Globals.ClientSettings = _priorClientSettings;
            Globals.STUNServers = new ConcurrentBag<StunServer>(_priorStun);
            BaseBridgeService.BaseRpcUrl = _priorRpc;
            BaseBridgeService.BaseRpcUrl2 = _priorRpc2;
            BaseBridgeService.BaseRpcUrl3 = _priorRpc3;
            BaseBridgeService.ContractAddress = _priorContract;
            BaseBridgeService.BaseChainId = _priorChainId;
            BaseBridgeService.ProvidersLeftOut = _priorLeftOut;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string ConfigFile => GetPathUtility.GetConfigPath() + "config.txt";

        /// <summary>The node's start as far as the bridge's providers go: the file established, read, processed, loaded.</summary>
        private static IReadOnlyList<string> StartNode()
        {
            Config.Config.EstablishConfigFile();
            for (var i = 0; i < 50 && !File.Exists(ConfigFile); i++) System.Threading.Thread.Sleep(100);
            Config.Config.ProcessConfig(Config.Config.ReadConfigFile());
            BaseBridgeService.LoadConfig();
            return BaseRpc.Providers();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ANewConfigFile_NamesThreeProvidersOfTheNetworksBaseChain(bool testNet)
        {
            Globals.IsTestNet = testNet;
            var defaults = testNet ? BaseRpc.TestnetDefaultProviders : BaseRpc.MainnetDefaultProviders;

            var providers = StartNode();

            var written = File.ReadAllLines(ConfigFile);
            Assert.Contains("BaseBridgeRpcUrl=" + defaults[0], written);
            Assert.Contains("BaseBridgeRpcUrl2=" + defaults[1], written);
            Assert.Contains("BaseBridgeRpcUrl3=" + defaults[2], written);
            Assert.Equal(defaults, providers);
            Assert.Equal(3, BaseRpc.DistinctProviders().Count);                                        // three addresses
            Assert.Equal(3, defaults.Select(d => string.Join(".", new Uri(d).Host.Split('.').TakeLast(2))).Distinct().Count()); // of three parties
        }

        [Fact]
        public void AnExistingConfigFile_IsNotRewritten_AndTheProvidersItLeavesOutAreFilledIn()
        {
            Globals.IsTestNet = false;
            // As every node's config.txt was written before: one provider.
            var before = string.Join(Environment.NewLine, "Port=3338", "APIPort=7292", "TestNet=false",
                "BaseBridgeRpcUrl=https://mainnet.base.org", "BaseBridgeContract=" + MainnetContract, "BaseBridgeChainId=8453");
            File.WriteAllText(ConfigFile, before);

            var providers = StartNode();

            Assert.Equal(before, File.ReadAllText(ConfigFile));
            Assert.Equal(BaseRpc.MainnetDefaultProviders, providers);
            Assert.Equal(3, BaseRpc.DistinctProviders().Count);
        }

        [Fact]
        public void TheProvidersAnOperatorWrote_AreKept_AndOnlyTheOnesLeftOutAreFilledIn()
        {
            Globals.IsTestNet = false;

            // One provider of the operator's own: it stays first, and two defaults stand beside it.
            File.WriteAllText(ConfigFile, string.Join(Environment.NewLine, "Port=3338", "TestNet=false",
                "BaseBridgeRpcUrl=https://base.my-own-node.example:8545", "BaseBridgeContract=" + MainnetContract));
            Assert.Equal(new[] { "https://base.my-own-node.example:8545", BaseRpc.MainnetDefaultProviders[0], BaseRpc.MainnetDefaultProviders[1] }, StartNode());

            // The operator's one provider is itself a default: the other two defaults are added, not the same one again.
            File.WriteAllText(ConfigFile, string.Join(Environment.NewLine, "Port=3338", "TestNet=false",
                "BaseBridgeRpcUrl=" + BaseRpc.MainnetDefaultProviders[1] + "/", "BaseBridgeContract=" + MainnetContract));
            var providers = StartNode();
            Assert.Equal(new[] { BaseRpc.MainnetDefaultProviders[1] + "/", BaseRpc.MainnetDefaultProviders[0], BaseRpc.MainnetDefaultProviders[2] }, providers);
            Assert.Equal(3, BaseRpc.DistinctProviders().Count);

            // Three of the operator's own: exactly those.
            var own = new[] { "https://a.example/base", "https://b.example/base", "https://c.example/base" };
            File.WriteAllText(ConfigFile, string.Join(Environment.NewLine, "Port=3338", "TestNet=false",
                "BaseBridgeRpcUrl=" + own[0], "BaseBridgeRpcUrl2=" + own[1], "BaseBridgeRpcUrl3=" + own[2], "BaseBridgeContract=" + MainnetContract));
            Assert.Equal(own, StartNode());
        }
    }
}
