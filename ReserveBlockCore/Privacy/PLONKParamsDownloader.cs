using System.Security.Cryptography;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Downloads PLONK universal parameters on first run and caches them permanently.
    /// The file is verified via a hardcoded SHA-256 hash before use.
    /// </summary>
    public static class PLONKParamsDownloader
    {
        /// <summary>GitHub Release URL for the VXPLNK03 params file.</summary>
        public const string DownloadUrl =
            "https://github.com/VerifiedXBlockchain/plonk/releases/download/v1/vfx_plonk_v1.params";

        /// <summary>Expected SHA-256 hash (lowercase hex) of the params file.</summary>
        public const string ExpectedSha256 =
            "e4ea423e068eb949c5b1321908da1b263fd472700272e879058dc5f55166d85c";

        /// <summary>Local filename stored inside the PlonkParams directory.</summary>
        public const string ParamsFileName = "vfx_plonk_v1.params";

        /// <summary>
        /// Ensures the PLONK params file is available locally.
        /// <list type="number">
        ///   <item>If <c>VFX_PLONK_PARAMS_PATH</c> env var is set → use that (skip download).</item>
        ///   <item>If the file already exists at the default location and hash matches → return its path.</item>
        ///   <item>Otherwise download from GitHub Releases, verify SHA-256, and cache.</item>
        /// </list>
        /// Returns the absolute path to the params file, or <c>null</c> if unavailable.
        /// </summary>
        public static async Task<string?> EnsureParamsAvailableAsync()
        {
            try
            {
                // 1. Honor explicit env-var override (existing behavior)
                var envPath = Environment.GetEnvironmentVariable(PLONKSetup.ParamsPathEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
                {
                    LogUtility.Log($"PLONK params: using VFX_PLONK_PARAMS_PATH = {envPath}", "PLONKParamsDownloader");
                    return envPath;
                }

                // 2. Check default cached location
                var paramsDir = GetPathUtility.GetPlonkParamsPath();
                var localPath = Path.Combine(paramsDir, ParamsFileName);

                if (File.Exists(localPath))
                {
                    if (VerifyFileHash(localPath))
                    {
                        LogUtility.Log("PLONK params: cached file verified.", "PLONKParamsDownloader");
                        return localPath;
                    }
                    else
                    {
                        LogUtility.Log("PLONK params: cached file hash mismatch — re-downloading.", "PLONKParamsDownloader");
                        try { File.Delete(localPath); } catch { }
                    }
                }

                // 3. Download
                LogUtility.Log($"PLONK params: downloading from {DownloadUrl} (one-time setup)...", "PLONKParamsDownloader");
                Console.WriteLine("Downloading PLONK parameters (one-time setup, ~253 MB)...");

                var tempPath = localPath + ".downloading";

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(30);

                    using var response = await httpClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength;

                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;
                    int lastPercent = -1;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int percent = (int)(totalRead * 100 / totalBytes.Value);
                            if (percent != lastPercent && percent % 10 == 0)
                            {
                                Console.WriteLine($"  PLONK params download: {percent}% ({totalRead / (1024 * 1024)} MB / {totalBytes.Value / (1024 * 1024)} MB)");
                                lastPercent = percent;
                            }
                        }
                    }
                }

                // 4. Verify hash
                if (!VerifyFileHash(tempPath))
                {
                    Console.WriteLine("PLONK params download: SHA-256 hash mismatch! File rejected.");
                    ErrorLogUtility.LogError("Downloaded PLONK params file failed SHA-256 verification.", "PLONKParamsDownloader");
                    try { File.Delete(tempPath); } catch { }
                    return null;
                }

                // 5. Move to final location
                if (File.Exists(localPath))
                    File.Delete(localPath);
                File.Move(tempPath, localPath);

                Console.WriteLine("PLONK parameters downloaded and verified successfully.");
                LogUtility.Log($"PLONK params: saved to {localPath}", "PLONKParamsDownloader");
                return localPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PLONK params download failed: {ex.Message}");
                Console.WriteLine("Privacy features will be unavailable until params are provided.");
                ErrorLogUtility.LogError($"PLONK params download failed: {ex}", "PLONKParamsDownloader");
                return null;
            }
        }

        /// <summary>
        /// Verifies the SHA-256 hash of a file against <see cref="ExpectedSha256"/>.
        /// </summary>
        public static bool VerifyFileHash(string filePath)
        {
            try
            {
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hashBytes = sha256.ComputeHash(stream);
                var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                return hashHex == ExpectedSha256;
            }
            catch
            {
                return false;
            }
        }
    }
}