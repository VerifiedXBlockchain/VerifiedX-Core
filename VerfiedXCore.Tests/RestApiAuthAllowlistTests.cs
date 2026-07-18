using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// REGRESSION GUARD: RestApiAuthFilter's wallet-lock gate is a hand-maintained
    /// BY-NAME allowlist (EncryptionRequiredActions). Two silent failure modes exist:
    ///
    ///   1. An entry goes stale — the action it names is renamed or removed, so the
    ///      entry matches nothing and the gate silently stops applying to that flow.
    ///   2. A critical signing action is dropped from the list, so a locked, encrypted
    ///      wallet no longer blocks it.
    ///
    /// These tests pin both invariants against the actual v2 controller surface via
    /// reflection. If you rename a v2 action that signs with local wallet keys, you
    /// MUST update the allowlist in the same change — that is what makes these fail.
    /// </summary>
    public class RestApiAuthAllowlistTests
    {
        /// <summary>
        /// Actions that sign with (or derive from) local wallet key material and must
        /// stay behind the wallet-lock gate. Deliberately a hard-coded floor: additions
        /// are fine without touching this, removals/renames must be conscious.
        /// </summary>
        private static readonly string[] CriticalSigningActions =
        {
            // core surface
            "Send", "ImportKey", "CreateSignature",
            "Mint", "Transfer", "Burn", "TransferOwnership",
            // vBTC
            "CreateVbtcContract", "CreateVbtcContractRaw", "TransferVbtc",
            "RequestVbtcWithdrawal", "CompleteVbtcWithdrawal", "CancelVbtcWithdrawalTx",
            "ShieldVbtc", "BridgeVbtcToBase",
            // Bitcoin / tokenized BTC
            "SendBtcTransaction", "ReplaceBtcByFee", "TransferBtcCoin",
            "TransferBtcCoinMulti", "WithdrawBtcCoin", "TokenizeBitcoin",
            "ImportBtcPrivateKey",
            // Privacy
            "ShieldVfx", "CreateShieldedAddressFromAccount", "GenerateShieldedAddress"
        };

        private static HashSet<string> GetFilterSet(string fieldName)
        {
            var field = typeof(RestApiAuthFilter).GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var value = field!.GetValue(null) as HashSet<string>;
            Assert.NotNull(value);
            return value!;
        }

        private static HashSet<string> GetV2ActionNames()
        {
            var controllerTypes = typeof(RestBaseController).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(RestBaseController).IsAssignableFrom(t));

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in controllerTypes)
            {
                var actions = type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => typeof(IActionResult).IsAssignableFrom(m.ReturnType)
                             || m.ReturnType == typeof(System.Threading.Tasks.Task<IActionResult>));
                foreach (var action in actions)
                    names.Add(action.Name);
            }
            return names;
        }

        [Fact]
        public void EncryptionRequiredActions_HasNoDanglingEntries()
        {
            var allowlist = GetFilterSet("EncryptionRequiredActions");
            var actionNames = GetV2ActionNames();

            var dangling = allowlist.Where(entry => !actionNames.Contains(entry)).ToList();

            Assert.True(dangling.Count == 0,
                "EncryptionRequiredActions entries match no v2 controller action — the wallet-lock " +
                $"gate is silently dead for: {string.Join(", ", dangling)}. " +
                "If the action was renamed, rename the allowlist entry with it.");
        }

        [Fact]
        public void EncryptionRequiredActions_ContainsAllCriticalSigningActions()
        {
            var allowlist = GetFilterSet("EncryptionRequiredActions");

            var missing = CriticalSigningActions.Where(a => !allowlist.Contains(a)).ToList();

            Assert.True(missing.Count == 0,
                "Critical local-key-signing actions are missing from EncryptionRequiredActions — " +
                $"a locked encrypted wallet would no longer block: {string.Join(", ", missing)}");
        }

        [Fact]
        public void TokenBypassActions_HasNoDanglingEntries()
        {
            var bypass = GetFilterSet("TokenBypassActions");
            var actionNames = GetV2ActionNames();

            var dangling = bypass.Where(entry => !actionNames.Contains(entry)).ToList();

            Assert.True(dangling.Count == 0,
                $"TokenBypassActions entries match no v2 controller action: {string.Join(", ", dangling)}");
        }

        [Fact]
        public void CriticalSigningActions_AllExistOnV2Controllers()
        {
            // Guards the guard: if a critical action is renamed, this points at the rename
            // rather than letting the no-dangling test report it indirectly.
            var actionNames = GetV2ActionNames();

            var missing = CriticalSigningActions.Where(a => !actionNames.Contains(a)).ToList();

            Assert.True(missing.Count == 0,
                "Critical signing actions no longer exist on any v2 controller (renamed or removed?): " +
                string.Join(", ", missing));
        }
    }
}
