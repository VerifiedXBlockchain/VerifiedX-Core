using LiteDB;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Data
{
    /// <summary>
    /// NEW-01 (found during the Sep 2026 audit remediation, not in the audit): LiteDB persists every public
    /// readable property, including the computed accessors Account.GetKey / GetPrivKey and ReserveAccount
    /// .GetKey / GetPrivKey. GetKey DECRYPTS when the wallet password (or a reserve unlock) is in memory, so
    /// any account saved while unlocked — balance updates happen constantly on a running node — wrote the
    /// PLAINTEXT private key into the wallet database next to its encrypted copy. Wallet encryption at rest
    /// was therefore not protecting keys.
    ///
    /// The accessors are now [BsonIgnore]. This one-time scrub removes the fields already on disk and
    /// rebuilds the wallet database so the old pages holding them are not left in the file.
    /// </summary>
    public static class KeyPersistenceScrub
    {
        private static readonly string[] AccessorFields = { "GetKey", "GetPrivKey" };

        /// <summary>Removes persisted accessor fields from one collection. Returns the number of documents fixed.</summary>
        public static int ScrubCollection(ILiteDatabase db, string collectionName)
        {
            var col = db.GetCollection(collectionName);
            int fixedCount = 0;
            foreach (var doc in col.FindAll().ToList())
            {
                var changed = false;
                foreach (var f in AccessorFields)
                    changed |= doc.Remove(f);
                if (changed)
                {
                    col.Update(doc);
                    fixedCount++;
                }
            }
            return fixedCount;
        }

        /// <summary>
        /// Scrubs the account and reserve-account collections of the wallet database; if anything was removed,
        /// rebuilds the file (LiteDB rewrites live data only) and deletes the rebuild's backup copy, which would
        /// otherwise still contain the removed values. Safe to run on every start: a clean database is untouched.
        /// </summary>
        public static int Run(ILiteDatabase walletDb, string walletDbPath)
        {
            int total = 0;
            try
            {
                total += ScrubCollection(walletDb, DbContext.RSRV_ACCOUNTS);
                total += ScrubCollection(walletDb, DbContext.RSRV_RESERVE_ACCOUNTS);
                if (total == 0)
                    return 0;

                walletDb.Checkpoint();
                walletDb.Rebuild();

                // LiteDB keeps "<name>-backup<ext>" from the rebuild: it is the pre-scrub file.
                var backup = Path.Combine(Path.GetDirectoryName(walletDbPath) ?? "",
                    Path.GetFileNameWithoutExtension(walletDbPath) + "-backup" + Path.GetExtension(walletDbPath));
                if (File.Exists(backup))
                    File.Delete(backup);

                LogUtility.Log($"NEW-01: removed persisted private-key accessor fields from {total} wallet record(s) and rebuilt the wallet database.", "KeyPersistenceScrub.Run()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"NEW-01 wallet key scrub failed: {ex.Message}", "KeyPersistenceScrub.Run()");
            }
            return total;
        }
    }
}
