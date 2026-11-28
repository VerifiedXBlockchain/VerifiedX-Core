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
        public void Marigold_EveryRuleIsActiveBy401_ExceptRulesAddedWhileItRuns()
        {
            StartTestnetNode(new Config.Config { TestNet = true, TestNetName = "marigold" });

            Assert.True(Globals.IsCustomTestNet);
            Assert.Equal(401, Globals.CustomTestNetRulesHeight);
            // Rules added after Marigold was already running start at CustomTestNetInputRulesHeight instead (a past
            // height would judge blocks produced under the old rules); every other rule is active by 401.
            var addedWhileRunning = new[] { nameof(Globals.CanonicalInputHeight), nameof(Globals.RawDataVerbatimHeight), nameof(Globals.StrictDataReaderHeight), nameof(Globals.ContractBodyKeySpellingHeight), nameof(Globals.ContractRunLimitsHeight), nameof(Globals.VrfMixHeight), nameof(Globals.BridgeLockMinimumHeight), nameof(Globals.BridgeAllocationsArrayHeight), nameof(Globals.PoolUnlockCreditsAddressHeight), nameof(Globals.BridgeLockEvmAddressHeight), nameof(Globals.WithdrawalOwnScriptAttributionHeight), nameof(Globals.PlonkKeyBoundRulesHeight) };
            var late = RuleHeights()
                .Where(f => !addedWhileRunning.Contains(f.Name))
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
            Assert.Equal(Globals.CustomTestNetInputRulesHeight, Globals.CanonicalInputHeight); // not yet set on the live testnet; later than 401, Marigold was already running
            // ... and the genesis-staged ones keep their order (bonds before the fund-loss rules, observation first).
            Assert.Equal(Globals.BitcoinAttestationObserveTestnetHeight, Globals.ValidatorBondGrandfatherHeight);
            Assert.Equal(Globals.FundLossAuditTestnetHeight - 1, Globals.ValidatorBondHeight);
            Assert.Equal(Globals.FundLossAuditTestnetHeight, Globals.PrivateTxProofRulesHeight);
            Assert.Equal(Globals.BitcoinAttestationTestnetHeight, Globals.BitcoinAttestationHeight);
            // Not yet on the live testnet or mainnet, active here.
            Assert.Equal(Globals.CustomTestNetInputRulesHeight, Globals.RawDataVerbatimHeight);
            Assert.Equal(Globals.CustomTestNetInputRulesHeight, Globals.StrictDataReaderHeight);
            Assert.Equal(Globals.CustomTestNetInputRulesHeight, Globals.ContractBodyKeySpellingHeight);
            Assert.Equal(Globals.CustomTestNetInputRulesHeight, Globals.ContractRunLimitsHeight);
            Assert.Equal(Globals.CustomTestNetInputRulesHeight, Globals.VrfMixHeight);
            Assert.Equal(Globals.CustomTestNetLateRulesHeight, Globals.BridgeLockMinimumHeight);
            Assert.Equal(Globals.CustomTestNetLateRulesHeight, Globals.BridgeAllocationsArrayHeight);
            Assert.Equal(Globals.CustomTestNetLateRulesHeight, Globals.PoolUnlockCreditsAddressHeight);
            Assert.Equal(Globals.CustomTestNetLateRulesHeight, Globals.BridgeLockEvmAddressHeight);
            Assert.Equal(Globals.CustomTestNetLateRulesHeight, Globals.WithdrawalOwnScriptAttributionHeight);
            Assert.Equal(Globals.CustomTestNetLateRulesHeight, Globals.PlonkKeyBoundRulesHeight);
            Assert.True(Globals.CustomTestNetInputRulesHeight > Globals.CustomTestNetRulesHeight, "rules added while Marigold runs start after its tip, not at 401");
            Assert.True(Globals.CustomTestNetLateRulesHeight > Globals.CustomTestNetInputRulesHeight, "rules added after Marigold passed 8,500 start after its tip then");
        }

        [Fact]
        public void ReleaseRules_AreUnsetOnMainnetAndSetTogetherOnTheLiveTestnet()
        {
            Globals.IsTestNet = false;
            Globals.IsCustomTestNet = false;
            Globals.ApplyNetworkHeights();
            Assert.Equal(long.MaxValue, Globals.RawDataVerbatimHeight);
            Assert.Equal(long.MaxValue, Globals.BridgeAllocationsArrayHeight);
            Assert.Equal(long.MaxValue, Globals.PoolUnlockCreditsAddressHeight);
            Assert.Equal(long.MaxValue, Globals.BridgeLockEvmAddressHeight);
            Assert.Equal(long.MaxValue, Globals.WithdrawalOwnScriptAttributionHeight);
            Assert.Equal(long.MaxValue, Globals.PlonkKeyBoundRulesHeight);

            StartTestnetNode(new Config.Config { TestNet = true });
            Assert.Equal(230_750L, Globals.ReleaseRulesTestnetHeight);
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.RawDataVerbatimHeight);
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.ContractBodyKeySpellingHeight);
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.ContractRunLimitsHeight);
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.BridgeLockMinimumHeight);
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.VrfMixHeight);
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.CanonicalInputHeight);
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.StrictDataReaderHeight);
            Assert.Equal(long.MaxValue, Globals.BridgeAllocationsArrayHeight); // not in that release
            Assert.Equal(long.MaxValue, Globals.PoolUnlockCreditsAddressHeight); // not in that release
            Assert.Equal(long.MaxValue, Globals.BridgeLockEvmAddressHeight); // not in that release
            Assert.Equal(long.MaxValue, Globals.WithdrawalOwnScriptAttributionHeight); // not in that release
            Assert.Equal(Globals.PlonkKeyBoundRulesTestnetHeight, Globals.PlonkKeyBoundRulesHeight); // not in that release
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
            Assert.Equal(Globals.ReleaseRulesTestnetHeight, Globals.CanonicalInputHeight);
        }

        /// <summary>
        /// A payment to a withdrawal's own vault stops settling it at WithdrawalOwnScriptAttributionHeight; such a withdrawal
        /// stops being fixed on coins at WithdrawalDestinationRecheckHeight. The first must never come before the second, on
        /// any network: between the two, a bridge exit's part to its own vault would be voided by its own payment, fixed
        /// again, paid to the vault again, and so on, at a Bitcoin fee each time.
        /// </summary>
        [Fact]
        public void OwnScriptAttribution_StartsNoEarlierThanTheDestinationRecheck_OnEveryNetwork()
        {
            Globals.IsTestNet = false;
            Globals.IsCustomTestNet = false;
            Globals.ApplyNetworkHeights();
            Assert.True(Globals.WithdrawalOwnScriptAttributionHeight >= Globals.WithdrawalDestinationRecheckHeight, "mainnet");

            StartTestnetNode(new Config.Config { TestNet = true });
            Assert.False(Globals.IsCustomTestNet);
            Assert.True(Globals.WithdrawalOwnScriptAttributionHeight >= Globals.WithdrawalDestinationRecheckHeight, "testnet");

            StartTestnetNode(new Config.Config { TestNet = true, TestNetName = "marigold" });
            Assert.True(Globals.IsCustomTestNet);
            Assert.True(Globals.WithdrawalOwnScriptAttributionHeight >= Globals.WithdrawalDestinationRecheckHeight, "custom testnet");
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
