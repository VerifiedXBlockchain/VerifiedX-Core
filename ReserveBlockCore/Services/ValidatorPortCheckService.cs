using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Provides startup IP validation and post-server-start port reachability checks.
    /// </summary>
    public static class ValidatorPortCheckService
    {
        /// <summary>
        /// Called early in Program.cs after config/args are processed.
        /// Only checks that an IP address has been provided (from config or --ipaddress arg).
        /// Does NOT check ports — nothing is listening yet at this point.
        /// </summary>
        public static void RunStartupIPCheck()
        {
            if (string.IsNullOrEmpty(Globals.ReportedIP))
            {
                LogUtility.Log(
                    "IP Check: No IP address provided. Set IPAddress in config.txt or use --ipaddress=x.x.x.x argument. " +
                    "Validator port verification will not be possible until an IP is known.",
                    "ValidatorPortCheckService.RunStartupIPCheck()");
                Console.WriteLine("Warning: No IP address configured. Validator port checks require an IP. Set IPAddress in config.txt or use --ipaddress=x.x.x.x");
            }
            else
            {
                LogUtility.Log($"IP Check: IP address {Globals.ReportedIP} is configured (from config or CLI arg).",
                    "ValidatorPortCheckService.RunStartupIPCheck()");
            }
        }

        /// <summary>
        /// Called from StartupValidatorProcess() AFTER validator servers have been started.
        /// Checks all required validator ports against Globals.ReportedIP to verify
        /// they are reachable from the outside (firewall/NAT).
        /// Sets Globals.PortsOpened accordingly.
        /// </summary>
        public static void RunValidatorPortCheck()
        {
            if (string.IsNullOrEmpty(Globals.ReportedIP))
            {
                LogUtility.Log(
                    "Port Check: No IP address available. Cannot verify validator ports. PortsOpened set to false.",
                    "ValidatorPortCheckService.RunValidatorPortCheck()");
                Globals.PortsOpened = false;
                return;
            }

            var ip = Globals.ReportedIP;
            LogUtility.Log($"Port Check: Checking validator ports against {ip}...", "ValidatorPortCheckService.RunValidatorPortCheck()");

            // These are the ports that should be listening after validator server startup
            var portsToCheck = new (int Port, string Name)[]
            {
                (Globals.ValPort, "Validator"),
                (Globals.ValAPIPort, "Validator API"),
                (Globals.FrostValidatorPort, "FROST Validator"),
            };

            bool allOpen = true;

            foreach (var (port, name) in portsToCheck)
            {
                bool isOpen = false;
                try
                {
                    isOpen = PortUtility.IsPortOpen(ip, port);
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"Port Check: Error checking port {port} ({name}): {ex.Message}", "ValidatorPortCheckService.RunValidatorPortCheck()");
                }

                if (isOpen)
                {
                    LogUtility.Log($"Port Check: Port {port} ({name}) - OPEN", "ValidatorPortCheckService.RunValidatorPortCheck()");
                }
                else
                {
                    LogUtility.Log($"Port Check: Port {port} ({name}) - CLOSED", "ValidatorPortCheckService.RunValidatorPortCheck()");
                    allOpen = false;
                }
            }

            // Also update the individual globals for backward compatibility
            Globals.IsValidatorPortOpen = PortUtility.IsPortOpen(ip, Globals.ValPort);
            Globals.IsValidatorAPIPortOpen = PortUtility.IsPortOpen(ip, Globals.ValAPIPort);
            Globals.IsFROSTAPIPortOpen = PortUtility.IsPortOpen(ip, Globals.FrostValidatorPort);

            Globals.PortsOpened = allOpen;

            if (allOpen)
            {
                LogUtility.Log("Port Check: All required validator ports are OPEN. PortsOpened set to true.", "ValidatorPortCheckService.RunValidatorPortCheck()");
            }
            else
            {
                LogUtility.Log(
                    "Port Check: One or more required validator ports are CLOSED. PortsOpened set to false. " +
                    "The StartupValidators loop will continue retrying every 30 seconds.",
                    "ValidatorPortCheckService.RunValidatorPortCheck()");
            }
        }
    }
}