using LiteDB;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Applies private transactions to <c>DB_Privacy</c> and any extra transparent ledger rows (vBTC SC, VFX unshield credit)
    /// after standard <see cref="StateData.UpdateTreis"/> account debits for shields.
    /// </summary>
    public static class PrivateTxLedgerService
    {
        public static async Task ApplyBlockTransactionAsync(Transaction tx, Block block, LiteDatabase? privacyDb = null)
        {
            if (!PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                return;

            if (!PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out _) || payload == null || !payload.TryValidateStructure(out _))
                return;

            var db = privacyDb ?? PrivacyDbContext.GetPrivacyDb();
            ApplyPrivacyStore(tx, block, payload, db);
            await ApplyTransparentLedgerAsync(tx, block, payload).ConfigureAwait(false);
        }

        private static void ApplyPrivacyStore(Transaction tx, Block block, PrivateTxPayload payload, LiteDatabase db)
        {
            var height = block.Height;
            var ts = tx.Timestamp;

            foreach (var n in payload.NullsB64)
                NullifierService.TryRecordNullifier(n, payload.Asset, height, ts, db);

            var store = new ShieldedMerkleStore(payload.Asset, db);
            store.LoadLeavesFromCommitments();

            foreach (var o in payload.Outs.OrderBy(x => x.Index))
            {
                byte[] g1;
                try
                {
                    g1 = Convert.FromBase64String(o.CommitmentB64);
                }
                catch
                {
                    continue;
                }
                if (g1.Length != PlonkNative.G1CompressedSize)
                    continue;
                store.AppendG1Commitment(g1, height, ts);
            }

            var poolCol = db.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE);
            var existing = poolCol.FindOne(x => x.AssetType == payload.Asset);
            var supply = existing?.TotalShieldedSupply ?? 0m;
            store.UpdatePoolStateRoot(height, supply, store.LeafDigests.Count);
        }

        private static async Task ApplyTransparentLedgerAsync(Transaction tx, Block block, PrivateTxPayload payload)
        {
            if (tx.TransactionType == TransactionType.VFX_UNSHIELD)
                await CreditVfxUnshieldAsync(tx, block).ConfigureAwait(false);

            if (tx.TransactionType == TransactionType.VBTC_V2_UNSHIELD
                && payload.VbtcTransparentAmount is > 0
                && !string.IsNullOrWhiteSpace(payload.VbtcContractUid))
            {
                CreditVbtcTransparent(tx.ToAddress, payload.VbtcContractUid, payload.VbtcTransparentAmount.Value);
            }

            if (tx.TransactionType == TransactionType.VBTC_V2_SHIELD
                && payload.VbtcTransparentAmount is > 0
                && !string.IsNullOrWhiteSpace(payload.VbtcContractUid))
            {
                DebitVbtcTransparent(tx.FromAddress, payload.VbtcContractUid, payload.VbtcTransparentAmount.Value);
            }
        }

        private static async Task CreditVfxUnshieldAsync(Transaction tx, Block block)
        {
            if (tx.ToAddress == "Adnr_Base"
                || tx.ToAddress == "DecShop_Base"
                || tx.ToAddress == "Topic_Base"
                || tx.ToAddress == "Vote_Base"
                || tx.ToAddress == "Reserve_Base"
                || tx.ToAddress == "Token_Base"
                || tx.ToAddress == "TW_Base")
                return;

            var accStTrei = StateData.GetAccountStateTrei();
            var to = StateData.GetSpecificAccountStateTrei(tx.ToAddress);
            if (to == null)
            {
                var acct = new AccountStateTrei
                {
                    Key = tx.ToAddress,
                    Nonce = 0,
                    Balance = tx.Amount,
                    StateRoot = block.StateRoot
                };
                await accStTrei.InsertSafeAsync(acct).ConfigureAwait(false);
            }
            else
            {
                to.StateRoot = block.StateRoot;
                to.Balance += tx.Amount;
                await accStTrei.UpdateSafeAsync(to).ConfigureAwait(false);
            }
        }

        private static void CreditVbtcTransparent(string toAddress, string scUid, decimal amount)
        {
            try
            {
                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUid);
                if (scStateTreiRec == null)
                    return;

                var leg = new List<SmartContractStateTreiTokenizationTX>
                {
                    new()
                    {
                        Amount = amount,
                        FromAddress = "+",
                        ToAddress = toAddress
                    }
                };

                if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                    scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(leg);
                else
                    scStateTreiRec.SCStateTreiTokenizationTXes = leg;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"PrivateTxLedgerService CreditVbtcTransparent: {ex}", "PrivateTxLedgerService.CreditVbtcTransparent()");
            }
        }

        private static void DebitVbtcTransparent(string fromAddress, string scUid, decimal amount)
        {
            try
            {
                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUid);
                if (scStateTreiRec == null)
                    return;

                var leg = new List<SmartContractStateTreiTokenizationTX>
                {
                    new()
                    {
                        Amount = amount * -1.0M,
                        FromAddress = fromAddress,
                        ToAddress = "-"
                    }
                };

                if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                    scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(leg);
                else
                    scStateTreiRec.SCStateTreiTokenizationTXes = leg;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"PrivateTxLedgerService DebitVbtcTransparent: {ex}", "PrivateTxLedgerService.DebitVbtcTransparent()");
            }
        }
    }
}
