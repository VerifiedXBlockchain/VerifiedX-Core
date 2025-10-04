using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Models;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// Provides secure JSON deserialization with configurable limits and validation
    /// </summary>
    public static class JsonSecurityHelper
    {
        // Configurable limits - can be moved to configuration file
        public static readonly int MaxJsonSizeBytes = 1024 * 1024; // 1MB
        public static readonly int MaxCollectionSize = 1000;
        public static readonly int MaxJsonDepth = 5;
        public static readonly int MaxResponseSizeBytes = 5 * 1024 * 1024; // 5MB for responses

        // Secure JSON serializer settings
        private static readonly JsonSerializerSettings SecureSettings = new JsonSerializerSettings
        {
            // Disable type name handling to prevent polymorphic type confusion attacks
            TypeNameHandling = TypeNameHandling.None,
            // Set maximum depth to prevent deeply nested JSON bombs
            MaxDepth = MaxJsonDepth,
            // Ignore metadata to prevent injection
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            // Don't parse dates automatically to prevent format confusion
            DateParseHandling = DateParseHandling.None,
            // Ignore missing members to be more resilient
            MissingMemberHandling = MissingMemberHandling.Ignore,
            // Don't throw on null values
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Validates JSON string before deserialization
        /// </summary>
        public static ValidationResult ValidateJsonInput(string jsonInput, string source = "Unknown")
        {
            try
            {
                // Check for null or empty input
                if (string.IsNullOrWhiteSpace(jsonInput))
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        Error = "JSON input is null or empty",
                        Source = source
                    };
                }

                // Check size limit
                var jsonBytes = Encoding.UTF8.GetBytes(jsonInput);
                if (jsonBytes.Length > MaxJsonSizeBytes)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        Error = $"JSON payload size ({jsonBytes.Length} bytes) exceeds maximum allowed ({MaxJsonSizeBytes} bytes)",
                        Source = source,
                        PayloadSize = jsonBytes.Length
                    };
                }

                // Validate JSON structure and depth
                try
                {
                    var token = JToken.Parse(jsonInput);
                    var depth = GetTokenDepth(token);
                    
                    if (depth > MaxJsonDepth)
                    {
                        return new ValidationResult
                        {
                            IsValid = false,
                            Error = $"JSON depth ({depth}) exceeds maximum allowed ({MaxJsonDepth})",
                            Source = source,
                            JsonDepth = depth
                        };
                    }

                    // Check for arrays and their sizes
                    if (token is JArray array && array.Count > MaxCollectionSize)
                    {
                        return new ValidationResult
                        {
                            IsValid = false,
                            Error = $"JSON array size ({array.Count}) exceeds maximum allowed ({MaxCollectionSize})",
                            Source = source,
                            CollectionSize = array.Count
                        };
                    }

                    return new ValidationResult { IsValid = true, Source = source };
                }
                catch (JsonReaderException ex)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        Error = $"Invalid JSON format: {ex.Message}",
                        Source = source
                    };
                }
            }
            catch (Exception ex)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Error = $"Validation error: {ex.Message}",
                    Source = source
                };
            }
        }

        /// <summary>
        /// Securely deserialize JSON to List<Proof> with validation
        /// </summary>
        public static SecureDeserializationResult<List<Proof>> DeserializeProofList(string jsonInput, string source = "Unknown")
        {
            var validationResult = ValidateJsonInput(jsonInput, source);
            if (!validationResult.IsValid)
            {
                return new SecureDeserializationResult<List<Proof>>
                {
                    IsSuccess = false,
                    ValidationResult = validationResult
                };
            }

            try
            {
                var proofList = JsonConvert.DeserializeObject<List<Proof>>(jsonInput, SecureSettings);
                
                if (proofList == null)
                {
                    return new SecureDeserializationResult<List<Proof>>
                    {
                        IsSuccess = false,
                        ValidationResult = new ValidationResult
                        {
                            IsValid = false,
                            Error = "Deserialization resulted in null object",
                            Source = source
                        }
                    };
                }

                // Additional validation for proof list
                if (proofList.Count > MaxCollectionSize)
                {
                    return new SecureDeserializationResult<List<Proof>>
                    {
                        IsSuccess = false,
                        ValidationResult = new ValidationResult
                        {
                            IsValid = false,
                            Error = $"Proof list size ({proofList.Count}) exceeds maximum allowed ({MaxCollectionSize})",
                            Source = source,
                            CollectionSize = proofList.Count
                        }
                    };
                }

                // Validate individual proof objects
                var invalidProofs = new List<string>();
                foreach (var proof in proofList)
                {
                    if (!ValidateProofObject(proof, out string validationError))
                    {
                        invalidProofs.Add(validationError);
                    }
                }

                if (invalidProofs.Any())
                {
                    return new SecureDeserializationResult<List<Proof>>
                    {
                        IsSuccess = false,
                        ValidationResult = new ValidationResult
                        {
                            IsValid = false,
                            Error = $"Invalid proof objects found: {string.Join("; ", invalidProofs.Take(5))}",
                            Source = source
                        }
                    };
                }

                return new SecureDeserializationResult<List<Proof>>
                {
                    IsSuccess = true,
                    Data = proofList,
                    ValidationResult = validationResult
                };
            }
            catch (JsonException ex)
            {
                return new SecureDeserializationResult<List<Proof>>
                {
                    IsSuccess = false,
                    ValidationResult = new ValidationResult
                    {
                        IsValid = false,
                        Error = $"JSON deserialization failed: {ex.Message}",
                        Source = source
                    }
                };
            }
            catch (Exception ex)
            {
                return new SecureDeserializationResult<List<Proof>>
                {
                    IsSuccess = false,
                    ValidationResult = new ValidationResult
                    {
                        IsValid = false,
                        Error = $"Unexpected error during deserialization: {ex.Message}",
                        Source = source
                    }
                };
            }
        }

        /// <summary>
        /// Securely serialize data with size limits
        /// </summary>
        public static SecureSerializationResult SerializeWithLimits<T>(T data, string source = "Unknown")
        {
            try
            {
                var json = JsonConvert.SerializeObject(data, SecureSettings);
                var jsonBytes = Encoding.UTF8.GetBytes(json);

                if (jsonBytes.Length > MaxResponseSizeBytes)
                {
                    return new SecureSerializationResult
                    {
                        IsSuccess = false,
                        Error = $"Serialized response size ({jsonBytes.Length} bytes) exceeds maximum allowed ({MaxResponseSizeBytes} bytes)",
                        Source = source,
                        PayloadSize = jsonBytes.Length
                    };
                }

                return new SecureSerializationResult
                {
                    IsSuccess = true,
                    Json = json,
                    Source = source,
                    PayloadSize = jsonBytes.Length
                };
            }
            catch (Exception ex)
            {
                return new SecureSerializationResult
                {
                    IsSuccess = false,
                    Error = $"Serialization failed: {ex.Message}",
                    Source = source
                };
            }
        }

        /// <summary>
        /// Get the depth of a JSON token
        /// </summary>
        private static int GetTokenDepth(JToken token, int currentDepth = 0)
        {
            if (currentDepth > MaxJsonDepth)
                return currentDepth; // Early exit to prevent deep recursion

            int maxDepth = currentDepth;

            if (token.HasValues)
            {
                foreach (var child in token.Children())
                {
                    var childDepth = GetTokenDepth(child, currentDepth + 1);
                    maxDepth = Math.Max(maxDepth, childDepth);
                }
            }

            return maxDepth;
        }

        /// <summary>
        /// Validate individual proof object
        /// </summary>
        private static bool ValidateProofObject(Proof proof, out string error)
        {
            error = string.Empty;

            if (proof == null)
            {
                error = "Null proof object";
                return false;
            }

            if (string.IsNullOrWhiteSpace(proof.Address))
            {
                error = "Missing or empty Address";
                return false;
            }

            if (string.IsNullOrWhiteSpace(proof.PublicKey))
            {
                error = "Missing or empty PublicKey";
                return false;
            }

            if (string.IsNullOrWhiteSpace(proof.ProofHash))
            {
                error = "Missing or empty ProofHash";
                return false;
            }

            if (string.IsNullOrWhiteSpace(proof.PreviousBlockHash))
            {
                error = "Missing or empty PreviousBlockHash";
                return false;
            }

            if (proof.BlockHeight < 0)
            {
                error = "Invalid BlockHeight (negative value)";
                return false;
            }

            // Additional length checks to prevent extremely long strings
            if (proof.Address.Length > 100 || proof.PublicKey.Length > 500 || 
                proof.ProofHash.Length > 500 || proof.PreviousBlockHash.Length > 500)
            {
                error = "One or more fields exceed maximum length";
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Result of JSON validation
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Error { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int? PayloadSize { get; set; }
        public int? JsonDepth { get; set; }
        public int? CollectionSize { get; set; }
    }

    /// <summary>
    /// Result of secure deserialization
    /// </summary>
    public class SecureDeserializationResult<T>
    {
        public bool IsSuccess { get; set; }
        public T? Data { get; set; }
        public ValidationResult ValidationResult { get; set; } = new ValidationResult();
    }

    /// <summary>
    /// Result of secure serialization
    /// </summary>
    public class SecureSerializationResult
    {
        public bool IsSuccess { get; set; }
        public string Json { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int PayloadSize { get; set; }
    }
}
