using System.Text.RegularExpressions;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// HAL-15 Security Fix: Input validation helper for P2P validator communications
    /// Provides validation and sanitization for handshake headers and NetworkValidator fields
    /// </summary>
    public static class InputValidationHelper
    {
        #region Constants - Field Length Limits
        
        // Handshake header limits
        public const int MAX_USERNAME_LENGTH = 50;
        public const int MAX_PUBLIC_KEY_LENGTH = 130;
        public const int MAX_ADDRESS_LENGTH = 42;
        public const int MAX_IP_ADDRESS_LENGTH = 45; // IPv6 support
        public const int MAX_SIGNATURE_LENGTH = 512;
        public const int MAX_SIGNATURE_MESSAGE_LENGTH = 300;
        public const int MAX_UNIQUE_NAME_LENGTH = 50;
        public const int MAX_NONCE_LENGTH = 64;
        public const int MAX_WALLET_VERSION_LENGTH = 20;
        
        // Collection limits
        public const int MAX_VALIDATOR_LIST_SIZE = 2000;
        public const int MAX_VALIDATOR_BROADCAST_SIZE = 2000;
        
        #endregion

        #region Handshake Header Validation

        /// <summary>
        /// Validates handshake headers from P2P connection
        /// </summary>
        public static HandshakeValidationResult ValidateHandshakeHeaders(
            string address, 
            string time, 
            string uName, 
            string publicKey, 
            string signature, 
            string walletVersion, 
            string nonce)
        {
            var result = new HandshakeValidationResult { IsValid = true };
            var errors = new List<string>();

            // Validate address - basic length and dangerous character check
            if (string.IsNullOrWhiteSpace(address))
            {
                errors.Add("Address is null or empty");
                result.IsValid = false;
            }
            else if (address.Length > MAX_ADDRESS_LENGTH)
            {
                errors.Add($"Address length ({address.Length}) exceeds maximum ({MAX_ADDRESS_LENGTH})");
                result.IsValid = false;
            }
            else if (address.Contains("<") || address.Contains(">") || address.Contains("&"))
            {
                errors.Add("Address contains invalid characters");
                result.IsValid = false;
            }

            // Validate time (numeric)
            if (string.IsNullOrWhiteSpace(time) || !long.TryParse(time, out _))
            {
                errors.Add("Time field is invalid or missing");
                result.IsValid = false;
            }

            // Validate uName (optional but if present must be valid)
            if (!string.IsNullOrWhiteSpace(uName) && uName.Length > MAX_USERNAME_LENGTH)
            {
                errors.Add($"Username length ({uName.Length}) exceeds maximum ({MAX_USERNAME_LENGTH})");
                result.IsValid = false;
            }

            // Validate publicKey
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                errors.Add("PublicKey is null or empty");
                result.IsValid = false;
            }
            else if (publicKey.Length > MAX_PUBLIC_KEY_LENGTH)
            {
                errors.Add($"PublicKey length ({publicKey.Length}) exceeds maximum ({MAX_PUBLIC_KEY_LENGTH})");
                result.IsValid = false;
            }

            // Validate signature
            if (string.IsNullOrWhiteSpace(signature))
            {
                errors.Add("Signature is null or empty");
                result.IsValid = false;
            }
            else if (signature.Length > MAX_SIGNATURE_LENGTH)
            {
                errors.Add($"Signature length ({signature.Length}) exceeds maximum ({MAX_SIGNATURE_LENGTH})");
                result.IsValid = false;
            }

            // Validate walletVersion (optional)
            if (!string.IsNullOrWhiteSpace(walletVersion) && walletVersion.Length > MAX_WALLET_VERSION_LENGTH)
            {
                errors.Add($"WalletVersion length ({walletVersion.Length}) exceeds maximum ({MAX_WALLET_VERSION_LENGTH})");
                result.IsValid = false;
            }

            // Validate nonce
            if (string.IsNullOrWhiteSpace(nonce))
            {
                errors.Add("Nonce is null or empty");
                result.IsValid = false;
            }
            else if (nonce.Length > MAX_NONCE_LENGTH)
            {
                errors.Add($"Nonce length ({nonce.Length}) exceeds maximum ({MAX_NONCE_LENGTH})");
                result.IsValid = false;
            }

            result.Errors = errors;
            return result;
        }

        #endregion

        #region NetworkValidator Validation

        /// <summary>
        /// Limits validator list to maximum broadcast size to prevent memory exhaustion
        /// </summary>
        public static List<NetworkValidator> LimitValidatorListForBroadcast(List<NetworkValidator> validators)
        {
            const int maxValidators = 2000; // Reasonable limit for broadcast
            
            if (validators?.Count > maxValidators)
            {
                return validators.Take(maxValidators).ToList();
            }
            
            return validators ?? new List<NetworkValidator>();
        }

        /// <summary>
        /// HAL-19 Fix: Validates block size for DoS protection
        /// </summary>
        public static (bool IsValid, string ErrorMessage) ValidateBlockSize(Block block, string sourceIP = "unknown")
        {
            try
            {
                if (block == null)
                {
                    return (false, "Block is null");
                }

                // Check if block size exceeds configured maximum
                if (block.Size > Globals.MaxBlockSizeBytes)
                {
                    var errorMsg = $"Block size {block.Size} bytes exceeds maximum allowed {Globals.MaxBlockSizeBytes} bytes";
                    ErrorLogUtility.LogError($"HAL-19 Security: {errorMsg} from {sourceIP}", "InputValidationHelper.ValidateBlockSize()");
                    return (false, errorMsg);
                }

                // Additional sanity checks
                if (block.Size < 0)
                {
                    return (false, "Block size cannot be negative");
                }

                // Check for reasonable minimum size (at least basic block structure)
                const int minBlockSize = 100; // Basic block header should be at least this size
                if (block.Size < minBlockSize)
                {
                    return (false, $"Block size {block.Size} is suspiciously small (minimum: {minBlockSize} bytes)");
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Exception during block size validation: {ex.Message}";
                ErrorLogUtility.LogError($"HAL-19 Security: {errorMsg} from {sourceIP}", "InputValidationHelper.ValidateBlockSize()");
                return (false, errorMsg);
            }
        }

        /// <summary>
        /// Validates a single NetworkValidator object
        /// </summary>
        public static NetworkValidatorValidationResult ValidateNetworkValidator(NetworkValidator validator)
        {
            var result = new NetworkValidatorValidationResult { IsValid = true };
            var errors = new List<string>();

            if (validator == null)
            {
                result.IsValid = false;
                result.Errors = new List<string> { "NetworkValidator is null" };
                return result;
            }

            // Validate Address
            if (string.IsNullOrWhiteSpace(validator.Address))
            {
                errors.Add("Address is null or empty");
                result.IsValid = false;
            }
            else if (validator.Address.Length > MAX_ADDRESS_LENGTH)
            {
                errors.Add($"Address length ({validator.Address.Length}) exceeds maximum ({MAX_ADDRESS_LENGTH})");
                result.IsValid = false;
            }

            // Validate IPAddress - basic validation
            if (!ValidateIPAddress(validator.IPAddress, errors))
                result.IsValid = false;

            // Validate UniqueName (optional)
            if (!string.IsNullOrWhiteSpace(validator.UniqueName) && validator.UniqueName.Length > MAX_UNIQUE_NAME_LENGTH)
            {
                errors.Add($"UniqueName length ({validator.UniqueName.Length}) exceeds maximum ({MAX_UNIQUE_NAME_LENGTH})");
                result.IsValid = false;
            }

            // Validate PublicKey
            if (string.IsNullOrWhiteSpace(validator.PublicKey))
            {
                errors.Add("PublicKey is null or empty");
                result.IsValid = false;
            }
            else if (validator.PublicKey.Length > MAX_PUBLIC_KEY_LENGTH)
            {
                errors.Add($"PublicKey length ({validator.PublicKey.Length}) exceeds maximum ({MAX_PUBLIC_KEY_LENGTH})");
                result.IsValid = false;
            }

            // Validate Signature
            if (string.IsNullOrWhiteSpace(validator.Signature))
            {
                errors.Add("Signature is null or empty");
                result.IsValid = false;
            }
            else if (validator.Signature.Length > MAX_SIGNATURE_LENGTH)
            {
                errors.Add($"Signature length ({validator.Signature.Length}) exceeds maximum ({MAX_SIGNATURE_LENGTH})");
                result.IsValid = false;
            }

            // Validate SignatureMessage
            if (string.IsNullOrWhiteSpace(validator.SignatureMessage))
            {
                errors.Add("SignatureMessage is null or empty");
                result.IsValid = false;
            }
            else if (validator.SignatureMessage.Length > MAX_SIGNATURE_MESSAGE_LENGTH)
            {
                errors.Add($"SignatureMessage length ({validator.SignatureMessage.Length}) exceeds maximum ({MAX_SIGNATURE_MESSAGE_LENGTH})");
                result.IsValid = false;
            }

            result.Errors = errors;
            return result;
        }

        /// <summary>
        /// Validates a list of NetworkValidators with size constraints
        /// </summary>
        public static NetworkValidatorListValidationResult ValidateNetworkValidatorList(List<NetworkValidator> validators)
        {
            var result = new NetworkValidatorListValidationResult { IsValid = true };
            var errors = new List<string>();

            if (validators == null)
            {
                result.IsValid = false;
                result.Errors = new List<string> { "Validator list is null" };
                return result;
            }

            // Check list size
            if (validators.Count > MAX_VALIDATOR_LIST_SIZE)
            {
                errors.Add($"Validator list size ({validators.Count}) exceeds maximum allowed ({MAX_VALIDATOR_LIST_SIZE})");
                result.IsValid = false;
                result.ShouldTruncate = true;
                result.TruncatedList = validators.Take(MAX_VALIDATOR_LIST_SIZE).ToList();
            }

            // Validate individual validators (sample first 10 for performance)
            var validatorsToCheck = validators.Take(10).ToList();
            int invalidCount = 0;

            foreach (var validator in validatorsToCheck)
            {
                var validatorResult = ValidateNetworkValidator(validator);
                if (!validatorResult.IsValid)
                {
                    invalidCount++;
                    if (invalidCount <= 3) // Only report first 3 errors
                    {
                        errors.Add($"Validator {validator?.Address ?? "unknown"}: {string.Join(", ", validatorResult.Errors)}");
                    }
                }
            }

            if (invalidCount > 3)
            {
                errors.Add($"... and {invalidCount - 3} more validators with validation errors");
            }

            if (invalidCount > validatorsToCheck.Count / 2)
            {
                result.IsValid = false;
            }

            result.Errors = errors;
            return result;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Validates IP address format - basic length and character check
        /// </summary>
        private static bool ValidateIPAddress(string ipAddress, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                errors.Add("IPAddress is null or empty");
                return false;
            }

            if (ipAddress.Length > MAX_IP_ADDRESS_LENGTH)
            {
                errors.Add($"IPAddress length ({ipAddress.Length}) exceeds maximum ({MAX_IP_ADDRESS_LENGTH})");
                return false;
            }

            // Basic sanity check - just ensure it's not obviously malicious
            if (ipAddress.Contains("<") || ipAddress.Contains(">") || ipAddress.Contains("&"))
            {
                errors.Add("IPAddress contains invalid characters");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sanitizes a string by removing potentially dangerous characters
        /// </summary>
        public static string SanitizeString(string input, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Remove dangerous characters
            var sanitized = input
                .Replace("<", "")
                .Replace(">", "")
                .Replace("&", "")
                .Replace("\"", "")
                .Replace("'", "");

            // Remove control characters
            sanitized = Regex.Replace(sanitized, @"[\x00-\x1F\x7F]", "");

            // Truncate if necessary
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength);

            return sanitized;
        }

        /// <summary>
        /// Limits and prioritizes validator list for broadcasting with advanced sorting
        /// </summary>
        public static List<NetworkValidator> LimitAndPrioritizeValidatorListAdvanced(List<NetworkValidator> validators)
        {
            if (validators == null || validators.Count <= MAX_VALIDATOR_BROADCAST_SIZE)
                return validators ?? new List<NetworkValidator>();

            // Priority order: 
            // 1. Lower CheckFailCount (more reliable)
            // 2. More ConfirmingSources (more trusted)
            // 3. Recent activity (by FirstAdvertised)
            var prioritized = validators
                .OrderBy(v => v.CheckFailCount)
                .ThenByDescending(v => v.ConfirmingSources?.Count ?? 0)
                .ThenByDescending(v => v.FirstAdvertised)
                .Take(MAX_VALIDATOR_BROADCAST_SIZE)
                .ToList();

            return prioritized;
        }

        #endregion

        #region Result Classes

        public class HandshakeValidationResult
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        public class NetworkValidatorValidationResult
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        public class NetworkValidatorListValidationResult
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
            public bool ShouldTruncate { get; set; }
            public List<NetworkValidator> TruncatedList { get; set; } = new List<NetworkValidator>();
        }

        #endregion
    }
}
