using ReserveBlockCore.Utilities;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ReserveBlockCore.Services
{
    public class WardenService
    {
        private static Process? _childProcess;
        private static HttpClient? _httpClient;
        private static bool _isRunning = true;

        public static async Task StartWarden(string[] args)
        {
            LogUtility.Log("Warden mode started - Will monitor and restart child process as needed", "WardenService.StartWarden()");
            
            // Ensure enableapi and logmemory are in args
            var argsList = args.ToList();
            if (!argsList.Any(x => x.ToLower() == "enableapi"))
            {
                argsList.Add("enableapi");
            }
            if (!argsList.Any(x => x.ToLower() == "logmemory"))
            {
                argsList.Add("logmemory");
            }

            if(Globals.IsTestNet)
            {
                //Globals.IsCustomTestNet = true; //devnet only
                Globals.V4Height = Globals.IsTestNet ? 1 : 3_074_181;//change for mainnet.
                Globals.V2ValHeight = Globals.IsTestNet ? 0 : 3_074_180;//change for mainnet.
                Globals.SpecialBlockHeight = Globals.IsTestNet ? 2000 : 3_074_185;//change for mainnet.
                Globals.GenesisValidator = Globals.IsTestNet ? "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" : "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH";
                Globals.TXHeightRule5 = Globals.IsTestNet ? 746313 : Globals.TXHeightRule5;
            }
            else
            {
                Globals.V4Height = Globals.IsTestNet ? 1 : 3_074_181;//change for mainnet.
                Globals.V2ValHeight = Globals.IsTestNet ? 0 : 3_074_180;//change for mainnet.
                Globals.SpecialBlockHeight = Globals.IsTestNet ? 2000 : 3_074_185;//change for mainnet.
                Globals.GenesisValidator = Globals.IsTestNet ? "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" : "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH";
                Globals.TXHeightRule5 = Globals.IsTestNet ? 746313 : Globals.TXHeightRule5;
            }

            Config.Config.EstablishConfigFile();
            Config.Config.EstablishABLFile();
            var config = Config.Config.ReadConfigFile();
            Config.Config.ProcessConfig(config);
            Config.Config.ProcessABL();

            // Remove "warden" from args and add "warden_monitoring"
            argsList.RemoveAll(x => x.ToLower() == "warden");
            argsList.Add("warden_monitoring");

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            while (_isRunning)
            {
                try
                {
                    // Start child process
                    await StartChildProcess(argsList.ToArray());

                    // Wait 120 seconds before starting health checks
                    LogUtility.Log("Waiting 60 seconds before starting health checks...", "WardenService.StartWarden()");
                    await Task.Delay(60000);

                    // Monitor child process
                    while (_childProcess != null && !_childProcess.HasExited)
                    {
                        try
                        {
                            // Perform health check
                            var isHealthy = await CheckChildHealth();
                            
                            if (!isHealthy)
                            {
                                LogUtility.Log("Child process health check failed - restarting...", "WardenService.StartWarden()");
                                await KillChildProcess();
                                break; // Exit monitoring loop to restart
                            }

                            // Wait 15 seconds before next health check
                            await Task.Delay(15000);
                        }
                        catch (Exception ex)
                        {
                            ErrorLogUtility.LogError($"Error during health check: {ex.Message}", "WardenService.StartWarden()");
                            await KillChildProcess();
                            break;
                        }
                    }

                    // If we get here, child process exited or was killed
                    if (_isRunning)
                    {
                        // Check exit code - 99 indicates intentional user exit
                        if (_childProcess != null && _childProcess.ExitCode == 99)
                        {
                            LogUtility.Log("Child process exited with code 99 (intentional user exit) - stopping warden...", "WardenService.StartWarden()");
                            _isRunning = false;
                            _httpClient?.Dispose();
                            break; // Exit the main warden loop
                        }
                        
                        LogUtility.Log("Child process stopped - waiting 60 seconds before restart...", "WardenService.StartWarden()");
                        await Task.Delay(60000);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error in warden loop: {ex.Message}", "WardenService.StartWarden()");
                    await Task.Delay(60000);
                }
            }

            Console.WriteLine("Warden service stopped. Exiting Now.");
            Environment.Exit(99);
        }

        private static async Task StartChildProcess(string[] args)
        {
            try
            {
                var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
                ProcessStartInfo startInfo;
                
                if (isLinux)
                {
                    // Linux: Use dotnet + DLL path
                    var exeLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
                    if (string.IsNullOrEmpty(exeLocation))
                    {
                        ErrorLogUtility.LogError("Could not determine assembly location", "WardenService.StartChildProcess()");
                        return;
                    }
                    
                    var dllPath = Path.Combine(exeLocation, "ReserveBlockCore.dll");
                    
                    // Build arguments: "dllPath arg1 arg2 arg3"
                    var allArgs = new List<string> { dllPath };
                    allArgs.AddRange(args);
                    
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = string.Join(" ", allArgs),
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = false,
                        WorkingDirectory = exeLocation
                    };
                    
                    LogUtility.Log($"Starting child process on Linux: dotnet {string.Join(" ", allArgs)}", "WardenService.StartChildProcess()");
                }
                else
                {
                    // Windows: Use executable directly
                    var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(executablePath))
                    {
                        ErrorLogUtility.LogError("Could not determine executable path", "WardenService.StartChildProcess()");
                        return;
                    }

                    startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = string.Join(" ", args),
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = false
                    };
                    
                    LogUtility.Log($"Starting child process on Windows: {executablePath} {string.Join(" ", args)}", "WardenService.StartChildProcess()");
                }

                _childProcess = new Process { StartInfo = startInfo };
                _childProcess.Start();

                LogUtility.Log($"Child process started with PID: {_childProcess.Id}", "WardenService.StartChildProcess()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to start child process: {ex.Message}", "WardenService.StartChildProcess()");
            }
        }

        private static async Task<bool> CheckChildHealth()
        {
            try
            {
                if (_childProcess == null || _childProcess.HasExited)
                {
                    return false;
                }

                // Check if API responds
                var url = $"http://localhost:{Globals.APIPort}/api/v1/";
                var response = await _httpClient!.GetAsync(url);
                
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static async Task KillChildProcess()
        {
            try
            {
                if (_childProcess != null && !_childProcess.HasExited)
                {
                    LogUtility.Log($"Killing child process (PID: {_childProcess.Id})...", "WardenService.KillChildProcess()");
                    _childProcess.Kill(true);
                    await Task.Delay(2000); // Wait for process to terminate
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error killing child process: {ex.Message}", "WardenService.KillChildProcess()");
            }
            finally
            {
                _childProcess?.Dispose();
                _childProcess = null;
            }
        }

        public static void Stop()
        {
            _isRunning = false;
            _httpClient?.Dispose();
        }
    }
}
