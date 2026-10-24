using System;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From DataOwnerBoundHeight an owner carried in a transaction's Data is the signed transaction's own address: a new
    /// shop's owner is its sender, and a token contract's new owner is the transaction's recipient, which the apply also
    /// stores. Below the height the data is taken as given.
    /// </summary>
    [Collection("DbContextSequential")]
    public class DataOwnerBoundTests : IDisposable
    {
        private const long Gate = 5_000;
        private readonly long _prior = Globals.DataOwnerBoundHeight;

        public DataOwnerBoundTests() => Globals.DataOwnerBoundHeight = Gate;

        public void Dispose() => Globals.DataOwnerBoundHeight = _prior;

        private static Transaction Tx() => new() { FromAddress = "RSENDER", ToAddress = "RRECIPIENT" };

        [Fact]
        public void DecShopCreate_InAnotherAddressesName_IsRefusedFromTheHeight()
        {
            var shop = new DecShop { OwnerAddress = "ROTHER" };
            Assert.Equal(TransactionValidatorService.DecShopOwnerReason, TransactionValidatorService.DecShopOwnerRefusal(Tx(), shop, Gate));
            Assert.Null(TransactionValidatorService.DecShopOwnerRefusal(Tx(), shop, Gate - 1));
        }

        [Fact]
        public void DecShopCreate_OwnedByItsSender_Passes()
        {
            Assert.Null(TransactionValidatorService.DecShopOwnerRefusal(Tx(), new DecShop { OwnerAddress = "RSENDER" }, Gate));
        }

        [Fact]
        public void TokenContractOwnerChange_ToAnotherThanTheRecipient_IsRefusedFromTheHeight()
        {
            Assert.Equal(TransactionValidatorService.TokenOwnerChangeReason, TransactionValidatorService.TokenOwnerChangeRefusal(Tx(), "ROTHER", Gate));
            Assert.Equal(TransactionValidatorService.TokenOwnerChangeReason, TransactionValidatorService.TokenOwnerChangeRefusal(Tx(), null, Gate));
            Assert.Null(TransactionValidatorService.TokenOwnerChangeRefusal(Tx(), "ROTHER", Gate - 1));
        }

        [Fact]
        public void TokenContractOwnerChange_ToTheRecipient_Passes()
        {
            Assert.Null(TransactionValidatorService.TokenOwnerChangeRefusal(Tx(), "RRECIPIENT", Gate));
        }
    }
}
