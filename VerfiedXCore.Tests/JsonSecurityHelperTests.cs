using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Text;

namespace VerfiedXCore.Tests
{
    [TestClass]
    public class JsonSecurityHelperTests
    {
        [TestMethod]
        public void ValidateJsonInput_ValidJson_ReturnsValid()
        {
            // Arrange
            var validJson = """{"Address":"testAddress","PublicKey":"testKey","BlockHeight":123}""";

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(validJson, "Test");

            // Assert
            Assert.IsTrue(result.IsValid);
            Assert.AreEqual("Test", result.Source);
        }

        [TestMethod]
        public void ValidateJsonInput_NullInput_ReturnsInvalid()
        {
            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(null, "Test");

            // Assert
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Error.Contains("null or empty"));
        }

        [TestMethod]
        public void ValidateJsonInput_EmptyInput_ReturnsInvalid()
        {
            // Act
            var result = JsonSecurityHelper.ValidateJsonInput("", "Test");

            // Assert
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Error.Contains("null or empty"));
        }

        [TestMethod]
        public void ValidateJsonInput_OversizedPayload_ReturnsInvalid()
        {
            // Arrange - Create a JSON payload larger than 1MB limit
            var largeString = new string('x', 1024 * 1024 + 1);
            var oversizedJson = $"""["{largeString}"]""";

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(oversizedJson, "Test");

            // Assert
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Error.Contains("exceeds maximum allowed"));
            Assert.IsTrue(result.PayloadSize > JsonSecurityHelper.MaxJsonSizeBytes);
        }

        [TestMethod]
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
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Error.Contains("depth"));
            Assert.IsTrue(result.JsonDepth > JsonSecurityHelper.MaxJsonDepth);
        }

        [TestMethod]
        public void ValidateJsonInput_LargeArray_ReturnsInvalid()
        {
            // Arrange - Create array larger than collection limit
            var largeArray = new StringBuilder("[");
            for (int i = 0; i < JsonSecurityHelper.MaxCollectionSize + 1; i++)
            {
                if (i > 0) largeArray.Append(",");
                largeArray.Append($""""{i}"""");
            }
            largeArray.Append("]");

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(largeArray.ToString(), "Test");

            // Assert
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Error.Contains("array size"));
            Assert.IsTrue(result.CollectionSize > JsonSecurityHelper.MaxCollectionSize);
        }

        [TestMethod]
        public void ValidateJsonInput_MalformedJson_ReturnsInvalid()
        {
            // Arrange
            var malformedJson = """{"Address":"test","MissingCloseBrace":true""";

            // Act
            var result = JsonSecurityHelper.ValidateJsonInput(malformedJson, "Test");

            // Assert
            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Error.Contains("Invalid JSON format"));
        }

        [TestMethod]
        public void DeserializeProofList_ValidProofList_ReturnsSuccess()
        {
            // Arrange
            var validProofListJson = """
            [
                {
                    "Address": "testAddress1",
                    "PublicKey": "testPublicKey1",
                    "BlockHeight": 123,
                    "PreviousBlockHash": "testPrevHash1",
                    "VRFNumber": 456,
                    "ProofHash": "testProofHash1",
                    "IPAddress": "192.168.1.1"
                },
                {
                    "Address": "testAddress2",
                    "PublicKey": "testPublicKey2",
                    "BlockHeight": 124,
                    "PreviousBlockHash": "testPrevHash2",
                    "VRFNumber": 789,
                    "ProofHash": "testProofHash2",
                    "IPAddress": "192.168.1.2"
                }
            ]
            """;

            // Act
            var result = JsonSecurityHelper.DeserializeProofList(validProofListJson, "Test");

            // Assert
            Assert.IsTrue(result.IsSuccess);
            Assert.IsNotNull(result.Data);
            Assert.AreEqual(2, result.Data.Count);
            Assert.AreEqual("testAddress1", result.Data[0].Address);
            Assert.AreEqual("testAddress2", result.Data[1].Address);
        }

        [TestMethod]
        public void DeserializeProofList_InvalidProofObject_ReturnsFailure()
        {
            // Arrange - Missing required fields
            var invalidProofListJson = """
            [
                {
                    "Address": "testAddress1",
                    "PublicKey": "",
                    "BlockHeight": 123,
                    "PreviousBlockHash": "testPrevHash1",
                    "VRFNumber": 456,
                    "ProofHash": "testProofHash1",
                    "IPAddress": "192.168.1.1"
                }
            ]
            """;

            // Act
            var result = JsonSecurityHelper.DeserializeProofList(invalidProofListJson, "Test");

            // Assert
            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(result.ValidationResult.Error.Contains("Invalid proof objects found"));
        }

        [TestMethod]
        public void DeserializeProofList_ExcessivelyLongFields_ReturnsFailure()
        {
            // Arrange - Fields that exceed length limits
            var longString = new string('x', 600); // Exceeds 500 char limit for some fields
            var invalidProofListJson = $@"
            [
                {{
                    ""Address"": ""testAddress1"",
                    ""PublicKey"": ""{longString}"",
                    ""BlockHeight"": 123,
                    ""PreviousBlockHash"": ""testPrevHash1"",
                    ""VRFNumber"": 456,
                    ""ProofHash"": ""testProofHash1"",
                    ""IPAddress"": ""192.168.1.1""
                }}
            ]
            ";

            // Act
            var result = JsonSecurityHelper.DeserializeProofList(invalidProofListJson, "Test");

            // Assert
            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(result.ValidationResult.Error.Contains("Invalid proof objects found"));
        }

        [TestMethod]
        public void DeserializeProofList_NegativeBlockHeight_ReturnsFailure()
        {
            // Arrange
            var invalidProofListJson = """
            [
                {
                    "Address": "testAddress1",
                    "PublicKey": "testPublicKey1",
                    "BlockHeight": -1,
                    "PreviousBlockHash": "testPrevHash1",
                    "VRFNumber": 456,
                    "ProofHash": "testProofHash1",
                    "IPAddress": "192.168.1.1"
                }
            ]
            """;

            // Act
            var result = JsonSecurityHelper.DeserializeProofList(invalidProofListJson, "Test");

            // Assert
            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(result.ValidationResult.Error.Contains("Invalid proof objects found"));
        }

        [TestMethod]
        public void DeserializeProofList_TooManyProofs_ReturnsFailure()
        {
            // Arrange - Create more proofs than allowed
            var proofList = new StringBuilder("[");
            for (int i = 0; i < JsonSecurityHelper.MaxCollectionSize + 1; i++)
            {
                if (i > 0) proofList.Append(",");
                proofList.Append($@"
                {{
                    ""Address"": ""testAddress{i}"",
                    ""PublicKey"": ""testPublicKey{i}"",
                    ""BlockHeight"": {i},
                    ""PreviousBlockHash"": ""testPrevHash{i}"",
                    ""VRFNumber"": {i + 100},
                    ""ProofHash"": ""testProofHash{i}"",
                    ""IPAddress"": ""192.168.1.{i % 255 + 1}""
                }}
                ");
            }
            proofList.Append("]");

            // Act
            var result = JsonSecurityHelper.DeserializeProofList(proofList.ToString(), "Test");

            // Assert
            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(result.ValidationResult.Error.Contains("exceeds maximum allowed"));
        }

        [TestMethod]
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
            Assert.IsTrue(result.IsSuccess);
            Assert.IsFalse(string.IsNullOrEmpty(result.Json));
            Assert.IsTrue(result.PayloadSize > 0);
            Assert.IsTrue(result.PayloadSize < JsonSecurityHelper.MaxResponseSizeBytes);
        }

        [TestMethod]
        public void SerializeWithLimits_ExcessivelyLargeData_ReturnsFailure()
        {
            // Arrange - Create data that would exceed response size limit
            var largeProofList = new List<Proof>();
            var largeString = new string('x', 10000); // Large field content

            // Add enough proofs to exceed the response size limit
            for (int i = 0; i < 1000; i++)
            {
                largeProofList.Add(new Proof
                {
                    Address = $"testAddress{i}",
                    PublicKey = largeString,
                    BlockHeight = i,
                    PreviousBlockHash = largeString,
                    VRFNumber = (uint)i,
                    ProofHash = largeString,
                    IPAddress = $"192.168.{i % 255}.{i % 255}"
                });
            }

            // Act
            var result = JsonSecurityHelper.SerializeWithLimits(largeProofList, "Test");

            // Assert
            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(result.Error.Contains("exceeds maximum allowed"));
            Assert.IsTrue(result.PayloadSize > JsonSecurityHelper.MaxResponseSizeBytes);
        }

        [TestMethod]
        public void JsonSecurityHelper_DepthCalculation_AccurateResults()
        {
            // Test that depth calculation works correctly for nested structures
            var shallowJson = """{"level1": "value"}""";
            var deepJson = """{"level1": {"level2": {"level3": {"level4": {"level5": "value"}}}}}""";

            var shallowResult = JsonSecurityHelper.ValidateJsonInput(shallowJson, "Test");
            var deepResult = JsonSecurityHelper.ValidateJsonInput(deepJson, "Test");

            Assert.IsTrue(shallowResult.IsValid);
            Assert.IsFalse(deepResult.IsValid);
            Assert.IsTrue(deepResult.JsonDepth > JsonSecurityHelper.MaxJsonDepth);
        }

        [TestMethod]
        public void JsonSecurityHelper_ConfigurableLimits_CorrectValues()
        {
            // Verify that the security limits are set to expected values
            Assert.AreEqual(1024 * 1024, JsonSecurityHelper.MaxJsonSizeBytes); // 1MB
            Assert.AreEqual(1000, JsonSecurityHelper.MaxCollectionSize);
            Assert.AreEqual(5, JsonSecurityHelper.MaxJsonDepth);
            Assert.AreEqual(5 * 1024 * 1024, JsonSecurityHelper.MaxResponseSizeBytes); // 5MB
        }
    }
}
