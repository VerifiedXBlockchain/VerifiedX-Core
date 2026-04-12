using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Runs a startup port check against the node's own ReportedIP to verify
    /// that all required validator ports are reachable from the outside.
    /// Sets Globals.PortsOpened accordingly.
    /// </summary>
    public static class ValidatorPortCheckService
    {
        /// <summary>
        /// Checks all required validator ports against Globals.ReportedIP.
        /// If no IP is set, logs a warning and sets PortsOpened = false.
        /// Called from Program.cs on every node startup.
        /// </summary>
        public static void RunStartupPortCheck()
        {
            LogUtility.Log("Port Check: Starting startup port verification...", "ValidatorPortCheckService.RunStartupPortCheck()");

            // First check: is an IP address available?
            if (string.IsNullOrEmpty(Globals.ReportedIP))
            {
                LogUtility.Log(
                    "Port Check: No IP address provided. Set IPAddress in config.txt or use --ipaddress=x.x.x.x argument. " +
                    "Ports cannot be verified. PortsOpened set to false.",
                    "ValidatorPortCheckService.RunStartupPortCheck()");
                Globals.PortsOpened = false;
                return;
            }

            var ip = Globals.ReportedIP;
            LogUtility.Log($"Port Check: Using IP address {ip} for port verification.", "ValidatorPortCheckService.RunStartupPortCheck()");

            // Define all ports to check with friendly names
            var portsToCheck = new (int Port, string Name)[]
            {
                (Globals.Port, "P2P"),
                (Globals.ValPort, "Validator"),
                (Globals.ValAPIPort, "Validator API"),
                (Globals.FrostValidatorPort, "FROST Validator"),
                (Globals.ArbiterPort, "Arbiter"),
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
                    LogUtility.Log($"Port Check: Error checking port {port} ({name}): {ex.Message}", "ValidatorPortCheckService.RunStartupPortCheck()");
                }

                if (isOpen)
                {
                    LogUtility.Log($"Port Check: Port {port} ({name}) - OPEN", "ValidatorPortCheckService.RunStartupPortCheck()");
                }
                else
                {
                    LogUtility.Log($"Port Check: Port {port} ({name}) - CLOSED", "ValidatorPortCheckService.RunStartupPortCheck()");
                    allOpen = false;
                }
            }

            Globals.PortsOpened = allOpen;

            if (allOpen)
            {
                LogUtility.Log("Port Check: All required ports are OPEN. PortsOpened set to true.", "ValidatorPortCheckService.RunStartupPortCheck()");
            }
            else
            {
                LogUtility.Log(
                    "Port Check: One or more required ports are CLOSED. PortsOpened set to false. " +
                    "Validator registration and startup will be blocked until all ports are open.",
                    "ValidatorPortCheckService.RunStartupPortCheck()");
            }
        }
    }
}