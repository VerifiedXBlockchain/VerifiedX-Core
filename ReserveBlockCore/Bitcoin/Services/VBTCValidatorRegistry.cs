using Newtonsoft.Json.Linq;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Block-scan-based vBTC validator registry.
    /// Replaces the old persistent LiteDB approach with on-demand scanning of the last N blocks.
    /// 
    /// Validators send HEARTBEAT TXs every ~500 blocks, so scanning the last 1000 blocks
    /// guarantees we see every active validator at least once (REGISTER or HEARTBEAT).
    /// EXIT TXs within the window mark validators as inactive.
    /// 
    /// Results are cached per block height — the 1000-block scan runs at most once per new block.
    /// </summary>
    public static class VBTCValidatorRegistry
    {
        /// <summary>
        /// How many blocks to scan for validator lifecycle TXs.
        /// At ~10s/block, 1000 blocks ≈ 2.8 hours — covers 2 full heartbeat cycles (500 blocks each).
        /// </summary>
        public const int SCAN_WINDOW = 1000;

        // Cached result + the height it was computed at
        private static List<VBTCValidator>? _cachedValidators;
        private static long _cachedAtHeight = -1;
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Returns the current active validator set by scanning the last 1000 blocks
        /// for REGISTER, HEARTBEAT, and EXIT transactions.
        /// Cached per block height — recomputed only when a new block arrives.
        /// </summary>
        public static List<VBTCValidator> GetActiveValidators()
        {
            if (!Globals.IsChainSynced)
                return new List<VBTCValidator>();

            var currentHeight = Globals.LastBlock.Height;

            lock (_cacheLock)
            {
                if (_cachedValidators != null && _cachedAtHeight == currentHeight)
                    return _cachedValidators;
            }

            var validators = ScanBlocks(currentHeight);

            lock (_cacheLock)
            {
                _cachedValidators = validators;
                _cachedAtHeight = currentHeight;
            }

            return validators;
        }

        /// <summary>
        /// Looks up a single validator by address from the active set.
        /// Returns null if the validator is not found or not active.
        /// </summary>
        public static VBTCValidator? GetValidator(string address)
        {
            if (string.IsNullOrEmpty(address))
                return null;
            return GetActiveValidators().FirstOrDefault(v => v.ValidatorAddress == address);
        }

        /// <summary>
        /// Returns the count of currently active validators.
        /// </summary>
        public static int GetActiveValidatorCount()
        {
            return GetActiveValidators().Count;
        }

        /// <summary>
        /// Invalidate the cache so the next call to GetActiveValidators() rescans.
        /// Call this if you need a fresh view before the next block arrives.
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedAtHeight = -1;
                _cachedValidators = null;
            }
        }

        /// <summary>
        /// Scans the last SCAN_WINDOW blocks for validator lifecycle transactions
        /// and builds a map of active validators.
        /// </summary>
        private static List<VBTCValidator> ScanBlocks(long currentHeight)
        {
            var scanFrom = Math.Max(0, currentHeight - SCAN_WINDOW);
            var validatorMap = new Dictionary<string, VBTCValidator>();

            try
            {
                for (long h = scanFrom; h <= currentHeight; h++)
                {
                    var block = BlockchainData.GetBlockByHeight(h);
                    if (block?.Transactions == null || !block.Transactions.Any())
                        continue;

                    foreach (var tx in block.Transactions)
                    {
                        if (tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_REGISTER)
                            ProcessRegister(tx, h, validatorMap);
                        else if (tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT)
                            ProcessHeartbeat(tx, h, validatorMap);
                        else if (tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_EXIT)
                            ProcessExit(tx, h, validatorMap);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error scanning blocks for validator state: {ex}",
                    "VBTCValidatorRegistry.ScanBlocks()");
            }

            // Return only validators that are active (registered/heartbeated and not exited)
            return validatorMap.Values
                .Where(v => v.IsActive)
                .ToList();
        }

        private static void ProcessRegister(Transaction tx, long blockHeight, Dictionary<string, VBTCValidator> map)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var validatorAddress = jobj["ValidatorAddress"]?.ToObject<string>();
                if (string.IsNullOrEmpty(validatorAddress)) return;

                var ipAddress = jobj["IPAddress"]?.ToObject<string>() ?? "";
                var frostPublicKey = jobj["FrostPublicKey"]?.ToObject<string>() ?? "";
                var registrationBlockHeight = jobj["RegistrationBlockHeight"]?.ToObject<long>() ?? blockHeight;
                var signature = jobj["Signature"]?.ToObject<string>();

                if (map.TryGetValue(validatorAddress, out var existing))
                {
                    // Update if this block is newer
                    if (blockHeight >= existing.LastHeartbeatBlock)
                    {
                        existing.LastHeartbeatBlock = blockHeight;
                        existing.IsActive = true;
                        if (!string.IsNullOrEmpty(ipAddress)) existing.IPAddress = ipAddress;
                        if (!string.IsNullOrEmpty(frostPublicKey)) existing.FrostPublicKey = frostPublicKey;
                    }
                }
                else
                {
                    map[validatorAddress] = new VBTCValidator
                    {
                        ValidatorAddress = validatorAddress,
                        IPAddress = ipAddress,
                        FrostPublicKey = frostPublicKey,
                        RegistrationBlockHeight = registrationBlockHeight,
                        LastHeartbeatBlock = blockHeight,
                        IsActive = true,
                        RegistrationSignature = signature,
                        RegisterTransactionHash = tx.Hash
                    };
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing REGISTER TX in block scan: {ex.Message}",
                    "VBTCValidatorRegistry.ProcessRegister()");
            }
        }

        private static void ProcessHeartbeat(Transaction tx, long blockHeight, Dictionary<string, VBTCValidator> map)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var validatorAddress = jobj["ValidatorAddress"]?.ToObject<string>();
                if (string.IsNullOrEmpty(validatorAddress)) return;

                var ipAddress = jobj["IPAddress"]?.ToObject<string>();
                var frostPublicKey = jobj["FrostPublicKey"]?.ToObject<string>();

                if (map.TryGetValue(validatorAddress, out var existing))
                {
                    if (blockHeight >= existing.LastHeartbeatBlock)
                    {
                        if (!string.IsNullOrEmpty(ipAddress)) existing.IPAddress = ipAddress;
                        existing.IsActive = true;
                        existing.LastHeartbeatBlock = blockHeight;
                        if (!string.IsNullOrEmpty(frostPublicKey))
                            existing.FrostPublicKey = frostPublicKey;
                    }
                }
                else
                {
                    // Validator may have registered before our scan window — create from heartbeat
                    map[validatorAddress] = new VBTCValidator
                    {
                        ValidatorAddress = validatorAddress,
                        IPAddress = ipAddress ?? "",
                        FrostPublicKey = frostPublicKey ?? "",
                        RegistrationBlockHeight = blockHeight,
                        LastHeartbeatBlock = blockHeight,
                        IsActive = true,
                        RegisterTransactionHash = tx.Hash
                    };
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing HEARTBEAT TX in block scan: {ex.Message}",
                    "VBTCValidatorRegistry.ProcessHeartbeat()");
            }
        }

        private static void ProcessExit(Transaction tx, long blockHeight, Dictionary<string, VBTCValidator> map)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var validatorAddress = jobj["ValidatorAddress"]?.ToObject<string>();
                if (string.IsNullOrEmpty(validatorAddress)) return;

                var exitBlockHeight = jobj["ExitBlockHeight"]?.ToObject<long>() ?? blockHeight;

                if (map.TryGetValue(validatorAddress, out var existing))
                {
                    // Only apply exit if it's after the last heartbeat/register
                    if (blockHeight >= existing.LastHeartbeatBlock)
                    {
                        existing.IsActive = false;
                        existing.ExitTransactionHash = tx.Hash;
                        existing.ExitBlockHeight = exitBlockHeight;
                    }
                }
                else
                {
                    // EXIT without a prior REGISTER/HEARTBEAT in window — record as inactive
                    map[validatorAddress] = new VBTCValidator
                    {
                        ValidatorAddress = validatorAddress,
                        IsActive = false,
                        ExitTransactionHash = tx.Hash,
                        ExitBlockHeight = exitBlockHeight,
                        LastHeartbeatBlock = blockHeight
                    };
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error processing EXIT TX in block scan: {ex.Message}",
                    "VBTCValidatorRegistry.ProcessExit()");
            }
        }
    }
}