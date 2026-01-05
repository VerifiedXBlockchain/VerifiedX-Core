using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReserveBlockCore.Bitcoin.Models
{
    public class VBTCValidator
    {
        #region Variables
        public long Id { get; set; }
        public string ValidatorAddress { get; set; }      // VFX address
        public string IPAddress { get; set; }
        public string BTCPublicKeyShare { get; set; }     // BTC public key for MPC
        public long RegistrationBlockHeight { get; set; }
        public long LastHeartbeatBlock { get; set; }
        public bool IsActive { get; set; }
        public string MPCSignature { get; set; }          // Proof of address ownership
        #endregion

        #region Database Methods
        public static ILiteCollection<VBTCValidator> GetDb()
        {
            var db = DbContext.DB_Assets.GetCollection<VBTCValidator>(DbContext.RSRV_VBTC_V2_VALIDATORS);
            return db;
        }

        public static VBTCValidator? GetValidator(string validatorAddress)
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validator = validators.FindOne(x => x.ValidatorAddress == validatorAddress);
                if (validator != null)
                {
                    return validator;
                }
            }

            return null;
        }

        public static List<VBTCValidator>? GetAllValidators()
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validatorList = validators.FindAll().ToList();
                if (validatorList.Any())
                {
                    return validatorList;
                }
            }

            return null;
        }

        public static List<VBTCValidator>? GetActiveValidators()
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validatorList = validators.Find(x => x.IsActive).ToList();
                if (validatorList.Any())
                {
                    return validatorList;
                }
            }

            return null;
        }

        public static List<VBTCValidator>? GetActiveValidatorsSinceBlock(long blockHeight)
        {
            var validators = GetDb();
            if (validators != null)
            {
                var validatorList = validators.Find(x => x.IsActive && x.LastHeartbeatBlock >= blockHeight).ToList();
                if (validatorList.Any())
                {
                    return validatorList;
                }
            }

            return null;
        }

        public static void SaveValidator(VBTCValidator validator)
        {
            try
            {
                var validators = GetDb();
                var existing = validators.FindOne(x => x.ValidatorAddress == validator.ValidatorAddress);

                if (existing == null)
                {
                    validators.InsertSafe(validator);
                }
                else
                {
                    validators.UpdateSafe(validator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.SaveValidator()");
            }
        }

        public static void UpdateHeartbeat(string validatorAddress, long blockHeight)
        {
            try
            {
                var validators = GetDb();
                var validator = validators.FindOne(x => x.ValidatorAddress == validatorAddress);

                if (validator != null)
                {
                    validator.LastHeartbeatBlock = blockHeight;
                    validator.IsActive = true;
                    validators.UpdateSafe(validator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.UpdateHeartbeat()");
            }
        }

        public static void MarkInactive(string validatorAddress)
        {
            try
            {
                var validators = GetDb();
                var validator = validators.FindOne(x => x.ValidatorAddress == validatorAddress);

                if (validator != null)
                {
                    validator.IsActive = false;
                    validators.UpdateSafe(validator);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.MarkInactive()");
            }
        }

        public static int GetActiveValidatorCount()
        {
            var validators = GetDb();
            if (validators != null)
            {
                return validators.Count(x => x.IsActive);
            }

            return 0;
        }

        public static void DeleteValidator(string validatorAddress)
        {
            try
            {
                var validators = GetDb();
                validators.DeleteManySafe(x => x.ValidatorAddress == validatorAddress);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCValidator.DeleteValidator()");
            }
        }
        #endregion
    }
}
