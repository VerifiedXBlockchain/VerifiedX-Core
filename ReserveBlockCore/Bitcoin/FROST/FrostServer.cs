using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
                    
                    Console.WriteLine($"FROST Validator Server started on port {Globals.FrostValidatorPort}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FROST Server Error: {ex}");
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
