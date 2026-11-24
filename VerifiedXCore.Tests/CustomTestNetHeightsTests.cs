using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VerifiedXCore;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A custom testnet (Marigold) starts from genesis, so every rule is active by Globals.CustomTestNetRulesHeight (401),
    /// while the live testnet keeps its own heights. Driven the way a node starts: the heights applied for testnet first
    /// (Program.cs / the Warden), then the config read through Config.ProcessConfig.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class CustomTestNetHeightsTests : IDisposable
    {
        /// <summary>Long fields named like heights that are not rule activations, or that are set elsewhere.</summary>
        private static readonly HashSet<string> NotRuleHeights = new(StringComparer.Ordinal)
        {
            nameof(Globals.SpecialBlockHeight),                   // the supply block (mints up to 200M), not a rule
            nameof(Globals.V3Height),                             // Program.cs sets 0 on testnet after the config is read
            nameof(Globals.TXHeightRule4),                        // the nonce rule, dark on every network
            nameof(Globals.ShieldedSupplyCorrectionsThroughHeight), // mainnet history
            nameof(Globals.BitcoinCheckpointHeightMainnet),       // Bitcoin heights
            nameof(Globals.BitcoinCheckpointHeightTestnet),
            nameof(Globals.CertEnforceHeight),                    // armed by the genesis membership record
            nameof(Globals.ValidatorLastBlockHeight),             // runtime state
            nameof(Globals.ValidatorListSyncedHeight),
            nameof(Globals.ConsensusStartHeight),
        };

        private readonly Dictionary<FieldInfo, object?> _priorFields;
        private readonly List<Bitcoin.ElectrumX.ClientSettings> _priorClientSettings = Globals.ClientSettings;
        private readonly StunServer[] _priorStun = Globals.STUNServers.ToArray();
        private readonly Dictionary<string, decimal> _priorBurns = new(Globals.ForgedVfxBurns, StringComparer.Ordinal);
        private readonly Dictionary<string, decimal> _priorCorrections = new(Globals.ShieldedSupplyCorrections, StringComparer.Ordinal);

        public CustomTestNetHeightsTests()
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
            Globals.ForgedVfxBurns.Clear();
            foreach (var kv in _priorBurns) Globals.ForgedVfxBurns[kv.Key] = kv.Value;
            Globals.ShieldedSupplyCorrections.Clear();
            foreach (var kv in _priorCorrections) Globals.ShieldedSupplyCorrections[kv.Key] = kv.Value;
        }

        private static void StartTestnetNode(Config.Config config)
        {
            Globals.IsTestNet = true;
            Globals.IsCustomTestNet = false;
            Globals.ApplyNetworkHeights();
            Config.Config.ProcessConfig(config);
        }

        private static IEnumerable<FieldInfo> RuleHeights() =>
            typeof(Globals).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(long))
                .Where(f => f.Name.EndsWith("Height", StringComparison.Ordinal) || f.Name.StartsWith("TXHeightRule", StringComparison.Ordinal))
                .Where(f => !NotRuleHeights.Contains(f.Name));

        [Fact]
        public void Marigold_EveryRuleIsActiveBy401()
        {
            StartTestnetNode(new Config.Config { TestNet = true, TestNetName = "marigold" });

            Assert.True(Globals.IsCustomTestNet);
            Assert.Equal(401, Globals.CustomTestNetRulesHeight);
            var late = RuleHeights()
                .Select(f => (f.Name, Value: (long)f.GetValue(null)!))
                .Where(x => x.Value > Globals.CustomTestNetRulesHeight)
                .Select(x => $"{x.Name}={x.Value}")
                .ToList();
            Assert.True(late.Count == 0, "Heights above 401 on Marigold: " + string.Join(", ", late));

            // The ones the live testnet staged later now start at 401 ...
            Assert.Equal(401, Globals.VrfSeedHeight);
            Assert.Equal(401, Globals.VreeActivationHeight);
            Assert.Equal(401, Globals.ContractBodyLimitsHeight);
            Assert.Equal(401, Globals.PlonkCurrentRulesHeight);
            Assert.Equal(401, Globals.TXHeightRule3);
            Assert.Equal(401, Globals.CanonicalInputHeight); // not yet set on the live testnet
            // ... and the genesis-staged ones keep their order (bonds before the fund-loss rules, observation first).
            Assert.Equal(Globals.BitcoinAttestationObserveTestnetHeight, Globals.ValidatorBondGrandfatherHeight);
            Assert.Equal(Globals.FundLossAuditTestnetHeight - 1, Globals.ValidatorBondHeight);
            Assert.Equal(Globals.FundLossAuditTestnetHeight, Globals.PrivateTxProofRulesHeight);
            Assert.Equal(Globals.BitcoinAttestationTestnetHeight, Globals.BitcoinAttestationHeight);
            // Not yet on the live testnet or mainnet, active here.
            Assert.Equal(401, Globals.RawDataVerbatimHeight);
        }

        [Fact]
        public void RawDataVerbatim_IsUnsetOnMainnetAndTheLiveTestnet()
        {
            Globals.IsTestNet = false;
            Globals.IsCustomTestNet = false;
            Globals.ApplyNetworkHeights();
            Assert.Equal(long.MaxValue, Globals.RawDataVerbatimHeight);

            StartTestnetNode(new Config.Config { TestNet = true });
            Assert.Equal(long.MaxValue, Globals.RawDataVerbatimHeight);
        }

        [Fact]
        public void LiveTestnet_KeepsItsOwnHeights()
        {
            var rule3 = Globals.TXHeightRule3;
            StartTestnetNode(new Config.Config { TestNet = true });

            Assert.False(Globals.IsCustomTestNet);
            Assert.Equal(Globals.LedgerRulesBatchTestnetHeight, Globals.VrfSeedHeight);
            Assert.Equal(Globals.VreeActivationTestnetHeight, Globals.VreeActivationHeight);
            Assert.Equal(Globals.ContractBodyLimitsTestnetHeight, Globals.ContractBodyLimitsHeight);
            Assert.Equal(Globals.PlonkCurrentRulesTestnetHeight, Globals.PlonkCurrentRulesHeight);
            Assert.Equal(rule3, Globals.TXHeightRule3);
            Assert.Equal(long.MaxValue, Globals.CanonicalInputHeight);
        }

        [Fact]
        public void Mainnet_LeavesTheCanonicalInputRuleUnset()
        {
            Globals.IsTestNet = false;
            Globals.IsCustomTestNet = false;
            Globals.CanonicalInputHeight = long.MaxValue;
            Globals.ApplyNetworkHeights();

            Assert.Equal(long.MaxValue, Globals.CanonicalInputHeight);
        }
    }
}
