using LiteDB;
using NBitcoin;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// The sender-less transaction types of the attestation layer. (<see cref="TransactionType.VBTC_V2_COIN_PIN"/> is
    /// not one: a fee bump is an ordinary transaction signed by the withdrawal's requester.)
    /// </summary>
    public static class BtcAttestTx
    {
        public static bool IsAttestationType(TransactionType type) =>
            type == TransactionType.VBTC_V2_BTC_REPORT || type == TransactionType.VBTC_V2_BTC_REORG;
    }

    /// <summary>
    /// Every write to the attestation state. All of it runs inside block application (StateData.UpdateTreis), so a state
    /// replay, a snapshot restore and a live node produce the same tables, and every write carries the block's stamp.
    /// docs/PLAN-vBTC-Bitcoin-Attestation.md, sections 3.5, 4 and 8.
    /// </summary>
    public static class BtcAttestApply
    {
        /// <summary>
        /// Runs before a block's transactions. At the recording height it sets up the Bitcoin view from the checkpoint;
        /// at the first block of every epoch it stores the signer set.
        /// </summary>
        public static void OnBlockStart(long vfxHeight, LiteDatabase? db = null)
        {
            if (!BtcAttestRules.Recording(vfxHeight)) return;
            try
            {
                if (BtcAttestStore.GetMeta(db) == null)
                    Initialize(vfxHeight, db);
                if (BtcAttestRules.IsEpochStart(vfxHeight))
                    StoreSignerSet(vfxHeight, db);
                BtcWithdrawals.OnBlockStart(vfxHeight, db); // requests never pinned and past their window: closed, refunded
            }
            catch (Exception ex)
            {
                // Logged here and passed on: the caller counts the block's state as incompletely applied on this node.
                ErrorLogUtility.LogError($"Bitcoin attestation block-start step failed at {vfxHeight}: {ex}", "BtcAttestApply.OnBlockStart()");
                throw;
            }
        }

        /// <summary>
        /// The starting point of the Bitcoin view: the twelve checkpoint headers. Normally at the recording height itself;
        /// a state that somehow reached a later block without it (the row is missing) is set up at that block, and the
        /// headers are the same either way because they come from the release's own settings.
        /// </summary>
        private static void Initialize(long vfxHeight, LiteDatabase? db)
        {
            var checkpoint = BtcCheckpoint.Configured(out var reason);
            if (checkpoint == null)
            {
                ErrorLogUtility.LogError($"Bitcoin attestation cannot start at block {vfxHeight}: {reason}. No Bitcoin report can be accepted until the checkpoint settings are valid.", "BtcAttestApply.Initialize()");
                return;
            }
            for (var i = 0; i < BtcCheckpoint.HeaderCount; i++)
            {
                var height = checkpoint.Height - (BtcCheckpoint.HeaderCount - 1) + i;
                BtcAttestStore.SaveHeader(height, checkpoint.Headers[i], Globals.BitcoinAttestationRecordHeight, db);
            }
            BtcAttestStore.SaveMeta(new AttestationMetaRow
            {
                CheckpointHeight = checkpoint.Height, TipHeight = checkpoint.Height, TipHash = checkpoint.Tip.Hash,
                EventsThroughHeight = checkpoint.Height,
            }, db);
            LogUtility.Log($"Bitcoin attestation started at block {vfxHeight}: checkpoint {checkpoint.Height} {checkpoint.Tip.Hash}.", "BtcAttestApply.Initialize()");
        }

        /// <summary>
        /// The validators whose signatures count for this epoch: active at the previous block, and holding the validator
        /// stake in state right now. Registration checks the stake only at the moment of its own transaction, so the
        /// stake is checked again here, at one instant for everyone: one stake backs one signer. From the bond height
        /// that stake is locked (ValidatorBond) and must have been locked for two days before its validator is counted
        /// (<see cref="VerifiedXCore.Services.ValidatorBond.SignerEligible"/>).
        /// </summary>
        private static void StoreSignerSet(long vfxHeight, LiteDatabase? db)
        {
            // The registry reads committed blocks only; the balance is the state after the previous block (this runs
            // before the block's own transactions). Both are the same on every node, live or replaying.
            var addresses = VerifiedXCore.Services.ValidatorBond.SignerEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(vfxHeight - 1), vfxHeight, db)
                .Select(v => v.ValidatorAddress)
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            BtcAttestStore.SaveSignerSet(new AttestationSignerSetRow { Epoch = BtcAttestRules.EpochOf(vfxHeight), AtVfxHeight = vfxHeight, Addresses = addresses }, db);
        }

        /// <summary>
        /// Registers a newly created vault's deposit script so its Bitcoin is watched. Called when a contract creation
        /// is applied at or after the recording height. A vault created before then has no row and is a legacy vault.
        /// A script that already belongs to another vault is not registered again: a contract that copies a vault's
        /// deposit address gets no coins.
        /// </summary>
        public static void RegisterVaultIfAny(string? scUID, long vfxHeight, LiteDatabase? db = null)
        {
            if (string.IsNullOrEmpty(scUID) || !BtcAttestRules.Recording(vfxHeight)) return;
            try
            {
                var vault = VBTCChainView.GetVault(scUID);
                if (vault == null) return;
                var address = vault.Feature.DepositAddress;
                if (string.IsNullOrWhiteSpace(address)) return;

                string scriptHex;
                try { scriptHex = BitcoinAddress.Create(address.Trim(), FrostDkgAttestation.ConsensusNetwork).ScriptPubKey.ToHex().ToLowerInvariant(); }
                catch
                {
                    ErrorLogUtility.LogError($"Vault {scUID} carries a deposit address that is not valid on this network ({address}); it is not watched.", "BtcAttestApply.RegisterVaultIfAny()");
                    return;
                }

                if (BtcAttestStore.ScriptRowForVault(scUID, db) != null) return;
                var taken = BtcAttestStore.ScriptRow(scriptHex, db);
                if (taken != null)
                {
                    ErrorLogUtility.LogError($"Vault {scUID} carries the deposit address of vault {taken.SmartContractUID}; it is not watched and holds no coins.", "BtcAttestApply.RegisterVaultIfAny()");
                    return;
                }

                var meta = BtcAttestStore.GetMeta(db);
                var watchFrom = meta == null ? 0 : Math.Max(meta.CheckpointHeight + 1, meta.EventsThroughHeight - BtcAttestRules.WatchBackBlocks + 1);
                BtcAttestStore.SaveScript(new VaultDepositScriptRow
                {
                    ScriptHex = scriptHex, SmartContractUID = scUID, DepositAddress = address.Trim(),
                    CreatedAtVfxHeight = vfxHeight, WatchFromBitcoinHeight = watchFrom,
                }, db);
            }
            catch (Exception ex)
            {
                // Passed on: a vault this node failed to register would be one it does not watch while every other
                // node does. The creation then counts as not applied here and the node is flagged.
                ErrorLogUtility.LogError($"Registering vault {scUID} for Bitcoin attestation failed: {ex}", "BtcAttestApply.RegisterVaultIfAny()");
                throw;
            }
        }

        /// <summary>
        /// Applies a mined Bitcoin report. The report is verified again here against the state it lands on (the same
        /// function admission and block validation used), and nothing is written unless it is valid. Returns the
        /// outcome so the withdrawal layer can react to the coins spent.
        /// </summary>
        public static BtcReportOutcome ApplyReport(VerifiedXCore.Models.Transaction tx, long vfxHeight, LiteDatabase? db = null)
        {
            if (!BtcReport.TryParse(tx?.Data, out var report, out var parseReason) || report == null)
                return BtcReportOutcome.Fail(parseReason ?? "the Bitcoin report does not parse");
            var outcome = BtcReportVerifier.Verify(report, vfxHeight, db);
            if (!outcome.Ok)
            {
                ErrorLogUtility.LogError($"Bitcoin report {tx!.Hash} at block {vfxHeight} was not applied: {outcome.Error}", "BtcAttestApply.ApplyReport()");
                return outcome;
            }

            var meta = BtcAttestStore.GetMeta(db)!;

            foreach (var (height, header) in outcome.NewHeaders)
                BtcAttestStore.SaveHeader(height, header, vfxHeight, db);
            if (outcome.ReplacesFromHeight > 0)
            {
                // A replacing branch may be shorter in blocks while heavier in work: retire what it does not overwrite.
                var headers = (db ?? BtcAttestStore.Db).GetCollection<AttestedHeaderRow>(BtcAttestStore.Headers);
                for (var h = outcome.NewTipHeight + 1; h <= meta.TipHeight; h++)
                {
                    var stale = headers.FindById(h);
                    if (stale == null || !stale.Active) continue;
                    stale.Active = false;
                    stale.LastModifiedHeight = StateWriteContext.StampHeight;
                    headers.Update(stale);
                }
            }

            foreach (var effect in outcome.Effects)
            {
                var existing = BtcAttestStore.Coin(effect.Outpoint, db);
                if (effect.IsSpend)
                {
                    if (existing == null) continue; // created and spent inside this report: written by the create below, then here
                    var spent = Clone(existing);
                    spent.Status = VaultCoinStatus.Spent;
                    spent.SpentByTxid = effect.Txid;
                    spent.SpentInHeight = effect.BitcoinHeight;
                    BtcAttestStore.SaveCoin(spent, existing, db);
                }
                else
                {
                    // A coin a rewind orphaned, confirmed again: held again for the withdrawal whose pinned transaction
                    // spends it, if that withdrawal is still to be paid.
                    var created = new VaultCoinRow
                    {
                        Outpoint = effect.Outpoint, SmartContractUID = effect.SmartContractUID, ValueSats = effect.Sats,
                        PinnedToRef = existing?.PinnedToRef, AlsoPinnedTo = existing?.AlsoPinnedTo?.ToList(),
                        CreatedInHeight = effect.BitcoinHeight, IsCoinbase = effect.IsCoinbase,
                    };
                    created.Status = BtcWithdrawals.SetHolders(created, db) ? VaultCoinStatus.Pinned : VaultCoinStatus.Unspent;
                    BtcAttestStore.SaveCoin(created, existing, db);
                }
            }

            meta.TipHeight = outcome.NewTipHeight;
            meta.TipHash = outcome.NewTipHash;
            if (outcome.QuietThroughHeight > 0)
                meta.EventsThroughHeight = outcome.QuietThroughHeight;
            if (report.BlockHeight != 0 && report.Scope == BtcReport.ScopeAll)
            {
                if (outcome.AdvancesEventsThrough)
                {
                    meta.EventsThroughHeight = report.BlockHeight;
                    meta.PendingPartBlockHeight = 0;
                    meta.PendingNextPart = 0;
                    meta.PendingLastPosition = 0;
                }
                else
                {
                    meta.PendingPartBlockHeight = report.BlockHeight;
                    meta.PendingNextPart = report.Part + 1;
                    meta.PendingLastPosition = outcome.LastPosition;
                }
            }
            BtcAttestStore.SaveMeta(meta, db);
            // The coins have moved: settle what Bitcoin now shows, void what can no longer confirm, pin what was waiting.
            BtcWithdrawals.OnCoinsChanged(outcome.Effects, tx!, vfxHeight, db);
            return outcome;
        }

        /// <summary>
        /// Applies a mined rewind (transaction type VBTC_V2_BTC_REORG). Verified again here against the state it lands
        /// on. Everything the events of the replaced blocks did is taken back, the headers above the shared height are
        /// replaced, and the events-through height returns to the shared height: the new branch's blocks are then
        /// reported like any others.
        /// </summary>
        public static BtcReportOutcome ApplyRewind(VerifiedXCore.Models.Transaction tx, long vfxHeight, LiteDatabase? db = null)
        {
            if (!BtcReport.TryParse(tx?.Data, out var report, out var parseReason) || report == null)
                return BtcReportOutcome.Fail(parseReason ?? "the Bitcoin rewind does not parse");
            var outcome = BtcReportVerifier.VerifyRewind(report, vfxHeight, db);
            if (!outcome.Ok)
            {
                ErrorLogUtility.LogError($"Bitcoin rewind {tx!.Hash} at block {vfxHeight} was not applied: {outcome.Error}", "BtcAttestApply.ApplyRewind()");
                return outcome;
            }

            var meta = BtcAttestStore.GetMeta(db)!;
            var to = outcome.ReplacesFromHeight - 1;
            var before = (meta.TipHeight, meta.EventsThroughHeight);

            // The events first, while the state still shows what they did.
            BtcWithdrawals.OnRewind(to, tx!, vfxHeight, db);

            foreach (var (height, header) in outcome.NewHeaders)
                BtcAttestStore.SaveHeader(height, header, vfxHeight, db);
            var headers = (db ?? BtcAttestStore.Db).GetCollection<AttestedHeaderRow>(BtcAttestStore.Headers);
            for (var h = outcome.NewTipHeight + 1; h <= meta.TipHeight; h++)
            {
                var stale = headers.FindById(h);
                if (stale == null || !stale.Active) continue;
                stale.Active = false;
                stale.LastModifiedHeight = StateWriteContext.StampHeight;
                headers.Update(stale);
            }

            meta.TipHeight = outcome.NewTipHeight;
            meta.TipHash = outcome.NewTipHash;
            meta.EventsThroughHeight = Math.Min(meta.EventsThroughHeight, to);
            meta.PendingPartBlockHeight = 0;
            meta.PendingNextPart = 0;
            meta.PendingLastPosition = 0;
            BtcAttestStore.SaveMeta(meta, db);
            ErrorLogUtility.LogError($"Bitcoin rewind {tx!.Hash} applied at block {vfxHeight}: Bitcoin reorganised below reported events. Back to block {to} (events were applied through {before.EventsThroughHeight}, attested tip was {before.TipHeight}); the attested tip is now {outcome.NewTipHeight}. The new branch is reported from {to + 1}.",
                "BtcAttestApply.ApplyRewind()");
            return outcome;
        }

        private static VaultCoinRow Clone(VaultCoinRow c) => new()
        {
            Outpoint = c.Outpoint, SmartContractUID = c.SmartContractUID, ValueSats = c.ValueSats, Status = c.Status, CreatedInHeight = c.CreatedInHeight,
            IsCoinbase = c.IsCoinbase, SpentByTxid = c.SpentByTxid, SpentInHeight = c.SpentInHeight, PinnedToRef = c.PinnedToRef,
            AlsoPinnedTo = c.AlsoPinnedTo?.ToList(), LastModifiedHeight = c.LastModifiedHeight,
        };
    }
}
