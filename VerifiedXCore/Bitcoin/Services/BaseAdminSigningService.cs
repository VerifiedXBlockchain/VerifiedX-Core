using System.Numerics;
using Nethereum.ABI;
using Nethereum.Web3;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// The node operator's signature on a vBTCb contract change (the localhost-only route SignBaseAdminAction): the V3
    /// UPGRADE message for the one move to V4, and on V4 a proposed upgrade or delay change, a proposed removal of
    /// validators or casters that went dark, or a veto of whatever is pending. The node reads the contract's version and admin nonce
    /// itself and signs for the contract and chain it is configured for, in the form that contract reads; the caller names
    /// only the action and its fields. For an upgrade it returns the implementation's code hash, for the operator to
    /// compare with the build they reviewed before handing the signature on.
    /// </summary>
    public static class BaseAdminSigningService
    {
        public const string Upgrade = "UPGRADE";
        public const string ProposeUpgrade = "PROPOSE_UPGRADE";
        public const string ProposeUpgradeDelay = "PROPOSE_UPGRADE_DELAY";
        public const string ProposeRemoval = "PROPOSE_REMOVAL";
        public const string CancelPending = "CANCEL_PENDING";

        /// <summary>The contract's bounds on upgradeDelay() (vBTCb.sol DEFAULT_UPGRADE_DELAY, MAX_UPGRADE_DELAY).</summary>
        private const long MinUpgradeDelay = 48 * 3600, MaxUpgradeDelay = 30 * 24 * 3600;

        public sealed class Request
        {
            public string Action { get; set; } = "";
            public string? Implementation { get; set; }
            public long? DelaySeconds { get; set; }
            public string[]? Validators { get; set; }
            public string[]? Casters { get; set; }
            public long? VfxBlockHeight { get; set; }
        }

        public sealed class Result
        {
            public bool Success { get; set; }
            public string? Error { get; set; }
            public string? Action { get; set; }
            public int MessageVersion { get; set; }
            public string? AdminNonce { get; set; }
            public string? Digest { get; set; }
            public string? Signature { get; set; }
            public string? Signer { get; set; }
            public string? ImplementationCodeHash { get; set; }
        }

        /// <summary>Stands in for eth_getCode in tests.</summary>
        internal static Func<string, Task<byte[]?>>? ReadCodeForTests;

        public static async Task<Result> SignAsync(Request? req)
        {
            static Result Fail(string error) => new() { Success = false, Error = error };
            if (req == null) return Fail("Empty request");
            if (string.IsNullOrEmpty(Globals.ValidatorAddress)) return Fail("Not running as a validator");
            if (!BaseBridgeService.IsBridgeConfigured) return Fail("Base bridge not configured");

            var action = (req.Action ?? "").Trim().ToUpperInvariant();
            if (action is not (Upgrade or ProposeUpgrade or ProposeUpgradeDelay or ProposeRemoval or CancelPending))
                return Fail("Unknown action");
            var view = await BaseBridgeService.ReadSignerViewAsync();
            if (view == null) return Fail("Could not read the Base contract");
            var v3 = action == Upgrade;
            if (v3 && view.MessageVersion != 3) return Fail($"UPGRADE is the V3 contract's message; this contract reads version {view.MessageVersion}");
            if (!v3 && view.MessageVersion != VbtcbTypedData.MessageVersion) return Fail($"{action} needs a version 4 contract; this one reads version {view.MessageVersion}");

            long chainId = BaseBridgeService.BaseChainId;
            var contract = BaseBridgeService.ContractAddress;
            string? codeHash = null;
            byte[] digest;
            switch (action)
            {
                case Upgrade:
                case ProposeUpgrade:
                    if (!IsAddress(req.Implementation)) return Fail("Implementation must be a 0x address");
                    var code = await ReadCodeAsync(req.Implementation!.Trim());
                    if (code == null) return Fail("Could not read the implementation's code");
                    if (code.Length == 0) return Fail("No contract at the implementation address");
                    codeHash = Hex(VbtcbTypedData.Keccak(code));
                    digest = v3
                        ? V3UpgradeHash(req.Implementation.Trim(), view.AdminNonce, chainId, contract)
                        : VbtcbTypedData.ProposeUpgradeDigest(req.Implementation.Trim(), view.AdminNonce, chainId, contract);
                    break;
                case ProposeUpgradeDelay:
                    if (req.DelaySeconds is not long delay || delay < MinUpgradeDelay || delay > MaxUpgradeDelay)
                        return Fail($"DelaySeconds must be from {MinUpgradeDelay} to {MaxUpgradeDelay}");
                    digest = VbtcbTypedData.ProposeUpgradeDelayDigest(delay, view.AdminNonce, chainId, contract);
                    break;
                case ProposeRemoval:
                    var targets = (req.Validators ?? Array.Empty<string>()).Select(t => t?.Trim() ?? "").ToArray();
                    var casterTargets = (req.Casters ?? Array.Empty<string>()).Select(t => t?.Trim() ?? "").ToArray();
                    if (targets.Length + casterTargets.Length == 0) return Fail("Name the validators or casters to remove");
                    if (targets.Any(t => !IsAddress(t))) return Fail("Validators must be 0x addresses");
                    if (casterTargets.Any(t => !IsAddress(t))) return Fail("Casters must be 0x addresses");
                    if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length) return Fail("Validators repeat an address");
                    if (casterTargets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != casterTargets.Length) return Fail("Casters repeat an address");
                    if (targets.Any(t => !view.Validators.Contains(t, StringComparer.OrdinalIgnoreCase))) return Fail("Every address must be a validator on the contract");
                    if (casterTargets.Any(t => !view.CasterList.Contains(t, StringComparer.OrdinalIgnoreCase))) return Fail("Every caster must be a caster on the contract");
                    // The way out for validators and casters that went dark: not for one that still runs as a vBTC
                    // validator, or that still sits in the caster committee.
                    var active = VBTCValidatorRegistry.GetActiveValidatorsAt(Globals.LastBlock?.Height ?? 0)
                        .Select(v => SafeBaseAddress(v.FrostPublicKey)).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (targets.FirstOrDefault(active.Contains) is { } live) return Fail($"{live} is still a registered vBTC validator");
                    var committee = (VerifiedXCore.Services.CasterMembershipStore.GetCurrent()?.Casters ?? new List<VerifiedXCore.Models.CasterInfo>())
                        .Select(c => SafeBaseAddress(c.PublicKey)).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (casterTargets.FirstOrDefault(committee.Contains) is { } sitting) return Fail($"{sitting} is still in the caster committee");
                    if (req.VfxBlockHeight is not long height || height < 0) return Fail("VfxBlockHeight required");
                    digest = VbtcbTypedData.ProposeRemovalDigest(targets, casterTargets, height, view.AdminNonce, chainId, contract);
                    break;
                default:
                    digest = VbtcbTypedData.CancelPendingDigest(view.AdminNonce, chainId, contract);
                    break;
            }

            var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);
            if (account == null) return Fail("Validator account not loaded");
            var privHex = account.GetKey;
            if (privHex.Length % 2 != 0) privHex = "0" + privHex;
            var priv = HexByteUtility.HexToByte(privHex);
            var signature = v3 ? ValidatorEthKeyService.EthSignMessageHex(digest, priv) : ValidatorEthKeyService.SignDigestHex(digest, priv);
            LogUtility.Log($"[BaseAdminSign] Signed {action} at admin nonce {view.AdminNonce} on {contract}" + (codeHash != null ? $" for {req.Implementation} (code hash {codeHash})" : ""), "BaseAdminSigningService");

            return new Result
            {
                Success = true, Action = action, MessageVersion = view.MessageVersion, AdminNonce = view.AdminNonce.ToString(),
                Digest = Hex(digest), Signature = signature, Signer = ValidatorEthKeyService.DeriveBaseAddress(priv), ImplementationCodeHash = codeHash,
            };
        }

        /// <summary>V3 upgradeWithValidatorApproval: keccak256(abi.encodePacked("UPGRADE", impl, adminNonce, chainid, contract)).</summary>
        internal static byte[] V3UpgradeHash(string implementation, BigInteger adminNonce, long chainId, string contractAddress) =>
            Nethereum.Util.Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncodedPacked(
                new ABIValue("string", Upgrade),
                new ABIValue("address", implementation),
                new ABIValue("uint256", adminNonce),
                new ABIValue("uint256", new BigInteger(chainId)),
                new ABIValue("address", contractAddress)));

        private static async Task<byte[]?> ReadCodeAsync(string address)
        {
            if (ReadCodeForTests != null) return await ReadCodeForTests(address);
            foreach (var url in BaseBridgeService.RpcUrlCandidates())
            {
                try
                {
                    var hex = await new Web3(url).Eth.GetCode.SendRequestAsync(address);
                    var body = (hex ?? "").StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex![2..] : hex ?? "";
                    return Convert.FromHexString(body);
                }
                catch { }
            }
            return null;
        }

        private static string SafeBaseAddress(string? publicKey)
        {
            try { return string.IsNullOrEmpty(publicKey) ? "" : ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(publicKey) ?? ""; }
            catch { return ""; }
        }

        private static bool IsAddress(string? a)
        {
            var t = (a ?? "").Trim();
            return t.Length == 42 && t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && t[2..].All(Uri.IsHexDigit);
        }

        private static string Hex(byte[] b) => "0x" + Convert.ToHexString(b).ToLowerInvariant();
    }
}
