using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Diagnostics;
using System.Text;

namespace ReserveBlockCore.Services
{
    public class MonitoringService
    {
        private static bool _isRunning = false;
        private static string? _logFilePath;
        private static DateTime _lastCpuCheck = DateTime.UtcNow;
        private static TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
        private static DateTime _startTime = DateTime.UtcNow;
        
        // CPU-based auto-restart settings
        private static Queue<double> _cpuHistory = new Queue<double>(3);
        private const double CPU_THRESHOLD = 75.0;
        private const int HIGH_CPU_CHECKS_REQUIRED = 3;

        public static void Start()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _startTime = DateTime.UtcNow;

            // Create timestamped log file
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var logsPath = Path.Combine(GetPathUtility.GetDatabasePath(), "Logs", "Monitoring");
            Directory.CreateDirectory(logsPath);
            _logFilePath = Path.Combine(logsPath, $"warden_monitor_{timestamp}.txt");

            LogUtility.Log($"Monitoring service started - Logging to: {_logFilePath}", "MonitoringService.Start()");

            // Initialize CPU monitoring
            var currentProcess = Process.GetCurrentProcess();
            _lastTotalProcessorTime = currentProcess.TotalProcessorTime;
            _lastCpuCheck = DateTime.UtcNow;

            // Start monitoring loop
            _ = Task.Run(MonitoringLoop);
        }

        private static async Task MonitoringLoop()
        {
            while (_isRunning)
            {
                try
                {
                    await Task.Delay(30000); // Wait 30 seconds between updates

                    if (!_isRunning || string.IsNullOrEmpty(_logFilePath))
                        break;

                    // Get current CPU usage for the check
                    var currentCpu = GetCpuUsagePercent();
                    
                    var report = GenerateMonitoringReport();
                    
                    // OVERWRITE the log file
                    File.WriteAllText(_logFilePath, report);
                    
                    // Check for sustained high CPU usage
                    await CheckForHighCpu(currentCpu);
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Error in monitoring loop: {ex.Message}", "MonitoringService.MonitoringLoop()");
                }
            }
        }

        private static string GenerateMonitoringReport()
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("=== Warden Monitoring Report ===");
            sb.AppendLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Uptime: {DateTime.UtcNow - _startTime:hh\\:mm\\:ss}");
            sb.AppendLine();

            try
            {
                var currentProcess = Process.GetCurrentProcess();
                
                // CPU Usage
                var cpuUsage = GetCpuUsagePercent();
                sb.AppendLine($"CPU Usage: {cpuUsage:F2}%");
                
                // Add warning if approaching or exceeding threshold
                if (cpuUsage >= CPU_THRESHOLD)
                {
                    sb.AppendLine($"⚠️  WARNING: CPU usage above {CPU_THRESHOLD}% threshold!");
                    var highCpuCount = _cpuHistory.Count(c => c >= CPU_THRESHOLD);
                    sb.AppendLine($"⚠️  High CPU checks: {highCpuCount}/{HIGH_CPU_CHECKS_REQUIRED}");
                    if (highCpuCount >= HIGH_CPU_CHECKS_REQUIRED - 1)
                    {
                        sb.AppendLine($"⚠️  CRITICAL: Process will restart on next high CPU reading!");
                    }
                }
                
                // Memory Usage
                var workingSetMB = currentProcess.WorkingSet64 / 1024.0 / 1024.0;
                var privateMemoryMB = currentProcess.PrivateMemorySize64 / 1024.0 / 1024.0;
                sb.AppendLine($"Working Set Memory: {workingSetMB:F2} MB");
                sb.AppendLine($"Private Memory: {privateMemoryMB:F2} MB");
                sb.AppendLine();

                // Dictionary Sizes
                sb.AppendLine("=== Dictionary Sizes ===");
                sb.AppendLine($"MessageLocks: {Globals.MessageLocks.Count}");
                sb.AppendLine($"NetworkValidators: {Globals.NetworkValidators.Count}");
                sb.AppendLine($"Signatures: {Globals.Signatures.Count}");
                sb.AppendLine($"ConsensusDump: {Globals.ConsensusDump.Count}");
                sb.AppendLine($"ConnectionHistoryDict: {Globals.ConnectionHistoryDict.Count}");
                sb.AppendLine($"Nodes: {Globals.Nodes.Count}");
                sb.AppendLine($"P2PPeerDict: {Globals.P2PPeerDict.Count}");
                sb.AppendLine($"P2PValDict: {Globals.P2PValDict.Count}");
                sb.AppendLine($"FortisPool: {Globals.FortisPool.Count}");
                sb.AppendLine($"BlockDownloadService.BlockDict: {Services.BlockDownloadService.BlockDict.Count}");
                sb.AppendLine();

                // System Stats
                sb.AppendLine("=== System Stats ===");
                sb.AppendLine($"Thread Count: {currentProcess.Threads.Count}");
                sb.AppendLine($"Handle Count: {currentProcess.HandleCount}");
                sb.AppendLine($"GC Total Memory: {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F2} MB");
                sb.AppendLine();

                // Blockchain Info
                sb.AppendLine("=== Blockchain Info ===");
                sb.AppendLine($"Current Block Height: {Globals.LastBlock.Height}");
                sb.AppendLine($"Is Chain Synced: {Globals.IsChainSynced}");
                sb.AppendLine($"Blocks Downloading: {Globals.BlocksDownloadSlim.CurrentCount == 0}");
                sb.AppendLine();

                // Validator Info (if applicable)
                if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    sb.AppendLine("=== Validator Info ===");
                    sb.AppendLine($"Validator Address: {Globals.ValidatorAddress}");
                    sb.AppendLine($"Validator Sending: {Globals.ValidatorSending}");
                    sb.AppendLine($"Validator Receiving: {Globals.ValidatorReceiving}");
                    sb.AppendLine($"Validator Balance Good: {Globals.ValidatorBalanceGood}");
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ERROR generating report: {ex.Message}");
            }

