﻿using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Utilities
{
    public class BlockVersionUtility
    {
        public static int GetBlockVersion(long height)
        {
            //testnet
            if(Globals.IsTestNet)
            {
                if (height >= Globals.V4Height)
                    return 4;
                if (height > Globals.V3Height)
                    return 3;
                if (height < 15)
                    return 2;
            }


            if (height >= Globals.V4Height)
                return 4;
            if (height > Globals.V3Height)
                return 3;
            else if (height > 294000)
                return 2;
            
            return 1;                        
        }

        public static async Task<bool> Version2Rules(Block block)
        {
            bool result = false;
            var leadAdjAddr = Globals.IsTestNet ? "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj" : "RBXpH37qVvNwzLjtcZiwEnb3aPNG815TUY";

            if (block.AdjudicatorSignature != null)
            {
                var sigResult = SignatureService.VerifySignature(leadAdjAddr, block.Hash, block.AdjudicatorSignature);
                result = sigResult;
            }

            return result;            
        }
        public static async Task<(bool, string)> Version3Rules(Block block)
        {
            if (!string.IsNullOrWhiteSpace(block.AdjudicatorSignature))
            {
                var ValidCount = 0;
                var AddressSignatures = block.AdjudicatorSignature.Split('|');
                var Addresses = new HashSet<string>();
                
                if(Globals.IsTestNet)
                {                    
                    foreach (var AddressSignature in AddressSignatures)
                    {
                        var split = AddressSignature.Split(':');
                        var (Address, Signature) = (split[0], split[1]);
                        if (!Globals.Signers.ContainsKey(Address))
                            return (false, "Signers Did Not Have Key.");
                        if (!(SignatureService.VerifySignature(Address, block.Hash, Signature)))
                            return (false, "Signature Failed to verify");
                        ValidCount++;
                        Addresses.Add(Address);
                    }
                    if (ValidCount == Addresses.Count && ValidCount >= Signer.Majority())
                        return (true, "");
                }

                foreach (var AddressSignature in AddressSignatures)
                {
                    var split = AddressSignature.Split(':');
                    var (Address, Signature) = (split[0], split[1]);
                    if (!Globals.Signers.ContainsKey(Address) && !Globals.RetiredSigners.ContainsKey(Address))
                        return (false, "Signers Did Not Have Key.");
                    if (!(SignatureService.VerifySignature(Address, block.Hash, Signature)))
                        return (false, "Signature Failed to verify");
                    ValidCount++;
                    Addresses.Add(Address);
                }
                if (ValidCount == Addresses.Count && ValidCount >= Signer.Majority())
                    return (true, "");
            }

            return (false, "Unknown Error.");
        }

        public static async Task<(bool, string)> Version4Rules(Block block)
        {
            try
            {
                var blockHeight = block.Height;
                var validatorAddress = block.Validator;
                var validatorProof = block.ValidatorAnswer;
                var validatorPubKey = block.ValidatorSignature.Split(".")[1];

                var pubKeyDecoded = HexByteUtility.ByteToHex(Base58Utility.Base58Decode(validatorPubKey));

                //This is a patch for sigs with 0000 start point. remove lock after update has been achieved.
                if (pubKeyDecoded.Length / 2 == 63)
                {
                    pubKeyDecoded = "00" + pubKeyDecoded;
                }

                var pubKeyByte = HexByteUtility.HexToByte(pubKeyDecoded);
                var publicKey = PublicKey.fromString(pubKeyByte);

                var _PublicKey = "04" + ByteToHex(publicKey.toString());

                var isProofValid = await ProofUtility.VerifyProofAsync(_PublicKey, blockHeight, Globals.LastBlock.Hash, validatorProof);
                var result = isProofValid ? "" : "Proof Invalid.";

                return (isProofValid, result);
            }
            catch (Exception ex)
            {
                return (false, $"Unknown Error: {ex.ToString()}");
            }
        }

        private static string ByteToHex(byte[] pubkey)
        {
            return Convert.ToHexString(pubkey).ToLower();
        }
    }
}
