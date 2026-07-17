using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ReserveBlockCore.Bitcoin.FROST.Models;

namespace ReserveBlockCore.Bitcoin.FROST
{
    /// <summary>
    /// FROST Validator Server - Decentralized MPC coordination for vBTC V2
    /// Each vBTC validator runs this server to participate in DKG and signing ceremonies
    /// </summary>
    public class FrostServer
    {
        public static async Task Start()
        {
            try
            {
                if (Globals.IsFrostValidator)
                {
                    var builder = Host.CreateDefaultBuilder()
                    .ConfigureWebHostDefaults(webBuilder =>
                    {
                        webBuilder.UseKestrel(options =>
                        {
                            options.ListenAnyIP(Globals.FrostValidatorPort + 1, listenOption => { listenOption.UseHttps(GetSelfSignedCertificate()); });
                            options.ListenAnyIP(Globals.FrostValidatorPort);
                        })
                        .UseStartup<FrostStartup>()
                        .ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
                    });

                    _ = builder.RunConsoleAsync();
                    
                    // FIND-0013 Fix: Start periodic session cleanup background task
                    _ = Task.Run(async () => await SessionCleanupLoop());

                    // Start FROST key backup background services
                    _ = Task.Run(async () => await ReserveBlockCore.Bitcoin.Services.FrostKeyBackupService.PeriodicRebroadcastLoop());
                    _ = Task.Run(async () => await ReserveBlockCore.Bitcoin.Services.FrostKeyBackupService.StaleBackupCleanupLoop());

                    // Auto-recovery: if this validator has no local FROST keys but is registered active,
                    // attempt to recover from peers. Delayed to allow validator registry sync to complete.
                    _ = Task.Run(async () => await AutoRecoverFrostKeysIfNeeded());
                    
                    Console.WriteLine($"FROST Validator Server started on port {Globals.FrostValidatorPort}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FROST Server Error: {ex}");
            }
        }

        /// <summary>
        /// Automatic FROST key recovery: if this validator has no local key packages but
        /// is registered as an active vBTC validator, attempt recovery from peers.
        /// Delayed 2 minutes to ensure DB_vBTC is initialized, the FROST HTTP server is running,
        /// and the validator registry has had time to sync from blockchain data.
        /// </summary>
        private static async Task AutoRecoverFrostKeysIfNeeded()
        {
            try
            {
                // Wait for chain sync and validator registry to populate
                await Task.Delay(TimeSpan.FromMinutes(2));

                if (!Globals.IsFrostValidator || string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return;

                var localKeys = FrostValidatorKeyStore.GetAllKeyPackages();
                if (localKeys.Count > 0)
                {
                    // Has local keys — no recovery needed. Trigger retroactive broadcast instead
                    // to ensure peers have copies.
                    Console.WriteLine($"[FROST Backup] Auto-check: {localKeys.Count} local key package(s) found. Starting retroactive backup broadcast.");
                    var validators = ReserveBlockCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidators();
                    if (validators != null && validators.Count > 1)
                    {
                        await ReserveBlockCore.Bitcoin.Services.FrostKeyBackupService.BroadcastAllExistingBackups(
                            Globals.ValidatorAddress, validators);
                    }
                    return;
                }

                // No local keys — check if we're a registered active validator
                var activeValidators = ReserveBlockCore.Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidators();
                if (activeValidators == null || activeValidators.Count == 0)
                    return;

                var isActiveValidator = activeValidators.Any(v => v.ValidatorAddress == Globals.ValidatorAddress);
                if (!isActiveValidator)
                    return;

                Console.WriteLine("[FROST Backup] Auto-recovery: No local FROST key packages found but this is an active validator. Attempting peer recovery...");
                var recoveredCount = await ReserveBlockCore.Bitcoin.Services.FrostKeyBackupService.RecoverKeysFromPeers(
                    Globals.ValidatorAddress, activeValidators);

                if (recoveredCount > 0)
                    Console.WriteLine($"[FROST Backup] Auto-recovery: Successfully recovered {recoveredCount} FROST key package(s) from peers!");
                else
                    Console.WriteLine("[FROST Backup] Auto-recovery: No backups found on any peer. Keys may need to be regenerated via new DKG ceremonies.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FROST Backup] Auto-recovery error: {ex.Message}");
            }
        }

        /// <summary>
        /// FIND-0013 Fix: Background cleanup loop that periodically removes expired sessions
        /// Runs every 5 minutes to prevent unbounded in-memory session growth
        /// </summary>
        private static async Task SessionCleanupLoop()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    FrostSessionStorage.CleanupOldSessions();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FROST] Session cleanup error: {ex.Message}");
                }
            }
        }

        #region Self Signed Cert (Optional - for HTTPS)
        private static X509Certificate2 GetSelfSignedCertificate()
        {
            var password = Guid.NewGuid().ToString();
            var commonName = "FROSTValidatorCert";
            var rsaKeySize = 2048;
            var years = 100;
            var hashAlgorithm = HashAlgorithmName.SHA256;

            using (var rsa = RSA.Create(rsaKeySize))
            {
                var request = new CertificateRequest($"cn={commonName}", rsa, hashAlgorithm, RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                  new X509KeyUsageExtension(X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false)
                );
                request.CertificateExtensions.Add(
                  new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)
                );

                var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(years));
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    certificate.FriendlyName = commonName;

                return new X509Certificate2(certificate.Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.MachineKeySet);
            }
        }
        #endregion
    }
}
