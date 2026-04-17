using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Bitcoin.FROST.Models
{
    /// <summary>
    /// FIND-024 Fix: Persistent storage for FROST validator key packages.
    /// After DKG completes, each validator stores their key package (needed for signing)
    /// and the group pubkey package (needed for signature aggregation).
    /// Stored in the vBTC database so it survives validator restarts.
    /// </summary>
    public class FrostValidatorKeyStore
    {
        public long Id { get; set; }
        
        /// <summary>Smart contract UID this key belongs to</summary>
        public string SmartContractUID { get; set; } = string.Empty;
        
        /// <summary>This validator's VFX address</summary>
        public string ValidatorAddress { get; set; } = string.Empty;
        
        /// <summary>This validator's FROST key package (secret - used for signing)</summary>
        public string KeyPackage { get; set; } = string.Empty;
        
        /// <summary>The group pubkey package (used for signature aggregation/verification)</summary>
        public string PubkeyPackage { get; set; } = string.Empty;
        
        /// <summary>The aggregated group public key (32-byte x-only hex)</summary>
        public string GroupPublicKey { get; set; } = string.Empty;
        
        /// <summary>
        /// The sorted participant addresses used during DKG, serialized as JSON array.
        /// This preserves the exact ordering that produced the FROST Identifiers baked into
        /// the key packages, so signing ceremonies can reconstruct the same mapping.
        /// </summary>
        public string ParticipantOrderJson { get; set; } = string.Empty;
        
        /// <summary>Timestamp when this key was created</summary>
        public long CreatedTimestamp { get; set; }

        #region Database Methods

        private static ILiteCollection<FrostValidatorKeyStore> GetDb()
        {
            var db = DbContext.DB_vBTC.GetCollection<FrostValidatorKeyStore>("rsrv_frost_validator_keys");
            return db;
        }

        /// <summary>
        /// Save or update a validator's key package for a specific contract
        /// </summary>
        public static void SaveKeyPackage(FrostValidatorKeyStore keyStore)
        {
            try
            {
                var db = GetDb();
                var existing = db.FindOne(x => 
                    x.SmartContractUID == keyStore.SmartContractUID && 
                    x.ValidatorAddress == keyStore.ValidatorAddress);

                if (existing == null)
                {
                    db.InsertSafe(keyStore);
                }
                else
                {
                    keyStore.Id = existing.Id;
                    db.UpdateSafe(keyStore);
                }

                LogUtility.Log($"[FROST KeyStore] Saved key package for contract {keyStore.SmartContractUID}, validator {keyStore.ValidatorAddress}", 
                    "FrostValidatorKeyStore.SaveKeyPackage");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to save FROST key package: {ex.Message}", "FrostValidatorKeyStore.SaveKeyPackage");
            }
        }

        /// <summary>
        /// Get a validator's key package for a specific contract
        /// </summary>
        public static FrostValidatorKeyStore? GetKeyPackage(string smartContractUID, string validatorAddress)
        {
            try
            {
                var db = GetDb();
                return db.FindOne(x => 
                    x.SmartContractUID == smartContractUID && 
                    x.ValidatorAddress == validatorAddress);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to get FROST key package: {ex.Message}", "FrostValidatorKeyStore.GetKeyPackage");
                return null;
            }
        }

        /// <summary>
        /// Get the pubkey package for a specific contract (any validator's record will have it)
        /// </summary>
        public static string? GetPubkeyPackage(string smartContractUID)
        {
            try
            {
                var db = GetDb();
                var record = db.FindOne(x => x.SmartContractUID == smartContractUID);
                return record?.PubkeyPackage;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to get FROST pubkey package: {ex.Message}", "FrostValidatorKeyStore.GetPubkeyPackage");
                return null;
            }
        }

        /// <summary>
        /// Delete key packages for a contract (e.g., if DKG needs to be re-run)
        /// </summary>
        public static void DeleteKeyPackages(string smartContractUID)
        {
            try
            {
                var db = GetDb();
                db.DeleteManySafe(x => x.SmartContractUID == smartContractUID);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to delete FROST key packages: {ex.Message}", "FrostValidatorKeyStore.DeleteKeyPackages");
            }
        }

        #endregion
    }
}
