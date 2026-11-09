using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Keeps the vBTCb contract's validator set in step with VFX (BaseSignerSetPolicy). The signing half: a validator signs
    /// an AddValidators or RemoveValidators only when it is exactly the change this node computes for the current epoch
    /// from its own chain and its own read of the contract, so whoever asks cannot choose what is signed. Only on a
    /// version 4 contract, whose typed messages cannot be read as anything else, and from BaseBridgeV4Height.
    /// </summary>
    public static class BaseValidatorSyncService
    {
        /// <summary>
        /// Pure selection rule for the Base minting set: with <paramref name="castersOnly"/>, only
        /// registered validators that are ALSO committee casters qualify (empty committee => empty
        /// set, fail closed). Otherwise every registered public validator qualifies (legacy).
        /// </summary>
        public static List<VBTCValidator> SelectBaseValidatorSet(IEnumerable<VBTCValidator>? validators, HashSet<string>? committee, bool castersOnly)
        {
            var list = (validators ?? Enumerable.Empty<VBTCValidator>()).Where(v => v != null && !string.IsNullOrEmpty(v.ValidatorAddress)).ToList();
            if (!castersOnly) return list;
            if (committee == null || committee.Count == 0) return new List<VBTCValidator>();
            return list.Where(v => committee.Contains(v.ValidatorAddress)).ToList();
        }

        /// <summary>The validators the caster attestation loop asks (a user's own node asks the contract's validators: UserBridgeMintService).</summary>
        public static List<VBTCValidator> SelectAttestingValidators()
        {
            var committee = BridgeCasterConsensus.GetCommitteeForHeight(Globals.LastBlock?.Height ?? 0);
            return SelectBaseValidatorSet(VBTCValidatorRegistry.GetPublicValidators(), committee, Globals.BaseValidatorSetCastersOnly);
        }

        /// <summary>Request body of <c>valapi/Validator/SignValidatorUpdate</c>.</summary>
        public class ValidatorUpdateSignRequest
        {
            /// <summary>ADD or REMOVE.</summary>
            public string Action { get; set; } = "";
            /// <summary>The batch, in the order it is sent.</summary>
            public string[] TargetAddresses { get; set; } = Array.Empty<string>();
            /// <summary>The epoch boundary the change is computed at.</summary>
            public long VfxBlockHeight { get; set; }
            /// <summary>The contract's admin nonce the signature is for.</summary>
            public string AdminNonce { get; set; } = "";
        }

        internal const string AdminChangesNotSignedReason =
            "This node signs Base validator ADD/REMOVE messages only for a version 4 contract, from BaseBridgeV4Height.";

        /// <summary>A request this node will sign: the digest, and whether it is an addition signed as a contract validator.</summary>
        internal sealed record Authorized(byte[] Digest, bool Add, bool AsMember, long Epoch, string AdminNonce);

        internal static async Task<(Authorized? Ok, string Reason)> AuthorizeAsync(ValidatorUpdateSignRequest? req)
        {
            if (req == null) return (null, "Empty request");
            if (string.IsNullOrEmpty(Globals.ValidatorAddress)) return (null, "Not a validator");
            if (!BaseBridgeService.IsBridgeConfigured) return (null, "Base bridge not configured");
            var action = (req.Action ?? "").Trim().ToUpperInvariant();
            if (action is not ("ADD" or "REMOVE")) return (null, "Unsupported action");

            var view = await BaseBridgeService.ReadSignerViewAsync();
            if (view == null) return (null, "Could not read the Base contract");
            if (view.MessageVersion != VbtcbTypedData.MessageVersion || req.VfxBlockHeight < Globals.BaseBridgeV4Height)
                return (null, AdminChangesNotSignedReason);

            var tip = Globals.LastBlock?.Height ?? -1;
            var epoch = BaseSignerSetPolicy.EpochAt(tip);
            if (req.VfxBlockHeight != epoch) return (null, "VfxBlockHeight is not the current epoch boundary");
            if (tip - epoch < BaseSignerSetPolicy.SettleBlocks) return (null, "The epoch boundary is not settled yet");
            if (BaseBridgeAttestationService.NotReadyToAttest() is { } notReady) return (null, notReady);
            if (req.AdminNonce != view.AdminNonce.ToString()) return (null, "Admin nonce is not the contract's");

            var targets = (req.TargetAddresses ?? Array.Empty<string>()).Select(t => (t ?? "").Trim()).ToArray();
            if (targets.Length == 0 || targets.Any(t => !BaseSignerSetPolicy.IsAddress(t))) return (null, "Invalid target address");
            var plan = BaseSignerSetPolicy.ForEpoch(epoch, view.Validators);
            if (plan == null) return (null, "No block at the epoch boundary");

            var myBase = ValidatorEthKeyService.DeriveBaseAddressFromAccount(Globals.ValidatorAddress);
            var asMember = !string.IsNullOrEmpty(myBase) && view.Validators.Contains(myBase, StringComparer.OrdinalIgnoreCase);
            var add = action == "ADD";
            if (!add)
            {
                if (!asMember) return (null, "Only a validator on the contract signs a removal");
                if (!targets.SequenceEqual(plan.Removes, StringComparer.OrdinalIgnoreCase)) return (null, "Not the removal this node computes for the epoch");
            }
            else
            {
                if (plan.Removes.Count > 0) return (null, "This epoch's removals come first");
                if (!InPlanOrder(targets, plan.Adds)) return (null, "Not an addition this node computes for the epoch");
                if (!asMember && !targets.Contains(myBase, StringComparer.OrdinalIgnoreCase))
                    return (null, "Only a validator on the contract, or one being added, signs an addition");
                if (asMember && BaseSignerSignLog.Get(BaseBridgeService.ContractAddress) is { } log && log.Epoch == epoch && log.AdminNonce != view.AdminNonce.ToString())
                    return (null, "This node already signed an addition that went through this epoch");
            }

            long chainId = BaseBridgeService.BaseChainId;
            var digest = add
                ? VbtcbTypedData.AddValidatorsDigest(targets, epoch, view.AdminNonce, chainId, BaseBridgeService.ContractAddress)
                : VbtcbTypedData.RemoveValidatorsDigest(targets, epoch, view.AdminNonce, chainId, BaseBridgeService.ContractAddress);
            return (new Authorized(digest, add, asMember, epoch, view.AdminNonce.ToString()), "");
        }

        /// <summary>This node's signature on the request, or null and why not (see <see cref="AuthorizeAsync"/>).</summary>
        public static async Task<(string? Signature, string Reason)> SignValidatorUpdateAsync(ValidatorUpdateSignRequest? req)
        {
            var (auth, reason) = await AuthorizeAsync(req);
            if (auth == null) return (null, reason);
            var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);
            if (account == null) return (null, "Validator account not loaded");
            var privHex = account.GetKey;
            if (privHex.Length % 2 != 0) privHex = "0" + privHex;
            var signature = ValidatorEthKeyService.SignDigestHex(auth.Digest, HexByteUtility.HexToByte(privHex));
            if (auth.Add && auth.AsMember) BaseSignerSignLog.Save(BaseBridgeService.ContractAddress, auth.Epoch, auth.AdminNonce);
            LogUtility.Log($"[BaseValidatorSync] Signed {(auth.Add ? "ADD" : "REMOVE")} of {req!.TargetAddresses.Length} for epoch {auth.Epoch} at admin nonce {auth.AdminNonce}", "BaseValidatorSyncService");
            return (signature, "");
        }

        /// <summary>Every target is in the plan's list, without repeats, in the plan's order.</summary>
        private static bool InPlanOrder(string[] targets, IReadOnlyList<string> plan)
        {
            var at = 0;
            foreach (var t in targets)
            {
                while (at < plan.Count && !string.Equals(plan[at], t, StringComparison.OrdinalIgnoreCase)) at++;
                if (at == plan.Count) return false;
                at++;
            }
            return true;
        }
    }
}
