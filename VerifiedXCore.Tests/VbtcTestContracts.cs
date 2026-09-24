using System;
using System.Collections.Generic;
using System.Text;
using VerifiedXCore;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Real, decompilable smart contract bodies for tests that exercise consensus rules which read
    /// the state trei ContractData (VX-01: a vBTC V2 transfer / withdrawal / bridge lock must target
    /// a contract whose code declares the TokenizationV2 feature). Produced by the same writer the
    /// node uses (<see cref="SmartContractWriterService.WriteSmartContract"/>) and encoded exactly as
    /// the mint transaction encodes it (UTF-16 → GZip → Base64). Built once per test run.
    /// </summary>
    internal static class VbtcTestContracts
    {
        private static readonly Lazy<string> _vbtcV2 = new(() => Build(vbtcV2: true));
        private static readonly Lazy<string> _plainNft = new(() => Build(vbtcV2: false));

        /// <summary>ContractData of a vBTC V2 contract (TokenizationV2 feature).</summary>
        public static string VbtcV2ContractData => _vbtcV2.Value;

        /// <summary>ContractData of an ordinary NFT with no features — NOT a vBTC contract.</summary>
        public static string PlainNftContractData => _plainNft.Value;

        /// <summary>
        /// Builds a real contract body with an arbitrary embedded UID, minter and feature list,
        /// encoded exactly as the mint transaction's Data field. Used by VX-02 tests to author bodies
        /// whose embedded identity differs from the carrying transaction.
        /// </summary>
        public static string BuildContractData(string embeddedUid, string embeddedMinter, List<SmartContractFeatures>? features, string name = "Fixture")
        {
            var scMain = new SmartContractMain
            {
                SmartContractUID = embeddedUid,
                Name = name,
                Description = "test fixture",
                MinterAddress = embeddedMinter,
                MinterName = "fixture",
                IsPublic = true,
                SCVersion = Globals.SCVersion,
                IsMinter = true,
                IsPublished = false,
                IsToken = false,
                SmartContractAsset = new SmartContractAsset { Name = "fixture_asset", Location = "default", AssetAuthorName = "fixture", FileSize = 0 },
                Features = features,
            };
            var (scText, _, _) = SmartContractWriterService.WriteSmartContract(scMain).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(scText) || scText.StartsWith("Failed", StringComparison.Ordinal))
                throw new InvalidOperationException($"Fixture contract generation failed: {scText}");
            return Encoding.Unicode.GetBytes(scText).ToCompress().ToBase64();
        }

        /// <summary>A fungible-token deploy body (Token feature) with the given embedded identity and supply.</summary>
        public static string TokenContractData(string embeddedUid, string embeddedMinter, long supply, int decimals = 2)
            => BuildContractData(embeddedUid, embeddedMinter, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.Token,
                    FeatureFeatures = Newtonsoft.Json.Linq.JObject.FromObject(new TokenFeature
                    {
                        TokenName = "Fixture Token",
                        TokenTicker = "FXT",
                        TokenDecimalPlaces = decimals,
                        TokenSupply = supply,
                        TokenBurnable = false,
                        TokenVoting = false,
                        TokenMintable = false,
                    }),
                },
            }, name: "Fixture Token");

        private static string Build(bool vbtcV2)
        {
            var scMain = new SmartContractMain
            {
                SmartContractUID = vbtcV2 ? "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f:1700000000" : "1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e:1700000000",
                Name = vbtcV2 ? "vBTC V2 Test" : "Plain NFT Test",
                Description = "test fixture",
                MinterAddress = "xFixtureMinter000000000000000000000",
                MinterName = "fixture",
                IsPublic = true,
                SCVersion = Globals.SCVersion,
                IsMinter = true,
                IsPublished = false,
                IsToken = false,
                SmartContractAsset = new SmartContractAsset
                {
                    Name = "vbtc_v2_token",
                    Location = "default",
                    AssetAuthorName = "fixture",
                    FileSize = 0,
                },
                Features = vbtcV2
                    ? new List<SmartContractFeatures>
                    {
                        new SmartContractFeatures
                        {
                            FeatureName = FeatureName.TokenizationV2,
                            FeatureFeatures = new TokenizationV2Feature
                            {
                                AssetName = "vBTC V2 Test",
                                AssetTicker = "vBTC",
                                DepositAddress = "tb1pqqqqp399et2xygdj5xreqhjjvcmzhxw4aywxecjdzew6hylgvsesf3hn0c",
                                Version = 2,
                                ValidatorAddressesSnapshot = new List<string> { "xFixtureValidator00000000000000000" },
                                FrostGroupPublicKey = "02" + new string('a', 64),
                                RequiredThreshold = 51,
                                DKGProof = "fixture-proof",
                                ProofBlockHeight = 1,
                                CeremonyId = "fixture-ceremony",
                                ImageBase = "default",
                            },
                        },
                    }
                    : null,
            };

            var (scText, _, _) = SmartContractWriterService.WriteSmartContract(scMain).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(scText) || scText.StartsWith("Failed", StringComparison.Ordinal))
                throw new InvalidOperationException($"Fixture contract generation failed: {scText}");

            return Encoding.Unicode.GetBytes(scText).ToCompress().ToBase64();
        }
    }
}
