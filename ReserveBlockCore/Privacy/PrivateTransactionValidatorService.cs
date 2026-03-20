using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Privacy
{
    public static class PrivateTransactionValidatorService
    {
        public static async Task<(bool ok, string message)> VerifyPrivateTX(
            Transaction txRequest,
            bool blockDownloads,
            bool blockVerify,
            bool twSkipVerify,
            Dictionary<string, long>? processedNonces)
        {
            _ = blockVerify;
            _ = twSkipVerify;

            if (txRequest.Data != null && txRequest.Data.Length > Globals.MaxPrivateTxDataSize)
                return (false, $"Private transaction Data exceeds MaxPrivateTxDataSize ({Globals.MaxPrivateTxDataSize}).");

            if (!blockDownloads)
            {
                var stale = await TransactionData.IsTxTimestampStale(txRequest, allowHistorical: false);
                if (stale)
                    return (false, "The timestamp of this transaction is too old or too far in the future.");
            }

            if (!await TransactionValidatorService.VerifyTXSize(txRequest))
                return (false, "This transactions is too large. Max size allowed is 30 kb.");

            var txExist = Globals.MemBlocks.ContainsKey(txRequest.Hash);
            if (txExist)
            {
                var mempool = TransactionData.GetPool();
                if (mempool.Count() > 0)
                {
                    mempool.DeleteManySafe(x => x.Hash == txRequest.Hash);
                    TransactionData.ReleasePrivateMempoolNullifiersForTx(txRequest.Hash);
                }
                return (false, "This transactions has already been sent.");
            }

            if (txRequest.FromAddress.StartsWith("xRBX") || txRequest.ToAddress.StartsWith("xRBX"))
                return (false, "Privacy transactions cannot involve reserve (xRBX) addresses.");

            if (!PrivateTxPayloadCodec.TryDecode(txRequest.Data, out var payload, out var decErr))
                return (false, decErr ?? "Invalid private payload.");

            if (!payload!.TryValidateStructure(out var structErr))
                return (false, structErr ?? "Invalid private payload structure.");

            if (payload.NullsB64.Count > 0)
            {
                foreach (var n in payload.NullsB64)
                {
                    if (string.IsNullOrWhiteSpace(n))
                        return (false, "Nullifier entry is empty.");
                    if (NullifierService.IsNullifierSpentInDb(n, payload.Asset))
                        return (false, "Nullifier already spent on-chain.");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.Kind) && !ExpectedKindMatches(txRequest.TransactionType, payload.Kind))
                return (false, "PrivateTxPayload kind does not match transaction type.");

            if (PrivateTransactionTypes.IsTransparentShield(txRequest.TransactionType))
            {
                var shield = ValidateTransparentShield(txRequest, payload!, processedNonces);
                if (!shield.ok)
                    return shield;
            }
            else if (PrivateTransactionTypes.IsZkAuthorizedPrivate(txRequest.TransactionType))
            {
                var zk = ValidateZkPrivate(txRequest);
                if (!zk.ok)
                    return zk;
                var merkle = ValidatePayloadMerkleRootRecency(payload!);
                if (!merkle.ok)
                    return merkle;
            }
            else
            {
                return (false, "Unknown private transaction type.");
            }

            if (!ValidateVbtcPayloadFields(txRequest.TransactionType, payload, out var vbtcErr))
                return (false, vbtcErr ?? "Invalid vBTC private payload.");

            if (!VerifyPrivateHash(txRequest))
                return (false, "This transactions hash is not equal to the private hash.");

            if (PrivateTransactionTypes.IsTransparentShield(txRequest.TransactionType))
            {
                if (string.IsNullOrEmpty(txRequest.Signature))
                    return (false, "Signature cannot be null.");
                if (!SignatureService.VerifySignature(txRequest.FromAddress, txRequest.Hash, txRequest.Signature))
                    return (false, "Signature Failed to verify.");
            }
            else
            {
                if (txRequest.Signature != PrivacyConstants.PlonkSignatureSentinel)
                    return (false, "Private ZK transaction must use PLONK signature sentinel until proof verification is wired.");
            }

            if (!blockDownloads && !blockVerify && payload.NullsB64.Count > 0)
            {
                if (!MempoolNullifierTracker.TryRegisterForMempool(txRequest.Hash, payload.Asset, payload.NullsB64, out var mErr))
                    return (false, mErr ?? "Mempool nullifier conflict.");
            }

            return (true, "Transaction has been verified.");
        }

        private static bool ValidateVbtcPayloadFields(TransactionType t, PrivateTxPayload payload, out string? err)
        {
            err = null;
            if (t == TransactionType.VBTC_V2_SHIELD)
            {
                if (string.IsNullOrWhiteSpace(payload.VbtcContractUid))
                {
                    err = "VBTC_V2_SHIELD requires vbtc_uid in payload.";
                    return false;
                }
                if (payload.VbtcTransparentAmount is not > 0)
                {
                    err = "VBTC_V2_SHIELD requires positive vbtc_amt in payload.";
                    return false;
                }
            }
            if (t == TransactionType.VBTC_V2_UNSHIELD)
            {
                if (string.IsNullOrWhiteSpace(payload.VbtcContractUid))
                {
                    err = "VBTC_V2_UNSHIELD requires vbtc_uid in payload.";
                    return false;
                }
                if (payload.VbtcTransparentAmount is not > 0)
                {
                    err = "VBTC_V2_UNSHIELD requires positive vbtc_amt in payload.";
                    return false;
                }
            }
            return true;
        }

        private static bool ExpectedKindMatches(TransactionType t, string kind)
        {
            var k = kind.Trim().ToLowerInvariant();
            return t switch
            {
                TransactionType.VFX_SHIELD or TransactionType.VBTC_V2_SHIELD => k is "shield" or "t2z",
                TransactionType.VFX_UNSHIELD or TransactionType.VBTC_V2_UNSHIELD => k is "unshield" or "z2t",
                TransactionType.VFX_PRIVATE_TRANSFER or TransactionType.VBTC_V2_PRIVATE_TRANSFER => k is "private_transfer" or "z2z",
                _ => true
            };
        }

        private static (bool ok, string message) ValidatePayloadMerkleRootRecency(PrivateTxPayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.MerkleRootB64))
                return (true, "");

            try
            {
                var st = ShieldedPoolService.GetState(payload.Asset);
                if (st == null || string.IsNullOrEmpty(st.CurrentMerkleRoot))
                    return (false, "Private payload merkle_root is set but shielded pool has no Merkle state for this asset.");

                if (!string.Equals(st.CurrentMerkleRoot.Trim(), payload.MerkleRootB64.Trim(), StringComparison.Ordinal))
                    return (false, "Private payload merkle_root does not match the current shielded pool Merkle root.");

                // Recency vs chain tip requires anchoring roots per block height (Phase 3). Globals.MaxMerkleRootAge reserved for that.
                return (true, "");
            }
            catch
            {
                return (false, "Could not verify Merkle root recency against DB_Privacy.");
            }
        }

        private static (bool ok, string message) ValidateTransparentShield(
            Transaction txRequest,
            PrivateTxPayload payload,
            Dictionary<string, long>? processedNonces)
        {
            if (txRequest.FromAddress == "Coinbase_BlkRwd" || txRequest.FromAddress == "Coinbase_TrxFees")
                return (false, "Invalid private shield from address.");

            if (!AddressValidateUtility.ValidateAddress(txRequest.FromAddress))
                return (false, "From Address failed to validate");

            if (txRequest.ToAddress != PrivacyConstants.ShieldedPoolAddress)
                return (false, "Shield transaction ToAddress must be Shielded_Pool.");

            if (Globals.LastBlock.Height > Globals.TXHeightRule1 && txRequest.Amount <= 0.0M)
                return (false, "Amount cannot be less than or equal to zero.");

            if (txRequest.TransactionType == TransactionType.VFX_SHIELD
                && Globals.LastBlock.Height > Globals.TXHeightRule1
                && txRequest.Amount < Globals.MinShieldAmountVFX)
                return (false, $"VFX shield amount must be at least {Globals.MinShieldAmountVFX}.");

            if (txRequest.TransactionType == TransactionType.VBTC_V2_SHIELD)
            {
                if (txRequest.Amount != 0.0M)
                    return (false, "VBTC shield must carry token amount in Data payload; transparent Amount must be 0.");
                if (Globals.LastBlock.Height > Globals.TXHeightRule1
                    && payload.VbtcTransparentAmount is not > 0)
                    return (false, "VBTC shield requires positive vbtc_amt in payload.");
                if (Globals.LastBlock.Height > Globals.TXHeightRule1
                    && payload.VbtcTransparentAmount < Globals.MinShieldAmountVBTC)
                    return (false, $"VBTC shield vbtc_amt must be at least {Globals.MinShieldAmountVBTC}.");
            }

            if (txRequest.Fee <= 0)
                return (false, "Fee cannot be less than or equal to zero.");

            if (Globals.LastBlock.Height > Globals.TXHeightRule2 && txRequest.Fee < Globals.MinFeePerKB)
                return (false, "Fee cannot be less than 0.000003 VFX");

            var from = StateData.GetSpecificAccountStateTrei(txRequest.FromAddress);
            if (from == null)
                return (false, "This is a new account with no balance, or your wallet does not have all the blocks in the chain.");

            if (from.Balance < txRequest.Amount + txRequest.Fee)
                return (false, "The balance of this account is less than the amount being sent.");

            if (Globals.LastBlock.Height > Globals.TXHeightRule4
                && txRequest.FromAddress != "Coinbase_BlkRwd"
                && txRequest.FromAddress != "Coinbase_TrxFees")
            {
                long expectedNonce;
                if (processedNonces != null && processedNonces.ContainsKey(txRequest.FromAddress))
                    expectedNonce = processedNonces[txRequest.FromAddress];
                else
                    expectedNonce = from.Nonce;

                if (txRequest.Nonce != expectedNonce)
                    return (false, $"Invalid transaction nonce. Expected: {expectedNonce}, Received: {txRequest.Nonce}");

                if (processedNonces != null)
                    processedNonces[txRequest.FromAddress] = expectedNonce + 1;
            }

            return (true, "");
        }

        private static (bool ok, string message) ValidateZkPrivate(Transaction txRequest)
        {
            if (txRequest.Fee != 0)
                return (false, "Private ZK transactions must have Fee 0 (fee is burned inside the proof in later phases).");

            if (txRequest.Nonce != 0)
                return (false, "Private ZK transactions must have Nonce 0.");

            if (txRequest.FromAddress != PrivacyConstants.ShieldedPoolAddress)
                return (false, "Private ZK transactions must use FromAddress Shielded_Pool.");

            switch (txRequest.TransactionType)
            {
                case TransactionType.VFX_UNSHIELD:
                    if (txRequest.ToAddress == PrivacyConstants.ShieldedPoolAddress)
                        return (false, "Unshield recipient must be a transparent address.");
                    if (!AddressValidateUtility.ValidateAddress(txRequest.ToAddress))
                        return (false, "To Address failed to validate");
                    if (Globals.LastBlock.Height > Globals.TXHeightRule1 && txRequest.Amount <= 0.0M)
                        return (false, "Unshield amount must be positive.");
                    break;

                case TransactionType.VFX_PRIVATE_TRANSFER:
                    if (txRequest.ToAddress != PrivacyConstants.ShieldedPoolAddress)
                        return (false, "Private transfer must send to Shielded_Pool.");
                    if (txRequest.Amount != 0.0M)
                        return (false, "Private transfer transparent Amount must be 0.");
                    break;

                case TransactionType.VBTC_V2_UNSHIELD:
                    if (txRequest.ToAddress == PrivacyConstants.ShieldedPoolAddress)
                        return (false, "vBTC unshield recipient must be a transparent address.");
                    if (!AddressValidateUtility.ValidateAddress(txRequest.ToAddress))
                        return (false, "To Address failed to validate");
                    if (txRequest.Amount != 0.0M)
                        return (false, "vBTC unshield transparent Amount must be 0.");
                    break;

                case TransactionType.VBTC_V2_PRIVATE_TRANSFER:
                    if (txRequest.ToAddress != PrivacyConstants.ShieldedPoolAddress)
                        return (false, "vBTC private transfer must send to Shielded_Pool.");
                    if (txRequest.Amount != 0.0M)
                        return (false, "vBTC private transfer transparent Amount must be 0.");
                    break;
            }

            return (true, "");
        }

        private static bool VerifyPrivateHash(Transaction txRequest)
        {
            var probe = new Transaction
            {
                Timestamp = txRequest.Timestamp,
                FromAddress = txRequest.FromAddress,
                ToAddress = txRequest.ToAddress,
                Amount = txRequest.Amount,
                Fee = txRequest.Fee,
                Nonce = txRequest.Nonce,
                TransactionType = txRequest.TransactionType,
                Data = txRequest.Data,
                UnlockTime = txRequest.UnlockTime
            };
            probe.BuildPrivate();
            return probe.Hash == txRequest.Hash;
        }
    }
}
