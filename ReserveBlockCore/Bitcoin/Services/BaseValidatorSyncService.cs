using Nethereum.ABI;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Web3;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text;

namespace ReserveBlockCore.Bitcoin.Services
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
                var vfxValidators = VBTCValidatorRegistry.GetActiveValidators();
                var vfxValidatorBaseAddresses = new Dictionary<string, string>();

                foreach (var val in vfxValidators)
                {
                    if (!string.IsNullOrEmpty(val.BaseAddress))
                        vfxValidatorBaseAddresses[val.BaseAddress.ToLowerInvariant()] = val.ValidatorAddress;
                }

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

            // Collect from remote validators via HTTP
            var validators = VBTCValidatorRegistry.GetActiveValidators();
            using var httpClient = Globals.HttpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var tasks = validators
                .Where(v => v.ValidatorAddress != Globals.ValidatorAddress && !string.IsNullOrEmpty(v.IPAddress))
                .Select(async v =>
                {
                    try
                    {
                        var url = $"http://{v.IPAddress}:{Globals.APIPort}/valapi/Validator/SignValidatorUpdate";
                        var payload = new
                        {
                            Action = action,
                            TargetAddresses = actions.Select(a => a.BaseAddress).ToArray(),
                            VfxBlockHeight = vfxBlockHeight
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

                var privBytes = Convert.FromHexString(privHex.StartsWith("0x") ? privHex[2..] : privHex);
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