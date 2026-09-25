using Microsoft.AspNetCore.SignalR;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using VerifiedXCore.Voting;

namespace VerifiedXCore.Models
{
    public class Mother
    {
        public int Id { get; set; }
        public string Name { get; set; }
        /// <summary>
        /// VX-17: a password VERIFIER, never the password. New records hold KeystoreCrypto "ks1:" (AES-GCM under
        /// PBKDF2-SHA256 600k) sealing a fixed marker, so only the right password opens it. Older records hold the
        /// password encrypted under an MD5 of itself (fast to guess offline); they are upgraded on the first
        /// successful kid login. Never returned by any route (GetMother uses a view without it).
        /// </summary>
        public string Password { get; set; }
        public DateTime StartDate {get; set;}

        private const string PasswordMarker = "VFX_MOTHER_AUTH_V1";

        /// <summary>VX-17: the stored form of a mother password.</summary>
        public static string CreatePasswordVerifier(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("A mother password is required.");
            return Services.KeystoreCrypto.Seal(PasswordMarker, password);
        }

        /// <summary>
        /// VX-17: checks a kid's password against the stored verifier. A legacy (self-encrypted) record that matches is
        /// upgraded to the KDF-based verifier in place.
        /// </summary>
        public static bool VerifyPassword(Mother mother, string? candidate)
        {
            if (mother == null || string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(mother.Password))
                return false;
            if (Services.KeystoreCrypto.IsSealed(mother.Password))
                return Services.KeystoreCrypto.TryOpen(mother.Password, candidate, out var marker) && marker == PasswordMarker;

            string legacy;
            try { legacy = mother.Password.ToDecrypt(candidate); } catch { return false; }
            if (legacy == "Fail" || legacy != candidate)
                return false;
            try
            {
                var db = GetMotherDb();
                var oldValue = mother.Password;
                var newValue = CreatePasswordVerifier(candidate);
                if (db != null && db.UpdateManySafe(x => new Mother { Password = newValue }, x => x.Id == mother.Id && x.Password == oldValue) > 0)
                    mother.Password = newValue;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Upgrading the mother password verifier failed: {ex.Message}", "Mother.VerifyPassword()");
            }
            return true;
        }

        public class Kids
        {
            public DateTime ConnectTime { get; set; }
            public DateTime LastDataSentTime { get; set; }
            public string Address { get; set; }
            public string IPAddress { get; set; }
            public DateTime? LastTaskSent { get; set; }
            public long LastTaskBlockSent { get; set; }
            public int PeerCount { get; set; }
            public decimal Balance { get; set; }
            public long BlockHeight { get; set; }
            public string ValidatorName { get; set; }
            public bool IsValidating { get; set; }
            public bool ActiveWithMother { get { return LastDataSentTime >= DateTime.Now.AddMinutes(-3) ? true : false; } }
            public bool ActiveWithValidating { get { return LastTaskSent == null ? false : LastTaskSent >= DateTime.Now.AddMinutes(-3) ? true : false; } }
        }

        public class DataPayload
        {
            public string Address { get; set; }
            public DateTime? LastTaskSent { get; set; }
            public long LastTaskBlockSent { get; set; }
            public int PeerCount { get; set; }
            public decimal Balance { get; set; }
            public long BlockHeight { get; set; }
            public string ValidatorName { get; set; }
            public bool IsValidating { get; set; }

        }

        public class MotherStartPayload
        { 
            public string Name { get; set; }
            public string Password { get; set; }
        }

        public class MotherJoinPayload
        {
            public string IPAddress { get; set; }
            public string Password { get; set; }
        }



        #region Get Mother DB
        public static LiteDB.ILiteCollection<Mother>? GetMotherDb()
        {
            try
            {
                var mother = DbContext.DB_Settings.GetCollection<Mother>(DbContext.RSRV_MOTHER);
                return mother;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "Mother.GetMotherDb()");
                return null;
            }

        }

        #endregion

        #region Get Mother
        public static Mother? GetMother()
        {
            var motherDb = GetMotherDb();

            if (motherDb != null)
            {
                var motherRec = motherDb.Query().FirstOrDefault();
                if (motherRec == null)
                {
                    return null;
                }

                return motherRec;
            }
            else
            {
                return null;
            }
        }

        #endregion

        #region Save Mother
        public static (bool,string) SaveMother(Mother mom)
        {
            var mother = GetMother();
            var motherDb = GetMotherDb();
            if (mother == null)
            {
                mom.Password = CreatePasswordVerifier(mom.Password); // VX-17: KDF-based verifier, not the password
                mom.StartDate = DateTime.Now;
                if(motherDb != null)
                {
                    motherDb.InsertSafe(mom);
                    return (true, "Mom saved.");
                }
            }
            else
            {
                if(motherDb != null)
                {
                    mother.Password = CreatePasswordVerifier(mom.Password); // VX-17
                    mother.Name = mom.Name;
                    motherDb.UpdateSafe(mother);
                    return (true, "Mom updated.");
                }
            }
            return (false, "Mom DB was null.");
        }

        #endregion

        #region Delete Mother
        public static (bool, string) DeleteMother(Mother mom)
        {
            var mother = GetMother();
            if (mother != null)
            {
                var motherDb = GetMotherDb();
                if (motherDb != null)
                {
                    motherDb.DeleteSafe(mom.Id);
                    return (true, "Mom deleted.");
                }
                else
                {
                    return (false, "Mom DB was null.");
                }
            }
            return (false, "Mother was not present.");

        }

        #endregion
    }
}
