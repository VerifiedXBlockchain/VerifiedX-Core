using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VerifiedXCore;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Every per-network activation height is set in one place, Globals.ApplyNetworkHeights, which both start paths call:
    /// the Warden's own copy had drifted from Program.cs on testnet.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class NetworkHeightsTests : IDisposable
    {
        private readonly bool _priorTestNet = Globals.IsTestNet;
        private readonly Dictionary<FieldInfo, object?> _priorFields;
        private readonly Dictionary<string, decimal> _priorBurns = new(Globals.ForgedVfxBurns, StringComparer.Ordinal);
        private readonly Dictionary<string, decimal> _priorCorrections = new(Globals.ShieldedSupplyCorrections, StringComparer.Ordinal);

        public NetworkHeightsTests()
        {
            _priorFields = typeof(Globals).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsLiteral && !f.IsInitOnly && (f.FieldType == typeof(long) || f.FieldType == typeof(string)))
                .ToDictionary(f => f, f => f.GetValue(null));
        }

        public void Dispose()
        {
            foreach (var (field, value) in _priorFields)
                field.SetValue(null, value);
            Globals.IsTestNet = _priorTestNet;
            Globals.ForgedVfxBurns.Clear();
            foreach (var kv in _priorBurns) Globals.ForgedVfxBurns[kv.Key] = kv.Value;
            Globals.ShieldedSupplyCorrections.Clear();
            foreach (var kv in _priorCorrections) Globals.ShieldedSupplyCorrections[kv.Key] = kv.Value;
        }

        private static string Source(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VerifiedXCore", "VerifiedXCore.csproj")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, "VerifiedXCore", relative));
        }

        [Fact]
        public void Testnet_GetsTheHeightsTheWardenCopyHadLeftAtMainnetValues()
        {
            Globals.IsTestNet = true;
            Globals.ApplyNetworkHeights();

            Assert.Equal(Globals.FundLossAuditTestnetHeight, Globals.PrivateTxProofRulesHeight);
            Assert.Equal(1, Globals.VbtcV1RetirementHeight);
            Assert.Equal(1, Globals.VbtcV2DkgAttestationHeight);
            Assert.Equal(1, Globals.VbtcVaultTxSizeHeight);
        }

        [Fact]
        public void BothStartPaths_SetTheHeightsThroughTheOneMethod()
        {
            var assignment = new Regex(@"Globals\.\w*Height\s*=(?!=)");
            var warden = Source(Path.Combine("Services", "WardenService.cs"));
            var program = Source("Program.cs");

            Assert.Contains("Globals.ApplyNetworkHeights();", warden);
            Assert.Contains("Globals.ApplyNetworkHeights();", program);
            Assert.False(assignment.IsMatch(warden), "WardenService sets an activation height of its own");
        }
    }
}
