using System.Reflection;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Update(), Transfer(), Burn(), Evolve(), Devolve() and ChangeEvolveStateSpecific() are applied only from the array
    /// form of their Data. From Globals.ContractPayloadArrayHeight validation refuses any other shape, and the applies
    /// skip one without throwing, so the block's state is not reported as failed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ContractCallArrayFormTests
    {
        private const long Height = 5000;

        private static string Call(string function) => JsonConvert.SerializeObject(new { Function = function, ContractUID = "uid:1", Data = "body" });

        private static Transaction Tx(string data, TransactionType type = TransactionType.NFT_TX) =>
            new Transaction { FromAddress = "xFrom", ToAddress = "xTo", TransactionType = type, Data = data };

        private static void AtHeight(Action check)
        {
            var prior = Globals.ContractPayloadArrayHeight;
            try
            {
                Globals.ContractPayloadArrayHeight = Height;
                check();
            }
            finally { Globals.ContractPayloadArrayHeight = prior; }
        }

        [Theory]
        [InlineData("Update()")]
        [InlineData("Transfer()")]
        [InlineData("Burn()")]
        [InlineData("Evolve()")]
        [InlineData("Devolve()")]
        [InlineData("ChangeEvolveStateSpecific()")]
        public void FromTheHeight_TheObjectFormIsRefused(string function) => AtHeight(() =>
        {
            Assert.NotNull(LedgerIntegrityRules.ContractCallIsArrayForm(Tx(Call(function)), Height));
        });

        [Theory]
        [InlineData("Transfer()")]
        [InlineData("Burn()")]
        [InlineData("Evolve()")]
        public void FromTheHeight_TheArrayFormStillPasses(string function) => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.ContractCallIsArrayForm(Tx("[" + Call(function) + "]"), Height));
        });

        [Fact]
        public void BeforeTheHeight_TheObjectFormIsLeftToTheOlderRules() => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.ContractCallIsArrayForm(Tx(Call("Burn()")), Height - 1));
        });

        [Fact]
        public void FromTheHeight_FunctionsThatReadTheObjectFormAreUntouched() => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.ContractCallIsArrayForm(Tx(Call("TokenTransfer()"), TransactionType.FTKN_TX), Height));
            Assert.Null(LedgerIntegrityRules.ContractCallIsArrayForm(Tx(Call("Mint()"), TransactionType.NFT_MINT), Height));
        });

        [Fact]
        public void FromTheHeight_NonContractTypesAreUntouched() => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.ContractCallIsArrayForm(Tx(Call("Burn()"), TransactionType.TX), Height));
        });

        [Theory]
        [InlineData("TransferSmartContract", "Transfer()")]
        [InlineData("BurnSmartContract", "Burn()")]
        [InlineData("EvolveSC", "Evolve()")]
        [InlineData("DevolveSC", "Devolve()")]
        [InlineData("EvolveDevolveSpecific", "ChangeEvolveStateSpecific()")]
        public void TheApply_SkipsTheObjectFormWithoutThrowing(string method, string function)
        {
            var apply = typeof(StateData).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!;
            var ex = Record.Exception(() => apply.Invoke(null, new object[] { Tx(Call(function)) }));
            Assert.Null(ex);
        }

        [Fact]
        public void TheRuleShipsNotActive_UntilAHeightIsChosen()
        {
            Assert.Equal(999_999_999_999, Globals.ContractPayloadArrayHeight);
        }
    }
}
