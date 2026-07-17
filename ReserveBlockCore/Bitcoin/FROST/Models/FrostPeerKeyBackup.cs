using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.FROST.Models
{
    /// <summary>
    /// Stores encrypted FROST key backups from peer validators.
    /// Each validator stores encrypted blobs for its peers — the ciphertext is AES-256-GCM
    /// encrypted with a key derived from the owning validator's VFX private key, so peers
    /// hold data they cannot decrypt. On recovery, a wiped validator retrieves its blobs
    /// from any surviving peer and decrypts locally.
    /// </summary>
    public class FrostPeerKeyBackup
    {
        public long Id { get; set; }

        /// <summary>VFX address of the validator who owns this backup</summary>
        public string OwnerAddress { get; set; } = string.Empty;

        /// <summary>Smart contract UID this backup is for</summary>
        public string SmartContractUID { get; set; } = string.Empty;

        /// <summary>AES-256-GCM encrypted blob (base64). Contains the KeyPackage + public components.</summary>
        public string EncryptedBlob { get; set; } = string.Empty;

        /// <summary>SHA-256 hash of the plaintext for integrity verification after decryption</summary>
        public string PlaintextHash { get; set; } = string.Empty;

        /// <summary>Backup format version (default 1). Allows future schema changes.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Timestamp when this backup was received</summary>
        public long StoredTimestamp { get; set; }

        #region Database Methods

        private static ILiteCollection<FrostPeerKeyBackup> GetDb()
        {
            var db = DbContext.DB_vBTC.GetCollection<FrostPeerKeyBackup>("rsrv_frost_peer_backups");
            return db;
        }

        /// <summary>
        /// Save or update a peer's encrypted backup (upsert by OwnerAddress + SmartContractUID)
        /// </summary>
        public static void SaveBackup(FrostPeerKeyBackup backup)
        {
            try
            {
                var db = GetDb();
                var existing = db.FindOne(x =>
                    x.OwnerAddress == backup.OwnerAddress &&
                    x.SmartContractUID == backup.SmartContractUID);

                if (existing == null)
                {
                    db.InsertSafe(backup);
                }
                else
                {
                    backup.Id = existing.Id;
                    db.UpdateSafe(backup);
                }

                LogUtility.Log($"[FROST Backup] Stored backup for owner {backup.OwnerAddress}, contract {backup.SmartContractUID}",
                    "FrostPeerKeyBackup.SaveBackup");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to save FROST peer backup: {ex.Message}", "FrostPeerKeyBackup.SaveBackup");
            }
        }

        /// <summary>
        /// Retrieve all backups stored for a specific owner address
        /// </summary>
        public static List<FrostPeerKeyBackup> GetBackupsForOwner(string ownerAddress)
        {
            try
            {
                var db = GetDb();
                return db.Find(x => x.OwnerAddress == ownerAddress).ToList();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to get FROST peer backups for owner {ownerAddress}: {ex.Message}",
                    "FrostPeerKeyBackup.GetBackupsForOwner");
                return new List<FrostPeerKeyBackup>();
            }
        }

        /// <summary>
        /// Get a specific backup by owner address and smart contract UID
        /// </summary>
        public static FrostPeerKeyBackup? GetBackup(string ownerAddress, string smartContractUID)
        {
            try
            {
                var db = GetDb();
                return db.FindOne(x =>
                    x.OwnerAddress == ownerAddress &&
                    x.SmartContractUID == smartContractUID);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to get FROST peer backup: {ex.Message}",
                    "FrostPeerKeyBackup.GetBackup");
                return null;
            }
        }

        /// <summary>
        /// Delete all backups for a specific owner
        /// </summary>
        public static void DeleteBackupsForOwner(string ownerAddress)
        {
            try
            {
                var db = GetDb();
                db.DeleteManySafe(x => x.OwnerAddress == ownerAddress);
                LogUtility.Log($"[FROST Backup] Deleted all backups for owner {ownerAddress}",
                    "FrostPeerKeyBackup.DeleteBackupsForOwner");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to delete FROST peer backups: {ex.Message}",
                    "FrostPeerKeyBackup.DeleteBackupsForOwner");
            }
        }

        /// <summary>
        /// Remove backups for validators no longer in the active set.
        /// Called periodically to prevent unbounded storage growth.
        /// </summary>
        public static int DeleteStaleBackups(List<string> activeValidatorAddresses)
        {
            try
            {
                var db = GetDb();
                var activeSet = new HashSet<string>(activeValidatorAddresses);
                var allBackups = db.FindAll().ToList();
                var staleCount = 0;

                foreach (var backup in allBackups)
                {
                    if (!activeSet.Contains(backup.OwnerAddress))
                    {
                        db.DeleteSafe(backup.Id);
                        staleCount++;
                    }
                }

                if (staleCount > 0)
                {
                    LogUtility.Log($"[FROST Backup] Cleaned up {staleCount} stale peer backups",
                        "FrostPeerKeyBackup.DeleteStaleBackups");
                }

                return staleCount;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to delete stale FROST peer backups: {ex.Message}",
                    "FrostPeerKeyBackup.DeleteStaleBackups");
                return 0;
            }
        }

        /// <summary>
        /// Get total count of stored peer backups (for monitoring/logging)
        /// </summary>
        public static int GetBackupCount()
        {
            try
            {
                var db = GetDb();
                return db.Count();
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to count FROST peer backups: {ex.Message}",
                    "FrostPeerKeyBackup.GetBackupCount");
                return 0;
            }
        }

        #endregion
    }
}
