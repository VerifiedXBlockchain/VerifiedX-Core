using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using VerifiedXCore.Utilities;
using Spectre.Console;
using System.Diagnostics;
using System.Formats.Asn1;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace VerifiedXCore.Services
{
    
    public class VersionControlService
    {
        #region GitHub Classes
        private class Release
        {
            public string url { get; set; }
            public string html_url { get; set; }
            public string assets_url { get; set; }
            public string upload_url { get; set; }
            public int id { get; set; }
            public string node_id { get; set; }
            public string tag_name { get; set; }
            public string target_commitish { get; set; }
            public string name { get; set; }
            public string body { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public DateTimeOffset created_at { get; set; }
            public DateTimeOffset? published_at { get; set; }
            public Author author { get; set; }
            public string tarball_url { get; set; }
            public string zipball_url { get; set; }
            public IReadOnlyList<ReleaseAsset> assets { get; set; }
        }

        private class ReleaseAsset
        {
            public string url { get; set; }
            public int id { get; set; }
            public string node_id { get; set; }
            public string name { get; set; }
            public string label { get; set; }
            public string State { get; set; }
            public string content_type { get; set; }
            public int size { get; set; }
            public int download_count { get; set; }
            public DateTimeOffset created_at { get; set; }
            public DateTimeOffset updated_at { get; set; }
            public string browser_download_url { get; set; }
            public Author uploader { get; set; }

        }

        private class Author
        {
            public string login { get; set; }
            public int id { get; set; }
            public string node_id { get; set; }
            public string avatar_url { get; set; }
            public string url { get; set; }
            public string html_url { get; set; }
            public string followers_url { get; set; }
            public string following_url { get; set; }
            public string gists_url { get; set; }
            public string starred_url { get; set; }
            public string subscriptions_url { get; set; }
            public string organizations_url { get; set; }
            public string repos_url { get; set; }
            public string events_url { get; set; }
            public string received_events_url { get; set; }
            public string type { get; set; }
            public bool site_admin { get; set; }

        }

        #endregion

        static SemaphoreSlim VesrionControlServiceLock = new SemaphoreSlim(1, 1);
        public static async Task RunVersionControl()
        {
            while(true)
            {
                var delay = Task.Delay(new TimeSpan(12,0,0));
                await VesrionControlServiceLock.WaitAsync();
                try
                {
                    await CheckVersion();
                }
                finally
                {
                    VesrionControlServiceLock.Release();
                }

                await delay;
            }
        }

        public static async Task DownloadLatestRelease()
        {
            try
            {
                if (Globals.UpToDate)
                {
                    Console.WriteLine("Client is already up to date.");
                    AnsiConsole.MarkupLine($"Client Version: [blue]{Globals.CLIVersion}[/] | GitHub Tag: [green]{Globals.GitHubVersion}[/]");
                    return;
                }
                if (Globals.ReleaseSignerAddresses.Length == 0)
                {
                    StartupService.MainMenu();
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ReleaseSignature.NoSignerReason)}[/]");
                    return;
                }

                var count = 1;
                var dict = new Dictionary<string, string>();
                Console.WriteLine("Please select the appropriate download:");
                foreach (var asset in ReleaseZips(Globals.GitHubLatestReleaseAssetsDict.Keys))
                {
                    Console.WriteLine($"{count}. {asset}");
                    dict.Add(count.ToString(), asset);
                    count += 1;
                }

                var assetChoice = Console.ReadLine();
                if (string.IsNullOrEmpty(assetChoice) || !dict.TryGetValue(assetChoice, out var chosen))
                {
                    StartupService.MainMenu();
                    AnsiConsole.MarkupLine("[yellow]Incorrect choice selected. Returned to main menu.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"You have chosen to download: {chosen}. Are you sure? ([green]'y'[/] for [green]yes[/] and [red]'n'[/] for [red]no[/])");
                if (Console.ReadLine()?.ToLower() != "y")
                {
                    StartupService.MainMenu();
                    AnsiConsole.MarkupLine("[yellow]You did not selected 'Yes'. Returned to main menu.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"Downloading and verifying [purple]{chosen}[/]...");
                var (zip, refusal) = await DownloadVerifiedAsync(chosen);
                if (zip == null)
                {
                    StartupService.MainMenu();
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(refusal ?? "The download failed.")}[/]");
                    return;
                }
                AnsiConsole.MarkupLine($"Download is complete and its signature verifies. Would you like to update files now? ([green]'y'[/] for [green]yes[/] and [red]'n'[/] for [red]no[/])");
                if (Console.ReadLine()?.ToLower() == "y")
                    InstallFromBytes(zip);
                else
                    await File.WriteAllBytesAsync(DownloadPath() + chosen, zip);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error updating client: {ex}", "VersionControlService.DownloadLatestRelease()");
                Console.WriteLine("There was an error updating client. Please download from GitHub.");
            }
        }

        /// <summary>The release assets self-update can install: the zips (a signature file is not one).</summary>
        public static List<string> ReleaseZips(IEnumerable<string> assetNames) =>
            assetNames.Where(a => a != null && a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// Downloads a release zip and its "&lt;asset&gt;.sig" into memory and verifies the signature (ReleaseSignature). The
        /// bytes returned are the ones verified, so they are the ones installed. (null, why) when it is not installed.
        /// </summary>
        public static async Task<(byte[]? Zip, string? Refusal)> DownloadVerifiedAsync(string assetName)
        {
            if (Globals.ReleaseSignerAddresses.Length == 0)
                return (null, ReleaseSignature.NoSignerReason);
            if (string.IsNullOrEmpty(assetName) || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (null, "Only a release .zip is installed.");
            if (!Globals.GitHubLatestReleaseAssetsDict.TryGetValue(assetName, out var url) || string.IsNullOrEmpty(url))
                return (null, $"{assetName} is not an asset of the latest release.");
            Globals.GitHubLatestReleaseAssetsDict.TryGetValue(assetName + ".sig", out var sigUrl);
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(10);
                var zip = await client.GetByteArrayAsync(url);
                var signature = string.IsNullOrEmpty(sigUrl) ? null : await client.GetStringAsync(sigUrl);
                var refusal = ReleaseSignature.Refusal(zip, signature);
                if (refusal != null)
                {
                    ErrorLogUtility.LogError($"Release asset {assetName} refused: {refusal}", "VersionControlService.DownloadVerifiedAsync()");
                    return (null, refusal);
                }
                return (zip, null);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Downloading release asset {assetName} failed: {ex.Message}", "VersionControlService.DownloadVerifiedAsync()");
                return (null, $"Downloading {assetName} failed: {ex.Message}");
            }
        }

        public static async Task<bool> DownloadLatestAndUpdate(string fileName, bool update = false) =>
            (await DownloadLatestAndUpdateWithReason(fileName, update)).Ok;

        /// <summary>Downloads and verifies a release zip, then installs it (<paramref name="update"/>) or saves it.</summary>
        public static async Task<(bool Ok, string? Refusal)> DownloadLatestAndUpdateWithReason(string fileName, bool update = false)
        {
            if (Globals.UpToDate)
                return (false, "The client is already up to date.");
            var (zip, refusal) = await DownloadVerifiedAsync(fileName);
            if (zip == null)
                return (false, refusal);
            try
            {
                if (update)
                    return InstallFromBytes(zip) ? (true, null) : (false, "Installing the update failed.");
                await File.WriteAllBytesAsync(DownloadPath() + fileName, zip);
                return (true, null);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error Performing update. Error: {ex}", "VersionControlService.DownloadLatestAndUpdateWithReason()");
                return (false, "Installing the update failed.");
            }
        }

        public static async Task<List<string>?> GetLatestDownloadFiles()
        {
            await CheckVersion();
            var fileList = Globals.GitHubLatestReleaseAssetsDict.Keys.ToList();

            if(fileList.Count() > 0)
                return fileList;

            return null;
        }

        /// <summary>Extracts a verified release zip from the bytes that were verified, and installs its files.</summary>
        public static bool InstallFromBytes(byte[] zip)
        {
            try
            {
                var unzipPath = DownloadPath() + "temp" + Path.DirectorySeparatorChar;
                if (Directory.Exists(unzipPath))
                    Directory.Delete(unzipPath, true);
                using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read))
                    System.IO.Compression.ZipFileExtensions.ExtractToDirectory(archive, unzipPath);
                var directory = Directory.GetDirectories(unzipPath);
                var newFilesPath = directory.FirstOrDefault();
                var filePaths = Directory.GetFiles(newFilesPath);

                string strFilePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (strFilePath != null)
                {
                    string? strWorkPath = Path.GetDirectoryName(strFilePath);
                    if (strWorkPath != null)
                    {
                        foreach (var filePath in filePaths)
                        {
                            var fileName = Path.GetFileName(filePath);
                            var oldFilePath = strWorkPath + Path.DirectorySeparatorChar + fileName;
                            if (File.Exists(oldFilePath))
                            {
                                if(File.Exists(oldFilePath + "_outdated"))
                                    File.Delete(oldFilePath + "_outdated");
                                
                                File.Move(oldFilePath, oldFilePath + "_outdated");
                                File.Move(filePath, oldFilePath);
                            }
                        }

                        Environment.SetEnvironmentVariable("VFX-Updated", "1", EnvironmentVariableTarget.User);
                        StartupService.MainMenu();
                        AnsiConsole.MarkupLine("[green]Update Has Finished. Please restart client to complete update.[/]");
                        AnsiConsole.MarkupLine("[yellow]Failure to restart may result in client glitches. Please restart now.[/]");

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error Performing Updated. Error: {ex.ToString()}");
                ErrorLogUtility.LogError($"Error Performing Updated. Error: {ex.ToString()}", "VersionControlService.InstallFromBytes()");
            }

            return false;
        }

        public static async Task DeleteOldFiles()
        {
            try
            {
                string strFilePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (strFilePath != null)
                {
                    string? strWorkPath = System.IO.Path.GetDirectoryName(strFilePath);
                    if (strWorkPath != null)
                    {
                        var files = Directory.GetFiles(strWorkPath);
                        var filesFiltered = files.Where(x => x.Contains("_outdated")).ToList();
                        foreach (var file in filesFiltered)
                        {
                            File.Delete(file);
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine($"DeleteOldFiles() Error: {ex.ToString()}");
                ErrorLogUtility.LogError($"DeleteOldFiles() Error: {ex.ToString()}", "VersionControlService.DeleteOldFiles()");
            }
            
        }

        private static string DownloadPath()
        {
            string MainFolder = Globals.IsTestNet != true ? "RBX" : "RBXTest";

            var downloadLocation = Globals.IsTestNet != true ? "Download" : "DownloadTestNet";

            string path = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = homeDirectory + Path.DirectorySeparatorChar + MainFolder.ToLower() + Path.DirectorySeparatorChar + downloadLocation + Path.DirectorySeparatorChar;
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "DBs" + Path.DirectorySeparatorChar + downloadLocation + Path.DirectorySeparatorChar;
                }
                else
                {
                    path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + MainFolder + Path.DirectorySeparatorChar + downloadLocation + Path.DirectorySeparatorChar;
                }
            }

            if (!string.IsNullOrEmpty(Globals.CustomPath))
            {
                path = Globals.CustomPath + MainFolder + Path.DirectorySeparatorChar + downloadLocation + Path.DirectorySeparatorChar;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        private static async Task CheckVersion()
        {
            try
            {
                if(Globals.NewUpdateLastChecked <= DateTime.UtcNow.AddHours(-1)) 
                {
                    var url = Globals.GitHubApiURL + Globals.GitHubRBXRepoURL;
                    using (var client = Globals.HttpClientFactory.CreateClient())
                    {
                        var productValue = new ProductInfoHeaderValue("VFX-Version-Check", "1.0");
                        client.DefaultRequestHeaders.UserAgent.Add(productValue);

                        using (var Response = await client.GetAsync(url, new CancellationTokenSource(5000).Token))
                        {
                            if (Response.StatusCode == HttpStatusCode.OK)
                            {
                                var responseString = await Response.Content.ReadAsStringAsync();
                                var release = JsonConvert.DeserializeObject<Release>(responseString.ToString());
                                if (release != null)
                                {
                                    var version = release.tag_name;
                                    if (version != Globals.GitHubVersion)
                                    {
                                        Globals.UpToDate = false;//out of date
                                        Globals.GitHubLatestReleaseVersion = version;
                                    }
                                    else
                                    {
                                        Globals.UpToDate = true;//up to date
                                        Globals.GitHubLatestReleaseVersion = version;
                                    }

                                    if (release.assets.Count > 0)
                                    {
                                        foreach (var asset in release.assets)
                                        {
                                            var added = Globals.GitHubLatestReleaseAssetsDict.TryAdd(asset.name, asset.browser_download_url);
                                            if (!added)
                                                Globals.GitHubLatestReleaseAssetsDict[asset.name] = asset.browser_download_url;
                                        }
                                    }

                                    Globals.NewUpdateLastChecked = DateTime.UtcNow;
                                }
                            }
                            else
                            {

                            }
                        }
                    }
                }
            }
            catch(Exception ex) 
            {
                //ConsoleWriterService.Output($"Error Checking GitHub for update. Error: {ex.ToString()}");
            }
        }

        
    }
}
