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
        /// Filed under an id that named no contract on chain when it was stored: a ceremony's session id before its vault
        /// exists, or an id nobody's vault has. Such backups count toward <see cref="MaxUnboundBackupsPerOwner"/>.
        /// </summary>
        public bool Unbound { get; set; }

        /// <summary>
        /// Most unbound backups one owner keeps on this node; storing another removes the owner's oldest. Each contract on
        /// chain holds one backup per owner, but ids that name no contract were unlimited: one registered validator could
        /// fill every peer's store with made-up ones (#274, C-162). An owner's ceremonies under way are far fewer.
        /// </summary>
        public const int MaxUnboundBackupsPerOwner = 16;

        /// <summary>
        /// How long an owner may be absent from the active set before its backups are deleted. A backup exists for the
        /// owner whose machine is gone; deleting it after a day's outage lost the share for good.
        /// </summary>
        public const long StaleGraceSeconds = 30L * 24 * 60 * 60;

        /// <summary>An owner's address and a contract or ceremony id are at most as long as a ceremony's ids.</summary>
        public const int MaxIdChars = FrostSessionStorage.MAX_SESSION_ID_LENGTH;
        /// <summary>The encrypted key package, as the owner's node sends it.</summary>
        public const int MaxBlobChars = 16 * 1024;
        /// <summary>The plaintext hash is a SHA-256 in hex.</summary>
        public const int PlaintextHashChars = 64;
        /// <summary>A group public key in hex, compressed or not, as some nodes write it with a 0x in front.</summary>
        public const int MaxGroupKeyChars = 132;

        /// <summary>
        /// Recovery answers this node is sending at once, each counted at <see cref="AnswerBatchBytes"/>, for the network
        /// the request came from and for the node. A request signed once could be sent many times at once, each answer
        /// held while a slow reader took it. One that does not fit is answered 503 before anything is read for it, and
        /// may be asked again; an answer for one network never waits on another's.
        /// </summary>
        internal static readonly BoundedJsonBodies.BodiesInProgress AnswersInProgress = new(perSource: 32L * 1024 * 1024, total: 128L * 1024 * 1024);

        /// <summary>
        /// Rows a recovery answer reads and writes at a time. The answer is written batch by batch, so serving one holds
        /// at most <see cref="AnswerBatchBytes"/> however many backups the owner has.
        /// </summary>
        public const int AnswerBatchRows = 64;

        /// <summary>The most one recovery answer holds at once: a batch of the longest rows, as read and as written.</summary>
        public const long AnswerBatchBytes = 4L * AnswerBatchRows * (MaxBlobChars + 2 * MaxIdChars + PlaintextHashChars + MaxGroupKeyChars + 64);

        /// <summary>The ids of the owner's backups, read without the backups themselves being kept.</summary>
        public static List<long> GetBackupIdsForOwner(string ownerAddress)
        {
            try { return GetDb().Query().Where(x => x.OwnerAddress == ownerAddress).Select(x => x.Id).ToList(); }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to list FROST peer backups for owner {ownerAddress}: {ex.Message}", "FrostPeerKeyBackup.GetBackupIdsForOwner");
                return new List<long>();
            }
        }

        /// <summary>
        /// The backups with these ids. A row kept before the field limits that is longer than any owner's node writes is
        /// deleted here, not returned: it is not a backup.
        /// </summary>
        public static List<FrostPeerKeyBackup> GetBackupsWithinLimits(IEnumerable<long> ids)
        {
            var kept = new List<FrostPeerKeyBackup>();
            var db = GetDb();
            foreach (var id in ids)
            {
                var backup = db.FindById(id);
                if (backup == null) continue;
                if (!ExceedsLengths(backup)) { kept.Add(backup); continue; }
                try { db.Delete(backup.Id); }
                catch (Exception ex) { ErrorLogUtility.LogError($"Deleting an over-long FROST peer backup failed: {ex.Message}", "FrostPeerKeyBackup.GetBackupsWithinLimits"); }
            }
            return kept;
        }

        /// <summary>
        /// Writes the owner's recovery answer, <c>{"Success":true,"Backups":[...]}</c>, to <paramref name="body"/>, reading
        /// and writing <see cref="AnswerBatchRows"/> backups at a time. Returns how many were sent.
        /// </summary>
        public static async Task<int> WriteRecoveryAnswerAsync(Stream body, string ownerAddress)
        {
            var ids = GetBackupIdsForOwner(ownerAddress);
            var sent = 0;
            await using var text = new StreamWriter(body, new System.Text.UTF8Encoding(false), 16 * 1024, leaveOpen: true);
            using var json = new Newtonsoft.Json.JsonTextWriter(text) { CloseOutput = false };
            await json.WriteStartObjectAsync();
            await json.WritePropertyNameAsync("Success");
            await json.WriteValueAsync(true);
            await json.WritePropertyNameAsync("Backups");
            await json.WriteStartArrayAsync();
            foreach (var batch in ids.Chunk(AnswerBatchRows))
            {
                foreach (var b in GetBackupsWithinLimits(batch))
                {
                    await json.WriteStartObjectAsync();
                    await json.WritePropertyNameAsync("SmartContractUID"); await json.WriteValueAsync(b.SmartContractUID);
                    await json.WritePropertyNameAsync("EncryptedBlob"); await json.WriteValueAsync(b.EncryptedBlob);
                    await json.WritePropertyNameAsync("PlaintextHash"); await json.WriteValueAsync(b.PlaintextHash);
                    await json.WritePropertyNameAsync("Version"); await json.WriteValueAsync(b.Version);
                    await json.WriteEndObjectAsync();
                    sent++;
                }
                await json.FlushAsync();
            }
            await json.WriteEndArrayAsync();
            await json.WriteEndObjectAsync();
            await json.FlushAsync();
            return sent;
        }

        /// <summary>
        /// Why a backup with these fields is not kept, or null. Each field is held to what the owner's node writes: a row
        /// is then at most about 16 KB, whoever sent it.
        /// </summary>
        public static string? FieldRefusal(string? ownerAddress, string? smartContractUID, string? encryptedBlob, string? plaintextHash, string? groupPublicKey)
        {
            if ((ownerAddress?.Length ?? 0) > MaxIdChars) return $"The owner address is longer than {MaxIdChars} characters.";
            if ((smartContractUID?.Length ?? 0) > MaxIdChars) return $"The contract id is longer than {MaxIdChars} characters.";
            if ((encryptedBlob?.Length ?? 0) > MaxBlobChars) return "EncryptedBlob exceeds 16 KB limit";
            if (!string.IsNullOrEmpty(plaintextHash) && (plaintextHash.Length != PlaintextHashChars || !IsHex(plaintextHash)))
                return "PlaintextHash is not a SHA-256 in hex.";
            if (!string.IsNullOrEmpty(groupPublicKey) && (groupPublicKey.Length > MaxGroupKeyChars
                    || !IsHex(groupPublicKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? groupPublicKey.Substring(2) : groupPublicKey)))
                return "GroupPublicKey is not a public key in hex.";
            return null;
        }

        private static bool IsHex(string text)
        {
            foreach (var c in text)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        /// <summary>Whether a stored row has a field longer than any owner's node writes. Its form is not judged here.</summary>
        public static bool ExceedsLengths(FrostPeerKeyBackup backup) =>
            backup.OwnerAddress.Length > MaxIdChars || backup.SmartContractUID.Length > MaxIdChars || backup.EncryptedBlob.Length > MaxBlobChars
            || backup.PlaintextHash.Length > PlaintextHashChars || backup.GroupPublicKey.Length > MaxGroupKeyChars;

        /// <summary>
        /// The owner's backups. A row kept before the field limits that is longer than any owner's node writes is deleted
        /// here: it is not a backup. A row of honest lengths is kept and sent whatever the form of its fields, so no real
        /// backup is lost to these limits.
        /// </summary>
        public static List<FrostPeerKeyBackup> GetBackupsForOwnerWithinLimits(string ownerAddress) =>
            GetBackupsWithinLimits(GetBackupIdsForOwner(ownerAddress));

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
        /// After an unbound backup is stored: the owner's unbound backups beyond <see cref="MaxUnboundBackupsPerOwner"/>
        /// are removed, oldest first, except one whose id has since come to name a contract (<paramref name="namesAContract"/>).
        /// Returns how many were removed.
        /// </summary>
        public static int TrimUnbound(string ownerAddress, Func<string, bool> namesAContract)
        {
            try
            {
                var db = GetDb();
                var unbound = db.Find(x => x.OwnerAddress == ownerAddress && x.Unbound)
                    .OrderByDescending(x => x.StoredTimestamp).ThenByDescending(x => x.Id).ToList();
                var removed = 0;
                foreach (var old in unbound.Skip(MaxUnboundBackupsPerOwner))
                {
                    if (namesAContract(old.SmartContractUID))
                    {
                        old.Unbound = false;
                        db.UpdateSafe(old);
                        continue;
                    }
                    if (db.Delete(old.Id)) removed++;
                }
                if (removed > 0)
                    LogUtility.Log($"[FROST Backup] Removed {removed} oldest backup(s) of {ownerAddress} filed under ids that name no contract (cap {MaxUnboundBackupsPerOwner}).", "FrostPeerKeyBackup.TrimUnbound");
                return removed;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Trimming unbound FROST peer backups of {ownerAddress} failed: {ex.Message}", "FrostPeerKeyBackup.TrimUnbound");
                return 0;
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
