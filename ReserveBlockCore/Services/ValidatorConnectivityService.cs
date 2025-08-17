using Microsoft.AspNetCore.SignalR.Client;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Net.Sockets;
using System.Collections.Concurrent;
using ReserveBlockCore.Data;
using Microsoft.AspNetCore.SignalR;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Handles connectivity verification for validators to ensure they have required ports open.
    /// Validates that validators can participate in SignalR and HTTP communication.
    /// </summary>
    public static class ValidatorConnectivityService
    {
        private static readonly SemaphoreSlim ConnectivityCheckLock = new(1, 1);

        /// <summary>
        /// Gets the list of required ports for validator participation.
        /// Uses global port variables to support both TestNet and MainNet.
        /// </summary>
        /// <returns>List of required port numbers</returns>
        public static List<int> GetRequiredPorts()
        {
            return new List<int>
            {
                Globals.Port,        // General P2P port
                Globals.ValPort,     // Validator SignalR port
                Globals.ValAPIPort   // Caster HTTP API port
            };
        }

        /// <summary>
        /// Comprehensively verifies that a validator has all required ports accessible.
        /// Tests TCP connectivity, HTTP endpoints, and SignalR connections.
        /// </summary>
        /// <param name="ipAddress">IP address of validator to test</param>
        /// <returns>True if all ports are accessible, false otherwise</returns>
        public static async Task<bool> VerifyValidatorConnectivity(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
                return false;

            try
            {
                // Clean IP address (remove IPv6 prefix if present)
                var cleanIP = ipAddress.Replace("::ffff:", "");

                // Test all required ports
                foreach (var port in GetRequiredPorts())
                {
                    if (!await IsPortAccessible(cleanIP, port))
                    {
                        ErrorLogUtility.LogError($"Port {port} not accessible for validator {cleanIP}", "ValidatorConnectivityService.VerifyValidatorConnectivity");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Connectivity verification failed for {ipAddress}: {ex.Message}", "ValidatorConnectivityService.VerifyValidatorConnectivity");
                return false;
            }
        }

        /// <summary>
        /// Tests if a specific port is accessible on the given IP address.
        /// Performs different tests based on port type (TCP, HTTP, SignalR).
        /// </summary>
        /// <param name="ipAddress">IP address to test</param>
        /// <param name="port">Port number to test</param>
        /// <returns>True if port is accessible, false otherwise</returns>
        public static async Task<bool> IsPortAccessible(string ipAddress, int port)
        {
            try
            {
                // Test 1: Basic TCP connection test
                if (!await TestTcpConnection(ipAddress, port))
                    return false;

                // Test 2: HTTP endpoint test for API ports
                if (port == Globals.ValAPIPort)
                {
                    return await TestHttpEndpoint(ipAddress, port);
                }

                // Test 3: SignalR connection test for validator ports
                if (port == Globals.ValPort)
                {
                    return await TestSignalRConnection(ipAddress, port);
                }

                // For other ports, TCP connection test is sufficient
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Port accessibility test failed for {ipAddress}:{port} - {ex.Message}", "ValidatorConnectivityService.IsPortAccessible");
                return false;
            }
        }

        /// <summary>
        /// Tests basic TCP connectivity to a port.
        /// </summary>
        /// <param name="ipAddress">IP address to test</param>
        /// <param name="port">Port to test</param>
        /// <returns>True if TCP connection successful, false otherwise</returns>
        private static async Task<bool> TestTcpConnection(string ipAddress, int port)
        {
            try
            {
                using var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(ipAddress, port);
                
                // 3-second timeout for connection
                if (await Task.WhenAny(connectTask, Task.Delay(3000)) != connectTask)
                {
                    return false; // Timeout
                }

                return tcpClient.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tests HTTP endpoint accessibility for validator API ports.
        /// </summary>
        /// <param name="ipAddress">IP address to test</param>
        /// <param name="port">HTTP port to test</param>
        /// <returns>True if HTTP endpoint responds, false otherwise</returns>
        private static async Task<bool> TestHttpEndpoint(string ipAddress, int port)
        {
            try
            {
                using var client = Globals.HttpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                // Test validator heartbeat endpoint
                var uri = $"http://{ipAddress}:{port}/valapi/validator/heartbeat";
                var response = await client.GetAsync(uri);
                
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tests SignalR connection for validator communication ports.
        /// Uses a simple connectivity test header that bypasses validation in P2PValidatorServer.
        /// </summary>
        /// <param name="ipAddress">IP address to test</param>
        /// <param name="port">SignalR port to test</param>
        /// <returns>True if SignalR connection successful, false otherwise</returns>
        private static async Task<bool> TestSignalRConnection(string ipAddress, int port)
        {
            HubConnection? connection = null;
            try
            {
                // Use single connectivity test header that P2PValidatorServer will recognize and allow
                connection = new HubConnectionBuilder()
                    .WithUrl($"http://{ipAddress}:{port}/consensus", options =>
                    {
                        options.Headers.Add("CONNECTIVITY_TEST", "true");
                    })
                    .Build();

                await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (connection != null)
                {
                    try
                    {
                        await connection.DisposeAsync();
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Validates that a validator meets all requirements for admission to the validator pool.
        /// Checks stake requirements, signature verification, and connectivity.
        /// </summary>
        /// <param name="validator">Validator to validate</param>
        /// <returns>True if validator meets all requirements, false otherwise</returns>
        public static async Task<bool> ValidateValidatorForAdmission(NetworkValidator validator)
        {
            try
            {
                // Check stake requirement
                if (!HasValidStake(validator.Address))
                {
                    LogUtility.Log($"Validator {validator.Address} rejected: insufficient stake", "ValidatorConnectivityService.ValidateValidatorForAdmission");
                    return false;
                }

                // Verify signature
                if (!SignatureService.VerifySignature(validator.Address, validator.SignatureMessage, validator.Signature))
                {
                    LogUtility.Log($"Validator {validator.Address} rejected: invalid signature", "ValidatorConnectivityService.ValidateValidatorForAdmission");
                    return false;
                }

                // Check connectivity
                if (!await VerifyValidatorConnectivity(validator.IPAddress))
                {
                    var ports = string.Join(",", GetRequiredPorts());
                    LogUtility.Log($"Validator {validator.Address} rejected: ports {ports} not accessible on IP {validator.IPAddress}", "ValidatorConnectivityService.ValidateValidatorForAdmission");
                    
                    // Track failed connectivity for monitoring
                    Globals.FailedConnectivityValidators.TryAdd(validator.Address, DateTime.UtcNow);
                    return false;
                }

                // Remove from failed connectivity list if previously failed
                Globals.FailedConnectivityValidators.TryRemove(validator.Address, out _);

                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Validator admission validation failed for {validator.Address}: {ex.Message}", "ValidatorConnectivityService.ValidateValidatorForAdmission");
                return false;
            }
        }

        /// <summary>
        /// Checks if a validator has the required stake amount.
        /// </summary>
        /// <param name="address">Validator address to check</param>
        /// <returns>True if has required stake, false otherwise</returns>
        private static bool HasValidStake(string address)
        {
            try
            {
                var account = StateData.GetSpecificAccountStateTrei(address);
                return account != null && account.Balance >= CasterConfiguration.GetRequiredStakeAmount();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Continuously monitors validator connectivity and removes failed validators.
        /// Runs as a background service to maintain network health.
        /// </summary>
        public static async Task ValidatorConnectivityMonitor()
        {
            while (!Globals.StopAllTimers)
            {
                await ConnectivityCheckLock.WaitAsync();
                try
                {
                    var validators = Globals.NetworkValidators.Values.ToList();
                    var requiredPorts = GetRequiredPorts();

                    foreach (var validator in validators)
                    {
                        try
                        {
                            if (!await VerifyValidatorConnectivity(validator.IPAddress))
                            {
                                validator.CheckFailCount++;

                                if (validator.CheckFailCount >= 3)
                                {
                                    // Remove from active pool
                                    Globals.NetworkValidators.TryRemove(validator.Address, out _);
                                    LogUtility.Log($"Validator {validator.Address} removed: repeated connectivity failures on ports {string.Join(",", requiredPorts)}", "ValidatorConnectivityMonitor");

                                    // If removed validator was a caster, we may need to handle re-selection
                                    if (Globals.IsBlockCaster && Globals.BlockCasters.Any(c => c.ValidatorAddress == validator.Address))
                                    {
                                        LogUtility.Log($"Caster {validator.Address} removed due to connectivity failure", "ValidatorConnectivityMonitor");
                                    }
                                }
                                else
                                {
                                    // Update fail count
                                    Globals.NetworkValidators[validator.Address] = validator;
                                }
                            }
                            else
                            {
                                // Reset fail count on successful check
                                if (validator.CheckFailCount > 0)
                                {
                                    validator.CheckFailCount = 0;
                                    Globals.NetworkValidators[validator.Address] = validator;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ErrorLogUtility.LogError($"Error checking validator {validator.Address}: {ex.Message}", "ValidatorConnectivityMonitor");
                        }

                        // Rate limit to prevent overwhelming the network
                        await Task.Delay(1000);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Connectivity monitor error: {ex.Message}", "ValidatorConnectivityMonitor");
                }
                finally
                {
                    ConnectivityCheckLock.Release();
                }

                // Check connectivity every 5 minutes
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        }

        /// <summary>
        /// Checks if a specific validator can produce blocks (has all required ports open).
        /// </summary>
        /// <param name="validatorAddress">Address of validator to check</param>
        /// <returns>True if validator can produce blocks, false otherwise</returns>
        public static async Task<bool> CanValidatorProduceBlock(string validatorAddress)
        {
            try
            {
                if (!Globals.NetworkValidators.TryGetValue(validatorAddress, out var validator))
                    return false;

                return await VerifyValidatorConnectivity(validator.IPAddress);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets connectivity status summary for monitoring and debugging.
        /// </summary>
        /// <returns>Connectivity status summary</returns>
        public static string GetConnectivityStatusSummary()
        {
            var totalValidators = Globals.NetworkValidators.Count;
            var failedValidators = Globals.FailedConnectivityValidators.Count;
            var requiredPorts = string.Join(",", GetRequiredPorts());

            return $"Connectivity Status - Total Validators: {totalValidators}, " +
                   $"Failed Connectivity: {failedValidators}, " +
                   $"Required Ports: {requiredPorts}";
        }
    }
}
