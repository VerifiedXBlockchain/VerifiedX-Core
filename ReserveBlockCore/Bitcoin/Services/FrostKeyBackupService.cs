using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.FROST.Models;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Peer-distributed encrypted FROST key backup and recovery service.
    /// 
    /// After every DKG ceremony, each validator encrypts their FROST key material using a key
    /// derived from their VFX private key and distributes the encrypted blob to all peer validators.
    /// On recovery, a wiped validator authenticates to peers, retrieves its encrypted backups,
    /// decrypts with its VFX private key, and restores the FrostValidatorKeyStore records.
    /// 
    /// Security: AES-256-GCM provides authenticated encryption — peers hold ciphertext they cannot
    /// decrypt, and any modification is detected at the decryption step via the GCM auth tag.
    /// </summary>
    public static class FrostKeyBackupService
    {
        private static readonly HttpClient _backupHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        /// <summary>Maximum encrypted blob size (16 KB — generous for ~1-2 KB actual data)</summary>
        private const int MAX_BLOB_SIZE = 16 * 1024;

        /// <summary>Backup format version</summary>
        private const int BACKUP_VERSION = 1;

        #region Encryption / Decryption

        /// <summary>
        /// Encrypt a FrostValidatorKeyStore into an AES-256-GCM blob.
        /// The encryption key is derived deterministically from the validator's VFX private key.
        /// </summary>
        /// <returns>(base64EncryptedBlob, sha256PlaintextHash) or (null, null) on failure</returns>
        public static (string? encryptedBlob, string? plaintextHash) EncryptKeyPackage(
            string validatorAddress, string smartContractUID, FrostValidatorKeyStore keyStore)
        {
            try
            {
                // Serialize the key store components to JSON
                var plaintextObj = new
                {
                    keyStore.KeyPackage,
                    keyStore.PubkeyPackage,
                    keyStore.GroupPublicKey,
                    keyStore.ParticipantOrderJson
                };
                var plaintextJson = JsonConvert.SerializeObject(plaintextObj);
                var plaintextBytes = Encoding.UTF8.GetBytes(plaintextJson);

                // Hash the plaintext for integrity verification
                var plaintextHash = Convert.ToHexString(SHA256.HashData(plaintextBytes)).ToLower();

                // Derive encryption key from VFX private key
                var encryptionKey = DeriveEncryptionKey(validatorAddress);
                if (encryptionKey == null)
                {
                    ErrorLogUtility.LogError("Failed to derive encryption key", "FrostKeyBackupService.EncryptKeyPackage");
                    return (null, null);
                }

                // Derive deterministic nonce from encryption key + contract UID
                var nonce = DeriveNonce(encryptionKey, smartContractUID);

                // Encrypt with AES-256-GCM
                var ciphertext = new byte[plaintextBytes.Length];
                var tag = new byte[16]; // GCM tag is 16 bytes

#pragma warning disable SYSLIB0053 // AesGcm(byte[]) is obsolete in .NET 8+; needed for .NET 6 compat
                using (var aesGcm = new AesGcm(encryptionKey))
#pragma warning restore SYSLIB0053
                {
                    aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
                }

                // Combine tag + ciphertext for storage (tag first for easy extraction on decrypt)
                var combined = new byte[tag.Length + ciphertext.Length];
                Buffer.BlockCopy(tag, 0, combined, 0, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, combined, tag.Length, ciphertext.Length);

                var encryptedBlob = Convert.ToBase64String(combined);

                // Clear sensitive data
                Array.Clear(encryptionKey, 0, encryptionKey.Length);

                return (encryptedBlob, plaintextHash);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Encryption failed: {ex.Message}", "FrostKeyBackupService.EncryptKeyPackage");
                return (null, null);
            }
        }

        /// <summary>
        /// Decrypt an AES-256-GCM encrypted backup blob and return the key store components.
        /// Returns null if decryption fails (wrong key, tampered data, etc.)
        /// </summary>
        public static FrostKeyBackupPlaintext? DecryptKeyPackage(
            string validatorAddress, string smartContractUID, string encryptedBlob)
        {
            try
            {
                var combined = Convert.FromBase64String(encryptedBlob);
                if (combined.Length < 17) // minimum: 16-byte tag + 1 byte ciphertext
                {
                    ErrorLogUtility.LogError("Encrypted blob too short", "FrostKeyBackupService.DecryptKeyPackage");
                    return null;
                }

                // Extract tag and ciphertext
                var tag = new byte[16];
                var ciphertext = new byte[combined.Length - 16];
                Buffer.BlockCopy(combined, 0, tag, 0, 16);
                Buffer.BlockCopy(combined, 16, ciphertext, 0, ciphertext.Length);

                // Derive encryption key from VFX private key
                var encryptionKey = DeriveEncryptionKey(validatorAddress);
                if (encryptionKey == null)
                {
                    ErrorLogUtility.LogError("Failed to derive encryption key for decryption", "FrostKeyBackupService.DecryptKeyPackage");
                    return null;
                }

                // Derive deterministic nonce from encryption key + contract UID
                var nonce = DeriveNonce(encryptionKey, smartContractUID);

                // Decrypt with AES-256-GCM (will throw if tag doesn't match = tampered data)
                var plaintext = new byte[ciphertext.Length];
#pragma warning disable SYSLIB0053
                using (var aesGcm = new AesGcm(encryptionKey))
#pragma warning restore SYSLIB0053
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
                }

                // Clear sensitive data
                Array.Clear(encryptionKey, 0, encryptionKey.Length);

                // Deserialize the plaintext
                var plaintextJson = Encoding.UTF8.GetString(plaintext);
                var result = JsonConvert.DeserializeObject<FrostKeyBackupPlaintext>(plaintextJson);

                return result;
            }
            catch (CryptographicException)
            {
                // GCM auth tag mismatch — data was tampered with or wrong key
                LogUtility.Log("[FROST Backup] Decryption failed: GCM authentication tag mismatch (tampered or wrong key)",
                    "FrostKeyBackupService.DecryptKeyPackage");
                return null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Decryption failed: {ex.Message}", "FrostKeyBackupService.DecryptKeyPackage");
                return null;
            }
        }

        /// <summary>
        /// Derive AES-256 encryption key from the validator's VFX private key.
        /// Key = HMAC-SHA256(vfx_private_key_bytes, "frost-key-backup-v1"), truncated to 32 bytes.
        /// </summary>
        private static byte[]? DeriveEncryptionKey(string validatorAddress)
        {
            try
            {
                var account = AccountData.GetSingleAccount(validatorAddress);
                if (account == null)
                {
                    ErrorLogUtility.LogError($"Account not found for address: {validatorAddress}",
                        "FrostKeyBackupService.DeriveEncryptionKey");
                    return null;
                }

                // Convert hex private key to bytes
                var privateKeyHex = account.GetKey;
                var privateKeyBytes = HexToBytes(privateKeyHex);

                // HMAC-SHA256 with private key bytes as key and domain separator as data
                using (var hmac = new HMACSHA256(privateKeyBytes))
                {
                    var derivedKey = hmac.ComputeHash(Encoding.UTF8.GetBytes("frost-key-backup-v1"));
                    Array.Clear(privateKeyBytes, 0, privateKeyBytes.Length);
                    return derivedKey; // 32 bytes = AES-256 key
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Key derivation failed: {ex.Message}",
                    "FrostKeyBackupService.DeriveEncryptionKey");
                return null;
            }
        }

        /// <summary>
        /// Derive a deterministic 12-byte nonce from the encryption key and contract UID.
        /// nonce = first 12 bytes of HMAC-SHA256(encryption_key, smartContractUID)
        /// Safe because each (key, contractUID) pair is unique and never reused with different plaintext.
        /// </summary>
        private static byte[] DeriveNonce(byte[] encryptionKey, string smartContractUID)
        {
            using (var hmac = new HMACSHA256(encryptionKey))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(smartContractUID));
                var nonce = new byte[12]; // AES-GCM nonce is 12 bytes
                Buffer.BlockCopy(hash, 0, nonce, 0, 12);
                return nonce;
            }
        }

        private static byte[] HexToBytes(string hex)
        {
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        #endregion

        #region Broadcast

        /// <summary>
        /// Encrypt and broadcast a single key package backup to all peer validators.
        /// Called immediately after SaveKeyPackage() and after UpdateSmartContractUID().
        /// </summary>
        public static async Task<(int successCount, int failCount)> BroadcastBackupToValidators(
            string validatorAddress, string smartContractUID,
            FrostValidatorKeyStore keyStore, List<VBTCValidator> validators)
        {
            try
            {
                // Encrypt the key package
                var (encryptedBlob, plaintextHash) = EncryptKeyPackage(validatorAddress, smartContractUID, keyStore);
                if (string.IsNullOrEmpty(encryptedBlob) || string.IsNullOrEmpty(plaintextHash))
                {
                    ErrorLogUtility.LogError("Failed to encrypt key package for backup broadcast",
                        "FrostKeyBackupService.BroadcastBackupToValidators");
                    return (0, validators.Count);
                }

                // Check blob size
                if (encryptedBlob.Length > MAX_BLOB_SIZE)
                {
                    ErrorLogUtility.LogError($"Encrypted blob exceeds max size: {encryptedBlob.Length} > {MAX_BLOB_SIZE}",
                        "FrostKeyBackupService.BroadcastBackupToValidators");
                    return (0, validators.Count);
                }

                // Sign the backup request
                var timestamp = TimeUtil.GetTime();
                var signMessage = $"{validatorAddress}.{smartContractUID}.{timestamp}";
                var signature = ReserveBlockCore.Services.SignatureService.AddressSignature(validatorAddress, signMessage);
                if (signature == "ERROR")
                {
                    ErrorLogUtility.LogError("Failed to sign backup request",
                        "FrostKeyBackupService.BroadcastBackupToValidators");
                    return (0, validators.Count);
                }

                var requestPayload = JsonConvert.SerializeObject(new
                {
                    OwnerAddress = validatorAddress,
                    SmartContractUID = smartContractUID,
                    EncryptedBlob = encryptedBlob,
                    PlaintextHash = plaintextHash,
                    Version = BACKUP_VERSION,
                    Timestamp = timestamp,
                    Signature = signature
                });

                // Compute expected hash for verification
                var expectedStoredHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptedBlob))).ToLower();

                // Send to all peers in parallel (exclude self)
                var successCount = 0;
                var failCount = 0;
                var tasks = new List<Task>();

                foreach (var validator in validators)
                {
                    if (validator.ValidatorAddress == validatorAddress) continue;
                    if (string.IsNullOrEmpty(validator.IPAddress)) continue;

                    var v = validator; // capture for closure
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var url = $"http://{v.IPAddress}:{Globals.FrostValidatorPort}/frost/backup/store";
                            var content = new StringContent(requestPayload, Encoding.UTF8, "application/json");
                            var response = await _backupHttpClient.PostAsync(url, content);

                            if (response.IsSuccessStatusCode)
                            {
                                var responseBody = await response.Content.ReadAsStringAsync();
                                var responseObj = JsonConvert.DeserializeAnonymousType(responseBody,
                                    new { Success = false, StoredHash = "" });

                                if (responseObj?.Success == true && responseObj.StoredHash == expectedStoredHash)
                                {
                                    Interlocked.Increment(ref successCount);
                                }
                                else
                                {
                                    LogUtility.Log($"[FROST Backup] Peer {v.ValidatorAddress} stored backup but hash mismatch",
                                        "FrostKeyBackupService.BroadcastBackupToValidators");
                                    Interlocked.Increment(ref failCount);
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref failCount);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogUtility.Log($"[FROST Backup] Failed to send backup to {v.ValidatorAddress}: {ex.Message}",
                                "FrostKeyBackupService.BroadcastBackupToValidators");
                            Interlocked.Increment(ref failCount);
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                LogUtility.Log($"[FROST Backup] Broadcast complete for contract {smartContractUID}: " +
                    $"{successCount} success, {failCount} failed out of {validators.Count - 1} peers",
                    "FrostKeyBackupService.BroadcastBackupToValidators");

                return (successCount, failCount);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Broadcast failed: {ex.Message}",
                    "FrostKeyBackupService.BroadcastBackupToValidators");
                return (0, validators.Count);
            }
        }

        /// <summary>
        /// Broadcast all existing local key packages as encrypted backups to peers.
        /// Used for retroactive backup of keys from already-completed DKG ceremonies.
        /// </summary>
        public static async Task<(int totalSuccess, int totalFail)> BroadcastAllExistingBackups(
            string validatorAddress, List<VBTCValidator> validators)
        {
            try
            {
                var allKeys = FrostValidatorKeyStore.GetAllKeyPackages();
                if (allKeys.Count == 0)
                {
                    LogUtility.Log("[FROST Backup] No local key packages found for retroactive broadcast",
                        "FrostKeyBackupService.BroadcastAllExistingBackups");
                    return (0, 0);
                }

                var totalSuccess = 0;
                var totalFail = 0;

                LogUtility.Log($"[FROST Backup] Starting retroactive broadcast of {allKeys.Count} key packages",
                    "FrostKeyBackupService.BroadcastAllExistingBackups");

                foreach (var keyStore in allKeys)
                {
                    var (success, fail) = await BroadcastBackupToValidators(
                        validatorAddress, keyStore.SmartContractUID, keyStore, validators);
                    totalSuccess += success;
                    totalFail += fail;

                    // Brief delay to avoid overwhelming peers
                    await Task.Delay(500);
                }

                LogUtility.Log($"[FROST Backup] Retroactive broadcast complete: {totalSuccess} total successes, {totalFail} total failures across {allKeys.Count} contracts",
                    "FrostKeyBackupService.BroadcastAllExistingBackups");

                return (totalSuccess, totalFail);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Retroactive broadcast failed: {ex.Message}",
                    "FrostKeyBackupService.BroadcastAllExistingBackups");
                return (0, 0);
            }
        }

        /// <summary>
        /// Smart re-broadcast: check if peer already has the backup before sending full blob.
        /// Sends a lightweight check probe first, only sends full backup if peer is missing it.
        /// </summary>
        public static async Task<(int successCount, int skipCount, int failCount)> SmartBroadcastBackupToValidators(
            string validatorAddress, string smartContractUID,
            FrostValidatorKeyStore keyStore, List<VBTCValidator> validators)
        {
            try
            {
                // Encrypt first to get the plaintext hash for the check probe
                var (encryptedBlob, plaintextHash) = EncryptKeyPackage(validatorAddress, smartContractUID, keyStore);
                if (string.IsNullOrEmpty(encryptedBlob) || string.IsNullOrEmpty(plaintextHash))
                    return (0, 0, validators.Count);

                var successCount = 0;
                var skipCount = 0;
                var failCount = 0;
                var tasks = new List<Task>();

                // Sign backup request once (reused for all peers)
                var timestamp = TimeUtil.GetTime();
                var signMessage = $"{validatorAddress}.{smartContractUID}.{timestamp}";
                var signature = ReserveBlockCore.Services.SignatureService.AddressSignature(validatorAddress, signMessage);
                if (signature == "ERROR") return (0, 0, validators.Count);

                var expectedStoredHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encryptedBlob))).ToLower();

                foreach (var validator in validators)
                {
                    if (validator.ValidatorAddress == validatorAddress) continue;
                    if (string.IsNullOrEmpty(validator.IPAddress)) continue;

                    var v = validator;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // Step 1: Check if peer already has this backup
                            var checkUrl = $"http://{v.IPAddress}:{Globals.FrostValidatorPort}/frost/backup/check";
                            var checkPayload = JsonConvert.SerializeObject(new
                            {
                                OwnerAddress = validatorAddress,
                                SmartContractUID = smartContractUID,
                                PlaintextHash = plaintextHash
                            });
                            var checkContent = new StringContent(checkPayload, Encoding.UTF8, "application/json");
                            var checkResponse = await _backupHttpClient.PostAsync(checkUrl, checkContent);

                            if (checkResponse.IsSuccessStatusCode)
                            {
                                var checkBody = await checkResponse.Content.ReadAsStringAsync();
                                var checkResult = JsonConvert.DeserializeAnonymousType(checkBody,
                                    new { HasBackup = false, HashMatch = false });

                                if (checkResult?.HasBackup == true && checkResult.HashMatch)
                                {
                                    Interlocked.Increment(ref skipCount);
                                    return; // Peer already has matching backup
                                }
                            }

                            // Step 2: Peer doesn't have it or check failed — send full backup
                            var storeUrl = $"http://{v.IPAddress}:{Globals.FrostValidatorPort}/frost/backup/store";
                            var storePayload = JsonConvert.SerializeObject(new
                            {
                                OwnerAddress = validatorAddress,
                                SmartContractUID = smartContractUID,
                                EncryptedBlob = encryptedBlob,
                                PlaintextHash = plaintextHash,
                                Version = BACKUP_VERSION,
                                Timestamp = timestamp,
                                Signature = signature
                            });
                            var storeContent = new StringContent(storePayload, Encoding.UTF8, "application/json");
                            var storeResponse = await _backupHttpClient.PostAsync(storeUrl, storeContent);

                            if (storeResponse.IsSuccessStatusCode)
                            {
                                var storeBody = await storeResponse.Content.ReadAsStringAsync();
                                var storeResult = JsonConvert.DeserializeAnonymousType(storeBody,
                                    new { Success = false, StoredHash = "" });

                                if (storeResult?.Success == true && storeResult.StoredHash == expectedStoredHash)
                                    Interlocked.Increment(ref successCount);
                                else
                                    Interlocked.Increment(ref failCount);
                            }
                            else
                            {
                                Interlocked.Increment(ref failCount);
                            }
                        }
                        catch
                        {
                            Interlocked.Increment(ref failCount);
                        }
                    }));
                }

                await Task.WhenAll(tasks);
                return (successCount, skipCount, failCount);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Smart broadcast failed: {ex.Message}",
                    "FrostKeyBackupService.SmartBroadcastBackupToValidators");
                return (0, 0, validators.Count);
            }
        }

        #endregion

        #region Recovery

        /// <summary>
        /// Recover FROST key packages from peer validators.
        /// Signs a recovery request, sends it to peers, decrypts returned blobs,
        /// and restores the local FrostValidatorKeyStore records.
        /// </summary>
        public static async Task<int> RecoverKeysFromPeers(
            string validatorAddress, List<VBTCValidator> validators)
        {
            try
            {
                LogUtility.Log($"[FROST Backup] Starting key recovery for {validatorAddress} from {validators.Count} peers",
                    "FrostKeyBackupService.RecoverKeysFromPeers");

                // Sign recovery request
                var timestamp = TimeUtil.GetTime();
                var signMessage = $"frost-recovery.{validatorAddress}.{timestamp}";
                var signature = ReserveBlockCore.Services.SignatureService.AddressSignature(validatorAddress, signMessage);
                if (signature == "ERROR")
                {
                    ErrorLogUtility.LogError("Failed to sign recovery request",
                        "FrostKeyBackupService.RecoverKeysFromPeers");
                    return 0;
                }

                var requestPayload = JsonConvert.SerializeObject(new
                {
                    RequesterAddress = validatorAddress,
                    Timestamp = timestamp,
                    Signature = signature
                });

                // Try peers until we get a successful response
                List<BackupRecoveryItem>? recoveredBackups = null;

                foreach (var validator in validators)
                {
                    if (validator.ValidatorAddress == validatorAddress) continue;
                    if (string.IsNullOrEmpty(validator.IPAddress)) continue;

                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/backup/recover";
                        var content = new StringContent(requestPayload, Encoding.UTF8, "application/json");
                        var response = await _backupHttpClient.PostAsync(url, content);

                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var responseObj = JsonConvert.DeserializeObject<BackupRecoveryResponse>(responseBody);

                            if (responseObj?.Success == true && responseObj.Backups != null && responseObj.Backups.Count > 0)
                            {
                                recoveredBackups = responseObj.Backups;
                                LogUtility.Log($"[FROST Backup] Received {recoveredBackups.Count} backup(s) from peer {validator.ValidatorAddress}",
                                    "FrostKeyBackupService.RecoverKeysFromPeers");
                                break; // Got backups from first successful peer
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST Backup] Recovery request to {validator.ValidatorAddress} failed: {ex.Message}",
                            "FrostKeyBackupService.RecoverKeysFromPeers");
                    }
                }

                if (recoveredBackups == null || recoveredBackups.Count == 0)
                {
                    LogUtility.Log("[FROST Backup] No backups recovered from any peer",
                        "FrostKeyBackupService.RecoverKeysFromPeers");
                    return 0;
                }

                // Decrypt each backup and restore to local key store
                var restoredCount = 0;
                foreach (var backup in recoveredBackups)
                {
                    try
                    {
                        var plaintext = DecryptKeyPackage(validatorAddress, backup.SmartContractUID, backup.EncryptedBlob);
                        if (plaintext == null)
                        {
                            LogUtility.Log($"[FROST Backup] Failed to decrypt backup for contract {backup.SmartContractUID} — skipping (may be tampered)",
                                "FrostKeyBackupService.RecoverKeysFromPeers");
                            continue;
                        }

                        // Verify plaintext hash if provided
                        if (!string.IsNullOrEmpty(backup.PlaintextHash))
                        {
                            var plaintextJson = JsonConvert.SerializeObject(new
                            {
                                plaintext.KeyPackage,
                                plaintext.PubkeyPackage,
                                plaintext.GroupPublicKey,
                                plaintext.ParticipantOrderJson
                            });
                            var computedHash = Convert.ToHexString(
                                SHA256.HashData(Encoding.UTF8.GetBytes(plaintextJson))).ToLower();

                            if (computedHash != backup.PlaintextHash)
                            {
                                LogUtility.Log($"[FROST Backup] Plaintext hash mismatch for contract {backup.SmartContractUID} — possible data corruption",
                                    "FrostKeyBackupService.RecoverKeysFromPeers");
                                // Continue anyway — GCM already authenticated the data
                            }
                        }

                        // Save to local FrostValidatorKeyStore
                        var keyStoreRecord = new FrostValidatorKeyStore
                        {
                            SmartContractUID = backup.SmartContractUID,
                            ValidatorAddress = validatorAddress,
                            KeyPackage = plaintext.KeyPackage,
                            PubkeyPackage = plaintext.PubkeyPackage,
                            GroupPublicKey = plaintext.GroupPublicKey,
                            ParticipantOrderJson = plaintext.ParticipantOrderJson,
                            CreatedTimestamp = TimeUtil.GetTime()
                        };

                        FrostValidatorKeyStore.SaveKeyPackage(keyStoreRecord);
                        restoredCount++;

                        LogUtility.Log($"[FROST Backup] Restored key package for contract {backup.SmartContractUID}, GroupPubKey={plaintext.GroupPublicKey?.Substring(0, Math.Min(16, plaintext.GroupPublicKey?.Length ?? 0))}...",
                            "FrostKeyBackupService.RecoverKeysFromPeers");
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Failed to restore backup for contract {backup.SmartContractUID}: {ex.Message}",
                            "FrostKeyBackupService.RecoverKeysFromPeers");
                    }
                }

                LogUtility.Log($"[FROST Backup] Recovery complete: {restoredCount}/{recoveredBackups.Count} key packages restored",
                    "FrostKeyBackupService.RecoverKeysFromPeers");

                return restoredCount;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Recovery failed: {ex.Message}",
                    "FrostKeyBackupService.RecoverKeysFromPeers");
                return 0;
            }
        }

        #endregion

        #region Background Services

        /// <summary>
        /// Periodic re-broadcast loop. Runs every 6 hours to ensure new validators
        /// that joined after original DKG also hold copies of peer backups.
        /// Uses smart check-before-send to minimize network traffic.
        /// </summary>
        public static async Task PeriodicRebroadcastLoop()
        {
            // Initial delay to let the validator fully start up
            await Task.Delay(TimeSpan.FromMinutes(10));

            while (true)
            {
                try
                {
                    if (Globals.IsFrostValidator && !string.IsNullOrEmpty(Globals.ValidatorAddress))
                    {
                        var allKeys = FrostValidatorKeyStore.GetAllKeyPackages();
                        if (allKeys.Count > 0)
                        {
                            var validators = VBTCValidatorRegistry.GetActiveValidators();
                            if (validators != null && validators.Count > 1)
                            {
                                LogUtility.Log($"[FROST Backup] Periodic re-broadcast: {allKeys.Count} key packages to {validators.Count} peers",
                                    "FrostKeyBackupService.PeriodicRebroadcastLoop");

                                foreach (var keyStore in allKeys)
                                {
                                    var (success, skip, fail) = await SmartBroadcastBackupToValidators(
                                        Globals.ValidatorAddress, keyStore.SmartContractUID, keyStore, validators);

                                    if (success > 0 || fail > 0)
                                    {
                                        LogUtility.Log($"[FROST Backup] Re-broadcast for {keyStore.SmartContractUID}: {success} new, {skip} already had, {fail} failed",
                                            "FrostKeyBackupService.PeriodicRebroadcastLoop");
                                    }

                                    await Task.Delay(1000); // 1s between contracts to avoid flooding
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Periodic re-broadcast error: {ex.Message}",
                        "FrostKeyBackupService.PeriodicRebroadcastLoop");
                }

                await Task.Delay(TimeSpan.FromHours(6));
            }
        }

        /// <summary>
        /// Periodic cleanup loop. Runs every 24 hours to remove backups for validators
        /// no longer in the active set, preventing unbounded storage growth.
        /// </summary>
        public static async Task StaleBackupCleanupLoop()
        {
            // Initial delay
            await Task.Delay(TimeSpan.FromMinutes(30));

            while (true)
            {
                try
                {
                    if (Globals.IsFrostValidator)
                    {
                        var validators = VBTCValidatorRegistry.GetActiveValidators();
                        if (validators != null && validators.Count > 0)
                        {
                            var activeAddresses = validators.Select(v => v.ValidatorAddress).ToList();
                            var removedCount = FrostPeerKeyBackup.DeleteStaleBackups(activeAddresses);
                            if (removedCount > 0)
                            {
                                LogUtility.Log($"[FROST Backup] Stale cleanup removed {removedCount} backups for inactive validators",
                                    "FrostKeyBackupService.StaleBackupCleanupLoop");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Stale backup cleanup error: {ex.Message}",
                        "FrostKeyBackupService.StaleBackupCleanupLoop");
                }

                await Task.Delay(TimeSpan.FromHours(24));
            }
        }

        #endregion

        #region DTOs

        /// <summary>
        /// Deserialized plaintext from a decrypted backup blob
        /// </summary>
        public class FrostKeyBackupPlaintext
        {
            public string KeyPackage { get; set; } = string.Empty;
            public string PubkeyPackage { get; set; } = string.Empty;
            public string GroupPublicKey { get; set; } = string.Empty;
            public string ParticipantOrderJson { get; set; } = string.Empty;
        }

        /// <summary>
        /// Individual backup item in recovery response
        /// </summary>
        public class BackupRecoveryItem
        {
            public string SmartContractUID { get; set; } = string.Empty;
            public string EncryptedBlob { get; set; } = string.Empty;
            public string PlaintextHash { get; set; } = string.Empty;
            public int Version { get; set; } = 1;
        }

        /// <summary>
        /// Response from /frost/backup/recover endpoint
        /// </summary>
        public class BackupRecoveryResponse
        {
            public bool Success { get; set; }
            public List<BackupRecoveryItem> Backups { get; set; } = new List<BackupRecoveryItem>();
        }

        #endregion
    }
}
