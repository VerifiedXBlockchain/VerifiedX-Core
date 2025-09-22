using System.Numerics;
using Xunit;
using ReserveBlockCore.EllipticCurve;

namespace VerfiedXCore.Tests
{
    public class EcdsaTests
    {
        [Fact]
        public void Sign_ShouldNeverReturnZeroRComponent()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var message = "test message for r=0 edge case";

            // Act - Generate multiple signatures to test the retry logic
            for (int i = 0; i < 100; i++)
            {
                var signature = Ecdsa.sign(message + i.ToString(), privateKey);
                
                // Assert
                Assert.True(signature.r > 0, $"Signature r component should never be zero (iteration {i})");
                Assert.True(signature.r < privateKey.curve.N, $"Signature r component should be less than curve order (iteration {i})");
            }
        }

        [Fact]
        public void Sign_ShouldNeverReturnZeroSComponent()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var message = "test message for s=0 edge case";

            // Act - Generate multiple signatures to test the retry logic
            for (int i = 0; i < 100; i++)
            {
                var signature = Ecdsa.sign(message + i.ToString(), privateKey);
                
                // Assert
                Assert.True(signature.s > 0, $"Signature s component should never be zero (iteration {i})");
                Assert.True(signature.s < privateKey.curve.N, $"Signature s component should be less than curve order (iteration {i})");
            }
        }

        [Fact]
        public void Sign_ShouldGenerateValidSignatureComponents()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var message = "test message for valid signature";

            // Act
            var signature = Ecdsa.sign(message, privateKey);

            // Assert - Verify ECDSA standard compliance: 0 < r < N and 0 < s < N
            Assert.True(signature.r > BigInteger.Zero, "Signature r component must be greater than zero");
            Assert.True(signature.r < privateKey.curve.N, "Signature r component must be less than curve order");
            Assert.True(signature.s > BigInteger.Zero, "Signature s component must be greater than zero");
            Assert.True(signature.s < privateKey.curve.N, "Signature s component must be less than curve order");
        }

        [Fact]
        public void Sign_AndVerify_ShouldWorkCorrectly()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var publicKey = privateKey.publicKey();
            var message = "test message for sign and verify";

            // Act
            var signature = Ecdsa.sign(message, privateKey);
            var isValid = Ecdsa.verify(message, signature, publicKey);

            // Assert
            Assert.True(isValid, "Signature should be valid when verified with correct public key");
            Assert.True(signature.r > BigInteger.Zero, "Signature r component should be non-zero");
            Assert.True(signature.s > BigInteger.Zero, "Signature s component should be non-zero");
        }

        [Fact]
        public void Verify_ShouldRejectZeroRComponent()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var publicKey = privateKey.publicKey();
            var message = "test message";
            
            // Create a signature with r = 0 (invalid)
            var invalidSignature = new Signature(BigInteger.Zero, BigInteger.One);

            // Act
            var isValid = Ecdsa.verify(message, invalidSignature, publicKey);

            // Assert
            Assert.False(isValid, "Verification should reject signatures with r = 0");
        }

        [Fact]
        public void Verify_ShouldRejectZeroSComponent()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var publicKey = privateKey.publicKey();
            var message = "test message";
            
            // Create a signature with s = 0 (invalid)
            var invalidSignature = new Signature(BigInteger.One, BigInteger.Zero);

            // Act
            var isValid = Ecdsa.verify(message, invalidSignature, publicKey);

            // Assert
            Assert.False(isValid, "Verification should reject signatures with s = 0");
        }

        [Fact]
        public void Verify_ShouldRejectComponentsEqualToOrGreaterThanCurveOrder()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var publicKey = privateKey.publicKey();
            var message = "test message";
            var curveOrder = privateKey.curve.N;
            
            // Create signatures with components >= curve order (invalid)
            var invalidSignatureR = new Signature(curveOrder, BigInteger.One);
            var invalidSignatureS = new Signature(BigInteger.One, curveOrder);

            // Act
            var isValidR = Ecdsa.verify(message, invalidSignatureR, publicKey);
            var isValidS = Ecdsa.verify(message, invalidSignatureS, publicKey);

            // Assert
            Assert.False(isValidR, "Verification should reject signatures with r >= curve order");
            Assert.False(isValidS, "Verification should reject signatures with s >= curve order");
        }

        [Fact]
        public void Sign_MultipleIterations_ShouldAlwaysProduceValidComponents()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var baseMessage = "iteration test message";

            // Act & Assert - Test multiple iterations to ensure robustness
            for (int i = 0; i < 1000; i++)
            {
                var message = $"{baseMessage}_{i}";
                var signature = Ecdsa.sign(message, privateKey);
                
                // Verify both components are within valid range
                Assert.True(signature.r > BigInteger.Zero && signature.r < privateKey.curve.N,
                    $"Invalid r component in iteration {i}: r={signature.r}");
                Assert.True(signature.s > BigInteger.Zero && signature.s < privateKey.curve.N,
                    $"Invalid s component in iteration {i}: s={signature.s}");
                
                // Verify the signature is actually valid
                var publicKey = privateKey.publicKey();
                var isValid = Ecdsa.verify(message, signature, publicKey);
                Assert.True(isValid, $"Signature should be valid in iteration {i}");
            }
        }

        [Fact]
        public void Sign_DifferentCurves_ShouldProduceValidSignatures()
        {
            // Arrange
            var message = "test message for different curves";
            var curves = new[] { "secp256k1", "p256" };

            foreach (var curveName in curves)
            {
                // Act
                var privateKey = new PrivateKey(curveName);
                var publicKey = privateKey.publicKey();
                var signature = Ecdsa.sign(message, privateKey);

                // Assert
                Assert.True(signature.r > BigInteger.Zero, $"r should be non-zero for curve {curveName}");
                Assert.True(signature.s > BigInteger.Zero, $"s should be non-zero for curve {curveName}");
                Assert.True(signature.r < privateKey.curve.N, $"r should be less than order for curve {curveName}");
                Assert.True(signature.s < privateKey.curve.N, $"s should be less than order for curve {curveName}");
                
                var isValid = Ecdsa.verify(message, signature, publicKey);
                Assert.True(isValid, $"Signature should be valid for curve {curveName}");
            }
        }

        // HAL-04 Tests: Public Key Curve Validation
        
        [Fact]
        public void Verify_ShouldAcceptValidOnCurvePublicKey()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var publicKey = privateKey.publicKey();
            var message = "test message";
            var signature = Ecdsa.sign(message, privateKey);

            // Act
            var isValid = Ecdsa.verify(message, signature, publicKey);

            // Assert
            Assert.True(isValid, "Verification should accept valid on-curve public keys");
        }

        [Fact]
        public void Verify_ShouldRejectOffCurvePublicKey()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var curve = privateKey.curve;
            var message = "test message";
            var signature = Ecdsa.sign(message, privateKey);

            // Create an off-curve point (coordinates that don't satisfy the curve equation)
            var offCurveX = BigInteger.One;
            var offCurveY = BigInteger.One; // (1,1) is definitely not on secp256k1
            var offCurvePoint = new Point(offCurveX, offCurveY);
            var offCurvePublicKey = new PublicKey(offCurvePoint, curve);

            // Verify the point is indeed off-curve
            Assert.False(curve.contains(offCurvePoint), "Test point should be off-curve");

            // Act
            var isValid = Ecdsa.verify(message, signature, offCurvePublicKey);

            // Assert
            Assert.False(isValid, "Verification should reject off-curve public keys");
        }

        [Fact]
        public void Verify_ShouldRejectOffCurvePublicKey_DifferentCurves()
        {
            // Arrange
            var message = "test message for curve validation";
            var curves = new[] { "secp256k1", "p256" };

            foreach (var curveName in curves)
            {
                var privateKey = new PrivateKey(curveName);
                var curve = privateKey.curve;
                var signature = Ecdsa.sign(message, privateKey);

                // Create various off-curve points
                var offCurvePoints = new[]
                {
                    new Point(BigInteger.One, BigInteger.One), // (1,1)
                    new Point(BigInteger.Zero, BigInteger.One), // (0,1)
                    new Point(BigInteger.Parse("12345"), BigInteger.Parse("67890")) // Arbitrary coordinates
                };

                foreach (var offCurvePoint in offCurvePoints)
                {
                    // Verify the point is off-curve
                    Assert.False(curve.contains(offCurvePoint), 
                        $"Point ({offCurvePoint.x}, {offCurvePoint.y}) should be off-curve for {curveName}");

                    var offCurvePublicKey = new PublicKey(offCurvePoint, curve);

                    // Act
                    var isValid = Ecdsa.verify(message, signature, offCurvePublicKey);

                    // Assert
                    Assert.False(isValid, 
                        $"Verification should reject off-curve public key ({offCurvePoint.x}, {offCurvePoint.y}) for {curveName}");
                }
            }
        }

        [Fact]
        public void Verify_ShouldRejectInvalidCoordinates()
        {
            // Arrange
            var privateKey = new PrivateKey("secp256k1");
            var curve = privateKey.curve;
            var message = "test message";
            var signature = Ecdsa.sign(message, privateKey);

            // Create points with coordinates outside the valid field range
            var invalidPoints = new[]
            {
                new Point(curve.P, BigInteger.One), // x >= p
                new Point(BigInteger.One, curve.P), // y >= p
                new Point(-BigInteger.One, BigInteger.One), // negative x
                new Point(BigInteger.One, -BigInteger.One)  // negative y
            };

            foreach (var invalidPoint in invalidPoints)
            {
                var invalidPublicKey = new PublicKey(invalidPoint, curve);

                // Act
                var isValid = Ecdsa.verify(message, signature, invalidPublicKey);

                // Assert
                Assert.False(isValid, 
                    $"Verification should reject public key with invalid coordinates ({invalidPoint.x}, {invalidPoint.y})");
            }
        }

        [Fact]
        public void Verify_CombinedHAL03AndHAL04_ShouldWorkTogether()
        {
            // Arrange - Test that both HAL-03 and HAL-04 fixes work together
            var privateKey = new PrivateKey("secp256k1");
            var curve = privateKey.curve;
            var message = "combined test for HAL-03 and HAL-04";

            // Act - Generate multiple signatures to test HAL-03 fix (no r=0, s=0)
            for (int i = 0; i < 50; i++)
            {
                var signature = Ecdsa.sign(message + i.ToString(), privateKey);
                var publicKey = privateKey.publicKey();

                // Assert HAL-03: Valid signature components
                Assert.True(signature.r > BigInteger.Zero, $"r should be non-zero (iteration {i})");
                Assert.True(signature.s > BigInteger.Zero, $"s should be non-zero (iteration {i})");

                // Assert HAL-04: Valid verification with on-curve public key
                var isValid = Ecdsa.verify(message + i.ToString(), signature, publicKey);
                Assert.True(isValid, $"Valid signature should verify correctly (iteration {i})");

                // Assert HAL-04: Rejection of off-curve public key with same signature
                var offCurvePoint = new Point(BigInteger.One, BigInteger.One);
                var offCurvePublicKey = new PublicKey(offCurvePoint, curve);
                var offCurveResult = Ecdsa.verify(message + i.ToString(), signature, offCurvePublicKey);
                Assert.False(offCurveResult, $"Off-curve public key should be rejected (iteration {i})");
            }
        }
    }
}
