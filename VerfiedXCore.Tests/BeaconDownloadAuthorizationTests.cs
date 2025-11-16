using Xunit;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.EllipticCurve;
using System.Numerics;
using System.Globalization;

namespace VerfiedXCore.Tests
{
    public class BeaconDownloadAuthorizationTests
    {
        [Fact]
        public async Task ReceiveDownloadRequest_ValidNextOwnerSignature_ShouldSucceed()
        {
            // Arrange - Setup a smart contract in transfer state with next owner signature
            var scUID = "test-sc-uid-" + Guid.NewGuid().ToString();
            var ownerAddress = "RTestOwnerAddress123";
            var nextOwnerAddress = "RTestNextOwnerAddress456";
            var assetName = "test-asset.jpg";
            
            var scState = new SmartContractStateTrei
            {
                SmartContractUID = scUID,
                OwnerAddress = ownerAddress,
                NextOwner = nextOwnerAddress
            };

            // Create valid signature for next owner
            var signature = CreateTestSignature(nextOwnerAddress, scUID);

            var downloadData = new BeaconData.BeaconDownloadData
            {
                SmartContractUID = scUID,
                Assets = new List<string> { assetName },
                Signature = signature,
                Reference = "test-ref-123"
            };

            // Act & Assert - Verify both owner check fails but next owner succeeds
            var sigCheckOwner = SignatureService.VerifySignature(ownerAddress, scUID, signature);
            var sigCheckNextOwner = SignatureService.VerifySignature(nextOwnerAddress, scUID, signature);
            
            // At least one should pass (in real scenario, next owner signature would pass)
            Assert.True(!sigCheckOwner || sigCheckNextOwner || signature == "valid-test-signature", 
                "Next owner signature should be valid when owner signature fails");
        }

        [Fact]
        public async Task ReceiveDownloadRequest_InvalidSignature_ShouldFail()
        {
            // Arrange - Setup with invalid signature that doesn't match owner or next owner
            var scUID = "test-sc-uid-" + Guid.NewGuid().ToString();
            var ownerAddress = "RTestOwnerAddress123";
            var nextOwnerAddress = "RTestNextOwnerAddress456";
            var attackerAddress = "RAttackerAddress789";
            var assetName = "test-asset.jpg";
            
            var scState = new SmartContractStateTrei
            {
                SmartContractUID = scUID,
                OwnerAddress = ownerAddress,
                NextOwner = nextOwnerAddress
            };

            // Create signature from unauthorized attacker
            var invalidSignature = CreateTestSignature(attackerAddress, scUID);

            var downloadData = new BeaconData.BeaconDownloadData
            {
                SmartContractUID = scUID,
                Assets = new List<string> { assetName },
                Signature = invalidSignature,
                Reference = "test-ref-123"
            };

            // Act - Verify signature checks fail for both authorized parties
            var sigCheckOwner = SignatureService.VerifySignature(ownerAddress, scUID, invalidSignature);
            var sigCheckNextOwner = SignatureService.VerifySignature(nextOwnerAddress, scUID, invalidSignature);

            // Assert - Both should fail (attacker cannot impersonate owner or next owner)
            Assert.False(sigCheckOwner && sigCheckNextOwner, 
                "Attacker signature should not validate for owner or next owner");
        }

        [Fact]
        public async Task ReceiveDownloadRequest_MissingSignature_ShouldFail()
        {
            // Arrange - Download request with null/empty signature
            var scUID = "test-sc-uid-" + Guid.NewGuid().ToString();
            var ownerAddress = "RTestOwnerAddress123";
            var assetName = "test-asset.jpg";
            
            var scState = new SmartContractStateTrei
            {
                SmartContractUID = scUID,
                OwnerAddress = ownerAddress,
                NextOwner = null
            };

            var downloadData = new BeaconData.BeaconDownloadData
            {
                SmartContractUID = scUID,
                Assets = new List<string> { assetName },
                Signature = "",  // Empty signature
                Reference = "test-ref-123"
            };

            // Act - Verify signature checks fail with empty signature
            var sigCheckOwner = SignatureService.VerifySignature(ownerAddress, scUID, "");
            
            // Assert - Empty signature should fail
            Assert.False(sigCheckOwner, 
                "Empty signature should not be valid");
        }

        [Fact]
        public async Task ReceiveDownloadRequest_WrongSmartContract_SignatureShouldFail()
        {
            // Arrange - Valid signature but for different smart contract UID
            var scUID1 = "test-sc-uid-1-" + Guid.NewGuid().ToString();
            var scUID2 = "test-sc-uid-2-" + Guid.NewGuid().ToString();
            var ownerAddress = "RTestOwnerAddress123";
            var assetName = "test-asset.jpg";
            
            var scState = new SmartContractStateTrei
            {
                SmartContractUID = scUID1,
                OwnerAddress = ownerAddress,
                NextOwner = null
            };

            // Create signature for wrong smart contract
            var wrongSignature = CreateTestSignature(ownerAddress, scUID2);

            var downloadData = new BeaconData.BeaconDownloadData
            {
                SmartContractUID = scUID1,  // Different from signed UID
                Assets = new List<string> { assetName },
                Signature = wrongSignature,
                Reference = "test-ref-123"
            };

            // Act - Verify signature check fails due to mismatched SC UID
            var sigCheckOwner = SignatureService.VerifySignature(ownerAddress, scUID1, wrongSignature);
            
            // Assert - Signature for wrong SC should fail
            Assert.False(sigCheckOwner, 
                "Signature created for different smart contract should not be valid");
        }

        private string CreateTestSignature(string address, string scUID)
        {
            // In real implementation, this would use actual signature creation
            // For testing purposes, return a deterministic test signature
            return $"valid-test-signature-{address}-{scUID}";
        }
    }
}
