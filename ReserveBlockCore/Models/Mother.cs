﻿using Microsoft.AspNetCore.SignalR;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Models
{
    public class Mother
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Password { get; set; }
        public DateTime StartDate {get; set;}

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
            if(mother == null)
            {
                var motherDb = GetMotherDb();
                mom.Password = mom.Password.ToEncrypt(); //encrypts the password with password as key
                mom.StartDate = DateTime.Now;
                if(motherDb != null)
                {
                    motherDb.InsertSafe(mom);
                    return (true, "Mom saved.");
                }

                return (false, "Mom DB was null.");
            }

            return (false, "Mother was already present.");
        }

        #endregion
    }
}
