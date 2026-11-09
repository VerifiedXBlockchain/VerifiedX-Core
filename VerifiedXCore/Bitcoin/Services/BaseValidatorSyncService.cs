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
            /// <summary>ADD or REMOVE (validators), or CASTERS.</summary>
            public string Action { get; set; } = "";
            /// <summary>The batch, in the order it is sent (for CASTERS, the casters added).</summary>
            public string[] TargetAddresses { get; set; } = Array.Empty<string>();
            /// <summary>For CASTERS, the casters removed, in the order they are sent.</summary>
            public string[] RemoveAddresses { get; set; } = Array.Empty<string>();
            /// <summary>The epoch boundary the change is computed at.</summary>
            public long VfxBlockHeight { get; set; }
            /// <summary>The contract's admin nonce the signature is for.</summary>
            public string AdminNonce { get; set; } = "";
        }

        internal const string AdminChangesNotSignedReason =
            "This node signs Base validator ADD/REMOVE messages only for a version 4 contract, from BaseBridgeV4Height.";

        /// <summary>A V4 contract upgraded from V3 has no casters until they are added; the validators alone change nothing.</summary>
        internal const string NoCastersYetReason = "The Base contract has no casters yet: they are added before any validator change.";

        /// <summary>
        /// A request this node will sign: the digest, and the once-an-epoch log it is recorded in (an addition or a caster
        /// change signed as a member of the contract; null for anything else).
        /// </summary>
        internal sealed record Authorized(byte[] Digest, string Action, string? LogKey, long Epoch, string AdminNonce);

        /// <summary>The once-an-epoch log of caster changes this node signed as a member.</summary>
        private static string CasterLogKey => BaseBridgeService.ContractAddress + "#casters";

        internal static async Task<(Authorized? Ok, string Reason)> AuthorizeAsync(ValidatorUpdateSignRequest? req)
        {
            if (req == null) return (null, "Empty request");
            if (string.IsNullOrEmpty(Globals.ValidatorAddress)) return (null, "Not a validator");
            if (!BaseBridgeService.IsBridgeConfigured) return (null, "Base bridge not configured");
            var action = (req.Action ?? "").Trim().ToUpperInvariant();
            if (action is not ("ADD" or "REMOVE" or "CASTERS")) return (null, "Unsupported action");
            var casters = action == "CASTERS";

            // Everything this node can judge by itself comes before the contract is read: a request it refuses anyway
            // costs it no Base call.
            if (req.VfxBlockHeight < Globals.BaseBridgeV4Height) return (null, AdminChangesNotSignedReason);
            var tip = Globals.LastBlock?.Height ?? -1;
            var epoch = BaseSignerSetPolicy.EpochAt(tip);
            if (req.VfxBlockHeight != epoch) return (null, "VfxBlockHeight is not the current epoch boundary");
            if (tip - epoch < BaseSignerSetPolicy.SettleBlocks) return (null, "The epoch boundary is not settled yet");
            if (BaseBridgeAttestationService.NotReadyToAttest() is { } notReady) return (null, notReady);
            var targets = (req.TargetAddresses ?? Array.Empty<string>()).Select(t => (t ?? "").Trim()).ToArray();
            var removing = casters ? (req.RemoveAddresses ?? Array.Empty<string>()).Select(t => (t ?? "").Trim()).ToArray() : Array.Empty<string>();
            if (targets.Length + removing.Length == 0 || targets.Concat(removing).Any(t => !BaseSignerSetPolicy.IsAddress(t))) return (null, "Invalid target address");

            var view = await SignerViewForSigningAsync();
            if (view == null) return (null, "Could not read the Base contract");
            if (view.MessageVersion != VbtcbTypedData.MessageVersion) return (null, AdminChangesNotSignedReason);
            if (!casters && view.CasterList.Count == 0) return (null, NoCastersYetReason);
            if (req.AdminNonce != view.AdminNonce.ToString()) return (null, "Admin nonce is not the contract's");

            // A member is on either of the contract's lists: the change needs both groups' signatures.
            var myBase = ValidatorEthKeyService.DeriveBaseAddressFromAccount(Globals.ValidatorAddress);
            var asMember = !string.IsNullOrEmpty(myBase)
                && (view.Validators.Contains(myBase, StringComparer.OrdinalIgnoreCase) || view.CasterList.Contains(myBase, StringComparer.OrdinalIgnoreCase));
            long chainId = BaseBridgeService.BaseChainId;

            if (casters)
            {
                var casterPlan = BaseSignerSetPolicy.CastersForEpoch(epoch, view.CasterList);
                if (casterPlan == null) return (null, "No block at the epoch boundary");
                if (casterPlan.Empty || !targets.SequenceEqual(casterPlan.Add, StringComparer.OrdinalIgnoreCase) || !removing.SequenceEqual(casterPlan.Remove, StringComparer.OrdinalIgnoreCase))
                    return (null, "Not the caster change this node computes for the epoch");
                if (!asMember && !targets.Contains(myBase, StringComparer.OrdinalIgnoreCase))
                    return (null, "Only a validator or caster on the contract, or a caster being added, signs a caster change");
                if (asMember && BaseSignerSignLog.Get(CasterLogKey) is { } casterLog && casterLog.Epoch == epoch && casterLog.AdminNonce != view.AdminNonce.ToString())
                    return (null, "This node already signed a caster change that went through this epoch");
                return (new Authorized(VbtcbTypedData.UpdateCastersDigest(targets, removing, epoch, view.AdminNonce, chainId, BaseBridgeService.ContractAddress),
                    action, asMember ? CasterLogKey : null, epoch, view.AdminNonce.ToString()), "");
            }

            var plan = BaseSignerSetPolicy.ForEpoch(epoch, view.Validators);
            if (plan == null) return (null, "No block at the epoch boundary");
            var add = action == "ADD";
            if (!add)
            {
                if (!asMember) return (null, "Only a validator or caster on the contract signs a removal");
                if (!targets.SequenceEqual(plan.Removes, StringComparer.OrdinalIgnoreCase)) return (null, "Not the removal this node computes for the epoch");
                // One removal an epoch: the next batch, computed against the smaller set, waits for the next boundary, so
                // fewer than a third leave in an epoch however often the loop runs.
                if (RemovalDone(epoch, view.AdminNonce.ToString())) return (null, "This node already signed a removal that went through this epoch");
            }
            else
            {
                if (plan.Removes.Count > 0) return (null, "This epoch's removals come first");
                if (!InPlanOrder(targets, plan.Adds)) return (null, "Not an addition this node computes for the epoch");
                if (!asMember && !targets.Contains(myBase, StringComparer.OrdinalIgnoreCase))
                    return (null, "Only a validator or caster on the contract, or one being added, signs an addition");
                if (asMember && BaseSignerSignLog.Get(BaseBridgeService.ContractAddress) is { } log && log.Epoch == epoch && log.AdminNonce != view.AdminNonce.ToString())
                    return (null, "This node already signed an addition that went through this epoch");
            }

            var digest = add
                ? VbtcbTypedData.AddValidatorsDigest(targets, epoch, view.AdminNonce, chainId, BaseBridgeService.ContractAddress)
                : VbtcbTypedData.RemoveValidatorsDigest(targets, epoch, view.AdminNonce, chainId, BaseBridgeService.ContractAddress);
            return (new Authorized(digest, action, add && asMember ? BaseBridgeService.ContractAddress : add ? null : RemovalLogKey, epoch, view.AdminNonce.ToString()), "");
        }

        /// <summary>The once-an-epoch log of validator removals this node signed.</summary>
        private static string RemovalLogKey => BaseBridgeService.ContractAddress + "#remove";

        /// <summary>Whether a removal this node signed this epoch went through (the admin nonce has moved since).</summary>
        private static bool RemovalDone(long epoch, string adminNonce) =>
            BaseSignerSignLog.Get(RemovalLogKey) is { } log && log.Epoch == epoch && log.AdminNonce != adminNonce;

        /// <summary>Seconds one read of the contract answers every signing request for (one read in flight at a time).</summary>
        internal const int SignerViewSeconds = 10;
        private static readonly object SignerViewLock = new();
        internal static (DateTime At, string Contract, Task<BaseBridgeService.SignerView?> View)? SignerViewCache;

        /// <summary>
        /// The contract as SignValidatorUpdate judges it. The route is open to anyone, so requests share one read for a few
        /// seconds rather than each making its own Base calls; a nonce read a moment late only makes the signature one the
        /// contract refuses.
        /// </summary>
        private static Task<BaseBridgeService.SignerView?> SignerViewForSigningAsync()
        {
            lock (SignerViewLock)
            {
                var contract = BaseBridgeService.ContractAddress ?? "";
                if (SignerViewCache is { } c && c.Contract == contract && (DateTime.UtcNow - c.At).TotalSeconds < SignerViewSeconds)
                    return c.View;
                var view = BaseBridgeService.ReadSignerViewAsync();
                SignerViewCache = (DateTime.UtcNow, contract, view);
                return view;
            }
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
            if (auth.LogKey != null) BaseSignerSignLog.Save(auth.LogKey, auth.Epoch, auth.AdminNonce);
            LogUtility.Log($"[BaseValidatorSync] Signed {auth.Action} of {(req!.TargetAddresses?.Length ?? 0) + (req.RemoveAddresses?.Length ?? 0)} for epoch {auth.Epoch} at admin nonce {auth.AdminNonce}", "BaseValidatorSyncService");
            return (signature, "");
        }

        // ── The submitting half ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Blocks each contract validator waits after the one before it in the epoch's order (keccak256(boundary block
        /// hash, address)) before it gathers the signatures and sends the change itself: one sender at a time, and the next
        /// steps in when one has no gas or is down. A second send of the same change fails at the gas estimate (the admin
        /// nonce has moved), so it costs nothing.
        /// </summary>
        public const int SubmitTurnBlocks = 50;

        private const string ValidatorSetAbi = @"[
            {""inputs"":[{""internalType"":""address[]"",""name"":""newValidators"",""type"":""address[]""},{""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""},{""internalType"":""bytes[]"",""name"":""newValidatorSignatures"",""type"":""bytes[]""}],""name"":""addValidatorBatch"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},
            {""inputs"":[{""internalType"":""address[]"",""name"":""oldValidators"",""type"":""address[]""},{""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""}],""name"":""removeValidatorBatch"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},
            {""inputs"":[{""internalType"":""address[]"",""name"":""add"",""type"":""address[]""},{""internalType"":""address[]"",""name"":""remove"",""type"":""address[]""},{""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""},{""internalType"":""bytes[]"",""name"":""newCasterSignatures"",""type"":""bytes[]""}],""name"":""updateCasters"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""}
        ]";

        /// <summary>Stands in for the Base transaction in tests: function name and arguments in, transaction hash out.</summary>
        internal static Func<string, object[], Task<string?>>? SubmitForTests;

        /// <summary>Background: once an epoch, the contract's members bring its casters and validators in step with VFX.</summary>
        public static Task ValidatorSyncLoop() =>
            PausableLoop.RunAsync(async () =>
            {
                try { await SyncOnceAsync(); }
                catch (Exception ex) { ErrorLogUtility.LogError($"[BaseValidatorSync] {ex.Message}", "BaseValidatorSyncService.ValidatorSyncLoop"); }
            }, 60_000, "BaseValidatorSyncService.ValidatorSyncLoop");

        /// <summary>
        /// One step: when it is this node's turn, it sends the epoch's caster change, else its validator removal, or once there
        /// is none its addition. Every
        /// signature is asked of the validator itself through SignValidatorUpdate, where each node checks the change
        /// against its own computation, and kept only when it recovers to that validator.
        /// </summary>
        internal static async Task SyncOnceAsync()
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress) || !BaseBridgeService.IsBridgeConfigured) return;
            var tip = Globals.LastBlock?.Height ?? -1;
            var epoch = BaseSignerSetPolicy.EpochAt(tip);
            if (epoch < 0 || epoch < Globals.BaseBridgeV4Height || tip - epoch < BaseSignerSetPolicy.SettleBlocks) return;
            if (BaseBridgeAttestationService.NotReadyToAttest() != null) return;

            var view = await BaseBridgeService.ReadSignerViewAsync();
            if (view == null || view.MessageVersion != VbtcbTypedData.MessageVersion) return;
            var myBase = ValidatorEthKeyService.DeriveBaseAddressFromAccount(Globals.ValidatorAddress);
            var members = view.Validators.Concat(view.CasterList).Select(m => m.ToLowerInvariant()).Distinct().ToList();
            if (string.IsNullOrEmpty(myBase) || !members.Contains(myBase.ToLowerInvariant())) return;
            var epochHash = BlockchainData.GetBlockByHeight(epoch)?.Hash;
            if (string.IsNullOrEmpty(epochHash)) return;

            var turn = members.OrderBy(m => BaseSignerSetPolicy.RankOf(epochHash, m), StringComparer.Ordinal).ToList().IndexOf(myBase.ToLowerInvariant());
            if (tip < epoch + BaseSignerSetPolicy.SettleBlocks + (long)turn * SubmitTurnBlocks) return;

            var ipOf = VBTCValidatorRegistry.GetActiveValidatorsAt(tip)
                .Select(v => (Base: BaseSignerSetPolicy.BaseAddressOf(v.FrostPublicKey).ToLowerInvariant(), v.IPAddress))
                .Where(x => x.Base.Length > 0 && !string.IsNullOrEmpty(x.IPAddress))
                .GroupBy(x => x.Base).ToDictionary(g => g.Key, g => g.First().IPAddress);
            foreach (var s in UserBridgeMintService.ContractSigners(view))   // casters too, at the address their record gives
                ipOf.TryAdd(s.BaseAddress.ToLowerInvariant(), s.IPAddress);
            foreach (var c in VerifiedXCore.Services.CasterMembershipStore.GetForHeight(epoch)?.Casters ?? new List<VerifiedXCore.Models.CasterInfo>())
                if (BaseSignerSetPolicy.BaseAddressOf(c.PublicKey) is { Length: > 0 } b && !string.IsNullOrEmpty(c.PeerIP))
                    ipOf.TryAdd(b.ToLowerInvariant(), c.PeerIP);                // casters joining the contract
            var nonce = view.AdminNonce.ToString();

            // The casters' change goes first (a contract upgraded from V3 has none, and nothing else changes until it has),
            // once an epoch: after one went through, this node signs no second one, so it does not try to send one.
            var casterPlan = BaseSignerSetPolicy.CastersForEpoch(epoch, view.CasterList);
            var casterChangeDone = BaseSignerSignLog.Get(CasterLogKey) is { } casterLog && casterLog.Epoch == epoch && casterLog.AdminNonce != nonce;
            if (casterPlan is { Empty: false } && !casterChangeDone)
            {
                var casterAdd = casterPlan.Add.ToArray();
                var casterRemove = casterPlan.Remove.ToArray();
                var casterDigest = VbtcbTypedData.UpdateCastersDigest(casterAdd, casterRemove, epoch, view.AdminNonce, BaseBridgeService.BaseChainId, BaseBridgeService.ContractAddress);
                var seat = new Dictionary<string, string>();
                foreach (var a in casterAdd)
                    if (ipOf.TryGetValue(a, out var ip) && await AskAsync(ip, "CASTERS", casterAdd, epoch, nonce, casterDigest, a, casterRemove) is { } sig)
                        seat[a] = sig;
                if (seat.Count != casterAdd.Length)
                {
                    LogUtility.Log($"[BaseValidatorSync] Caster change for epoch {epoch}: {seat.Count}/{casterAdd.Length} new casters signed their own seat.", "BaseValidatorSyncService");
                    return;
                }
                var casterSigs = await CollectAsync("CASTERS", casterAdd, epoch, nonce, members, ipOf, myBase, view, casterDigest, casterRemove);
                if (!UserBridgeMintService.Enough(view, view.RequiredMintSignatures, casterSigs.Keys))
                {
                    LogUtility.Log($"[BaseValidatorSync] Caster change for epoch {epoch}: {Tally(view, casterSigs)}.", "BaseValidatorSyncService");
                    return;
                }
                await SubmitAsync("updateCasters", new object[]
                {
                    casterAdd.ToList(), casterRemove.ToList(), new System.Numerics.BigInteger(epoch), casterSigs.Values.Select(HexBytes).ToList(), casterAdd.Select(a => HexBytes(seat[a])).ToList(),
                });
                return;
            }
            if (view.CasterList.Count == 0) return;

            var plan = BaseSignerSetPolicy.ForEpoch(epoch, view.Validators);
            if (plan == null || (plan.Removes.Count == 0 && plan.Adds.Count == 0)) return;

            if (plan.Removes.Count > 0)
            {
                if (RemovalDone(epoch, nonce)) return;                           // the rest leave at the next boundary
                var removes = plan.Removes.ToArray();
                var sigs = await CollectAsync("REMOVE", removes, epoch, nonce, members, ipOf, myBase, view,
                    VbtcbTypedData.RemoveValidatorsDigest(removes, epoch, view.AdminNonce, BaseBridgeService.BaseChainId, BaseBridgeService.ContractAddress));
                if (!UserBridgeMintService.Enough(view, view.RequiredMintSignatures, sigs.Keys))
                {
                    LogUtility.Log($"[BaseValidatorSync] Removal for epoch {epoch}: {Tally(view, sigs)}. If too many went dark, a proposal removes them (SignBaseAdminAction PROPOSE_REMOVAL).", "BaseValidatorSyncService");
                    return;
                }
                await SubmitAsync("removeValidatorBatch", new object[] { removes.ToList(), new System.Numerics.BigInteger(epoch), sigs.Values.Select(HexBytes).ToList() });
                return;
            }

            // Each newcomer signs its own addition over the exact list; one that does not answer waits for the next epoch.
            var adds = plan.Adds.ToArray();
            var own = new Dictionary<string, string>();
            var allSigned = false;
            for (var round = 0; round < 3 && adds.Length > 0 && !allSigned; round++)
            {
                own.Clear();
                var digest = VbtcbTypedData.AddValidatorsDigest(adds, epoch, view.AdminNonce, BaseBridgeService.BaseChainId, BaseBridgeService.ContractAddress);
                foreach (var a in adds)
                    if (ipOf.TryGetValue(a, out var ip) && await AskAsync(ip, "ADD", adds, epoch, nonce, digest, a) is { } sig)
                        own[a] = sig;
                allSigned = own.Count == adds.Length;
                if (!allSigned) adds = adds.Where(own.ContainsKey).ToArray();   // signed over the longer list: asked again
            }
            if (!allSigned || adds.Length == 0) return;
            var memberSigs = await CollectAsync("ADD", adds, epoch, nonce, members, ipOf, myBase, view,
                VbtcbTypedData.AddValidatorsDigest(adds, epoch, view.AdminNonce, BaseBridgeService.BaseChainId, BaseBridgeService.ContractAddress));
            if (!UserBridgeMintService.Enough(view, view.RequiredMintSignatures, memberSigs.Keys))
            {
                LogUtility.Log($"[BaseValidatorSync] Addition for epoch {epoch}: {Tally(view, memberSigs)}.", "BaseValidatorSyncService");
                return;
            }
            await SubmitAsync("addValidatorBatch", new object[]
            {
                adds.ToList(), new System.Numerics.BigInteger(epoch), memberSigs.Values.Select(HexBytes).ToList(), adds.Select(a => HexBytes(own[a])).ToList(),
            });
        }

        /// <summary>
        /// Distinct members' signatures over the digest by their Base address, this node's own first, until they meet both of
        /// the contract's thresholds (2/3 of its validators and a majority of its casters).
        /// </summary>
        private static async Task<Dictionary<string, string>> CollectAsync(string action, string[] targets, long epoch, string nonce, List<string> members,
            Dictionary<string, string> ipOf, string myBase, BaseBridgeService.SignerView view, byte[] digest, string[]? removes = null)
        {
            var sigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var (mine, _) = await SignValidatorUpdateAsync(new ValidatorUpdateSignRequest { Action = action, TargetAddresses = targets, RemoveAddresses = removes ?? Array.Empty<string>(), VfxBlockHeight = epoch, AdminNonce = nonce });
            if (mine != null) sigs[myBase] = mine;
            foreach (var m in members)
            {
                if (UserBridgeMintService.Enough(view, view.RequiredMintSignatures, sigs.Keys)) break;
                if (sigs.ContainsKey(m) || !ipOf.TryGetValue(m, out var ip)) continue;
                if (await AskAsync(ip, action, targets, epoch, nonce, digest, m, removes) is { } sig) sigs[m] = sig;
            }
            return sigs;
        }

        private static string Tally(BaseBridgeService.SignerView view, Dictionary<string, string> sigs) =>
            $"{view.Validators.Count(sigs.ContainsKey)}/{view.RequiredMintSignatures} validator and {view.CasterList.Count(sigs.ContainsKey)}/{view.RequiredCasterSignatures} caster signatures";

        /// <summary>One validator's signature over the digest, or null when it refuses, cannot be reached, or signs with another key.</summary>
        private static async Task<string?> AskAsync(string ip, string action, string[] targets, long epoch, string nonce, byte[] digest, string expectedSigner, string[]? removes = null)
        {
            try
            {
                var client = Globals.HttpClientFactory?.CreateClient() ?? new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var body = Newtonsoft.Json.JsonConvert.SerializeObject(new ValidatorUpdateSignRequest { Action = action, TargetAddresses = targets, RemoveAddresses = removes ?? Array.Empty<string>(), VfxBlockHeight = epoch, AdminNonce = nonce });
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync($"http://{ip.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/Validator/SignValidatorUpdate", content);
                if (!resp.IsSuccessStatusCode) return null;
                var text = await resp.Content.ReadAsStringAsync();
                if (text.TrimStart().StartsWith("\"")) text = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(text) ?? "";
                var sig = Newtonsoft.Json.Linq.JObject.Parse(text)["Signature"]?.ToString();
                return string.Equals(ValidatorEthKeyService.RecoverSigner(digest, sig, personalMessage: false), expectedSigner, StringComparison.OrdinalIgnoreCase) ? sig : null;
            }
            catch { return null; }
        }

        /// <summary>Sends the change from this validator's Base address (it pays the gas); a send the contract would refuse stops at the estimate.</summary>
        private static async Task SubmitAsync(string function, object[] args)
        {
            string? txHash;
            if (SubmitForTests != null) txHash = await SubmitForTests(function, args);
            else
            {
                var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);
                if (account == null) return;
                var privHex = account.GetKey;
                if (privHex.Length % 2 != 0) privHex = "0" + privHex;
                var ethAccount = new Nethereum.Web3.Accounts.Account(new Nethereum.Signer.EthECKey(HexByteUtility.HexToByte(privHex), true), BaseBridgeService.BaseChainId);
                var (ethOk, eth, _) = await BaseBridgeService.GetEthBalanceAsync(ethAccount.Address);
                if (!ethOk || eth <= 0)
                {
                    LogUtility.Log($"[BaseValidatorSync] {function}: this validator's Base address {ethAccount.Address} has no ETH for gas; the next validator in turn sends it.", "BaseValidatorSyncService");
                    return;
                }
                var web3 = new Nethereum.Web3.Web3(ethAccount, BaseBridgeService.BaseRpcUrl);
                var fn = web3.Eth.GetContract(ValidatorSetAbi, BaseBridgeService.ContractAddress).GetFunction(function);
                var gas = await fn.EstimateGasAsync(ethAccount.Address, null, null, args);
                txHash = await fn.SendTransactionAsync(ethAccount.Address, new Nethereum.Hex.HexTypes.HexBigInteger(gas.Value * 12 / 10), null, null, args);
                // Wait for it, so the next step reads the contract after it rather than sending it again.
                for (var i = 0; i < 60; i++)
                {
                    await Task.Delay(3_000);
                    var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                    if (receipt == null) continue;
                    LogUtility.Log($"[BaseValidatorSync] {function} {txHash} {(receipt.Status?.Value == 1 ? "confirmed" : "reverted")} in Base block {receipt.BlockNumber?.Value}", "BaseValidatorSyncService");
                    return;
                }
            }
            LogUtility.Log($"[BaseValidatorSync] Sent {function}: {txHash}", "BaseValidatorSyncService");
        }

        private static byte[] HexBytes(string hex) => Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);

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