            sb.AppendLine("=== End of Report ===");
            return sb.ToString();
        }

        private static double GetCpuUsagePercent()
        {
            try
            {
                var currentTime = DateTime.UtcNow;
                var currentProcess = Process.GetCurrentProcess();
                var currentTotalProcessorTime = currentProcess.TotalProcessorTime;

                var cpuUsedMs = (currentTotalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;
                var totalPassedMs = (currentTime - _lastCpuCheck).TotalMilliseconds;
                
                if (totalPassedMs <= 0)
                    return 0;

                var cpuUsagePercent = (cpuUsedMs / (Environment.ProcessorCount * totalPassedMs)) * 100;

                _lastCpuCheck = currentTime;
                _lastTotalProcessorTime = currentTotalProcessorTime;

                return cpuUsagePercent;
            }
            catch
            {
                return 0;
            }
        }

        private static async Task CheckForHighCpu(double cpuUsage)
        {
            try
            {
                // Add current CPU reading to history (max 3 entries)
                _cpuHistory.Enqueue(cpuUsage);
                if (_cpuHistory.Count > HIGH_CPU_CHECKS_REQUIRED)
                    _cpuHistory.Dequeue();
                
                // Check if all readings in history are above threshold
                if (_cpuHistory.Count == HIGH_CPU_CHECKS_REQUIRED && 
                    _cpuHistory.All(cpu => cpu >= CPU_THRESHOLD))
                {
                    var avgCpu = _cpuHistory.Average();
                    var checkDuration = HIGH_CPU_CHECKS_REQUIRED * 30; // 30 seconds per check
                    
                    LogUtility.Log($"HIGH CPU DETECTED - Average: {avgCpu:F2}% over {HIGH_CPU_CHECKS_REQUIRED} checks ({checkDuration}s). Terminating process for restart...", 
                        "MonitoringService.CheckForHighCpu()");
                    
                    ErrorLogUtility.LogError($"Process terminated due to sustained high CPU usage: {avgCpu:F2}% over {checkDuration} seconds (threshold: {CPU_THRESHOLD}%)", 
                        "MonitoringService.CheckForHighCpu()");
                    
                    // Log final report with termination notice
                    if (!string.IsNullOrEmpty(_logFilePath))
                    {
                        try
                        {
                            var finalReport = new StringBuilder();
                            finalReport.AppendLine("\n\n=== PROCESS TERMINATED DUE TO HIGH CPU USAGE ===");
                            finalReport.AppendLine($"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                            finalReport.AppendLine($"Average CPU: {avgCpu:F2}%");
                            finalReport.AppendLine($"Duration: {checkDuration} seconds");
                            finalReport.AppendLine($"Threshold: {CPU_THRESHOLD}%");
                            finalReport.AppendLine($"CPU History: [{string.Join(", ", _cpuHistory.Select(c => $"{c:F2}%"))}]");
                            finalReport.AppendLine("=== Warden will restart process in 60 seconds ===\n");
                            
                            File.AppendAllText(_logFilePath, finalReport.ToString());
                        }
                        catch { /* Ignore errors writing final log */ }
                    }

                    // Exit with code 1 to signal abnormal termination
                    // Warden will detect this and restart the process
                    Globals.StopAllTimers = true;
                    Console.WriteLine("Closing and Exiting Wallet Application.");
                    while (Globals.TreisUpdating)
                    {
                        await Task.Delay(100);
                        //waiting for treis to stop
                    }

                    await Settings.InitiateShutdownUpdate();
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error in CheckForHighCpu: {ex.Message}", "MonitoringService.CheckForHighCpu()");
            }
        }

        public static void Stop()
        {
            _isRunning = false;
        }
    }
}
