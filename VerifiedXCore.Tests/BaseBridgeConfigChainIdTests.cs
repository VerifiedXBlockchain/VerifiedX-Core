using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The network decides which Base chain its bridge is on (8453 on mainnet, 84532 on testnet). A BaseBridgeChainId in
    /// the configuration that names another chain is refused and the network's is used. Driven the way a node starts:
    /// the configuration through Config.ProcessConfig, then BaseBridgeService.LoadConfig.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class BaseBridgeConfigChainIdTests : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> _priorFields;
        private readonly List<Bitcoin.ElectrumX.ClientSettings> _priorClientSettings = Globals.ClientSettings;
        private readonly StunServer[] _priorStun = Globals.STUNServers.ToArray();
        private readonly int _priorChainId = BaseBridgeService.BaseChainId;
        private readonly string _priorRpc = BaseBridgeService.BaseRpcUrl;
        private readonly string _priorRpc2 = BaseBridgeService.BaseRpcUrl2;
        private readonly string _priorRpc3 = BaseBridgeService.BaseRpcUrl3;
        private readonly bool[] _priorLeftOut = BaseBridgeService.ProvidersLeftOut;

        public BaseBridgeConfigChainIdTests()
        {
            _priorFields = typeof(Globals).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsLiteral && !f.IsInitOnly)
                .ToDictionary(f => f, f => f.GetValue(null));
        }

        public void Dispose()
        {
            foreach (var (field, value) in _priorFields)
                field.SetValue(null, value);
            Globals.ClientSettings = _priorClientSettings;
            Globals.STUNServers = new ConcurrentBag<StunServer>(_priorStun);
            BaseBridgeService.BaseChainId = _priorChainId;
            // Loading the bridge's configuration fills the provider settings a configuration left out.
            BaseBridgeService.BaseRpcUrl = _priorRpc;
            BaseBridgeService.BaseRpcUrl2 = _priorRpc2;
            BaseBridgeService.BaseRpcUrl3 = _priorRpc3;
            BaseBridgeService.ProvidersLeftOut = _priorLeftOut;
        }

        [Fact]
        public void OnMainnet_AConfiguredChainIdThatIsNotBaseMainnets_IsRefused()
        {
            Globals.IsTestNet = false;
            Globals.IsCustomTestNet = false;

            Config.Config.ProcessConfig(new Config.Config { BaseBridgeChainId = 84532 });
            Assert.Equal(8453, BaseBridgeService.BaseChainId);

            Config.Config.ProcessConfig(new Config.Config { BaseBridgeChainId = 1 });
            BaseBridgeService.LoadConfig();
            Assert.Equal(8453, BaseBridgeService.BaseChainId);

            Config.Config.ProcessConfig(new Config.Config { BaseBridgeChainId = 8453 });
            BaseBridgeService.LoadConfig();
            Assert.Equal(8453, BaseBridgeService.BaseChainId);
        }

        [Fact]
        public void OnTestnet_TheBridgeIsOnBaseSepolia_WhateverTheConfigurationSays()
        {
            Globals.IsTestNet = true;
            Globals.IsCustomTestNet = false;

            Config.Config.ProcessConfig(new Config.Config { TestNet = true, BaseBridgeChainId = 8453 });
            Assert.Equal(84532, BaseBridgeService.BaseChainId);

            Config.Config.ProcessConfig(new Config.Config { TestNet = true, BaseBridgeChainId = 84532 });
            Assert.Equal(84532, BaseBridgeService.BaseChainId);

            // No chain id written at all: the network's, not the one the field starts with.
            BaseBridgeService.BaseChainId = 8453;
            Config.Config.ProcessConfig(new Config.Config { TestNet = true });
            BaseBridgeService.LoadConfig();
            Assert.Equal(84532, BaseBridgeService.BaseChainId);
        }
    }
}
