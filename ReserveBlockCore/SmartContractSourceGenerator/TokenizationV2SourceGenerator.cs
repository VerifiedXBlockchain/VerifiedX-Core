using ReserveBlockCore.Models.SmartContracts;
using System.Text;

namespace ReserveBlockCore.SmartContractSourceGenerator
{
    public class TokenizationV2SourceGenerator
    {
        public static async Task<(StringBuilder, StringBuilder)> Build(TokenizationV2Feature tknzV2, StringBuilder strBuild)
        {
            var appendChar = "\"|->\"";
            StringBuilder strTknzV2Bld = new StringBuilder();
            
            // Basic variables
            strBuild.AppendLine("let AssetName = \"" + tknzV2.AssetName + "\"");
            strBuild.AppendLine("let AssetTicker = \"" + tknzV2.AssetTicker + "\"");
            strBuild.AppendLine("let DepositAddress = \"" + tknzV2.DepositAddress + "\"");
            strBuild.AppendLine("let TokenizationVersion = " + tknzV2.Version.ToString());
            
            // FROST Data
            strBuild.AppendLine("let RequiredThreshold = " + tknzV2.RequiredThreshold.ToString());
            
            // DKG Proof Data
            strBuild.AppendLine("let ProofBlockHeight = " + tknzV2.ProofBlockHeight.ToString());

            // Function: Get Asset Info
            strTknzV2Bld.AppendLine("function GetAssetInfo() : string");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   return AssetName + " + appendChar + " + AssetTicker + " + appendChar + " + DepositAddress");
            strTknzV2Bld.AppendLine("}");

            // Function: Get FROST Group Public Key
            strTknzV2Bld.AppendLine("function GetFrostGroupPublicKey() : string");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   var frostGroupKey = \"" + tknzV2.FrostGroupPublicKey + "\"");
            strTknzV2Bld.AppendLine("   return (frostGroupKey)");
            strTknzV2Bld.AppendLine("}");

            // Function: Get Validator Snapshot
            strTknzV2Bld.AppendLine("function GetValidatorSnapshot() : string");
            strTknzV2Bld.AppendLine("{");
            if (tknzV2.ValidatorAddressesSnapshot != null && tknzV2.ValidatorAddressesSnapshot.Count > 0)
            {
                var validatorsString = string.Join(",", tknzV2.ValidatorAddressesSnapshot);
                strTknzV2Bld.AppendLine("   var validators = \"" + validatorsString + "\"");
            }
            else
            {
                strTknzV2Bld.AppendLine("   var validators = \"\"");
            }
            strTknzV2Bld.AppendLine("   return (validators)");
            strTknzV2Bld.AppendLine("}");

            // Function: Get Required Threshold
            strTknzV2Bld.AppendLine("function GetRequiredThreshold() : int");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   return RequiredThreshold");
            strTknzV2Bld.AppendLine("}");

            // Function: Get Ceremony ID
            strTknzV2Bld.AppendLine("function GetCeremonyId() : string");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   var ceremonyId = \"" + (tknzV2.CeremonyId ?? "") + "\"");
            strTknzV2Bld.AppendLine("   return (ceremonyId)");
            strTknzV2Bld.AppendLine("}");

            // Function: Get DKG Proof
            strTknzV2Bld.AppendLine("function GetDKGProof() : string");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   var proof = \"" + tknzV2.DKGProof + "\"");
            strTknzV2Bld.AppendLine("   var blockHeight = \"" + tknzV2.ProofBlockHeight.ToString() + "\"");
            strTknzV2Bld.AppendLine("   return (proof + " + appendChar + " + blockHeight)");
            strTknzV2Bld.AppendLine("}");

            // Function: Get Image Base
            strTknzV2Bld.AppendLine("function GetImageBase() : string");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   var imageBase = \"" + tknzV2.ImageBase + "\"");
            strTknzV2Bld.AppendLine("   return (imageBase)");
            strTknzV2Bld.AppendLine("}");

            // Function: Get Tokenization Version
            strTknzV2Bld.AppendLine("function GetTokenizationVersion() : int");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   return TokenizationVersion");
            strTknzV2Bld.AppendLine("}");

            // Function: Get IsS3C (S3C — Self-Sovereign Smart Contracts). Emitted as a string
            // so older contracts that lack this function decode to a null Value → IsS3C=false.
            strTknzV2Bld.AppendLine("function GetIsS3C() : string");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   var isS3C = \"" + (tknzV2.IsS3C ? "true" : "false") + "\"");
            strTknzV2Bld.AppendLine("   return (isS3C)");
            strTknzV2Bld.AppendLine("}");

            // Function: Get Linked Contract UID (companion → S3C back-pointer; empty if none)
            strTknzV2Bld.AppendLine("function GetLinkedContractUID() : string");
            strTknzV2Bld.AppendLine("{");
            strTknzV2Bld.AppendLine("   var linkedContractUID = \"" + (tknzV2.LinkedContractUID ?? "") + "\"");
            strTknzV2Bld.AppendLine("   return (linkedContractUID)");
            strTknzV2Bld.AppendLine("}");

            return (strBuild, strTknzV2Bld);
        }
    }
}
