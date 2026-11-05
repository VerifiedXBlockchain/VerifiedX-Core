using System.Collections.Generic;
using System.Text;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Trillium;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The token contract source the node writes: TokenMintable comes from the token's mintable setting (not its voting
    /// setting), and GetTokenFunctions() returns the functions joined by "|->" with no stray quote.
    /// </summary>
    public class TokenSourceGeneratorTests
    {
        private static string Write(bool voting, bool burnable, bool mintable)
        {
            var scMain = new SmartContractMain
            {
                SmartContractUID = "2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a:1700000000",
                Name = "Gen Token",
                Description = "generator test",
                MinterAddress = "RFixtureMinterAddressxxxxxxxxxxxxx",
                MinterName = "fixture",
                IsPublic = true,
                SCVersion = Globals.SCVersion,
                IsMinter = true,
                IsPublished = false,
                IsToken = true,
                SmartContractAsset = new SmartContractAsset { Name = "fixture_asset", Location = "default", AssetAuthorName = "fixture", FileSize = 0 },
                Features = new List<SmartContractFeatures>
                {
                    new SmartContractFeatures
                    {
                        FeatureName = FeatureName.Token,
                        FeatureFeatures = Newtonsoft.Json.Linq.JObject.FromObject(new TokenFeature
                        {
                            TokenName = "Gen Token", TokenTicker = "GEN", TokenDecimalPlaces = 2, TokenSupply = 0,
                            TokenBurnable = burnable, TokenVoting = voting, TokenMintable = mintable,
                        }),
                    },
                },
            };
            var (scText, _, _) = SmartContractWriterService.WriteSmartContract(scMain).GetAwaiter().GetResult();
            Assert.False(string.IsNullOrWhiteSpace(scText) || scText.StartsWith("Failed"), scText);
            return scText;
        }

        private static (string Mintable, string Functions) Run(string scText)
        {
            var repl = new TrilliumRepl();
            repl.Run("#reset");
            repl.Run(ContractClock.Prepare(scText));
            return (repl.Run("TokenMintable").Value.ToString()!, repl.Run("GetTokenFunctions()").Value.ToString()!);
        }

        [Fact]
        public void VotingToken_ThatIsNotMintable_IsWrittenAsNotMintable()
        {
            var text = Write(voting: true, burnable: false, mintable: false);

            Assert.Contains("let TokenMintable = false", text);
            Assert.Equal("False", Run(text).Mintable, ignoreCase: true);
        }

        [Fact]
        public void MintableToken_WithoutVoting_IsWrittenAsMintable()
        {
            var text = Write(voting: false, burnable: false, mintable: true);

            Assert.Contains("let TokenMintable = true", text);
            Assert.Equal("True", Run(text).Mintable, ignoreCase: true);
        }

        [Theory]
        [InlineData(false, false, false, "TokenTransfer()|->TokenDeploy()")]
        [InlineData(true, true, true, "TokenTransfer()|->TokenDeploy()|->TokenVote()|->TokenCreateVote()|->TokenBurn()|->TokenMint()")]
        [InlineData(false, true, false, "TokenTransfer()|->TokenDeploy()|->TokenBurn()")]
        public void GetTokenFunctions_JoinsTheFunctionsCleanly(bool voting, bool burnable, bool mintable, string expected)
        {
            var text = Write(voting, burnable, mintable);

            Assert.Equal(expected, Run(text).Functions);
            Assert.DoesNotContain("\"\"TokenDeploy()", text);
        }
    }
}
