using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// BB-3: default-deny encryption gate. While the wallet is encrypted and locked only actions in
    /// <see cref="LockedWalletPolicy.AllowedWhileLocked"/> run; everything else gets 401.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class LockedWalletPolicyTests : IDisposable
    {
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly SecureString? _priorToken = Globals.APIToken;
        private readonly bool _priorAlways = Globals.AlwaysRequireAPIPassword;

        public void Dispose()
        {
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.APIToken = _priorToken;
            Globals.AlwaysRequireAPIPassword = _priorAlways;
        }

        /// <summary>Every API action on the API host, as "ControllerName.ActionName".</summary>
        internal static HashSet<string> AllApiActions()
        {
            var asm = typeof(ActionFilterController).Assembly;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in asm.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
            {
                if (t.Name == "ValidatorController") continue; // served on the validator host only
                var ctrl = t.Name.EndsWith("Controller") ? t.Name[..^"Controller".Length] : t.Name;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (m.GetCustomAttributes<HttpMethodAttribute>().Any())
                        set.Add($"{ctrl}.{m.Name}");
            }
            return set;
        }

        [Fact]
        public void EveryAllowlistEntry_NamesARealAction()
        {
            var all = AllApiActions();
            var stale = LockedWalletPolicy.AllowedWhileLocked.Where(a => !all.Contains(a)).ToList();
            Assert.True(stale.Count == 0, "Allowlist names actions that do not exist: " + string.Join(", ", stale));
        }

        /// <summary>Actions that use or return local private-key material, or change key state. Must never be listed.</summary>
        public static readonly string[] MustBeDeniedWhileLocked =
        {
            "V1.GetNewAddress", "V1.ImportPrivateKey", "V1.GetMother", "V1.GetEncryptedPassword", "V1.GetHDWallet",
            "V1.GetRestoreHDWallet", "V1.SendTransaction", "V1.CreateSignature", "V1.CreateSignatureFromPrivateKey",
            "V1.GetEncryptWallet", "V1.StartMother", "V1.JoinMother", "V1.GetPrivateKey",
            "BTCV2.GetNewAddress", "BTCV2.GetBitcoinAccount", "BTCV2.ImportPrivateKey",
            "BTCV2.ReplaceByFee", "BTCV2.SendTransaction", "BTCV2.Broadcast", "BTCV2.ResetAccount",
            "RSV1.DecodeRestoreCode", "RSV1.UnlockReserveAccount", "RSV1.NewReserveAddress", "RSV1.GetReserveAccountNFTAssets",
            "TXV1.TestMempool", "TXV1.SendTransaction",
            "Wallet.SendVFX", "Wallet.ShieldVFX", "Wallet.UnshieldVFX", "Wallet.PrivateTransferVFX",
            "Wallet.CreateShieldedAddress", "Wallet.VBTCTransfer", "Wallet.VBTCWithdrawRequest", "Wallet.LinkBtcEvm",
            "PrivacyV1.ExportViewingKey", "PrivacyV1.ShieldVFX", "PrivacyV1.GenerateShieldedAddress",
            "VBTC.RequestWithdrawal", "VBTC.TransferVBTC", "VBTC.CreateVBTCContract",
        };

        [Fact]
        public void KeyBearingActions_AreNeverAllowedWhileLocked()
        {
            var all = AllApiActions();
            foreach (var a in MustBeDeniedWhileLocked.Where(all.Contains))
                Assert.False(LockedWalletPolicy.AllowedWhileLocked.Contains(a), $"{a} must not run while the wallet is locked");
        }

        [Fact]
        public void BitcoinAccountList_IsDeniedWhileLocked_UntilKeysAreRemovedFromTheResponse()
        {
            // BB-3 lands before VX-13; the list route returned plaintext keys until VX-13 replaces it with a DTO.
            Assert.False(LockedWalletPolicy.IsAllowedWhileLocked("BTCV2", "GetBitcoinAccountList"));
        }

        private static ActionExecutingContext Context(string controller, string action)
        {
            var http = new DefaultHttpContext();
            var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
            var actionContext = new ActionContext(http, new RouteData(), descriptor);
            return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);
        }

        private static void Lock()
        {
            Globals.AlwaysRequireAPIPassword = false;
            Globals.APIToken = null;
            Globals.IsWalletEncrypted = true;
            Globals.EncryptPassword = new SecureString();
        }

        [Fact]
        public void Locked_ListedRead_Passes()
        {
            Lock();
            var ctx = Context("V1", "GetWalletInfo");
            new ActionFilterController().OnActionExecuting(ctx);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public void Locked_AuditBypassRoutes_Are401()
        {
            Lock();
            foreach (var (c, a) in new[] { ("BTCV2", "GetBitcoinAccountList"), ("BTCV2", "GetBitcoinAccount"), ("BTCV2", "GetNewAddress"), ("BTCV2", "ReplaceByFee") })
            {
                var ctx = Context(c, a);
                new ActionFilterController().OnActionExecuting(ctx);
                var result = Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
                Assert.Equal(ActionFilterController.LockedWalletMessage, result.Value);
            }
        }

        [Fact]
        public void Locked_UnknownOrNewAction_IsDeniedByDefault()
        {
            Lock();
            var ctx = Context("V1", "SomeRouteAddedNextYear");
            new ActionFilterController().OnActionExecuting(ctx);
            Assert.IsType<UnauthorizedObjectResult>(ctx.Result);
        }

        [Fact]
        public void Locked_RawExternallySignedRelay_Passes()
        {
            Lock();
            var ctx = Context("TXV1", "SendRawTransaction");
            new ActionFilterController().OnActionExecuting(ctx);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public void Unlocked_UnlistedAction_Passes()
        {
            Lock();
            var pw = new SecureString();
            foreach (var ch in "pw") pw.AppendChar(ch);
            Globals.EncryptPassword = pw;
            var ctx = Context("BTCV2", "ReplaceByFee");
            new ActionFilterController().OnActionExecuting(ctx);
            Assert.Null(ctx.Result);
        }

        [Fact]
        public void NotEncrypted_UnlistedAction_Passes()
        {
            Globals.AlwaysRequireAPIPassword = false;
            Globals.APIToken = null;
            Globals.IsWalletEncrypted = false;
            var ctx = Context("BTCV2", "ReplaceByFee");
            new ActionFilterController().OnActionExecuting(ctx);
            Assert.Null(ctx.Result);
        }
    }
}
