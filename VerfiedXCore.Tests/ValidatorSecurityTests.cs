using Xunit;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using System.Reflection;

namespace VerfiedXCore.Tests
{
    public class ValidatorSecurityTests
    {
        /// <summary>
        /// Test that ValidateAddressPublicKeyBinding correctly validates matching address-publicKey pairs
        /// </summary>
        [Fact]
        public void ValidateAddressPublicKeyBinding_ShouldAcceptValidPair()
        {
            // Arrange - Create a legitimate account
            var account = AccountData.CreateNewAccount(skipSave: true);
            
            // Act - Use reflection to call the private validation method
            var result = InvokeValidateAddressPublicKeyBinding(account.Address, account.PublicKey);
            
            // Assert - Valid pair should be accepted
            Assert.True(result, "Valid address-publicKey pair should be accepted");
        }

        /// <summary>
        /// HAL-023: Test that mismatched address-publicKey pairs are rejected
        /// This is the core vulnerability - attacker provides valid address but different publicKey
        /// </summary>
        [Fact]
        public void ValidateAddressPublicKeyBinding_ShouldRejectMismatchedPair()
        {
            // Arrange - Create two different accounts
            var account1 = AccountData.CreateNewAccount(skipSave: true);
            var account2 = AccountData.CreateNewAccount(skipSave: true);
            
            // Act - Try to use account1's address with account2's publicKey (spoofing attack)
            var result = InvokeValidateAddressPublicKeyBinding(account1.Address, account2.PublicKey);
            
            // Assert - Mismatched pair should be rejected
            Assert.False(result, "HAL-023: Mismatched address-publicKey pair should be rejected");
        }

        /// <summary>
        /// HAL-023: Test that empty/null address is rejected
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ValidateAddressPublicKeyBinding_ShouldRejectNullOrEmptyAddress(string invalidAddress)
        {
            // Arrange
            var account = AccountData.CreateNewAccount(skipSave: true);
            
            // Act
            var result = InvokeValidateAddressPublicKeyBinding(invalidAddress, account.PublicKey);
            
            // Assert
            Assert.False(result, "HAL-023: Null/empty address should be rejected");
        }

        /// <summary>
        /// HAL-023: Test that empty/null publicKey is rejected
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ValidateAddressPublicKeyBinding_ShouldRejectNullOrEmptyPublicKey(string invalidPublicKey)
        {
            // Arrange
            var account = AccountData.CreateNewAccount(skipSave: true);
            
            // Act
            var result = InvokeValidateAddressPublicKeyBinding(account.Address, invalidPublicKey);
            
            // Assert
            Assert.False(result, "HAL-023: Null/empty publicKey should be rejected");
        }

        /// <summary>
        /// HAL-023: Test that malformed publicKey is rejected
        /// </summary>
        [Theory]
        [InlineData("invalid_hex")]
        [InlineData("04zzzzz")]
        [InlineData("04")]
        public void ValidateAddressPublicKeyBinding_ShouldRejectMalformedPublicKey(string malformedPublicKey)
        {
            // Arrange
            var account = AccountData.CreateNewAccount(skipSave: true);
            
            // Act
            var result = InvokeValidateAddressPublicKeyBinding(account.Address, malformedPublicKey);
            
            // Assert
            Assert.False(result, "HAL-023: Malformed publicKey should be rejected");
        }

        /// <summary>
        /// HAL-023: Test address derivation consistency
        /// Verifies that deriving address from publicKey is deterministic
        /// </summary>
        [Fact]
        public void AddressDerivation_ShouldBeDeterministic()
        {
            // Arrange
            var account = AccountData.CreateNewAccount(skipSave: true);
            
            // Act - Derive address multiple times from same publicKey
            var derivedAddress1 = AccountData.GetHumanAddress(account.PublicKey);
            var derivedAddress2 = AccountData.GetHumanAddress(account.PublicKey);
            
            // Assert
            Assert.Equal(derivedAddress1, derivedAddress2);
            Assert.Equal(account.Address, derivedAddress1);
            Assert.Equal(account.Address, derivedAddress2);
        }

        /// <summary>
        /// HAL-023: Test that validation works correctly with multiple valid accounts
        /// </summary>
        [Fact]
        public void ValidateAddressPublicKeyBinding_ShouldWorkWithMultipleAccounts()
        {
            // Arrange - Create multiple accounts
            var accounts = new List<Account>();
            for (int i = 0; i < 5; i++)
            {
                accounts.Add(AccountData.CreateNewAccount(skipSave: true));
            }
            
            // Act & Assert - Each account's address-publicKey pair should validate
            foreach (var account in accounts)
            {
                var result = InvokeValidateAddressPublicKeyBinding(account.Address, account.PublicKey);
                Assert.True(result, $"Account {account.Address} should validate with its own publicKey");
            }
            
            // Act & Assert - Cross-validation should fail
            for (int i = 0; i < accounts.Count; i++)
            {
                for (int j = 0; j < accounts.Count; j++)
                {
                    if (i != j)
                    {
                        var result = InvokeValidateAddressPublicKeyBinding(
                            accounts[i].Address, 
                            accounts[j].PublicKey);
                        Assert.False(result, 
                            $"HAL-023: Account {i} address should NOT validate with Account {j} publicKey");
                    }
                }
            }
        }

        /// <summary>
        /// Helper method to invoke the private ValidateAddressPublicKeyBinding method via reflection
        /// </summary>
        private bool InvokeValidateAddressPublicKeyBinding(string address, string publicKey)
        {
            var type = typeof(ReserveBlockCore.P2P.P2PValidatorServer);
            var method = type.GetMethod("ValidateAddressPublicKeyBinding", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            if (method == null)
            {
                throw new InvalidOperationException("ValidateAddressPublicKeyBinding method not found");
            }
            
            try
            {
                var result = method.Invoke(null, new object[] { address, publicKey });
                return (bool)result;
            }
            catch (TargetInvocationException ex)
            {
                // If the method throws an exception, treat it as validation failure
                return false;
            }
        }
    }
}
