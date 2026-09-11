using Nethereum.ABI;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Web3;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Detects VFX validator set changes that need syncing to the Base contract.
    /// Batches add/remove operations with a 10-block cooldown, collects caster endorsements,
    /// and the winning caster submits addValidator/addValidatorBatch/removeValidator/removeValidatorBatch.
    /// </summary>
    public static class BaseValidatorSyncService
    {
        private const int COOLDOWN_BLOCKS = 10;
        private const int SYNC_CHECK_INTERVAL_MS = 30_000;
        private const int MAX_BATCH_SIZE = 100;

        private static readonly ConcurrentDictionary<string, ValidatorSyncAction> _pendingAdds = new();
        private static readonly ConcurrentDictionary<string, ValidatorSyncAction> _pendingRemoves = new();

        public enum SyncActionType { Add, Remove }

        public class ValidatorSyncAction
        {
            public string BaseAddress { get; set; } = "";
            public string VfxAddress { get; set; } = "";
            public SyncActionType ActionType { get; set; }
            public long DetectedAtBlock { get; set; }
        }

        /// <summary>
        /// Typed, caster-signed request body for <c>valapi/Validator/SignValidatorUpdate</c>.
        /// The requester (a block caster) signs <see cref="BuildSignRequestMessage"/> with its VFX key.
        /// </summary>
        public class ValidatorUpdateSignRequest
        {
            public string Action { get; set; } = "";
            public string[] TargetAddresses { get; set; } = Array.Empty<string>();
            public long VfxBlockHeight { get; set; }
            public string RequesterAddress { get; set; } = "";
            public long Timestamp { get; set; }
            public string Signature { get; set; } = "";
        }

        /// <summary>Max clock skew (seconds) accepted on a sign request.</summary>
        private const long SIGN_REQUEST_MAX_AGE_SECONDS = 300;
        /// <summary>Max distance between the requester's VfxBlockHeight and our local height.</summary>
        private const long SIGN_REQUEST_MAX_HEIGHT_DRIFT = 50;

        public static string BuildSignRequestMessage(string action, IEnumerable<string> targetAddresses, long vfxBlockHeight, string requesterAddress, long timestamp)
        {
            var targets = string.Join(",", targetAddresses.Select(t => t.Trim().ToLowerInvariant()).OrderBy(t => t, StringComparer.Ordinal));
            return $"VFX_BASE_VALSYNC|{NormalizeAction(action)}|{targets}|{vfxBlockHeight}|{requesterAddress}|{timestamp}";
        }

        private static string NormalizeAction(string action)
        {
            var a = (action ?? "").Trim().ToUpperInvariant();
            if (a.EndsWith("_BATCH", StringComparison.Ordinal))
                a = a[..^"_BATCH".Length];
            return a;
        }

        private static bool IsEvmAddress(string? addr)
        {
            if (string.IsNullOrWhiteSpace(addr)) return false;
            var a = addr.Trim();
            if (!a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || a.Length != 42) return false;
            return a[2..].All(Uri.IsHexDigit);
        }

        private static bool IsKnownCasterAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            if (Globals.BootstrapCasterAddresses.Contains(address)) return true;
            if (Globals.BlockCasters.Any(p => string.Equals(p.ValidatorAddress, address, StringComparison.Ordinal))) return true;
            lock (Globals.KnownCasters)
            {
                if (Globals.KnownCasters.Any(c => string.Equals(c.Address, address, StringComparison.Ordinal))) return true;
            }
            return false;
        }

        /// <summary>
        /// Builds the current (lower-cased Base address -> VFX address) map of registered public
        /// VFX validators. This is the authoritative "should be on the Base contract" set.
        /// </summary>
        private static Dictionary<string, string> BuildVfxValidatorBaseAddressMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var val in VBTCValidatorRegistry.GetPublicValidators())
            {
                var baseAddr = val.BaseAddress;
                if (string.IsNullOrEmpty(baseAddr) && !string.IsNullOrEmpty(val.FrostPublicKey))
                    baseAddr = ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(val.FrostPublicKey);
                if (!string.IsNullOrEmpty(baseAddr))
                    map[baseAddr.ToLowerInvariant()] = val.ValidatorAddress;
            }
            return map;
        }

        /// <summary>
        /// Authorizes a remote request to sign a validator ADD/REMOVE for the Base contract.
        /// A node only signs a change that it has INDEPENDENTLY detected (present in its own
        /// pending queue, past cooldown) and that still agrees with current VFX validator state,
        /// and only when the request itself is signed by a known block caster with a fresh timestamp.
        /// This is the guard against an unauthenticated caller collecting signatures for an
        /// attacker-chosen validator set change.
        /// </summary>
        public static (bool Ok, string Reason) AuthorizeSignRequest(ValidatorUpdateSignRequest? req)
        {
            if (req == null) return (false, "Empty request");
            if (!BaseBridgeService.IsBridgeConfigured) return (false, "Base bridge not configured");
            if (string.IsNullOrEmpty(Globals.ValidatorAddress)) return (false, "Not a validator");

            var action = NormalizeAction(req.Action);
            if (action != "ADD" && action != "REMOVE") return (false, "Unsupported action");

            var targets = (req.TargetAddresses ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToLowerInvariant())
                .Distinct()
                .ToArray();
            if (targets.Length == 0) return (false, "Missing target address");
            if (targets.Length > MAX_BATCH_SIZE) return (false, "Too many targets");
            if (targets.Any(t => !IsEvmAddress(t))) return (false, "Invalid target address");

            var now = TimeUtil.GetTime();
            if (Math.Abs(now - req.Timestamp) > SIGN_REQUEST_MAX_AGE_SECONDS) return (false, "Request timestamp out of range");

            var localHeight = Globals.LastBlock?.Height ?? 0;
            if (Math.Abs(localHeight - req.VfxBlockHeight) > SIGN_REQUEST_MAX_HEIGHT_DRIFT) return (false, "VfxBlockHeight too far from local height");

            if (string.IsNullOrWhiteSpace(req.RequesterAddress) || string.IsNullOrWhiteSpace(req.Signature))
                return (false, "Requester address and signature required");
            if (!IsKnownCasterAddress(req.RequesterAddress)) return (false, "Requester is not a known block caster");

            var msg = BuildSignRequestMessage(action, targets, req.VfxBlockHeight, req.RequesterAddress, req.Timestamp);
            if (!VerifiedXCore.Services.SignatureService.VerifySignature(req.RequesterAddress, msg, req.Signature))
                return (false, "Invalid requester signature");

            // Independent proof: every target must be a change THIS node has queued and aged.
            var pending = action == "ADD" ? _pendingAdds : _pendingRemoves;
            var vfxMap = BuildVfxValidatorBaseAddressMap();
            foreach (var t in targets)
            {
                if (!pending.TryGetValue(t, out var pa)) return (false, $"No pending {action} sync action for {t}");
                if (localHeight - pa.DetectedAtBlock < COOLDOWN_BLOCKS) return (false, $"Pending {action} for {t} has not passed cooldown");

                // Re-check against live VFX validator state at signing time.
                var isVfxValidator = vfxMap.ContainsKey(t);
                if (action == "ADD" && !isVfxValidator) return (false, $"{t} is not a registered VFX validator");
                if (action == "REMOVE" && isVfxValidator) return (false, $"{t} is still a registered VFX validator");
            }

            return (true, "");
        }

        /// <summary>
        /// Background loop: checks for validator set changes and syncs to Base contract.
        /// </summary>
        public static async Task ValidatorSyncLoop(CancellationToken ct = default)
        {
            while (!Globals.IsChainSynced && !ct.IsCancellationRequested)
                await Task.Delay(5_000, ct);

            LogUtility.Log("[BaseValidatorSync] Validator sync loop started.", "BaseValidatorSyncService");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CheckForValidatorChanges();
                    await ProcessPendingActions();
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"[BaseValidatorSync] Error: {ex.Message}", "BaseValidatorSyncService.ValidatorSyncLoop()");
                }

                await Task.Delay(SYNC_CHECK_INTERVAL_MS, ct);
            }
        }

        private static async Task CheckForValidatorChanges()
        {
            if (string.IsNullOrEmpty(Globals.ValidatorBaseAddress))
                return;

            var rpcUrl = BaseBridgeService.BaseRpcUrl;
            var contractAddress = BaseBridgeService.ContractAddress;
            if (string.IsNullOrEmpty(rpcUrl) || string.IsNullOrEmpty(contractAddress))
                return;

            try
            {
                var web3 = new Web3(rpcUrl);

                // Call getValidators() on the Base contract
                var baseValidators = await web3.Eth.GetContract(MinimalAbi.VBTCb, contractAddress)
                    .GetFunction("getValidators")
                    .CallAsync<List<string>>();

                var baseValidatorSet = new HashSet<string>(
                    baseValidators.Select(v => v.ToLowerInvariant()));

                // Get active VFX validators with Base addresses
                var vfxValidatorBaseAddresses = BuildVfxValidatorBaseAddressMap();

                var currentBlock = Globals.LastBlock?.Height ?? 0;

                foreach (var kvp in vfxValidatorBaseAddresses)
                {
                    if (!baseValidatorSet.Contains(kvp.Key) && !_pendingAdds.ContainsKey(kvp.Key))
                    {
                        _pendingAdds.TryAdd(kvp.Key, new ValidatorSyncAction
                        {
                            BaseAddress = kvp.Key,
                            VfxAddress = kvp.Value,
                            ActionType = SyncActionType.Add,
                            DetectedAtBlock = currentBlock
                        });
                        LogUtility.Log($"[BaseValidatorSync] Queued ADD for {kvp.Key} (VFX: {kvp.Value})", "BaseValidatorSyncService");
                    }
                }

                foreach (var baseAddr in baseValidatorSet)
                {
                    if (!vfxValidatorBaseAddresses.ContainsKey(baseAddr) && !_pendingRemoves.ContainsKey(baseAddr))
                    {
                        _pendingRemoves.TryAdd(baseAddr, new ValidatorSyncAction
                        {
                            BaseAddress = baseAddr,
                            VfxAddress = "",
                            ActionType = SyncActionType.Remove,
                            DetectedAtBlock = currentBlock
                        });
                        LogUtility.Log($"[BaseValidatorSync] Queued REMOVE for {baseAddr}", "BaseValidatorSyncService");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[BaseValidatorSync] CheckForValidatorChanges error: {ex.Message}", "BaseValidatorSyncService");
            }
        }

        private static async Task ProcessPendingActions()
        {
            var currentBlock = Globals.LastBlock?.Height ?? 0;

            var readyAdds = _pendingAdds.Values
                .Where(a => currentBlock - a.DetectedAtBlock >= COOLDOWN_BLOCKS)
                .ToList();

            if (readyAdds.Any() && Globals.IsBlockCaster)
            {
                // Collect signatures and submit — for now log the intent
                LogUtility.Log($"[BaseValidatorSync] {readyAdds.Count} validator ADD(s) ready for Base submission.", "BaseValidatorSyncService");

                var signatures = await CollectValidatorSignatures("ADD", readyAdds, currentBlock);
                if (signatures.Count >= 2)
                {
                    LogUtility.Log($"[BaseValidatorSync] Collected {signatures.Count} signatures for ADD. Submitting to Base contract.", "BaseValidatorSyncService");
                    // TODO: Submit addValidator/addValidatorBatch transaction to Base contract
                    // This requires a funded Base account to pay gas. For now, log the action.
                    foreach (var add in readyAdds)
                        _pendingAdds.TryRemove(add.BaseAddress, out _);
                }
                else
                {
                    ErrorLogUtility.LogError($"[BaseValidatorSync] Insufficient signatures for ADD ({signatures.Count}/2 minimum)", "BaseValidatorSyncService");
                }
            }

            var readyRemoves = _pendingRemoves.Values
                .Where(a => currentBlock - a.DetectedAtBlock >= COOLDOWN_BLOCKS)
                .ToList();

            if (readyRemoves.Any() && Globals.IsBlockCaster)
            {
                LogUtility.Log($"[BaseValidatorSync] {readyRemoves.Count} validator REMOVE(s) ready for Base submission.", "BaseValidatorSyncService");

                var signatures = await CollectValidatorSignatures("REMOVE", readyRemoves, currentBlock);
                if (signatures.Count >= 2)
                {
                    LogUtility.Log($"[BaseValidatorSync] Collected {signatures.Count} signatures for REMOVE. Submitting to Base contract.", "BaseValidatorSyncService");
                    // TODO: Submit removeValidator/removeValidatorBatch transaction to Base contract
                    foreach (var rem in readyRemoves)
                        _pendingRemoves.TryRemove(rem.BaseAddress, out _);
                }
                else
                {
                    ErrorLogUtility.LogError($"[BaseValidatorSync] Insufficient signatures for REMOVE ({signatures.Count}/2 minimum)", "BaseValidatorSyncService");
                }
            }
        }

        /// <summary>
        /// Collects EIP-191 signatures from active validators for a validator sync operation.
        /// </summary>
        private static async Task<List<byte[]>> CollectValidatorSignatures(string action, List<ValidatorSyncAction> actions, long vfxBlockHeight)
        {
            var signatures = new List<byte[]>();

            // Sign locally first
            var localSig = SignValidatorUpdateLocally(action, actions.Select(a => a.BaseAddress).ToArray(), vfxBlockHeight);
            if (localSig != null)
                signatures.Add(localSig);

            // Collect from remote validators via HTTP. The request is signed with this caster's
            // VFX key; remote nodes refuse unsigned or non-caster requests (see AuthorizeSignRequest).
            var targetAddresses = actions.Select(a => a.BaseAddress).ToArray();
            var requestTimestamp = TimeUtil.GetTime();
            var requestSignature = VerifiedXCore.Services.SignatureService.ValidatorSignature(
                BuildSignRequestMessage(action, targetAddresses, vfxBlockHeight, Globals.ValidatorAddress, requestTimestamp));
            if (string.IsNullOrEmpty(requestSignature))
            {
                ErrorLogUtility.LogError("[BaseValidatorSync] Could not sign validator update request; skipping remote collection.", "BaseValidatorSyncService");
                return signatures;
            }

            var validators = VBTCValidatorRegistry.GetPublicValidators();
            using var httpClient = Globals.HttpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var tasks = validators
                .Where(v => v.ValidatorAddress != Globals.ValidatorAddress && !string.IsNullOrEmpty(v.IPAddress))
                .Select(async v =>
                {
                    try
                    {
                        var url = $"http://{v.IPAddress}:{Globals.ValAPIPort}/valapi/Validator/SignValidatorUpdate";
                        var payload = new ValidatorUpdateSignRequest
                        {
                            Action = action,
                            TargetAddresses = targetAddresses,
                            VfxBlockHeight = vfxBlockHeight,
                            RequesterAddress = Globals.ValidatorAddress,
                            Timestamp = requestTimestamp,
                            Signature = requestSignature
                        };
                        var content = new StringContent(
                            Newtonsoft.Json.JsonConvert.SerializeObject(payload),
                            Encoding.UTF8,
                            "application/json");

                        var response = await httpClient.PostAsync(url, content);
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(json);
                            string? sigHex = result?.Signature;
                            if (!string.IsNullOrEmpty(sigHex))
                            {
                                return Convert.FromHexString(sigHex.StartsWith("0x") ? sigHex[2..] : sigHex);
                            }
                        }
                    }
                    catch { }
                    return null;
                });

            var results = await Task.WhenAll(tasks);
            foreach (var sig in results.Where(s => s != null))
                signatures.Add(sig!);

            return signatures;
        }

        /// <summary>
        /// Signs a validator update message locally using this node's Base private key.
        /// Matches the Solidity keccak256(abi.encodePacked(action, address, vfxBlockHeight, adminNonce, chainid, contractAddress)).
        /// </summary>
        public static byte[]? SignValidatorUpdateLocally(string action, string[] targetAddresses, long vfxBlockHeight)
        {
            try
            {
                var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);
                if (account == null) return null;

                var privHex = account.GetKey;
                if (string.IsNullOrEmpty(privHex)) return null;

                // Strip 0x prefix if present
                var cleanHex = privHex.StartsWith("0x") ? privHex[2..] : privHex;
                // Pad odd-length hex strings (some VFX private keys have odd nibble counts)
                if (cleanHex.Length % 2 != 0)
                    cleanHex = "0" + cleanHex;

                byte[] privBytes;
                try
                {
                    privBytes = Convert.FromHexString(cleanHex);
                }
                catch (FormatException)
                {
                    ErrorLogUtility.LogError(
                        $"[BaseValidatorSync] SignValidatorUpdateLocally: private key is not valid hex (length={cleanHex.Length}). Skipping.",
                        "BaseValidatorSyncService");
                    return null;
                }
                var chainId = BaseBridgeService.BaseChainId;
                var contractAddr = BaseBridgeService.ContractAddress;

                // Read adminNonce from Base contract
                BigInteger adminNonce = 0;
                try
                {
                    var web3 = new Web3(BaseBridgeService.BaseRpcUrl);
                    var contract = web3.Eth.GetContract(MinimalAbi.VBTCb, contractAddr);
                    adminNonce = contract.GetFunction("getAdminNonce").CallAsync<BigInteger>().GetAwaiter().GetResult();
                }
                catch { }

                // Build Solidity-compatible message hash
                var abiEncoder = new ABIEncode();
                byte[] messageHash;

                if (targetAddresses.Length == 1)
                {
                    // Single: keccak256(abi.encodePacked(action, address, vfxBlockHeight, adminNonce, chainid, contractAddress))
                    messageHash = Nethereum.Util.Sha3Keccack.Current.CalculateHash(
                        abiEncoder.GetABIEncodedPacked(
                            new ABIValue("string", action),
                            new ABIValue("address", targetAddresses[0]),
                            new ABIValue("uint256", new BigInteger(vfxBlockHeight)),
                            new ABIValue("uint256", adminNonce),
                            new ABIValue("uint256", new BigInteger(chainId)),
                            new ABIValue("address", contractAddr)));
                }
                else
                {
                    // Batch: keccak256(abi.encodePacked(action, abi.encodePacked(addresses), vfxBlockHeight, adminNonce, chainid, contractAddress))
                    var packedAddresses = abiEncoder.GetABIEncodedPacked(
                        targetAddresses.Select(a => new ABIValue("address", a)).ToArray());

                    messageHash = Nethereum.Util.Sha3Keccack.Current.CalculateHash(
                        abiEncoder.GetABIEncodedPacked(
                            new ABIValue("string", action.Contains("BATCH") ? action : action + "_BATCH"),
                            new ABIValue("bytes", packedAddresses),
                            new ABIValue("uint256", new BigInteger(vfxBlockHeight)),
                            new ABIValue("uint256", adminNonce),
                            new ABIValue("uint256", new BigInteger(chainId)),
                            new ABIValue("address", contractAddr)));
                }

                return ValidatorEthKeyService.EthSign(messageHash, privBytes);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[BaseValidatorSync] SignValidatorUpdateLocally error: {ex.Message}", "BaseValidatorSyncService");
                return null;
            }
        }

        /// <summary>
        /// Returns the current pending sync actions for diagnostic/API purposes.
        /// </summary>
        public static (int PendingAdds, int PendingRemoves) GetPendingCounts()
        {
            return (_pendingAdds.Count, _pendingRemoves.Count);
        }

        /// <summary>Minimal ABI fragments for read-only calls.</summary>
        private static class MinimalAbi
        {
            public const string VBTCb = @"[
                {""inputs"":[],""name"":""getValidators"",""outputs"":[{""internalType"":""address[]"",""name"":"""",""type"":""address[]""}],""stateMutability"":""view"",""type"":""function""},
                {""inputs"":[],""name"":""getAdminNonce"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},
                {""inputs"":[],""name"":""validatorCount"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},
                {""inputs"":[],""name"":""requiredMintSignatures"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""}
            ]";
        }
    }
}