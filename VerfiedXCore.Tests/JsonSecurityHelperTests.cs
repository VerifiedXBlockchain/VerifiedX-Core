using Xunit;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Text;

namespace VerfiedXCore.Tests
{
    public class JsonSecurityHelperTests
    {
        [Fact]
        public void ValidateJsonInput_ValidJson_ReturnsValid()
        {
            // Arrange
            var validJson = "{\"Address\":\"testAddress\",\"PublicKey\":\"testKey\",\"BlockHeight\":123}";

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(validJson, "Test");

            // Assert
            Assert.True(result.IsValid);
            Assert.Equal("Test", result.Source);
        }

        [Fact]
        public void ValidateJsonInput_NullInput_ReturnsInvalid()
        {
            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(null, "Test");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("null or empty", result.Error);
        }

        [Fact]
        public void ValidateJsonInput_EmptyInput_ReturnsInvalid()
        {
            // Act
            var result = JsonSecurityHelper.ValidateJsonInput("", "Test");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("null or empty", result.Error);
        }

        [Fact]
        public void ValidateJsonInput_OversizedPayload_ReturnsInvalid()
        {
            // Arrange - Create a JSON payload larger than 1MB limit
            var largeString = new string('x', 1024 * 1024 + 1);
            var oversizedJson = $"[\"{largeString}\"]";

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(oversizedJson, "Test");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("exceeds maximum allowed", result.Error);
            Assert.True(result.PayloadSize > JsonSecurityHelper.MaxJsonSizeBytes);
        }

        [Fact]
        public void ValidateJsonInput_DeeplyNestedJson_ReturnsInvalid()
        {
            // Arrange - Create deeply nested JSON beyond the limit
            var deepJson = new StringBuilder();
            for (int i = 0; i < JsonSecurityHelper.MaxJsonDepth + 2; i++)
            {
                deepJson.Append("{\"level\":");
            }
            deepJson.Append("\"value\"");
            for (int i = 0; i < JsonSecurityHelper.MaxJsonDepth + 2; i++)
            {
                deepJson.Append("}");
            }

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(deepJson.ToString(), "Test");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("depth", result.Error);
            Assert.True(result.JsonDepth > JsonSecurityHelper.MaxJsonDepth);
        }

        [Fact]
        public void ValidateJsonInput_LargeArray_ReturnsInvalid()
        {
            // Arrange - Create array larger than collection limit
            var largeArray = new StringBuilder("[");
            for (int i = 0; i < JsonSecurityHelper.MaxCollectionSize + 1; i++)
            {
                if (i > 0) largeArray.Append(",");
                largeArray.Append($"\"{i}\"");
            }
            largeArray.Append("]");

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(largeArray.ToString(), "Test");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("array size", result.Error);
            Assert.True(result.CollectionSize > JsonSecurityHelper.MaxCollectionSize);
        }

        [Fact]
        public void ValidateJsonInput_MalformedJson_ReturnsInvalid()
        {
            // Arrange
            var malformedJson = "{\"Address\":\"test\",\"MissingCloseBrace\":true";

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(malformedJson, "Test");

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("Invalid JSON format", result.Error);
        }

        [Fact]
        public void DeserializeProofList_ValidProofList_ReturnsSuccess()
        {
            // Arrange
            var validProofListJson = @"[
                {
                    ""Address"": ""testAddress1"",
                    ""PublicKey"": ""testPublicKey1"",
                    ""BlockHeight"": 123,
                    ""PreviousBlockHash"": ""testPrevHash1"",
                    ""VRFNumber"": 456,
                    ""ProofHash"": ""testProofHash1"",
                    ""IPAddress"": ""192.168.1.1""
                }
            ]";

            // Act
            var result = JsonSecurityHelper.DeserializeProofList(validProofListJson, "Test");

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Data);
            Assert.Equal(1, result.Data.Count);
            Assert.Equal("testAddress1", result.Data[0].Address);
        }

        [Fact]
        public void DeserializeProofList_InvalidProofObject_ReturnsFailure()
        {
            // Arrange - Missing required fields
            var invalidProofListJson = @"[
                {
                    ""Address"": ""testAddress1"",
                    ""PublicKey"": """",
                    ""BlockHeight"": 123,
                    ""PreviousBlockHash"": ""testPrevHash1"",
                    ""VRFNumber"": 456,
                    ""ProofHash"": ""testProofHash1"",
                    ""IPAddress"": ""192.168.1.1""
                }
            ]";

            // Act
            var result = JsonSecurityHelper.DeserializeProofList(invalidProofListJson, "Test");

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Contains("Invalid proof objects found", result.ValidationResult.Error);
        }

        [Fact]
        public void SerializeWithLimits_ValidData_ReturnsSuccess()
        {
            // Arrange
            var proofList = new List<Proof>
            {
                new Proof
                {
                    Address = "testAddress",
                    PublicKey = "testPublicKey",
                    BlockHeight = 123,
                    PreviousBlockHash = "testPrevHash",
                    VRFNumber = 456,
                    ProofHash = "testProofHash",
                    IPAddress = "192.168.1.1"
                }
            };

            // Act
            var result = JsonSecurityHelper.SerializeWithLimits(proofList, "Test");

            // Assert
            Assert.True(result.IsSuccess);
            Assert.False(string.IsNullOrEmpty(result.Json));
            Assert.True(result.PayloadSize > 0);
            Assert.True(result.PayloadSize < JsonSecurityHelper.MaxResponseSizeBytes);
        }

        [Fact]
        public void JsonSecurityHelper_DepthCalculation_AccurateResults()
        {
            // Test that depth calculation works correctly for nested structures
            var shallowJson = "{\"level1\": \"value\"}";
            var deepJson = "{\"level1\": {\"level2\": {\"level3\": {\"level4\": {\"level5\": \"value\"}}}}}";

            var shallowResult = JsonSecurityHelper.ValidateJsonInput(shallowJson, "Test");
            var deepResult = JsonSecurityHelper.ValidateJsonInput(deepJson, "Test");

            Assert.True(shallowResult.IsValid);
            Assert.False(deepResult.IsValid);
            Assert.True(deepResult.JsonDepth > JsonSecurityHelper.MaxJsonDepth);
        }

        [Fact]
        public void JsonSecurityHelper_ConfigurableLimits_CorrectValues()
        {
            // Verify that the security limits are set to expected values
            Assert.Equal(1024 * 1024, JsonSecurityHelper.MaxJsonSizeBytes); // 1MB
            Assert.Equal(1000, JsonSecurityHelper.MaxCollectionSize);
            Assert.Equal(5, JsonSecurityHelper.MaxJsonDepth);
            Assert.Equal(5 * 1024 * 1024, JsonSecurityHelper.MaxResponseSizeBytes); // 5MB
        }
    }
}
