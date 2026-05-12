using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Cold snapshot: checkpoint LiteDB files while the main node has not opened them, then copy into
    /// <c>{databasePath}/snapshots/{timestamp}/</c>.
    /// </summary>
    /// <remarks>
    /// Excluded on purpose (keys, signing secrets, or shielded wallet material — do not add without a security review):
    /// <list type="bullet">
    /// <item><description><c>rsrvwaldata.db</c> / <c>rsrvhdwaldata.db</c> — <see cref="Models.Account"/>, <see cref="Models.HDWallet"/>, <see cref="Models.AccountKeystore"/>, <see cref="Models.ReserveAccount"/>.</description></item>
    /// <item><description><c>rsrvkeystore.db</c> — <see cref="Models.Keystore"/> (<c>PrivateKey</c>).</description></item>
    /// <item><description><c>rsrvbitcoin.db</c> — <see cref="Bitcoin.Models.BitcoinAccount"/> (<c>PrivateKey</c>, <c>WifKey</c>).</description></item>
    /// <item><description><c>DB_Privacy.db</c> — shielded pool / z-balance state (<see cref="Privacy.ShieldedWalletService"/>, etc.).</description></item>
    /// <item><description><c>rsrvvbtc.db</c> — includes <see cref="Bitcoin.FROST.Models.FrostValidatorKeyStore"/> (<c>KeyPackage</c> signing material).</description></item>
    /// <item><description><c>rsrvshares.db</c> — <see cref="Arbiter.Shares"/> (<c>Share</c> secret shares).</description></item>
    /// </list>
    /// </remarks>
    internal static class LocalDbSnapshotService
    {
        /// <summary>Non-sensitive chain / network files to checkpoint and copy (subset of <see cref="DbContext"/> files).</summary>
        private static readonly string[] ChainDatabaseFiles =
        {
            DbContext.RSRV_DB_NAME,
            DbContext.RSRV_DB_BLOCKCHAIN,
            DbContext.RSRV_DB_WSTATE_TREI,
            DbContext.RSRV_DB_ASTATE_TREI,
            DbContext.RSRV_DB_SCSTATE_TREI,
            DbContext.RSRV_DB_DECSHOPSTATE_TREI,
            DbContext.RSRV_DB_DST,
            DbContext.RSRV_DB_DNR,
            DbContext.RSRV_DB_TOPIC_TREI,
            DbContext.RSRV_DB_VOTE,
            DbContext.RSRV_DB_RESERVE,
            DbContext.RSRV_DB_TOKENIZED_WITHDRAWALS,
            DbContext.RSRV_DB_VBTC_WITHDRAWAL_REQUESTS,
        };

        internal static Task CreateChainSnapshotAsync()
        {
            var dbDir = GetPathUtility.GetDatabasePath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var destDir = Path.Combine(dbDir, "snapshots", stamp);
            Directory.CreateDirectory(destDir);

            ConsoleWriterService.Output(
                $"Creating chain DB snapshot (wallet, keystore, Bitcoin, privacy, vBTC signing, and arbiter share DBs excluded) at:{Environment.NewLine}{destDir}");

            foreach (var relativeName in ChainDatabaseFiles)
            {
                var name = relativeName.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var srcMain = Path.Combine(dbDir, name);
                if (!File.Exists(srcMain))
                    continue;

                try
                {
                    using (var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = srcMain,
                        Connection = ConnectionType.Direct,
                        ReadOnly = false
                    }))
                    {
                        db.Checkpoint();
                    }
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"Snapshot checkpoint failed for {name}: {ex.Message}", "LocalDbSnapshotService.CreateChainSnapshotAsync()");
                    ConsoleWriterService.Output($"Warning: could not checkpoint {name} — {ex.Message}. Copying files as-is.");
                }

                CopySnapshotFile(srcMain, Path.Combine(destDir, name));

                var logPath = GetLiteDbLogPath(srcMain);
                if (File.Exists(logPath))
                    CopySnapshotFile(logPath, Path.Combine(destDir, Path.GetFileName(logPath)));
            }

            LogUtility.Log($"Chain DB snapshot completed: {destDir}", "LocalDbSnapshotService.CreateChainSnapshotAsync()");
            ConsoleWriterService.Output("Chain DB snapshot finished.");
            return Task.CompletedTask;
        }

        private static string GetLiteDbLogPath(string mainDbPath)
        {
            var dir = Path.GetDirectoryName(mainDbPath) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(mainDbPath);
            return Path.Combine(dir, baseName + "-log.db");
        }

        private static void CopySnapshotFile(string source, string destination)
        {
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(source, destination, overwrite: true);
        }
    }
}
