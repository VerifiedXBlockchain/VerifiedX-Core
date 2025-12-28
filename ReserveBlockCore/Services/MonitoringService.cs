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

                    var report = GenerateMonitoringReport();
                    
                    // OVERWRITE the log file
                    File.WriteAllText(_logFilePath, report);
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

        public static void Stop()
        {
            _isRunning = false;
        }
    }
}
