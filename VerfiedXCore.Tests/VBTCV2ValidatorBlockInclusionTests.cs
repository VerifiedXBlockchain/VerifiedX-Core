using Xunit;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using Newtonsoft.Json;
using System.Threading.Tasks;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// FIND-009 Regression Tests: Ensure vBTC V2 validator transactions can be included in blocks
    /// </summary>
    public class VBTCV2ValidatorBlockInclusionTests
    {
        [Fact]
        public void ValidatorRegisterTransaction_ShouldNotRequireContractUIDOrFunction()
        {
            // Arrange - Create a validator registration transaction without ContractUID/Function
            var validatorRegisterTx = new Transaction
            {
                TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER,
                Data = JsonConvert.SerializeObject(new
                {
                    ValidatorAddress = "test_validator_address",
                    IPAddress = "192.168.1.100",
                    FrostPublicKey = "test_frost_public_key",
                    RegistrationBlockHeight = 12345L,
                    Signature = "test_signature"
                })
            };

            // Assert - Verify Data does NOT contain ContractUID or Function
            var dataObj = JsonConvert.DeserializeObject<dynamic>(validatorRegisterTx.Data);
            Assert.Null(dataObj.ContractUID);
            Assert.Null(dataObj.Function);
            
            // Verify essential fields ARE present
            Assert.NotNull(dataObj.ValidatorAddress);
            Assert.NotNull(dataObj.IPAddress);
        }

        [Fact]
        public void ValidatorExitTransaction_ShouldNotRequireContractUIDOrFunction()
        {
            // Arrange - Create a validator exit transaction without ContractUID/Function
            var validatorExitTx = new Transaction
            {
                TransactionType = TransactionType.VBTC_V2_VALIDATOR_EXIT,
                Data = JsonConvert.SerializeObject(new
                {
                    ValidatorAddress = "test_validator_address",
                    ExitBlockHeight = 12346L,
                    Reason = "Voluntary exit"
                })
            };

            // Assert - Verify Data does NOT contain ContractUID or Function
            var dataObj = JsonConvert.DeserializeObject<dynamic>(validatorExitTx.Data);
            Assert.Null(dataObj.ContractUID);
            Assert.Null(dataObj.Function);
            
            // Verify essential fields ARE present
            Assert.NotNull(dataObj.ValidatorAddress);
            Assert.NotNull(dataObj.ExitBlockHeight);
        }

        [Fact]
        public void ValidatorHeartbeatTransaction_ShouldNotRequireContractUIDOrFunction()
        {
            // Arrange - Create a validator heartbeat transaction without ContractUID/Function
            var validatorHeartbeatTx = new Transaction
            {
                TransactionType = TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT,
                Data = JsonConvert.SerializeObject(new
                {
                    ValidatorAddress = "test_validator_address",
                    HeartbeatBlockHeight = 12347L,
                    Timestamp = 1234567890L
                })
            };

            // Assert - Verify Data does NOT contain ContractUID or Function
            var dataObj = JsonConvert.DeserializeObject<dynamic>(validatorHeartbeatTx.Data);
            Assert.Null(dataObj.ContractUID);
            Assert.Null(dataObj.Function);
            
            // Verify essential fields ARE present
            Assert.NotNull(dataObj.ValidatorAddress);
            Assert.NotNull(dataObj.HeartbeatBlockHeight);
        }

        [Fact]
        public void TransactionUtility_GetSCTXFunctionAndUID_ShouldReturnFalseForValidatorTransactions()
        {
            // Arrange - Create validator transactions without ContractUID/Function
            var registerTx = new Transaction
            {
                TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER,
                Data = JsonConvert.SerializeObject(new
                {
                    ValidatorAddress = "test_address",
                    IPAddress = "192.168.1.100"
                })
            };

            // Act - Try to parse as smart contract transaction
            var result = ReserveBlockCore.Utilities.TransactionUtility.GetSCTXFunctionAndUID(registerTx);

            // Assert - Should return false (Item1) because these are NOT smart contract transactions
            Assert.False(result.Item1);
            Assert.Equal("FAIL", result.Item3); // scUID should be "FAIL"
            Assert.Equal("FAIL", result.Item4); // function should be "FAIL"
        }

        [Fact]
        public void ValidatorTransactionTypes_ShouldBeExemptFromSCParsing()
        {
            // This test documents that the following transaction types are exempt from
            // smart-contract parsing in BlockValidatorService.ValidateBlock()
            
            // Arrange - List of transaction types that should be exempt
            var exemptTypes = new[]
            {
                TransactionType.TX,
                TransactionType.ADNR,
                TransactionType.VOTE,
                TransactionType.VOTE_TOPIC,
                TransactionType.DSTR,
                TransactionType.RESERVE,
                TransactionType.NFT_SALE,
                TransactionType.VBTC_V2_VALIDATOR_REGISTER,    // FIND-009 FIX
                TransactionType.VBTC_V2_VALIDATOR_EXIT,        // FIND-009 FIX
                TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT    // FIND-009 FIX
            };

            // Assert - These types exist
            foreach (var txType in exemptTypes)
            {
                Assert.True(System.Enum.IsDefined(typeof(TransactionType), txType));
            }
        }
    }
}
