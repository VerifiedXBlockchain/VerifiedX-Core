using LiteDB;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST.Models
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

        /// <summary>Owner's FROST group public key for this contract (public). Guards against a
        /// backup for a DIFFERENT group key silently replacing the real vault key's backup.</summary>
        public string GroupPublicKey { get; set; } = string.Empty;

        /// <summary>Timestamp when this backup was received</summary>
        public long StoredTimestamp { get; set; }

        /// <summary>When the owner was first found missing from the active validators (unix seconds); 0 while it is there.</summary>
        public long MissingSince { get; set; }

        /// <summary>
        /// How long an owner may be absent from the active set before its backups are deleted. A backup exists for the
        /// owner whose machine is gone; deleting it after a day's outage lost the share for good.
        /// </summary>
        public const long StaleGraceSeconds = 30L * 24 * 60 * 60;

        #region Database Methods

        private static ILiteCollection<FrostPeerKeyBackup> GetDb()
        {
            var db = DbContext.DB_vBTC.GetCollection<FrostPeerKeyBackup>("rsrv_frost_peer_backups");
            return db;
        }

        /// <summary>
        /// Save or update a peer's encrypted backup (upsert by OwnerAddress + SmartContractUID)
        /// </summary>
        public static bool SaveBackup(FrostPeerKeyBackup backup)
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
                    if (!CanReplace(existing, backup, out var refuseReason))
                    {
                        ErrorLogUtility.LogError($"[FROST Backup] REFUSED to overwrite backup for owner {backup.OwnerAddress}, contract {backup.SmartContractUID}: {refuseReason}",
                            "FrostPeerKeyBackup.SaveBackup");
                        return false;
                    }
                    backup.Id = existing.Id;
                    db.UpdateSafe(backup);
                }

                LogUtility.Log($"[FROST Backup] Stored backup for owner {backup.OwnerAddress}, contract {backup.SmartContractUID}",
                    "FrostPeerKeyBackup.SaveBackup");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to save FROST peer backup: {ex.Message}", "FrostPeerKeyBackup.SaveBackup");
                return false;
            }
        }

        /// <summary>
        /// Pure replacement rule: a stored backup with a known group public key may only be replaced
        /// by a backup for the SAME group public key.
        /// </summary>
        public static bool CanReplace(FrostPeerKeyBackup existing, FrostPeerKeyBackup incoming, out string reason)
        {
            reason = "";
            if (existing == null) return true;
            var existingGroup = existing.GroupPublicKey ?? "";
            var incomingGroup = incoming?.GroupPublicKey ?? "";
            if (!string.IsNullOrEmpty(existingGroup)
                && !string.Equals(existingGroup, incomingGroup, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"existing group key {existingGroup} != incoming {incomingGroup}";
                return false;
            }
            return true;
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
        /// Remove backups whose owner has been absent from the active set for <see cref="StaleGraceSeconds"/>. The first
        /// sweep that misses an owner stamps <see cref="MissingSince"/> on its backups, a sweep that finds it again clears
        /// the stamp, and only a stamp older than the grace deletes. Called periodically to bound storage.
        /// </summary>
        public static int DeleteStaleBackups(List<string> activeValidatorAddresses, long? nowUnix = null)
        {
            try
            {
                var now = nowUnix ?? TimeUtil.GetTime();
                var db = GetDb();
                var activeSet = new HashSet<string>(activeValidatorAddresses);
                var allBackups = db.FindAll().ToList();
                var staleCount = 0;

                foreach (var backup in allBackups)
                {
                    if (activeSet.Contains(backup.OwnerAddress))
                    {
                        if (backup.MissingSince != 0)
                        {
                            backup.MissingSince = 0;
                            db.UpdateSafe(backup);
                        }
                    }
                    else if (backup.MissingSince == 0)
                    {
                        backup.MissingSince = now;
                        db.UpdateSafe(backup);
                    }
                    else if (now - backup.MissingSince > StaleGraceSeconds)
                    {
                        db.DeleteSafe(backup.Id);
                        staleCount++;
                    }
                }

                if (staleCount > 0)
                {
                    LogUtility.Log($"[FROST Backup] Cleaned up {staleCount} peer backups whose owner has been inactive for over {StaleGraceSeconds / 86400} days",
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
