using VerifiedXCore.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;
using VerifiedXCore.Services;
using System.Collections.Concurrent;
using System.Xml.Linq;
using LiteDB;
using System.Net;
using System.Security.Principal;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Privacy;
using NBitcoin.JsonConverters;
using VerifiedXCore.Models.DST;
using System;

namespace VerifiedXCore.Data
{
    public class StateData
    {
        
        public static async Task CreateGenesisWorldTrei(Block block)
        {
            var trxList = block.Transactions.ToList();
            var accStTrei = new List<AccountStateTrei>();

            trxList.ForEach(x => {

                var acctStateTreiTo = new AccountStateTrei
                {
                    Key = x.ToAddress,
                    Nonce = 0, 
                    Balance = (x.Amount), //subtract from the address
                    StateRoot = block.StateRoot
                };

                accStTrei.Add(acctStateTreiTo);

            });

            var worldTrei = new WorldTrei {
                StateRoot = block.StateRoot,
                ShieldedStateRoot = global::VerifiedXCore.Privacy.ShieldedStateRoot.Compute(),
            };

            var wTrei = DbContext.DB_WorldStateTrei.GetCollection<WorldTrei>(DbContext.RSRV_WSTATE_TREI);
            await wTrei.InsertSafeAsync(worldTrei);
            var aTrei = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
            await aTrei.InsertBulkSafeAsync(accStTrei);
        }

        /// <summary>
        /// Fund-loss audit item 1 (burn): each listed address loses min(balance, listed amount) of VFX. Nothing is credited:
        /// the supply shrinks by what the forged unshields created. Bounded below at zero.
        ///
        /// It runs in block application (<see cref="UpdateTreis"/>), before the block's transactions:
        /// <list type="bullet">
        /// <item>at exactly Globals.ForgedVfxBurnHeight on every node that applies that block with this code;</item>
        /// <item>at the first block applied after that height on a state that has not been burned (a node that crossed the
        /// height on an older build, or a state restored from a snapshot taken before the burn).</item>
        /// </list>
        /// Whether a state has been burned is recorded IN the state (<see cref="WorldTrei.ForgedVfxBurnAppliedHeight"/>): the
        /// world record is wiped by a full rebuild, copied by every snapshot cycle and restored with a snapshot, so the
        /// marker always describes the balances beside it. (Fourth review: the earlier startup catch-up read the tip
        /// before it was loaded and never ran; a marker file beside the databases would also have outlived a restore,
        /// and a write made at startup carries no snapshot stamp.) Running inside block application, the write is stamped
        /// with the block's height like every other state write.
        ///
        /// A late node burns min(balance at that block, listed): the address is frozen from the same height, so its balance
        /// can only have grown by receipts, and up to (listed - balance at the height) of those go too. Balances are not in
        /// the state root, so this is not a fork; a node that must match to the unit re-syncs its state.
        /// </summary>
        public static async Task ApplyForgedVfxBurnsAsync(Block block)
        {
            if (block == null || Globals.ForgedVfxBurns.Count == 0 || block.Height < Globals.ForgedVfxBurnHeight)
                return;
            var atHeight = block.Height == Globals.ForgedVfxBurnHeight;
            try
            {
                if (!atHeight && ForgedVfxBurnApplied())
                    return;
                if (!atHeight)
                    LogUtility.Log($"Forged-VFX burn: this state passed block {Globals.ForgedVfxBurnHeight} without the burn (older build, or a snapshot from before it); applying it at block {block.Height}.", "StateData.ApplyForgedVfxBurnsAsync()");
                await BurnListedAsync(block.Height, block.StateRoot, atHeight ? "at block" : "catch-up at block").ConfigureAwait(false);
                MarkForgedVfxBurnApplied(block);
            }
            catch (Exception ex)
            {
                // No marker is written, so the next block tries again. A fault of this node's storage also flags the block:
                // the same block sets the validator bonds and the first signer set from these balances (fourth review, round 5).
                NoteApplyStepFailed(block.Height, ex);
                ErrorLogUtility.LogError($"Forged-VFX burn at block {block.Height} failed (retried at the next block): {ex}", "StateData.ApplyForgedVfxBurnsAsync()");
            }
        }

        private static async Task BurnListedAsync(long height, string? stateRoot, string how)
        {
            var accStTrei = GetAccountStateTrei();
            foreach (var (address, listed) in Globals.ForgedVfxBurns)
            {
                var account = GetSpecificAccountStateTrei(address);
                if (account == null || account.Balance <= 0M || listed <= 0M)
                {
                    LogUtility.Log($"Forged-VFX burn {how} {height}: {address} holds nothing to burn.", "StateData.ApplyForgedVfxBurnsAsync()");
                    continue;
                }
                var burned = Math.Min(account.Balance, listed);
                account.Balance -= burned;
                account.StateRoot = stateRoot;
                await accStTrei.UpdateSafeAsync(account);
                LogUtility.Log($"Forged-VFX burn {how} {height}: {burned} VFX burned from {address} (listed {listed}); balance now {account.Balance}.", "StateData.ApplyForgedVfxBurnsAsync()");
            }
        }

        /// <summary>Whether this state already carries the burn (the world record's marker).</summary>
        public static bool ForgedVfxBurnApplied()
        {
            var record = WorldTrei.GetWorldTrei().FindOne(x => true);
            return record != null && record.ForgedVfxBurnAppliedHeight > 0;
        }

        private static void MarkForgedVfxBurnApplied(Block block)
        {
            var wTrei = WorldTrei.GetWorldTrei();
            var record = wTrei.FindOne(x => true);
            if (record == null)
            {
                wTrei.InsertSafe(new WorldTrei { StateRoot = block.StateRoot, ForgedVfxBurnAppliedHeight = block.Height });
                return;
            }
            record.ForgedVfxBurnAppliedHeight = block.Height;
            wTrei.UpdateSafe(record);
        }

        /// <summary>
        /// The height of the last block whose block-start step (the Bitcoin view, the validator bonds) failed on this
        /// node, or -1. A full replay reads it: it cannot count every block that returns false here (blocks of the
        /// chain's history hold transactions whose apply has always failed, the same on every node), but a block-start
        /// step that fails leaves this node with other signer sets or bonds than the network, and a replay that ends
        /// so has not rebuilt the state.
        /// </summary>
        public static long BlockStartFailedAtHeight = -1;

        /// <summary>A step of the block being applied failed on this node (set by <see cref="NoteApplyStepFailed"/>, cleared as each block starts).</summary>
        private static bool _applyStepFailed;

        /// <summary>
        /// A consensus step inside a transaction's apply (recording a withdrawal, settling after a report, a cancel, a fee
        /// raise, a bridge exit) failed on this node and was caught where it ran. The block's state is then reported as
        /// incomplete and marked like a failed block-start step, so the node is flagged instead of quietly holding other
        /// state than its peers (fourth review).
        /// </summary>
        /// <remarks>
        /// Only a fault of this node's storage flags the block (<see cref="IsNodeLocalFault"/>). An exception the
        /// transaction's own data causes is thrown on every node alike: the step is a no-op everywhere, as before, and
        /// flagging it let anyone send one cheap request with an unreadable field and leave every node's state marked
        /// failed, its snapshots stopped and every later rebuild failing (fourth review, round 4).
        /// </remarks>
        /// <summary>Test seam: runs at the start of each transaction's apply.</summary>
        internal static Action<Transaction>? TxApplyForTests;

        public static void NoteApplyStepFailed(long height, Exception ex)
        {
            if (!IsNodeLocalFault(ex)) return;
            _applyStepFailed = true;
            BlockStartFailedAtHeight = height;
        }

        /// <summary>A step writing the withdrawal store (requests, cancellations) failed on this node (cleared as each block starts).</summary>
        private static bool _withdrawalStoreStepFailed;

        /// <summary>
        /// As <see cref="NoteApplyStepFailed"/>, for a step that writes the withdrawal store: a fault of this node's
        /// storage there may have left it without what the chain wrote, so the store is also marked unfinished (blocks
        /// held, store rebuilt; see <see cref="UpdateTreis"/>). Faults elsewhere in the apply do not mark it.
        /// </summary>
        public static void NoteWithdrawalStoreStepFailed(long height, Exception ex)
        {
            if (IsNodeLocalFault(ex)) _withdrawalStoreStepFailed = true;
            NoteApplyStepFailed(height, ex);
        }

        /// <summary>Whether an exception comes from this node's storage or process (not from what a transaction says).</summary>
        public static bool IsNodeLocalFault(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is System.IO.IOException || e is UnauthorizedAccessException || e is ObjectDisposedException
                    || e is TimeoutException || e is OutOfMemoryException)
                    return true;
                if (e is LiteDB.LiteException lite && (lite.ErrorCode == LiteDB.LiteException.LOCK_TIMEOUT || lite.ErrorCode == LiteDB.LiteException.DATABASE_SHUTDOWN
                        || lite.ErrorCode == LiteDB.LiteException.FILE_NOT_FOUND || lite.ErrorCode == LiteDB.LiteException.INVALID_DATABASE
                        || lite.ErrorCode == LiteDB.LiteException.FILE_SIZE_EXCEEDED || lite.ErrorCode == LiteDB.LiteException.ALREADY_OPEN_DATAFILE))
                    return true;
            }
            return false;
        }

        public static async Task<bool> UpdateTreis(Block block)
        {
            Globals.TreisUpdating = true;
            try
            {
                // A write to the withdrawal store the storage swallowed would leave this node's table unlike its peers':
                // the block's apply fails, as for any other fault of this node's storage.
                using var storeWrites = new Extensions.DbExtensions.FailureScope(Bitcoin.Services.VBTCWithdrawalStoreRebuildService.StoreCollections);
                bool applied;
                try { applied = await UpdateTreisCore(block); }
                finally
                {
                    // A lost store write, or a fault of this node's storage in a step that writes the store (also when the
                    // apply then threw): neither the store nor the block's other writes (an escrow debit, a refund) may hold
                    // what the chain wrote. Blocks are held until the state is rebuilt, the store with it. The state is
                    // marked failed here too (the caller's mark is skipped when the apply throws), so a restart restores it.
                    if (storeWrites.Count > 0 || _withdrawalStoreStepFailed)
                    {
                        var why = storeWrites.Count > 0
                            ? $"{storeWrites.Count} write(s) to it failed while block {block.Height} was applied"
                            : $"a fault of this node's storage interrupted a withdrawal step of block {block.Height}";
                        Bitcoin.Services.VBTCWithdrawalStoreRebuildService.MarkNeedsStateRebuild(why);
                        try { VerifiedXCore.Services.StateTreiStatusService.SetFailed($"Withdrawal store: {why}."); } catch { }
                    }
                }
                if (storeWrites.Count > 0)
                {
                    NoteApplyStepFailed(block.Height, new System.IO.IOException($"{storeWrites.Count} write(s) to the withdrawal store failed while block {block.Height} was applied."));
                    ErrorLogUtility.LogError($"Block {block.Height}: {storeWrites.Count} write(s) to the withdrawal store failed; the block's apply is incomplete.", "StateData.UpdateTreis()");
                    return false;
                }
                return applied;
            }
            finally
            {
                // AddBlock waits while this is set, so a throw anywhere in the apply must still clear it.
                StateWriteContext.Clear();
                Globals.TreisUpdating = false;
            }
        }

        internal static Action<long>? ApplyStepForTests;

        private static async Task<bool> UpdateTreisCore(Block block)
        {
            _applyStepFailed = false;
            _withdrawalStoreStepFailed = false;
            ApplyStepForTests?.Invoke(block.Height);
            lock (_ownerChangesThisBlock) _ownerChangesThisBlock.Clear();
            StateWriteContext.SetHeight(block.Height); //stamp LastModifiedHeight on state writes for snapshot diffing
            LedgerIntegrityRules.NormalizeTransactionHeights(block); // NEW-13: every apply path uses the block's height
            using var contractClock = ContractClock.ForBlock(block.Timestamp, block.Height); // contract code is read as of this block
            await ApplyForgedVfxBurnsAsync(block); // fund-loss audit item 1 (burn): at ForgedVfxBurnHeight, before the block's transactions
            try
            {
                PrivacyEpochService.ApplyIfResetBlock(block); // fund-loss audit item 1 (stage 2): shielded pools restart at PrivateTxProofRulesHeight
            }
            catch (Exception ex)
            {
                // Uncaught it left TreisUpdating set for good, and the node waited on it forever (fourth review, round 6).
                NoteApplyStepFailed(block.Height, ex);
                ErrorLogUtility.LogError($"Shielded-pool restart at block {block.Height} failed: {ex}", "StateData.UpdateTreis()");
            }
            // The steps a block takes before its transactions write consensus state (signer sets, bonds). One that
            // fails on this node is counted like a transaction that failed to apply: the block's state is reported as
            // incomplete, so the node is flagged, not left quietly holding other state than its peers.
            var blockStartFailed = false;
            try
            {
                VerifiedXCore.Bitcoin.Attestation.BtcAttestApply.OnBlockStart(block.Height); // Bitcoin attestation: the checkpoint at the recording height, the signer set at each epoch
            }
            catch (Exception ex)
            {
                blockStartFailed = true;
                BlockStartFailedAtHeight = block.Height;
                ErrorLogUtility.LogError($"Block {block.Height}: the Bitcoin attestation block-start step failed: {ex}", "StateData.UpdateTreis() - block start");
            }
            try
            {
                // Each step on its own: one that fails must not also skip the other for this block.
                VerifiedXCore.Services.ValidatorBond.OnBlockStart(block.Height); // validator bonds: active validators bonded at the bond height; funded stakes confirmed
            }
            catch (Exception ex)
            {
                blockStartFailed = true;
                BlockStartFailedAtHeight = block.Height;
                ErrorLogUtility.LogError($"Block {block.Height}: the validator bond block-start step failed: {ex}", "StateData.UpdateTreis() - block start");
            }
            var txList = block.Transactions.ToList();
            var txCount = txList.Count();
            int txTreiUpdateSuccessCount = 0;
            var txFailList = new List<Transaction>();

            var accStTrei = GetAccountStateTrei();
            ConcurrentDictionary<string, StateTreiAuditData> StateTreiAuditDict = new ConcurrentDictionary<string, StateTreiAuditData>();

            foreach(var tx in txList)
            {
                try
                {
                    TxApplyForTests?.Invoke(tx);
                    if (block.Height == 0)
                    {
                        var acctStateTreiFrom = new AccountStateTrei
                        {
                            Key = tx.FromAddress,
                            Nonce = tx.Nonce + 1, //increase Nonce for next use
                            Balance = 0, //subtract from the address
                            StateRoot = block.StateRoot
                        };

                        await accStTrei.InsertSafeAsync(acctStateTreiFrom);
                    }
                    else
                    {
                        // ZK private txs use FromAddress Shielded_Pool with nonce 0 — skip AccountStateTrei from updates.
                        // Bitcoin attestation transactions have no sender: no account, no nonce, no fee.
                        if (tx.FromAddress != "Coinbase_TrxFees" && tx.FromAddress != "Coinbase_BlkRwd"
                            && !PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType)
                            && !VerifiedXCore.Bitcoin.Attestation.BtcAttestTx.IsAttestationType(tx.TransactionType))
                        {
                            var from = GetSpecificAccountStateTrei(tx.FromAddress);

                            if(from == null)
                            {
                                var acctStateTreiFrom = new AccountStateTrei
                                {
                                    Key = tx.FromAddress,
                                    Nonce = tx.Nonce + 1, //increase Nonce for next use
                                    Balance = 0, //subtract from the address
                                    StateRoot = block.StateRoot
                                };

                                await accStTrei.InsertSafeAsync(acctStateTreiFrom);

                                from = GetSpecificAccountStateTrei(tx.FromAddress);

                                if (from == null)
                                    continue;
                            }

                            if (!tx.FromAddress.StartsWith("xRBX"))
                            {
                                from.Nonce += 1;
                                from.StateRoot = block.StateRoot;
                                from.Balance -= (tx.Amount + tx.Fee);

                                await accStTrei.UpdateSafeAsync(from);
                            }
                            else
                            {
                                if(tx.TransactionType != TransactionType.RESERVE)
                                {
                                    ReserveTransactions rTx = new ReserveTransactions
                                    {
                                        ConfirmTimestamp = (long)(tx.UnlockTime ?? 0),
                                        FromAddress = tx.FromAddress,
                                        ToAddress = tx.ToAddress,
                                        Hash = tx.Hash,
                                        Height = tx.Height,
                                        Data = tx.Data,
                                        Amount = tx.Amount,
                                        Fee = tx.Fee,
                                        Nonce= tx.Nonce,
                                        ReserveTransactionStatus = ReserveTransactionStatus.Pending,
                                        Signature = tx.Signature,
                                        Timestamp = tx.Timestamp,
                                        TransactionType = tx.TransactionType,
                                        UnlockTime = tx.UnlockTime,
                                    };

                                    // Not kept on this node, the delayed transaction would never settle here (round 8).
                                    if (!ReserveTransactions.SaveReserveTx(rTx))
                                        NoteApplyStepFailed(block.Height, new System.IO.IOException($"The reserve row of {tx.Hash} was not written on this node."));
                                }

                                from.Nonce += 1;
                                from.StateRoot = block.StateRoot;
                                from.Balance -= (tx.Amount + tx.Fee);
                                if(tx.TransactionType == TransactionType.TX)
                                    from.LockedBalance += tx.Amount;

                                await accStTrei.UpdateSafeAsync(from);
                            }
                            
                        }
                    }

                    if (tx.ToAddress != "Adnr_Base" && 
                        tx.ToAddress != "DecShop_Base" && 
                        tx.ToAddress != "Topic_Base" && 
                        tx.ToAddress != "Vote_Base" && 
                        tx.ToAddress != "Reserve_Base" &&
                        tx.ToAddress != "Token_Base")
                    {
                        var to = GetSpecificAccountStateTrei(tx.ToAddress);
                        if (tx.TransactionType == TransactionType.TX)
                        {
                            if (to == null)
                            {
                                var acctStateTreiTo = new AccountStateTrei
                                {
                                    Key = tx.ToAddress,
                                    Nonce = 0,
                                    Balance = 0.0M,
                                    StateRoot = block.StateRoot
                                };

                                if (!tx.FromAddress.StartsWith("xRBX"))
                                {
                                    acctStateTreiTo.Balance += tx.Amount;
                                }
                                else
                                {
                                    acctStateTreiTo.LockedBalance += tx.Amount;
                                }

                                await accStTrei.InsertSafeAsync(acctStateTreiTo);
                            }
                            else
                            {
                                to.StateRoot = block.StateRoot;
                                if (!tx.FromAddress.StartsWith("xRBX"))
                                {
                                    to.Balance += tx.Amount;
                                }
                                else
                                {
                                    to.LockedBalance += tx.Amount;
                                }
                                
                                await accStTrei.UpdateSafeAsync(to);
                            }
                        }
                    }

                    if (tx.TransactionType != TransactionType.TX)
                    {
                        if (tx.TransactionType == TransactionType.NFT_TX
                            || tx.TransactionType == TransactionType.NFT_MINT
                            || tx.TransactionType == TransactionType.NFT_BURN
                            || tx.TransactionType == TransactionType.FTKN_MINT
                            || tx.TransactionType == TransactionType.FTKN_TX
                            || tx.TransactionType == TransactionType.FTKN_BURN
                            || tx.TransactionType == TransactionType.TKNZ_MINT
                            || tx.TransactionType == TransactionType.TKNZ_TX
                            || tx.TransactionType == TransactionType.TKNZ_BURN
                            || tx.TransactionType == TransactionType.SC_MINT
                            || tx.TransactionType == TransactionType.SC_TX
                            || tx.TransactionType == TransactionType.SC_BURN
                            || tx.TransactionType == TransactionType.VBTC_V2_CONTRACT_CREATE // FIND-017 Fix: Route vBTC V2 contract creation through mint dispatcher
                            || tx.TransactionType == TransactionType.TKNZ_WD_ARB
                            || tx.TransactionType == TransactionType.TKNZ_WD_OWNER)
                        {
                            string scUID = "";
                            string function = "";
                            bool skip = false;
                            JToken? scData = null;
                            try
                            {
                                var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
                                scData = scDataArray[0];

                                function = (string?)scData["Function"];
                                scUID = (string?)scData["ContractUID"];
                                skip = true;
                            }
                            catch { }

                            try
                            {
                                if (!skip)
                                {
                                    var jobj = JObject.Parse(tx.Data);
                                    scUID = jobj["ContractUID"]?.ToObject<string?>();
                                    function = jobj["Function"]?.ToObject<string?>();
                                }
                            }
                            catch { }

                            if (!string.IsNullOrWhiteSpace(function))
                            {
                                switch (function)
                                {
                                    case "Mint()":
                                        AddNewlyMintedContract(tx);
                                        // A vault created from the recording height on has its deposit script watched.
                                        VerifiedXCore.Bitcoin.Attestation.BtcAttestApply.RegisterVaultIfAny(scUID, block.Height);
                                        break;
                                    case "Update()":
                                        UpdateSmartContract(tx);
                                        break;
                                    case "Transfer()":
                                        TransferSmartContract(tx);
                                        break;
                                    case "Burn()":
                                        BurnSmartContract(tx, block.Height);
                                        break;
                                    case "Evolve()":
                                        EvolveSC(tx);
                                        break;
                                    case "Devolve()":
                                        DevolveSC(tx);
                                        break;
                                    case "ChangeEvolveStateSpecific()":
                                        EvolveDevolveSpecific(tx);
                                        break;
                                    case "TokenDeploy()":
                                        // VX-02: awaited. It was fire-and-forget, so a later TX in the same
                                        // block could run before this deploy's state writes landed.
                                        await DeployTokenContract(tx, block);
                                        // (Validation refuses a vault deployed as a token from the recording height; if one is
                                        // ever applied all the same, it is watched like any other.)
                                        VerifiedXCore.Bitcoin.Attestation.BtcAttestApply.RegisterVaultIfAny(scUID, block.Height);
                                        break;
                                    case "TokenTransfer()":
                                        await TokenTransfer(tx, block); // NEW-12: awaited (ran concurrently with the rest of the block)
                                        break;
                                    case "TokenMint()":
                                        await TokenMint(tx); // NEW-12: awaited (ran concurrently with the rest of the block)
                                        break;
                                    case "TokenBurn()":
                                        await TokenBurn(tx); // NEW-12: awaited (ran concurrently with the rest of the block)
                                        break;
                                    case "TokenPause()":
                                        TokenPause(tx);
                                        break;
                                    case "TokenBanAddress()":
                                        await TokenBanAddress(tx); // NEW-12: awaited (ran concurrently with the rest of the block)
                                        break;
                                    case "TokenContractOwnerChange()":
                                        TokenContractOwnerChange(tx);
                                        break;
                                    case "TokenVoteTopicCreate()":
                                        TokenVoteTopicCreate(tx);
                                        break;
                                    case "TokenVoteTopicCast()":
                                        TokenVoteTopicCast(tx);
                                        break;
                                    case "TransferCoin()":
                                        TransferCoin(tx);
                                        break;
                                    case "TransferCoinMulti()":
                                        TransferCoinMulti(tx);
                                        break;
                                    case "TransferVBTCV2()":
                                        TransferVBTCV2(tx);
                                        break;
                                    case "TransferVBTC()":
                                        TransferVBTC(tx);
                                        break;
                                    case "TransferVBTCMulti()":
                                        TransferVBTCMulti(tx);
                                        break;
                                    case "TokenizedWithdrawalRequest()":
                                        TokenizedWithdrawalRequest(tx);
                                        break;
                                    case "TokenizedWithdrawalComplete()":
                                        TokenizedWithdrawalComplete(tx);
                                        break;
                                    default:
                                        break;
                                }
                            }

                        }

                        if(tx.TransactionType == TransactionType.NFT_SALE)
                        {
                            var txData = tx.Data;
                            if (!string.IsNullOrWhiteSpace(txData))
                            {
                                var jobj = JObject.Parse(txData);
                                var function = (string?)jobj["Function"];
                                if (!string.IsNullOrWhiteSpace(function))
                                {
                                    switch (function)
                                    {
                                        case "Sale_Start()":
                                            StartSaleSmartContract(tx);
                                            break;
                                        case "M_Sale_Start()":
                                            StartSaleSmartContract(tx);
                                            break;
                                        case "Sale_Complete()":
                                            await CompleteSaleSmartContract(tx, block); // NEW-12: awaited (ran concurrently with the rest of the block)
                                            break;
                                        case "M_Sale_Complete()":
                                            await CompleteSaleSmartContract(tx, block); // NEW-12: awaited (ran concurrently with the rest of the block)
                                            break;
                                        case "Sale_Cancel()":
                                            CancelSaleSmartContract(tx);
                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }
                        }

                        if (tx.TransactionType == TransactionType.ADNR)
                        {
                            var txData = tx.Data;
                            if (!string.IsNullOrWhiteSpace(txData))
                            {
                                var jobj = JObject.Parse(txData);
                                var function = (string?)jobj["Function"];
                                if (!string.IsNullOrWhiteSpace(function))
                                {
                                    switch (function)
                                    {
                                        case "AdnrCreate()":
                                            AddNewAdnr(tx);
                                            break;
                                        case "AdnrTransfer()":
                                            await TransferAdnr(tx); // NEW-12: awaited (ran concurrently with the rest of the block)
                                            break;
                                        case "AdnrDelete()":
                                            DeleteAdnr(tx);
                                            break;
                                        case "BTCAdnrCreate()":
                                            AddNewBTCAdnr(tx);
                                            break;
                                        case "BTCAdnrTransfer()":
                                            await TransferBTCAdnr(tx, block.Height); // NEW-12: awaited (ran concurrently with the rest of the block)
                                            break;
                                        case "BTCAdnrDelete()":
                                            DeleteBTCAdnr(tx, block.Height);
                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }
                        }

                        if (tx.TransactionType == TransactionType.VOTE_TOPIC)
                        {
                            var txData = tx.Data;
                            if (!string.IsNullOrWhiteSpace(txData))
                            {
                                var jobj = JObject.Parse(txData);
                                if (jobj != null)
                                {
                                    var function = (string)jobj["Function"];
                                    TopicTrei topic = jobj["Topic"].ToObject<TopicTrei>();
                                    if (topic != null)
                                    {
                                        topic.Id = 0;//save new
                                        topic.BlockHeight = tx.Height;
                                        TopicTrei.SaveTopic(topic);
                                        if(topic.VoteTopicCategory == VoteTopicCategories.AdjVoteIn)
                                            AdjVoteInQueue.SaveToQueue(topic);
                                    }
                                }
                            }
                        }

                        if (tx.TransactionType == TransactionType.VOTE)
                        {
                            var txData = tx.Data;
                            if (!string.IsNullOrWhiteSpace(txData))
                            {
                                var jobj = JObject.Parse(txData);
                                if (jobj != null)
                                {
                                    var function = (string)jobj["Function"];
                                    Vote vote = jobj["Vote"].ToObject<Vote>();
                                    if (vote != null)
                                    {
                                        vote.Id = 0;
                                        vote.TransactionHash = tx.Hash;
                                        vote.BlockHeight = tx.Height;
                                        var result = Vote.SaveVote(vote);
                                        if (result)
                                        {
                                            var topic = TopicTrei.GetSpecificTopic(vote.TopicUID);
                                            if (topic != null)
                                            {
                                                if (vote.VoteType == VoteType.Yes)
                                                    topic.VoteYes += 1;
                                                if (vote.VoteType == VoteType.No)
                                                    topic.VoteNo += 1;

                                                TopicTrei.UpdateTopic(topic);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (tx.TransactionType == TransactionType.DSTR)
                        {
                            var txData = tx.Data;
                            if (!string.IsNullOrWhiteSpace(txData))
                            {
                                var jobj = JObject.Parse(txData);
                                var function = (string?)jobj["Function"];
                                if (!string.IsNullOrWhiteSpace(function))
                                {
                                    switch (function)
                                    {
                                        case "DecShopCreate()":
                                            AddNewDecShop(tx);
                                            break;
                                        case "DecShopUpdate()":
                                            UpdateDecShop(tx);
                                            break;
                                        case "DecShopDelete()":
                                            await DeleteDecShop(tx); // NEW-12: awaited (ran concurrently with the rest of the block)
                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }
                        }

                        // vBTC V2 Transfer/Withdrawal Handling - explicit type-based dispatch
                        if (tx.TransactionType == TransactionType.VBTC_V2_TRANSFER)
                        {
                            // Reserve (xRBX) sends are deferred: the Pending ReserveTransactions row
                            // is written above, and the ledger moves when ReserveService finalizes it
                            // after UnlockTime (UpdateTreiFromReserve) — or is redirected by recovery.
                            if (!tx.FromAddress.StartsWith("xRBX"))
                            {
                                // Multi-contract transfer dispatch is height-gated to mirror the
                                // validator: before V2TransferMultiHeight every node applies single
                                // semantics regardless of Function (a hybrid Data with multi
                                // Function plus valid single fields must apply identically on old
                                // and new nodes during a mixed-fleet window). xRBX never reaches
                                // here for multi — the validator rejects reserve multi senders.
                                string? vbtcV2Fn = null;
                                try { vbtcV2Fn = JObject.Parse(tx.Data)["Function"]?.ToObject<string?>(); } catch { }

                                if (vbtcV2Fn == Bitcoin.Services.VBTCService.MultiTransferFunction && block.Height >= Globals.V2TransferMultiHeight)
                                    TransferVBTCV2Multi(tx);
                                else
                                    TransferVBTCV2(tx);
                            }
                        }

                        // A registration or heartbeat bonds the validator's stake; an exit starts its wait.
                        if (tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_REGISTER || tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT
                            || tx.TransactionType == TransactionType.VBTC_V2_VALIDATOR_EXIT)
                        {
                            VerifiedXCore.Services.ValidatorBond.OnTransactionApplied(tx, block.Height);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_REQUEST)
                        {
                            // Multi-contract withdrawal dispatch keys on Function alone — NOT
                            // height-gated (deliberate; see the validator branch). Every node must
                            // run a binary that knows this Function before the first multi request
                            // is mined; an older binary rejects the shape and forks.
                            string? vbtcWdFn = null;
                            try { vbtcWdFn = JObject.Parse(tx.Data)["Function"]?.ToObject<string?>(); } catch { }

                            if (vbtcWdFn == Bitcoin.Services.VBTCService.MultiWithdrawalFunction)
                                RequestVBTCV2WithdrawalMulti(tx);
                            else
                                RequestVBTCV2Withdrawal(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE)
                        {
                            CompleteVBTCV2Withdrawal(tx);
                        }

                        // A requester raises the Bitcoin fee of its pinned withdrawal (watched vaults).
                        if (tx.TransactionType == TransactionType.VBTC_V2_COIN_PIN)
                        {
                            VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ApplyFeeBump(tx, tx.Height);
                        }

                        // FIND-018 Fix: vBTC V2 Withdrawal Cancellation/Vote governance handlers
                        if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_CANCEL)
                        {
                            CancelVBTCV2Withdrawal(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_VOTE)
                        {
                            VoteOnVBTCV2Cancellation(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_LOCK)
                        {
                            ApplyVBTCBridgeLock(tx, block.Height);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_UNLOCK)
                        {
                            ApplyVBTCBridgeUnlock(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK)
                        {
                            ApplyVBTCBridgePoolUnlock(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC)
                        {
                            ApplyVBTCBridgeExitToBTC(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE)
                        {
                            ApplyVBTCBridgeExitToBTCComplete(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL)
                        {
                            ApplyVBTCBridgeExitToBTCFail(tx);
                        }

                        if(tx.TransactionType == TransactionType.RESERVE)
                        {
                            var txData = tx.Data;
                            if (!string.IsNullOrWhiteSpace(txData))
                            {
                                var jobj = JObject.Parse(txData);
                                var function = (string?)jobj["Function"];
                                if (!string.IsNullOrWhiteSpace(function))
                                {
                                    switch (function)
                                    {
                                        case "Register()":
                                            await RegisterReserveAccount(tx); // NEW-12: awaited (ran concurrently with the rest of the block)
                                            break;
                                        case "CallBack()":
                                            var callBackHash = (string?)jobj["Hash"];
                                            // Awaited: these now mutate the tokenization ledger
                                            // (consensus state) and must not race the rest of
                                            // block application or the world-trei update.
                                            await CallBackReserveAccountTx(callBackHash,
                                                HistoricalTransactionExceptions.KeepsReversedTransferPending(tx, block.Height), block.Height); // historical, see there
                                            break;
                                        case "Recover()":
                                            string recoveryAddress = jobj["RecoveryAddress"].ToObject<string>();
                                            string recoverySigScript = jobj["RecoverySigScript"].ToObject<string>();
                                            // Fund-loss audit item 3: the sweep goes to the recovery address recorded in state
                                            // (validation now requires the carried one to equal it); a reserve without one is
                                            // left untouched rather than swept to whatever the transaction named.
                                            if (block.Height >= Globals.ReserveRecoverRulesHeight)
                                            {
                                                var recordedRecovery = GetSpecificAccountStateTrei(tx.FromAddress)?.RecoveryAccount;
                                                if (string.IsNullOrEmpty(recordedRecovery))
                                                {
                                                    ErrorLogUtility.LogError($"Recover() {tx.Hash} from {tx.FromAddress}: no recovery address in state; nothing swept.", "StateData.UpdateTreis()");
                                                    break;
                                                }
                                                recoveryAddress = recordedRecovery;
                                            }
                                            await RecoverReserveAccountTx(recoveryAddress, tx.FromAddress, block.StateRoot,
                                                HistoricalTransactionExceptions.KeepsReversedTransferPending(tx, block.Height), block.Height, block.Timestamp); // historical, see there
                                            break;
                                        default:
                                            break;
                                    }
                                }
                        }
                    }
                }

                    if (PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                        await PrivateTxLedgerService.ApplyBlockTransactionAsync(tx, block);

                    if (tx.TransactionType == TransactionType.VBTC_V2_BTC_REPORT)
                        VerifiedXCore.Bitcoin.Attestation.BtcAttestApply.ApplyReport(tx, block.Height);
                    if (tx.TransactionType == TransactionType.VBTC_V2_BTC_REORG)
                        VerifiedXCore.Bitcoin.Attestation.BtcAttestApply.ApplyRewind(tx, block.Height);
                    if (tx.TransactionType == TransactionType.VBTC_V2_VAULT_REKEY)
                        VerifiedXCore.Bitcoin.Attestation.BtcVaultKeys.ApplyRekey(tx, block.Height);

                    txTreiUpdateSuccessCount += 1;
                }
                catch(Exception ex)
                {
                    txFailList.Add(tx);
                    // A storage fault on this node (not what the transaction says) is reported like a failed block step, so a
                    // rebuild that hits one does not count as complete. Faults the transaction's data causes are not: they
                    // happen on every node alike, and some historical transactions do.
                    NoteApplyStepFailed(block.Height, ex);
                    var txJson = JsonConvert.SerializeObject(tx);
                    ErrorLogUtility.LogError($"Error Updating State Treis. Error: {ex.ToString()}", "StateData.UpdateTreis() - Part 1");
                    ErrorLogUtility.LogError($"TX Info. TX: {txJson}", "StateData.UpdateTreis() - Part 2");
                }
            }

            try
            {
                // Bitcoin attestation: the withdrawals this block requested are fixed on coins in an order no producer chooses.
                VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.OnBlockTransactionsApplied(block.Height);
            }
            catch (Exception ex)
            {
                blockStartFailed = true;
                BlockStartFailedAtHeight = block.Height;
                ErrorLogUtility.LogError($"Block {block.Height}: fixing the block's withdrawals on coins failed: {ex}", "StateData.UpdateTreis() - block end");
            }

            if(txTreiUpdateSuccessCount != txCount)
            {
                var txFailListJson = JsonConvert.SerializeObject(txFailList);
                ErrorLogUtility.LogError($"TX Success Count Failed to match tx Count. TX Fail List: {txFailListJson}", "StateData.UpdateTreis() - Part 3");
            }

            try { CarryDebtsLeftInTheBlock(block.Height); }
            catch (Exception ex) { NoteApplyStepFailed(block.Height, ex); }

            try
            {
                WorldTrei.UpdateWorldTrei(block);
            }
            finally
            {
                // A stale (too-low) StateWriteContext height would make later out-of-band writes
                // under-stamp and be missed by snapshot diffs — always clear, even on throw.
                StateWriteContext.Clear();
            }

            // ROOT-CAUSE GUARD: Report whether every transaction's state mutation actually applied.
            // A false here means the block was/will be committed to the chain while its state
            // effects are missing — the exact condition that produces a permanent
            // "new account with no balance" hole and gets a node stuck on a later block.
            return txTreiUpdateSuccessCount == txCount && !blockStartFailed && !_applyStepFailed;
        }

        /// <param name="asOf">The block whose timestamp made these transfers fall due: contract code is read as of it.</param>
        public static async Task<bool> UpdateTreiFromReserve(List<ReserveTransactions> txList, Block? asOf = null)
        {
            using var contractClock = asOf != null ? ContractClock.ForBlock(asOf.Timestamp, asOf.Height + 1) : null;
            var accStTrei = GetAccountStateTrei();
            var rtxDb = ReserveTransactions.GetReserveTransactionsDb();
            var txDb = Transaction.GetAll();

            // In one order on every node: a row's id is a random Guid made on each node, and the rows were read in that
            // order. The checks when a row falls due (still covered, the vault's holder limit) read what earlier rows of
            // the batch left, so two rows competing for one balance or one place on a ledger came out differently on
            // different nodes (fourth review, round 7).
            var appliedCleanly = true;
            foreach (var rtx in txList.OrderBy(x => x.ConfirmTimestamp).ThenBy(x => x.Height).ThenBy(x => x.Hash, StringComparer.Ordinal))
            {
                try
                {
                    // HAL-070 Fix + reserve-vBTC hardening: re-read the row's CURRENT status from
                    // the DB (not the caller's snapshot) — real idempotence against re-entry and
                    // replay. Anything not Pending has already been finalized/cancelled/recovered.
                    var currentRow = ReserveTransactions.GetRowForApply(rtxDb, rtx.Id);
                    if (currentRow == null || currentRow.ReserveTransactionStatus != ReserveTransactionStatus.Pending)
                    {
                        ErrorLogUtility.LogError($"UpdateTreiFromReserve skipped for hash {rtx.Hash} - status {(currentRow?.ReserveTransactionStatus.ToString() ?? "missing")}, not Pending", "StateData.UpdateTreiFromReserve()");
                        continue;
                    }

                    if(rtx.TransactionType == TransactionType.TX)
                    {
                        if (rtx.FromAddress != "Coinbase_TrxFees" && rtx.FromAddress != "Coinbase_BlkRwd" && rtx.ToAddress != "Reserve_Base")
                        {
                            var from = GetSpecificAccountStateTrei(rtx.FromAddress);
                            if (from != null)
                            {
                                // HAL-070 Fix: Lower-bound check on LockedBalance
                                if (from.LockedBalance >= rtx.Amount)
                                {
                                    from.LockedBalance -= rtx.Amount;
                                }
                                else
                                {
                                    ErrorLogUtility.LogError($"UpdateTreiFromReserve clamping LockedBalance to 0 for {from.Key} - attempted to subtract {rtx.Amount} from {from.LockedBalance}", "StateData.UpdateTreiFromReserve()");
                                    from.LockedBalance = 0;
                                }
                                await Written(accStTrei.UpdateSafeAsync(from), "from");
                            }

                        }

                        if (rtx.ToAddress != "Adnr_Base" &&
                            rtx.ToAddress != "DecShop_Base" &&
                            rtx.ToAddress != "Topic_Base" &&
                            rtx.ToAddress != "Vote_Base" &&
                            rtx.ToAddress != "Reserve_Base" &&
                            rtx.ToAddress != "Token_Base")
                        {
                            var to = GetSpecificAccountStateTrei(rtx.ToAddress);
                            if (rtx.TransactionType == TransactionType.TX)
                            {
                                if (to != null)
                                {
                                    if (rtx.FromAddress.StartsWith("xRBX"))
                                    {
                                        to.Balance += rtx.Amount;
                                        // HAL-070 Fix: Lower-bound check on LockedBalance
                                        if (to.LockedBalance >= rtx.Amount)
                                        {
                                            to.LockedBalance -= rtx.Amount;
                                        }
                                        else
                                        {
                                            ErrorLogUtility.LogError($"UpdateTreiFromReserve clamping LockedBalance to 0 for {to.Key} - attempted to subtract {rtx.Amount} from {to.LockedBalance}", "StateData.UpdateTreiFromReserve()");
                                            to.LockedBalance = 0;
                                        }

                                        await Written(accStTrei.UpdateSafeAsync(to), "to");
                                    }
                                }
                            }
                        }
                    }
                    if (rtx.TransactionType == TransactionType.NFT_TX ||
                        rtx.TransactionType == TransactionType.FTKN_TX ||
                        rtx.TransactionType == TransactionType.TKNZ_TX ||
                        rtx.TransactionType == TransactionType.SC_TX)
                    {
                        var scDataArray = JsonConvert.DeserializeObject<JArray>(rtx.Data);
                        var scData = scDataArray[0];
                        var function = (string?)scData["Function"];
                        var scUID = (string?)scData["ContractUID"];

                        if(function != null)
                        {
                            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);

                            if (function == "Transfer()")
                            {
                                // A delayed transfer takes effect only as the transfer state is waiting for: the
                                // contract is still its sender's and still marked for its recipient. Otherwise the
                                // row is a leftover (its lock was undone, or the contract has moved on since) and
                                // must not hand the contract to anyone.
                                if (scStateTreiRec != null && PathAuditRulesAtTime(rtx.ConfirmTimestamp) && !DeferredTransferStillStands(scStateTreiRec, rtx))
                                {
                                    ErrorLogUtility.LogError($"UpdateTreiFromReserve: transfer {rtx.Hash} of {scUID} from {rtx.FromAddress} to {rtx.ToAddress} no longer stands (owner {scStateTreiRec.OwnerAddress}, next owner {scStateTreiRec.NextOwner ?? "none"}); not applied.", "StateData.UpdateTreiFromReserve()");
                                    rtx.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
                                    await Written(rtxDb.UpdateSafeAsync(rtx), "rtx");
                                    await TransactionData.UpdateTxStatusForAllByHash(rtx.Hash, TransactionStatus.Failed);
                                    continue;
                                }
                                if (scStateTreiRec != null)
                                {
                                    
                                    CarryOwnerPosition(scStateTreiRec, rtx.ToAddress);
                                    scStateTreiRec.OwnerAddress = rtx.ToAddress;
                                    scStateTreiRec.NextOwner = null;
                                    scStateTreiRec.IsLocked = false;

                                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);

                                    // Sync VBTCContractV2.OwnerAddress on reserve transfer completion
                                    try
                                    {
                                        var vbtcContract = VBTCContractV2.GetContract(scUID);
                                        if (vbtcContract != null && vbtcContract.OwnerAddress != rtx.ToAddress)
                                        {
                                            vbtcContract.OwnerAddress = rtx.ToAddress;
                                            VBTCContractV2.UpdateContract(vbtcContract);
                                        }
                                    }
                                    catch { }
                                }
                            }

                            if(function == "TokenContractOwnerChange()")
                            {
                                if (scStateTreiRec != null)
                                {
                                    scStateTreiRec.OwnerAddress = rtx.ToAddress;
                                    scStateTreiRec.NextOwner = null;
                                    scStateTreiRec.IsLocked = false;
                                    if (scStateTreiRec.TokenDetails != null)
                                    {
                                        scStateTreiRec.TokenDetails.ContractOwner = rtx.ToAddress;
                                    }

                                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                                }
                            }
                        }
                    }

                    if (rtx.TransactionType == TransactionType.VBTC_V2_TRANSFER)
                    {
                        // Reserve vBTC sends defer the ledger move to here (unlock time reached).
                        // NOTE: vBTC data is a flat JObject, not the JArray shape used by SC types.
                        // Poison handling: consensus validated these fields at admission, so a parse
                        // failure means a corrupt row — mark it CalledBack (funds never moved; the
                        // sender keeps the balance) instead of retrying forever or, worse, falling
                        // through to a Confirmed flip without applying the ledger pair.
                        string? vbtcScUID = null;
                        decimal? vbtcAmount = null;
                        try
                        {
                            var vbtcData = JObject.Parse(rtx.Data);
                            vbtcScUID = vbtcData["ContractUID"]?.ToObject<string?>();
                            vbtcAmount = vbtcData["Amount"]?.ToObject<decimal?>();
                        }
                        catch { }

                        // A watched vault's ledger names at most so many addresses: judged now that it falls due, as other
                        // transfers sent meanwhile may have filled it (fourth review, round 6). The sender keeps its vBTC.
                        var holderLimitRefusal = string.IsNullOrEmpty(vbtcScUID) ? null
                            : VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.CreditRefusal(SmartContractStateTrei.GetSmartContractState(vbtcScUID), rtx.ToAddress);
                        if (holderLimitRefusal != null)
                        {
                            ErrorLogUtility.LogError($"UpdateTreiFromReserve: vBTC transfer {rtx.Hash} on {vbtcScUID} to {rtx.ToAddress} not applied: {holderLimitRefusal}", "StateData.UpdateTreiFromReserve()");
                            rtx.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
                            await Written(rtxDb.UpdateSafeAsync(rtx), "rtx");
                            await TransactionData.UpdateTxStatusForAllByHash(rtx.Hash, TransactionStatus.Failed);
                            continue;
                        }

                        if (!string.IsNullOrEmpty(vbtcScUID) && vbtcAmount.HasValue && vbtcAmount.Value > 0
                            && PathAuditRulesAtTime(rtx.ConfirmTimestamp) && !DeferredVbtcTransferStillCovered(vbtcScUID, rtx.FromAddress, vbtcAmount.Value))
                        {
                            // Judged again now that it falls due: nothing was taken from the sender when it was sent.
                            ErrorLogUtility.LogError($"UpdateTreiFromReserve: vBTC transfer {rtx.Hash} of {vbtcAmount.Value} on {vbtcScUID} from {rtx.FromAddress} is no longer covered by its sender's balance; not applied.", "StateData.UpdateTreiFromReserve()");
                            rtx.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
                            await Written(rtxDb.UpdateSafeAsync(rtx), "rtx");
                            await TransactionData.UpdateTxStatusForAllByHash(rtx.Hash, TransactionStatus.Failed);
                            continue;
                        }

                        if (!string.IsNullOrEmpty(vbtcScUID) && vbtcAmount.HasValue && vbtcAmount.Value > 0)
                        {
                            ApplyVBTCV2LedgerPair(vbtcScUID, rtx.FromAddress, rtx.ToAddress, vbtcAmount.Value, rtx.Hash);
                        }
                        else
                        {
                            ErrorLogUtility.LogError($"UpdateTreiFromReserve could not parse vBTC V2 transfer data for hash {rtx.Hash} - marking CalledBack (no funds moved)", "StateData.UpdateTreiFromReserve()");
                            rtx.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
                            await Written(rtxDb.UpdateSafeAsync(rtx), "rtx");
                            await TransactionData.UpdateTxStatusForAllByHash(rtx.Hash, TransactionStatus.Failed);
                            continue;
                        }
                    }

                    var rtxRec = ReserveTransactions.GetRowForApply(rtxDb, rtx.Id);
                    var hash = rtx.Hash;

                    if (rtxRec != null)
                    {
                        rtx.ReserveTransactionStatus = ReserveTransactionStatus.Confirmed;
                        await Written(rtxDb.UpdateSafeAsync(rtx), "rtx");
                    }

                    // Flip ALL local rows for the hash (same-wallet sends store two).
                    await TransactionData.UpdateTxStatusForAllByHash(hash, TransactionStatus.Success);
                }
                catch (Exception ex)
                {
                    // Runs after the block's state is applied, so the block is not flagged for it: a fault of this node's
                    // storage marks the state failed here. Left as it was, the row stayed Pending on this node alone and
                    // was judged again a block later, against other state than its peers' (fourth review, round 6).
                    if (IsNodeLocalFault(ex))
                    {
                        appliedCleanly = false;
                        StateTreiStatusService.SetFailed($"Reserve settlement {rtx.Hash} failed on this node: {ex.Message}");
                    }
                }
            }
            return appliedCleanly;
        }

        public static LiteDB.ILiteCollection<AccountStateTrei> GetAccountStateTrei()
        {
            var aTrei = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
            return aTrei;
            
        }

        public static AccountStateTrei? GetSpecificAccountStateTrei(string address)
        {
            var aTrei = GetAccountStateTrei();
            var account = aTrei.FindOne(x => x.Key == address);
            if (account == null)
            {
                return null;
            }
            else
            {
                return account;
            }
        }

        public static SmartContractStateTrei GetSpecificSmartContractStateTrei(string scUID)
        {
            var scTrei = DbContext.DB_SmartContractStateTrei.GetCollection<SmartContractStateTrei>(DbContext.RSRV_SCSTATE_TREI);
            var account = scTrei.Find(x => x.SmartContractUID == scUID).OrderBy(x => x.Id).FirstOrDefault(); // the oldest, as before the index (round 8)
            if (account == null)
            {
                return null;
            }
            else
            {
                return account;
            }
        }
        private static async Task RegisterReserveAccount(Transaction tx)
        {
            try
            {
                if (tx.Data != null)
                {
                    var jobj = JObject.Parse(tx.Data);
                    if (jobj != null)
                    {
                        var recoveryAddress = (string?)jobj["RecoveryAddress"];
                        if (recoveryAddress != null)
                        {
                            var stateDB = GetAccountStateTrei();
                            var reserveAccountLeaf = GetSpecificAccountStateTrei(tx.FromAddress);
                            if (reserveAccountLeaf != null)
                            {
                                reserveAccountLeaf.RecoveryAccount = recoveryAddress;
                                await stateDB.UpdateSafeAsync(reserveAccountLeaf);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        /// <param name="keepTransferPending">Only for the historical mainnet callbacks in
        /// HistoricalTransactionExceptions: refund as usual but leave the reserve row Pending, as the network applied them.</param>
        private static async Task CallBackReserveAccountTx(string? callBackHash, bool keepTransferPending = false, long height = 0)
        {
            try
            {
                if(callBackHash != null)
                {
                    var rTX = ReserveTransactions.GetTransactionsForApply(callBackHash);
                    if (rTX != null)
                    {
                        // HAL-070 Fix: Idempotence guard - skip if already processed
                        if (rTX.ReserveTransactionStatus != ReserveTransactionStatus.Pending)
                        {
                            ErrorLogUtility.LogError($"CallBack skipped for hash {callBackHash} - status is {rTX.ReserveTransactionStatus}, not Pending", "StateData.CallBackReserveAccountTx()");
                            return;
                        }

                        var rtxDb = ReserveTransactions.GetReserveTransactionsDb();

                        if(rTX.TransactionType == TransactionType.TX)
                        {
                            var stDb = GetAccountStateTrei();
                            var stateTreiFrom = GetSpecificAccountStateTrei(rTX.FromAddress);
                            var stateTreiTo = GetSpecificAccountStateTrei(rTX.ToAddress);

                            if (stateTreiFrom != null)
                            {
                                //return amount to From address
                                // HAL-070 Fix: Lower-bound check on LockedBalance
                                if (stateTreiFrom.LockedBalance >= rTX.Amount)
                                {
                                    stateTreiFrom.LockedBalance -= rTX.Amount;
                                }
                                else
                                {
                                    ErrorLogUtility.LogError($"CallBack clamping LockedBalance to 0 for {stateTreiFrom.Key} - attempted to subtract {rTX.Amount} from {stateTreiFrom.LockedBalance}", "StateData.CallBackReserveAccountTx()");
                                    stateTreiFrom.LockedBalance = 0;
                                }
                                stateTreiFrom.Balance += rTX.Amount;
                                if (stDb != null)
                                    await Written(stDb.UpdateSafeAsync(stateTreiFrom), "stateTreiFrom");

                                var rLocalAccount = ReserveAccount.GetReserveAccountSingle(stateTreiFrom.Key);
                                if (rLocalAccount != null)
                                {
                                    var rDb = ReserveAccount.GetReserveAccountsDb();
                                    // HAL-070 Fix: Lower-bound check on LockedBalance
                                    if (rLocalAccount.LockedBalance >= rTX.Amount)
                                    {
                                        rLocalAccount.LockedBalance -= rTX.Amount;
                                    }
                                    else
                                    {
                                        ErrorLogUtility.LogError($"CallBack clamping ReserveAccount LockedBalance to 0 for {rLocalAccount.Address} - attempted to subtract {rTX.Amount} from {rLocalAccount.LockedBalance}", "StateData.CallBackReserveAccountTx()");
                                        rLocalAccount.LockedBalance = 0;
                                    }
                                    rLocalAccount.AvailableBalance += rTX.Amount;
                                    if (rDb != null)
                                        await rDb.UpdateSafeAsync(rLocalAccount);
                                }
                            }
                            // Read again from the path-audit rules: the sender's write above may be this same account (a
                            // reserve calling back its payment to itself, on testnet), and the copy read before it overwrote
                            // the refund (fifth review, round 28).
                            if (stateTreiTo != null && height >= Globals.PathAuditRulesHeight)
                                stateTreiTo = GetSpecificAccountStateTrei(rTX.ToAddress) ?? stateTreiTo;
                            if (stateTreiTo != null)
                            {
                                //remove amount from locked To address
                                // HAL-070 Fix: Lower-bound check on LockedBalance
                                if (stateTreiTo.LockedBalance >= rTX.Amount)
                                {
                                    stateTreiTo.LockedBalance -= rTX.Amount;
                                }
                                else
                                {
                                    ErrorLogUtility.LogError($"CallBack clamping LockedBalance to 0 for {stateTreiTo.Key} - attempted to subtract {rTX.Amount} from {stateTreiTo.LockedBalance}", "StateData.CallBackReserveAccountTx()");
                                    stateTreiTo.LockedBalance = 0;
                                }
                                if (stDb != null)
                                    await Written(stDb.UpdateSafeAsync(stateTreiTo), "stateTreiTo");

                                var localAccount = AccountData.GetSingleAccount(stateTreiTo.Key);
                                if (localAccount != null)
                                {
                                    var accountDB = AccountData.GetAccounts();
                                    // HAL-070 Fix: Lower-bound check on LockedBalance
                                    if (localAccount.LockedBalance >= rTX.Amount)
                                    {
                                        localAccount.LockedBalance -= rTX.Amount;
                                    }
                                    else
                                    {
                                        ErrorLogUtility.LogError($"CallBack clamping Account LockedBalance to 0 for {localAccount.Address} - attempted to subtract {rTX.Amount} from {localAccount.LockedBalance}", "StateData.CallBackReserveAccountTx()");
                                        localAccount.LockedBalance = 0;
                                    }
                                    if (accountDB != null)
                                        await accountDB.UpdateSafeAsync(localAccount);
                                }

                                var rLocalAccount = ReserveAccount.GetReserveAccountSingle(stateTreiTo.Key);
                                if (rLocalAccount != null)
                                {
                                    var rDb = ReserveAccount.GetReserveAccountsDb();
                                    // HAL-070 Fix: Lower-bound check on LockedBalance
                                    if (rLocalAccount.LockedBalance >= rTX.Amount)
                                    {
                                        rLocalAccount.LockedBalance -= rTX.Amount;
                                    }
                                    else
                                    {
                                        ErrorLogUtility.LogError($"CallBack clamping ReserveAccount LockedBalance to 0 for {rLocalAccount.Address} - attempted to subtract {rTX.Amount} from {rLocalAccount.LockedBalance}", "StateData.CallBackReserveAccountTx()");
                                        rLocalAccount.LockedBalance = 0;
                                    }
                                    if (rDb != null)
                                        await rDb.UpdateSafeAsync(rLocalAccount);
                                }
                            }
                        }

                        // Unwind the pre-transfer lock for ALL smart-contract transfer envelopes,
                        // not just NFT_TX — a called-back TKNZ_TX/SC_TX transfer previously left
                        // NextOwner set + IsLocked=true forever, permanently bricking the contract
                        // (any further Transfer() is rejected while locked/NextOwner is set).
                        if(rTX.TransactionType == TransactionType.NFT_TX ||
                           rTX.TransactionType == TransactionType.TKNZ_TX ||
                           rTX.TransactionType == TransactionType.SC_TX)
                        {
                          // Own try/catch: some legacy TKNZ_TX rows carry flat-JObject data and
                          // the JArray parse throws — that must NEVER skip the unconditional
                          // status flip below, or the row stays Pending and the finalizer later
                          // applies a transfer that was called back.
                          try
                          {
                            var scDataArray = JsonConvert.DeserializeObject<JArray>(rTX.Data);
                            var scData = scDataArray[0];
                            var function = (string?)scData["Function"];
                            var scUID = (string?)scData["ContractUID"];

                            if(scUID != null)
                            {
                                var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
                                // A callback undoes the lock its own transfer set, and no other: any called-back row
                                // naming a contract used to clear that contract's lock, whoever owned it and whatever
                                // the row's function was (second path audit, 14 October 2026).
                                var ownLock = height < Globals.PathAuditRulesHeight
                                    || (function == "Transfer()" || function == "TokenContractOwnerChange()") && scStateTrei != null && DeferredTransferStillStands(scStateTrei, rTX);
                                if(scStateTrei != null && ownLock)
                                {
                                    var scDb = SmartContractStateTrei.GetSCST();
                                    if(scDb != null)
                                    {
                                        scStateTrei.NextOwner = null;
                                        scStateTrei.IsLocked = false;
                                        await WriteContractStateAsync(scDb, scStateTrei);
                                    }

                                    // Re-sync any local vBTC V2 record to the (unchanged) state-trei
                                    // owner — reverts a recipient's prematurely stamped ownership on
                                    // every node applying the callback.
                                    try
                                    {
                                        var vbtcContract = VBTCContractV2.GetContract(scUID);
                                        if (vbtcContract != null && vbtcContract.OwnerAddress != scStateTrei.OwnerAddress)
                                        {
                                            vbtcContract.OwnerAddress = scStateTrei.OwnerAddress;
                                            VBTCContractV2.UpdateContract(vbtcContract);
                                        }
                                    }
                                    catch { }
                                }
                            }
                          }
                          catch (Exception unwindEx)
                          {
                              // A watched vault's write throws on a storage fault: flagged, not left locked on this node alone (round 9).
                              NoteApplyStepFailed(height, unwindEx);
                              ErrorLogUtility.LogError($"CallBack SC unwind failed for {rTX.Hash} (status flip proceeds): {unwindEx.Message}", "StateData.CallBackReserveAccountTx()");
                          }
                        }

                        // VBTC_V2_TRANSFER callback needs no ledger unwind: reserve sends defer the
                        // ledger move until finalize, which this callback prevents. The generic
                        // status flip below is all that is required.


                        // Flip the reserve row UNCONDITIONALLY — this runs on every node at
                        // state-apply, and most nodes hold no local wallet TX for the hash.
                        // Gating this flip on the wallet row left the row Pending on non-wallet
                        // nodes, where the finalizer would later apply a transfer that was
                        // called back everywhere else (consensus divergence).
                        if (!keepTransferPending)
                        {
                            rTX.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
                            if (rtxDb != null)
                                await Written(rtxDb.UpdateSafeAsync(rTX), "rTX");
                        }

                        // Local wallet rows (0, 1, or 2 — same-wallet sends store both the
                        // sender and recipient copies under one hash).
                        await TransactionData.UpdateTxStatusForAllByHash(rTX.Hash, TransactionStatus.CalledBack);
                    }
                }
            }
            catch (Exception ex) { NoteApplyStepFailed(height, ex); } // a fault of this node's storage is flagged (fourth review, round 6)
        }
        /// <param name="keepTransfersPending">Only for the historical mainnet recovery in HistoricalTransactionExceptions:
        /// redirect as usual but leave the reserve rows Pending, as the network applied it.</param>
        private static async Task RecoverReserveAccountTx(string? _recoveryAddress, string _fromAddress, string stateRoot, bool keepTransfersPending = false, long height = 0, long blockTimestamp = 0)
        {
            try
            {
                var stDb = GetAccountStateTrei();
                var rTXList = ReserveTransactions.GetTransactionList(_fromAddress);

                if(rTXList?.Count() > 0)
                {
                    // HAL-070 Fix: Filter to only Pending transactions for recovery
                    var pendingTXList = rTXList.Where(x => x.ReserveTransactionStatus == ReserveTransactionStatus.Pending)
                        .OrderBy(x => x.ConfirmTimestamp).ThenBy(x => x.Height).ThenBy(x => x.Hash, StringComparer.Ordinal).ToList(); // one order on every node (round 7)
                    // From the path-audit rules a payment whose unlock time had passed by the block's time (with the margin)
                    // is left to settle, as a call-back of it is refused: Recover() took it back a block late, or across a
                    // halt, from a payee that had waited for it (fifth review, round 29).
                    if (height >= Globals.PathAuditRulesHeight && blockTimestamp > 0)
                        // Left only when its settlement is judged by the same rules (still covered, still stands): a row that
                        // fell due just before the activation block, settling under the older rules, was paid to its payee
                        // after the sweep below had moved the vBTC behind it (round 30).
                        pendingTXList = pendingTXList.Where(x => x.ConfirmTimestamp > LedgerIntegrityRules.NowForAgeRules(blockTimestamp)
                            || !PathAuditRulesAtTime(x.ConfirmTimestamp)).ToList();
                    
                    foreach(var rTX in pendingTXList) 
                    {
                        var rtxDb = ReserveTransactions.GetReserveTransactionsDb();
                        var stateTreiFrom = GetSpecificAccountStateTrei(rTX.FromAddress);
                        if (rTX.TransactionType == TransactionType.TX)
                        {
                            var stateTreiTo = GetSpecificAccountStateTrei(rTX.ToAddress);

                            if (stateTreiFrom != null)
                            {
                                var recoveryAddress = stateTreiFrom.RecoveryAccount;
                                if (recoveryAddress != null)
                                {
                                    // HAL-070 Fix: Lower-bound check on LockedBalance
                                    if (stateTreiFrom.LockedBalance >= rTX.Amount)
                                    {
                                        stateTreiFrom.LockedBalance -= rTX.Amount;
                                    }
                                    else
                                    {
                                        ErrorLogUtility.LogError($"Recover clamping LockedBalance to 0 for {stateTreiFrom.Key} - attempted to subtract {rTX.Amount} from {stateTreiFrom.LockedBalance}", "StateData.RecoverReserveAccountTx()");
                                        stateTreiFrom.LockedBalance = 0;
                                    }
                                    if (stDb != null)
                                        await Written(stDb.UpdateSafeAsync(stateTreiFrom), "stateTreiFrom");

                                    var rLocalAccount = ReserveAccount.GetReserveAccountSingle(stateTreiFrom.Key);
                                    if (rLocalAccount != null)
                                    {
                                        var rDb = ReserveAccount.GetReserveAccountsDb();
                                        // HAL-070 Fix: Lower-bound check on LockedBalance
                                        if (rLocalAccount.LockedBalance >= rTX.Amount)
                                        {
                                            rLocalAccount.LockedBalance -= rTX.Amount;
                                        }
                                        else
                                        {
                                            ErrorLogUtility.LogError($"Recover clamping ReserveAccount LockedBalance to 0 for {rLocalAccount.Address} - attempted to subtract {rTX.Amount} from {rLocalAccount.LockedBalance}", "StateData.RecoverReserveAccountTx()");
                                            rLocalAccount.LockedBalance = 0;
                                        }
                                        if (rDb != null)
                                            await rDb.UpdateSafeAsync(rLocalAccount);
                                    }

                                    var stateTreiRecovery = GetSpecificAccountStateTrei(recoveryAddress);
                                    if (stateTreiRecovery != null)
                                    {
                                        stateTreiRecovery.Balance += rTX.Amount;
                                        if (stDb != null)
                                            await Written(stDb.UpdateSafeAsync(stateTreiRecovery), "stateTreiRecovery");
                                    }
                                    else
                                    {
                                        var acctStateTreiTo = new AccountStateTrei
                                        {
                                            Key = recoveryAddress,
                                            Nonce = 0,
                                            Balance = rTX.Amount, //subtract from the address
                                            StateRoot = stateRoot
                                        };

                                        if (stDb != null)
                                            await Written(stDb.InsertSafeAsync(acctStateTreiTo), "acctStateTreiTo");

                                    }

                                    var localAccount = AccountData.GetSingleAccount(recoveryAddress);
                                    if (localAccount != null)
                                    {
                                        var accountDB = AccountData.GetAccounts();
                                        localAccount.Balance += rTX.Amount;
                                        if (accountDB != null)
                                            await accountDB.UpdateSafeAsync(localAccount);
                                    }
                                }
                            }

                            // Read again from the path-audit rules: a payment to the reserve's own recovery address was
                            // credited to it just above, and the copy read before that overwrote the credit - the payment left
                            // the supply (fifth review, round 28).
                            if (stateTreiTo != null && height >= Globals.PathAuditRulesHeight)
                                stateTreiTo = GetSpecificAccountStateTrei(rTX.ToAddress) ?? stateTreiTo;
                            if (stateTreiTo != null)
                            {
                                // HAL-070 Fix: Lower-bound check on LockedBalance
                                if (stateTreiTo.LockedBalance >= rTX.Amount)
                                {
                                    stateTreiTo.LockedBalance -= rTX.Amount;
                                }
                                else
                                {
                                    ErrorLogUtility.LogError($"Recover clamping LockedBalance to 0 for {stateTreiTo.Key} - attempted to subtract {rTX.Amount} from {stateTreiTo.LockedBalance}", "StateData.RecoverReserveAccountTx()");
                                    stateTreiTo.LockedBalance = 0;
                                }
                                if (stDb != null)
                                    await Written(stDb.UpdateSafeAsync(stateTreiTo), "stateTreiTo");
                            }
                        }

                        if (rTX.TransactionType == TransactionType.NFT_TX)
                        {
                            var scDataArray = JsonConvert.DeserializeObject<JArray>(rTX.Data);
                            var scData = scDataArray[0];
                            var function = (string?)scData["Function"];
                            var scUID = (string?)scData["ContractUID"];
                            var recoveryAddress = stateTreiFrom?.RecoveryAccount;

                            if (scUID != null)
                            {
                                var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
                                if (scStateTrei != null)
                                {
                                    var scDb = SmartContractStateTrei.GetSCST();
                                    // Only a contract the recovered account still owns goes to its recovery address: a
                                    // leftover row of a transfer long since made must not take the contract back.
                                    if (scDb != null && (height < Globals.PathAuditRulesHeight || scStateTrei.OwnerAddress == rTX.FromAddress))
                                    {
                                        if (recoveryAddress != null)
                                        {
                                            CarryOwnerPosition(scStateTrei, recoveryAddress);
                                            scStateTrei.OwnerAddress = recoveryAddress;
                                            scStateTrei.NextOwner = null;
                                            scStateTrei.IsLocked = false;
                                            await WriteContractStateAsync(scDb, scStateTrei);
                                        }

                                    }
                                }
                            }
                        }

                        if (rTX.TransactionType == TransactionType.VBTC_V2_TRANSFER)
                        {
                            // In-flight reserve vBTC send: the ledger never moved, so redirect the
                            // amount to the recovery address instead of the original recipient.
                            try
                            {
                                var recoveryAddress = stateTreiFrom?.RecoveryAccount;
                                if (recoveryAddress != null && rTX.Data != null)
                                {
                                    var vbtcData = JObject.Parse(rTX.Data);
                                    var vbtcScUID = vbtcData["ContractUID"]?.ToObject<string?>();
                                    var vbtcAmount = vbtcData["Amount"]?.ToObject<decimal?>();
                                    if (!string.IsNullOrEmpty(vbtcScUID) && vbtcAmount.HasValue && vbtcAmount.Value > 0
                                        && (height < Globals.PathAuditRulesHeight || DeferredVbtcTransferStillCovered(vbtcScUID, rTX.FromAddress, vbtcAmount.Value)))
                                    {
                                        ApplyVBTCV2LedgerPair(vbtcScUID, rTX.FromAddress, recoveryAddress, vbtcAmount.Value, rTX.Hash);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                NoteApplyStepFailed(height, ex); // a fault of this node's storage is flagged (fourth review, round 7)
                                ErrorLogUtility.LogError($"Recover failed to redirect pending vBTC transfer {rTX.Hash}: {ex.Message}", "StateData.RecoverReserveAccountTx()");
                            }
                        }

                        // Flip the reserve row UNCONDITIONALLY (see CallBack note above) —
                        // on non-wallet nodes a Pending row would otherwise be finalized later,
                        // applying the ORIGINAL transfer on top of the recovery redirect.
                        if (!keepTransfersPending)
                        {
                            rTX.ReserveTransactionStatus = ReserveTransactionStatus.Recovered;
                            if (rtxDb != null)
                                await Written(rtxDb.UpdateSafeAsync(rTX), "rTX");
                        }

                        await TransactionData.UpdateTxStatusForAllByHash(rTX.Hash, TransactionStatus.Recovered);
                    }
                }

                //find current NFTs from the from address reserve and send to recovery
                //find balance for from address reserve and send to recovery
                var rsrvAccount = GetSpecificAccountStateTrei(_fromAddress);
                var _stateTreiRecovery = GetSpecificAccountStateTrei(_recoveryAddress);

                if (_stateTreiRecovery != null)
                {
                    _stateTreiRecovery.Balance += rsrvAccount.Balance;
                    if (stDb != null)
                        await Written(stDb.UpdateSafeAsync(_stateTreiRecovery), "_stateTreiRecovery");
                }
                else
                {
                    var acctStateTreiTo = new AccountStateTrei
                    {
                        Key = _recoveryAddress,
                        Nonce = 0,
                        Balance = rsrvAccount.Balance, //subtract from the address
                        StateRoot = stateRoot
                    };

                    if (stDb != null)
                        await Written(stDb.InsertSafeAsync(acctStateTreiTo), "acctStateTreiTo");
                }

                // From the path-audit rules a reserve moves no token (round 9's rule), so its token balances go with the
                // recovery too, or they were frozen for good (fifth review, round 28).
                if (height >= Globals.PathAuditRulesHeight && rsrvAccount.TokenAccounts?.Count > 0)
                {
                    var recoveryRow = GetSpecificAccountStateTrei(_recoveryAddress);
                    if (recoveryRow != null)
                    {
                        recoveryRow.TokenAccounts ??= new List<TokenAccount>();
                        foreach (var token in rsrvAccount.TokenAccounts.Where(t => t != null && t.Balance > 0M))
                        {
                            var held = recoveryRow.TokenAccounts.FirstOrDefault(t => t.SmartContractUID == token.SmartContractUID);
                            if (held == null)
                                recoveryRow.TokenAccounts.Add(TokenAccount.CreateTokenAccount(token.SmartContractUID, token.TokenName, token.TokenTicker, token.Balance, token.DecimalPlaces));
                            else
                                held.Balance += token.Balance;
                            token.Balance = 0M;
                        }
                        await Written(stDb.UpdateSafeAsync(recoveryRow), "recoveryRow tokens");
                    }
                }

                rsrvAccount.Balance = 0.0M;
                rsrvAccount.LockedBalance = 0.0M;

                await Written(stDb.UpdateSafeAsync(rsrvAccount), "rsrvAccount");

                var _scDb = SmartContractStateTrei.GetSCST();

                if(_scDb != null)
                {
                    var scList = _scDb.Query().Where(x => x.OwnerAddress == _fromAddress).ToList().OrderBy(x => x.Id).ToList(); // in the scan's order (not indexed: owners are free text, round 34)
                    if(scList?.Count > 0 )
                    {
                        foreach(var sc in scList)
                        {
                            CarryOwnerPosition(sc, _recoveryAddress);
                            sc.OwnerAddress = _recoveryAddress;
                            sc.NextOwner = null;
                            sc.IsLocked = false;
                            await WriteContractStateAsync(_scDb, sc);
                        }
                    }

                    // ... and a token contract it owns: from the path-audit rules a reserve cannot pause, mint or hand it
                    // on, and its owner powers (a pause in force) were stuck for good (round 28).
                    if (height >= Globals.PathAuditRulesHeight && _recoveryAddress != null)
                    {
                        foreach (var token in _scDb.Query().Where(x => x.TokenDetails.ContractOwner == _fromAddress).ToList().Where(x => x.TokenDetails != null).OrderBy(x => x.Id).ToList())
                        {
                            token.TokenDetails!.ContractOwner = _recoveryAddress;
                            await WriteContractStateAsync(_scDb, token);
                        }
                    }

                    // Seize vBTC balances: contract ownership alone does not move tokenization
                    // ledger balances, so sweep every contract where the seized reserve address
                    // nets positive and move the full balance to the recovery address. Runs after
                    // the per-pending-TX redirects above so only the remainder moves.
                    if (_recoveryAddress != null)
                    {
                        // FindAll + in-memory filter: a LiteDB BsonExpression over a list property
                        // is unverified territory, and a throw here is swallowed by the outer catch,
                        // silently skipping the entire seizure. Recovery is rare; perf is fine.
                        // Re-run idempotence is implicit: the sweep's own debit row drives the
                        // address's net to 0, so a second pass writes nothing.
                        var vbtcHoldings = _scDb.FindAll()
                            .Where(x => x.SCStateTreiTokenizationTXes != null &&
                                        x.SCStateTreiTokenizationTXes.Any(t => t.FromAddress == _fromAddress || t.ToAddress == _fromAddress))
                            .ToList();

                        foreach (var sc in vbtcHoldings)
                        {
                            try
                            {
                                var net = sc.SCStateTreiTokenizationTXes
                                    .Where(t => t.FromAddress == _fromAddress || t.ToAddress == _fromAddress)
                                    .Sum(t => t.Amount);

                                if (net > 0)
                                {
                                    ApplyVBTCV2LedgerPair(sc.SmartContractUID, _fromAddress, _recoveryAddress, net, $"RECOVERY:{_fromAddress}");
                                }
                            }
                            catch (Exception ex)
                            {
                                NoteApplyStepFailed(height, ex);
                                ErrorLogUtility.LogError($"Recover failed to sweep vBTC balance on {sc.SmartContractUID}: {ex.Message}", "StateData.RecoverReserveAccountTx()");
                            }
                        }
                    }
                }

            }
            catch (Exception ex) { NoteApplyStepFailed(height, ex); } // a fault of this node's storage is flagged (fourth review, round 6)
        }
        private static void AddNewDecShop(Transaction tx)
        {
            try
            {
                if (tx.Data != null)
                {
                    var jobj = JObject.Parse(tx.Data);
                    if (jobj != null)
                    {
                        DecShop? decshop = jobj["DecShop"]?.ToObject<DecShop>();
                        if (decshop != null)
                        {
                            decshop.OriginalBlockHeight = tx.Height;
                            decshop.OriginalTXHash = tx.Hash;
                            decshop.LatestBlockHeight = tx.Height;
                            decshop.LatestTXHash = tx.Hash;
                            decshop.IsPublished = true;
                            decshop.NeedsPublishToNetwork = false;
                            var result = DecShop.SaveDecShopStateTrei(decshop);
                        }
                    }
                }
            }
            catch { }
        }
        private static void UpdateDecShop(Transaction tx)
        {
            try
            {
                if (tx.Data != null)
                {
                    var jobj = JObject.Parse(tx.Data);
                    if (jobj != null)
                    {
                        DecShop? decshop = jobj["DecShop"]?.ToObject<DecShop>();
                        if (decshop != null)
                        {
                            decshop.LatestBlockHeight = tx.Height;
                            decshop.LatestTXHash = tx.Hash;
                            decshop.UpdateTimestamp = TimeUtil.GetTime();
                            decshop.NeedsPublishToNetwork = false;
                            decshop.IsPublished = true;
                            var result = DecShop.UpdateDecShopStateTrei(decshop);
                        }
                    }
                }
            }
            catch { }
        }

        private static async Task DeleteDecShop(Transaction tx)
        {
            try
            {
                if (tx.Data != null)
                {
                    var jobj = JObject.Parse(tx.Data);
                    if(jobj != null)
                    {
                        var uId = (string?)jobj["UniqueId"];
                        if(!string.IsNullOrEmpty(uId))
                        {
                            var db = DecShop.DecShopTreiDb();
                            var decShop = DecShop.GetDecShopStateTreiLeaf(uId);
                            if(db != null)
                            {
                                if(decShop != null)
                                {
                                    await db.DeleteSafeAsync(decShop.Id);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }
        private static void AddNewBTCAdnr(Transaction tx)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var name = (string?)jobj["Name"];
                var btcAddress = (string?)jobj["BTCAddress"];

                if(btcAddress != null)
                {
                    BitcoinAdnr adnr = new BitcoinAdnr
                    {
                        BTCAddress = btcAddress,
                        RBXAddress = tx.FromAddress,
                        Name = name + ".btc",
                        Timestamp = tx.Timestamp,
                        TxHash = tx.Hash
                    };

                    BitcoinAdnr.SaveAdnr(adnr);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError("Failed to deserialized TX Data for BTC ADNR", "StateData.AddNewBTCAdnr()");
            }
        }

        /// <summary>
        /// Moves a BTC ADNR. It looped until the name was found: a second transfer (or a delete, then a transfer) of one
        /// sender's name in one block passes validation, which reads the state before the block, and found nothing at
        /// apply, so every node spun on that block for good (fifth review, round 26). A name that is not there is left.
        /// </summary>
        /// <remarks>
        /// From <see cref="Globals.BtcNameOneRecordPerAddressHeight"/> a transfer to an address that already holds a name
        /// (another transfer to it earlier in the block) is left, so an address keeps one record, and the name moved is the
        /// sender's own (validation checks the sender holds one there, but an address could hold two records before).
        /// </remarks>
        internal static async Task TransferBTCAdnr(Transaction tx, long height)
        {
            try
            {
                if (tx.Data == null) return;
                var jobj = JObject.Parse(tx.Data);
                var BTCToAddress = (string?)jobj["BTCToAddress"];
                var BTCFromAddress = (string?)jobj["BTCFromAddress"];

                var adnrs = BitcoinAdnr.GetBitcoinAdnr();
                var ownOnly = height >= Globals.BtcNameOneRecordPerAddressHeight;
                var adnr = height >= Globals.BtcNameSignerOwnsAddressHeight
                    ? (adnrs == null ? null : SignedBtcNameRecord(adnrs, BTCFromAddress, tx.FromAddress, (string?)jobj["BTCToMessage"]))
                    : ownOnly
                        ? adnrs?.FindOne(x => x.BTCAddress == BTCFromAddress && x.RBXAddress == tx.FromAddress)
                        : adnrs?.FindOne(x => x.BTCAddress == BTCFromAddress);
                if (adnr == null)
                {
                    ErrorLogUtility.LogError($"BTC ADNR transfer {tx.Hash}: no name for {BTCFromAddress} (moved or deleted earlier in the block); nothing applied.", "StateData.TransferBTCAdnr()");
                    return;
                }
                if (height >= Globals.BtcNameOneRecordPerAddressHeight && adnrs!.FindOne(x => x.BTCAddress == BTCToAddress) != null)
                {
                    ErrorLogUtility.LogError($"BTC ADNR transfer {tx.Hash}: {BTCToAddress} already holds a name (moved there earlier in the block); nothing applied.", "StateData.TransferBTCAdnr()");
                    return;
                }
                adnr.BTCAddress = BTCToAddress;
                adnr.RBXAddress = tx.ToAddress;
                adnr.TxHash = tx.Hash;
                await adnrs!.UpdateSafeAsync(adnr);
            }
            catch { }
        }

        /// <summary>
        /// From <see cref="Globals.BtcNameSignerOwnsAddressHeight"/> a transfer moves the sender's record at the address
        /// whose name the receiver signed, so validation and apply agree on the record even when the address holds two.
        /// </summary>
        internal static BitcoinAdnr? SignedBtcNameRecord(LiteDB.ILiteCollection<BitcoinAdnr> adnrs, string? btcAddress, string owner, string? acceptMessage)
        {
            var signedName = Bitcoin.Services.BtcNameSignatures.SignedName(acceptMessage);
            if (signedName == null) return null;
            return adnrs.Find(x => x.BTCAddress == btcAddress && x.RBXAddress == owner)
                .FirstOrDefault(x => string.Equals(x.Name, signedName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Deletes a BTC name. From <see cref="Globals.BtcNameOneRecordPerAddressHeight"/> only the sender's own record at the
        /// address goes; before, every record there did.
        /// </summary>
        internal static void DeleteBTCAdnr(Transaction tx, long height)
        {
            try
            {
                if(tx.Data != null)
                {
                    var jobj = JObject.Parse(tx.Data);
                    var BTCFromAddress = (string?)jobj["BTCFromAddress"];
                    if (height >= Globals.BtcNameOneRecordPerAddressHeight)
                        BitcoinAdnr.DeleteAdnr(BTCFromAddress, tx.FromAddress);
                    else
                        BitcoinAdnr.DeleteAdnr(BTCFromAddress);
                }
                
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to delete BTC ADNR at state level! Error: {ex}", "StateData.DeleteBTCAdnr()");
            }
        }

        private static void AddNewAdnr(Transaction tx)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var name = (string?)jobj["Name"];
                Adnr adnr = new Adnr();

                adnr.Address = tx.FromAddress;
                adnr.Timestamp = tx.Timestamp;
                adnr.Name = Globals.V4Height > Globals.LastBlock.Height ? name + ".rbx" : name + ".vfx";
                adnr.TxHash = tx.Hash;

                Adnr.SaveAdnr(adnr);
                
            }
            catch(Exception ex)
            {                
                ErrorLogUtility.LogError("Failed to add ADNR at state level!", "StateData.AddNewAdnr()");
            }
        }
        /// <summary>Moves an ADNR; as <see cref="TransferBTCAdnr"/>, a name that is not there is left (it looped for good; round 26).</summary>
        private static async Task TransferAdnr(Transaction tx)
        {
            var adnrs = Adnr.GetAdnr();
            var adnr = adnrs?.FindOne(x => x.Address == tx.FromAddress);
            if (adnr == null)
            {
                ErrorLogUtility.LogError($"ADNR transfer {tx.Hash}: no name for {tx.FromAddress} (moved or deleted earlier in the block); nothing applied.", "StateData.TransferAdnr()");
                return;
            }
            adnr.Address = tx.ToAddress;
            adnr.TxHash = tx.Hash;
            await adnrs!.UpdateSafeAsync(adnr);
        }

        private static void DeleteAdnr(Transaction tx)
        {
            try
            {
                Adnr.DeleteAdnr(tx.FromAddress);
            }
            catch (Exception ex)
            {                
                ErrorLogUtility.LogError("Failed to delete ADNR State Level!", "StateData.DeleteAdnr()");
            }
        }

        private static void AddNewlyMintedContract(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            
            // Handle both JArray and JObject formats defensively
            string function = "";
            string data = "";
            string scUID = "";
            string md5List = "";
            bool skip = false;
            JToken? scData = null;
            
            try
            {
                var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
                scData = scDataArray[0];
                function = (string?)scData["Function"];
                data = (string?)scData["Data"];
                scUID = (string?)scData["ContractUID"];
                md5List = (string?)scData["MD5List"];
                skip = true;
            }
            catch { }
            
            try
            {
                if (!skip)
                {
                    var jobj = JObject.Parse(tx.Data);
                    function = jobj["Function"]?.ToObject<string?>();
                    data = jobj["Data"]?.ToObject<string?>();
                    scUID = jobj["ContractUID"]?.ToObject<string?>();
                    md5List = jobj["MD5List"]?.ToObject<string?>();
                }
            }
            catch { }
            
            if (!string.IsNullOrWhiteSpace(scUID))
            {


                scST.ContractData = data;
                scST.MinterAddress = tx.FromAddress;
                scST.OwnerAddress = tx.FromAddress;
                scST.SmartContractUID = scUID;
                scST.Nonce = 0;
                scST.MD5List = md5List;

                try
                {
                    var sc = SmartContractMain.GenerateSmartContractInMemory(data);
                    if (sc.Features != null)
                    {
                        var evoFeatures = sc.Features.Where(x => x.FeatureName == FeatureName.Evolving).Select(x => x.FeatureFeatures).FirstOrDefault();
                        var isDynamic = false;
                        if (evoFeatures != null)
                        {
                            var evoFeatureList = (List<EvolvingFeature>)evoFeatures;
                            foreach (var feature in evoFeatureList)
                            {
                                var evoFeature = (EvolvingFeature)feature;
                                if (evoFeature.IsDynamic == true)
                                    isDynamic = true;
                            }
                        }

                        if (!isDynamic)
                            scST.MinterManaged = true;
                    }
                }
                catch { }

                //Save to state trei
                SmartContractStateTrei.SaveSmartContract(scST);
            }
        }
        private static void UpdateSmartContract(Transaction tx)
        {
            try
            {
                SmartContractStateTrei scST = new SmartContractStateTrei();
                var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
                var scData = scDataArray[0];
                if (scData != null)
                {
                    var function = (string?)scData["Function"];
                    var data = (string?)scData["Data"];
                    var scUID = (string?)scData["ContractUID"];

                    if (scUID != null)
                    {
                        var scMain = SmartContractStateTrei.GetSmartContractState(scUID);

                        if(scMain != null)
                        {
                            //Update state level contract data.
                            scMain.ContractData = data;
                            SmartContractStateTrei.UpdateSmartContract(scMain);
                        }
                    }
                }
            }
            catch { }
            
        }
        /// <summary>
        /// The call of a contract transaction whose apply reads only the array form. Any other shape gives null, and the apply
        /// then has no effect, as it always had (it threw before reading anything), without reporting the block's state as
        /// failed on every node.
        /// </summary>
        private static JToken? ArrayFormCall(Transaction tx)
        {
            try { return JsonConvert.DeserializeObject<JArray>(tx.Data)?[0] as JObject; }
            catch { return null; }
        }

        private static void TransferSmartContract(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scData = ArrayFormCall(tx);
            if (scData == null) return;

            var function = (string?)scData["Function"];
            var data = (string?)scData["Data"];
            var scUID = (string?)scData["ContractUID"];
            var locator = (string?)scData["Locators"];

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if(scStateTreiRec != null)
            {
                if(tx.FromAddress.StartsWith("xRBX"))
                {
                    scStateTreiRec.NextOwner = tx.ToAddress;
                    scStateTreiRec.IsLocked = true;
                    scStateTreiRec.Nonce += 1;
                    scStateTreiRec.ContractData = data;
                    scStateTreiRec.Locators = !string.IsNullOrWhiteSpace(locator) ? locator : scStateTreiRec.Locators;
                }
                else
                {
                    CarryOwnerPosition(scStateTreiRec, tx.ToAddress);
                    scStateTreiRec.OwnerAddress = tx.ToAddress;
                    scStateTreiRec.Nonce += 1;
                    scStateTreiRec.ContractData = data;
                    scStateTreiRec.Locators = !string.IsNullOrWhiteSpace(locator) ? locator : scStateTreiRec.Locators;

                    // Sync VBTCContractV2.OwnerAddress when ownership transfers at state level
                    // This ensures the local vBTC V2 contract DB stays in sync with state trei
                    try
                    {
                        var vbtcContract = VBTCContractV2.GetContract(scUID);
                        if (vbtcContract != null && vbtcContract.OwnerAddress != tx.ToAddress)
                        {
                            vbtcContract.OwnerAddress = tx.ToAddress;
                            VBTCContractV2.UpdateContract(vbtcContract);
                            SCLogUtility.Log($"Synced VBTCContractV2.OwnerAddress to {tx.ToAddress} for contract {scUID}", 
                                "StateData.TransferSmartContract()");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Failed to sync VBTCContractV2 owner: {ex.Message}", "StateData.TransferSmartContract()");
                    }
                }
                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }

        }
        private static void BurnSmartContract(Transaction tx, long height)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scData = ArrayFormCall(tx);
            if (scData == null) return;
            var function = (string?)scData["Function"];
            var data = (string?)scData["Data"];
            var scUID = (string?)scData["ContractUID"];

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                // From the path-audit rules a burned UID is retired (as stored): nothing is created under it again.
                if (height >= Globals.PathAuditRulesHeight)
                {
                    try { RetiredContractUid.Retire(scStateTreiRec.SmartContractUID, height, tx.Hash); }
                    catch (Exception ex)
                    {
                        // Not written here while every other node has it: this node's state is flagged incomplete.
                        ErrorLogUtility.LogError($"Retiring burned UID {scStateTreiRec.SmartContractUID} at {height} failed: {ex.Message}", "StateData.BurnSmartContract()");
                        NoteApplyStepFailed(height, ex);
                    }
                }
                SmartContractStateTrei.DeleteSmartContract(scStateTreiRec);
            }

        }

        private static void EvolveSC(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scData = ArrayFormCall(tx);
            if (scData == null) return;

            var data = (string?)scData["Data"];
            var scUID = (string?)scData["ContractUID"];

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                scStateTreiRec.Nonce += 1;
                scStateTreiRec.ContractData = data;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }
        }

        private static void DevolveSC(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scData = ArrayFormCall(tx);
            if (scData == null) return;

            var data = (string?)scData["Data"];
            var scUID = (string?)scData["ContractUID"];

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                scStateTreiRec.Nonce += 1;
                scStateTreiRec.ContractData = data;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }
        }

        private static void EvolveDevolveSpecific(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scData = ArrayFormCall(tx);
            if (scData == null) return;

            var data = (string?)scData["Data"];
            var scUID = (string?)scData["ContractUID"];

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                scStateTreiRec.Nonce += 1;
                scStateTreiRec.ContractData = data;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }
        }

        /// <summary>
        /// TokenDeploy() apply. VX-02: every identity used here comes from the SIGNED transaction:
        /// the state record, the token owner (TokenDetails.ContractOwner), the credited account and the
        /// token account's contract UID are tx.FromAddress / the tx ContractUID. The decompiled body is
        /// read only for the token parameters. Previously the supply was credited under the body's own
        /// SmartContractUID to the body's own MinterAddress, so a deploy under a fresh UID could inflate
        /// an existing token for any address. The validator now rejects a mismatched body; this apply is
        /// written so it could not credit a foreign identity even if one were ever mined.
        /// Never throws: it is awaited by UpdateTreis, where a throw would flag the block's state apply.
        /// </summary>
        private static async Task DeployTokenContract(Transaction tx, Block block)
        {
            try
            {
                var (_, scUID, data, md5List) = SmartContractDeployBinding.ReadPayload(tx.Data);
                if (string.IsNullOrEmpty(scUID))
                {
                    ErrorLogUtility.LogError($"DeployTokenContract: missing ContractUID. TX: {tx.Hash}", "StateData.DeployTokenContract()");
                    return;
                }

                var owner = tx.FromAddress;
                var stDb = GetAccountStateTrei();

                SmartContractStateTrei scST = new SmartContractStateTrei
                {
                    ContractData = data,
                    MinterAddress = owner,
                    OwnerAddress = owner,
                    SmartContractUID = scUID,
                    Nonce = 0,
                    MD5List = md5List,
                    IsToken = true,
                };

                try
                {
                    var sc = SmartContractMain.GenerateSmartContractInMemory(data);
                    if (sc.Features != null)
                    {
                        var tokenFeatures = sc.Features.Where(x => x.FeatureName == FeatureName.Token).Select(x => x.FeatureFeatures).FirstOrDefault();
                        var tokenizationV2Features = sc.Features.Where(x => x.FeatureName == FeatureName.TokenizationV2).Select(x => x.FeatureFeatures).FirstOrDefault();
                        if (tokenFeatures != null)
                        {
                            var tokenFeature = (TokenFeature)tokenFeatures;
                            if(tokenFeature != null)
                            {
                                var tokenDetails = TokenDetails.CreateTokenDetails(tokenFeature, sc);
                                tokenDetails.ContractOwner = owner; // VX-02: not sc.MinterAddress
                                scST.TokenDetails = tokenDetails;

                                if(tokenFeature.TokenSupply > 0)
                                {
                                    // VX-02: token account keyed on the tx UID, credited to the tx sender.
                                    var tokenAccount = TokenAccount.CreateTokenAccount(scUID, tokenFeature.TokenName,
                                        tokenFeature.TokenTicker, tokenFeature.TokenSupply, tokenFeature.TokenDecimalPlaces);

                                    var toAddress = GetSpecificAccountStateTrei(owner);
                                    if(toAddress != null)
                                    {
                                        if(toAddress.TokenAccounts?.Count > 0)
                                        {
                                            toAddress.TokenAccounts.Add(tokenAccount);
                                        }
                                        else
                                        {
                                            toAddress.TokenAccounts = new List<TokenAccount> { tokenAccount };
                                        }

                                        await stDb.UpdateSafeAsync(toAddress);
                                    }
                                    else
                                    {
                                        var acctStateTreiTo = new AccountStateTrei
                                        {
                                            Key = owner, // VX-02: was tx.ToAddress (a third identity)
                                            Nonce = 0,
                                            Balance = 0.0M,
                                            StateRoot = block.StateRoot,
                                            LockedBalance = 0.0M,
                                            TokenAccounts = new List<TokenAccount> { tokenAccount }
                                        };

                                        await stDb.InsertSafeAsync(acctStateTreiTo);
                                    }
                                }
                            }
                        }
                        else if (tokenizationV2Features != null)
                        {
                            TokenizationV2Feature tv2 = null;
                            if (tokenizationV2Features is TokenizationV2Feature tv2Direct)
                                tv2 = tv2Direct;
                            else
                                tv2 = JsonConvert.DeserializeObject<TokenizationV2Feature>(tokenizationV2Features.ToString());

                            if (tv2 != null)
                            {
                                var tokenDetails = new TokenDetails
                                {
                                    TokenName = tv2.AssetName,
                                    TokenTicker = tv2.AssetTicker,
                                    StartingSupply = 0,
                                    CurrentSupply = 0,
                                    IsPaused = false,
                                    ContractOwner = owner, // VX-02: not sc.MinterAddress
                                    DecimalPlaces = 8,
                                    TokenBurnable = false,
                                    TokenMintable = false,
                                    TokenVoting = false,
                                };
                                scST.TokenDetails = tokenDetails;
                            }
                        }
                    }
                }
                catch { }

                //Save to state trei
                SmartContractStateTrei.SaveSmartContract(scST);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DeployTokenContract error: {ex.Message}. TX: {tx.Hash}", "StateData.DeployTokenContract()");
            }
        }
        private static void TokenContractOwnerChange(Transaction tx)
        {
            var txData = tx.Data;
            var jobj = JObject.Parse(txData);
            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var toAddress = jobj["ToAddress"]?.ToObject<string?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if (scStateTreiRec.TokenDetails != null)
                {
                    if (tx.FromAddress.StartsWith("xRBX"))
                    {
                        scStateTreiRec.NextOwner = tx.ToAddress;
                        scStateTreiRec.IsLocked = true;
                        scStateTreiRec.Nonce += 1;
                        SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                    }
                    else
                    {
                        scStateTreiRec.TokenDetails.ContractOwner = toAddress;
                        scStateTreiRec.OwnerAddress = toAddress;
                        SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                    }
                    
                }
            }
        }

        private static void TokenVoteTopicCreate(Transaction tx)
        {
            var txData = tx.Data;
            var jobj = JObject.Parse(txData);
            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();
            var topic = jobj["TokenVoteTopic"]?.ToObject<TokenVoteTopic?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if (scStateTreiRec.TokenDetails != null)
                {
                    var topicList = scStateTreiRec.TokenDetails.TokenTopicList;
                    if (topicList?.Count > 0)
                    {
                        var exist = scStateTreiRec.TokenDetails.TokenTopicList.Exists(x => x.TopicUID == topic.TopicUID);
                        if (!exist)
                        {
                            scStateTreiRec.TokenDetails.TokenTopicList.Add(topic);
                        }
                    }
                    else
                    {
                        scStateTreiRec.TokenDetails.TokenTopicList = new List<TokenVoteTopic> { topic };
                    }
                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                }
            }
        }

        private static void TokenVoteTopicCast(Transaction tx)
        {
            var txData = tx.Data;
            var jobj = JObject.Parse(txData);
            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();
            var topicUID = jobj["TopicUID"]?.ToObject<string?>();
            var voteType = jobj["VoteType"]?.ToObject<VoteType?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if (scStateTreiRec.TokenDetails != null)
                {
                    var topicList = scStateTreiRec.TokenDetails.TokenTopicList;
                    if (topicList?.Count > 0)
                    {
                        var topic = scStateTreiRec.TokenDetails.TokenTopicList.Where(x => x.TopicUID == topicUID).FirstOrDefault();
                        if (topic != null)
                        {
                            if (string.IsNullOrEmpty(fromAddress) || string.IsNullOrEmpty(topicUID) || !voteType.HasValue)
                            {
                                //bad vote don't save.
                            }
                            else
                            {
                                TokenVote tkVote = new TokenVote
                                {
                                    Address = fromAddress,
                                    BlockHeight = tx.Height,
                                    SmartContractUID = scUID,
                                    TopicUID = topicUID,
                                    TransactionHash = tx.Hash,
                                    VoteType = voteType.Value
                                };

                                TokenVote.SaveVote(tkVote);

                                if (voteType == VoteType.Yes)
                                    topic.VoteYes += 1;
                                if (voteType == VoteType.No)
                                    topic.VoteNo += 1;

                                int fromIndex = scStateTreiRec.TokenDetails.TokenTopicList.FindIndex(a => a.TopicUID == topicUID);
                                scStateTreiRec.TokenDetails.TokenTopicList[fromIndex] = topic;
                                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                            }
                        }
                    }
                }
            }
        }
        private static async Task TokenBanAddress(Transaction tx)
        {
            var txData = tx.Data;
            var jobj = JObject.Parse(txData);

            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var banAddress = jobj["BanAddress"]?.ToObject<string?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if (scStateTreiRec.TokenDetails != null)
                {
                    var banList = scStateTreiRec.TokenDetails.AddressBlackList;
                    if(banList?.Count > 0)
                    {
                        var exist = scStateTreiRec.TokenDetails.AddressBlackList.Exists(x => x == banAddress);
                        if(!exist)
                        {
                            scStateTreiRec.TokenDetails.AddressBlackList.Add(banAddress);
                        }
                    }
                    else
                    {
                        scStateTreiRec.TokenDetails.AddressBlackList = new List<string> { banAddress };
                    }
                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                }
            }

        }
        private static void TokenPause(Transaction tx)
        {
            var txData = tx.Data;
            var jobj = JObject.Parse(txData);
            
            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var pause = jobj["Pause"]?.ToObject<bool?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if (scStateTreiRec.TokenDetails != null)
                {
                    scStateTreiRec.TokenDetails.IsPaused = !scStateTreiRec.TokenDetails.IsPaused;
                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                }
            }

        }
        private static async Task TokenMint(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var txData = tx.Data;
            var stDB = GetAccountStateTrei();

            var jobj = JObject.Parse(txData);

            var function = (string?)jobj["Function"];
            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var amount = jobj["Amount"]?.ToObject<decimal?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if (scStateTreiRec.TokenDetails != null)
                {
                    var fromAccount = GetSpecificAccountStateTrei(fromAddress);
                    var tokenAccountFrom = fromAccount.TokenAccounts?.Where(x => x.SmartContractUID == scUID).FirstOrDefault();
                    if (tokenAccountFrom != null)
                    {
                        tokenAccountFrom.Balance += amount.Value;
                        int fromIndex = fromAccount.TokenAccounts.FindIndex(a => a.SmartContractUID == scUID);
                        fromAccount.TokenAccounts[fromIndex] = tokenAccountFrom;
                        await stDB.UpdateSafeAsync(fromAccount);
                    }
                    else
                    {
                        var nTokenAccountT0 = TokenAccount.CreateTokenAccount(scUID, scStateTreiRec.TokenDetails.TokenName, scStateTreiRec.TokenDetails.TokenTicker,
                            amount.Value, scStateTreiRec.TokenDetails.DecimalPlaces);

                        if (fromAccount.TokenAccounts == null)
                        {
                            List<TokenAccount> tokenAccounts = new List<TokenAccount>
                            {
                                nTokenAccountT0
                            };

                            fromAccount.TokenAccounts = tokenAccounts;
                            await stDB.UpdateSafeAsync(fromAccount);
                        }
                        else
                        {
                            fromAccount.TokenAccounts.Add(nTokenAccountT0);
                            await stDB.UpdateSafeAsync(fromAccount);
                        }
                    }
                    scStateTreiRec.TokenDetails.CurrentSupply += amount.Value;
                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                }
            }

        }
        private static async Task TokenTransfer(Transaction tx, Block block)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var txData = tx.Data;
            var stDB = GetAccountStateTrei();

            var jobj = JObject.Parse(txData);

            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var toAddress = jobj["ToAddress"]?.ToObject<string?>();
            var amount = jobj["Amount"]?.ToObject<decimal?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if(scStateTreiRec.TokenDetails != null)
                {
                    var toAccount = GetSpecificAccountStateTrei(toAddress);
                    var fromAccount = GetSpecificAccountStateTrei(fromAddress);

                    if(toAccount == null)
                    {
                        var accStTrei = GetAccountStateTrei();
                        var acctStateTreiTo = new AccountStateTrei
                        {
                            Key = tx.ToAddress,
                            Nonce = 0,
                            Balance = 0.0M,
                            StateRoot = block.StateRoot
                        };

                        if (!tx.FromAddress.StartsWith("xRBX"))
                        {
                            acctStateTreiTo.Balance += tx.Amount;
                        }
                        else
                        {
                            acctStateTreiTo.LockedBalance += tx.Amount;
                        }
                        await accStTrei.InsertSafeAsync(acctStateTreiTo);
                        toAccount = acctStateTreiTo;
                    }

                    var tokenAccountFrom = fromAccount.TokenAccounts?.Where(x => x.SmartContractUID == scUID).FirstOrDefault();
                    if(tokenAccountFrom != null)
                    {
                        tokenAccountFrom.Balance -= amount.Value;
                        int fromIndex = fromAccount.TokenAccounts.FindIndex(a => a.SmartContractUID == scUID);
                        fromAccount.TokenAccounts[fromIndex] = tokenAccountFrom;
                        await stDB.UpdateSafeAsync(fromAccount);
                    }

                    var tokenAccountTo = toAccount.TokenAccounts?.Where(x => x.SmartContractUID == scUID).FirstOrDefault();
                    if(tokenAccountTo == null)
                    {
                        var nTokenAccountT0 = TokenAccount.CreateTokenAccount(scUID, scStateTreiRec.TokenDetails.TokenName, scStateTreiRec.TokenDetails.TokenTicker, 
                            amount.Value, scStateTreiRec.TokenDetails.DecimalPlaces);

                        if(toAccount.TokenAccounts == null)
                        {
                            List<TokenAccount> tokenAccounts = new List<TokenAccount>
                            {
                                nTokenAccountT0
                            };

                            toAccount.TokenAccounts = tokenAccounts;
                        }
                        else
                        {
                            toAccount.TokenAccounts.Add(nTokenAccountT0);
                            await stDB.UpdateSafeAsync(toAccount);
                        }
                    }
                    else
                    {
                        tokenAccountTo.Balance += amount.Value;
                        int toIndex = toAccount.TokenAccounts.FindIndex(a => a.SmartContractUID == scUID);
                        toAccount.TokenAccounts[toIndex] = tokenAccountTo;
                    }
                    await stDB.UpdateSafeAsync(toAccount);
                }
                

                //if (tx.FromAddress.StartsWith("xRBX"))
                //{
                //    scStateTreiRec.NextOwner = tx.ToAddress;
                //    scStateTreiRec.IsLocked = true;
                //    scStateTreiRec.Nonce += 1;
                //    scStateTreiRec.ContractData = data;
                //    scStateTreiRec.Locators = !string.IsNullOrWhiteSpace(locator) ? locator : scStateTreiRec.Locators;
                //}
                //else
                //{
                //    scStateTreiRec.OwnerAddress = tx.ToAddress;
                //    scStateTreiRec.Nonce += 1;
                //    scStateTreiRec.ContractData = data;
                //    scStateTreiRec.Locators = !string.IsNullOrWhiteSpace(locator) ? locator : scStateTreiRec.Locators;
                //}
            }

        }

        private static async Task TokenBurn(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var txData = tx.Data;
            var stDB = GetAccountStateTrei();

            var jobj = JObject.Parse(txData);

            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var amount = jobj["Amount"]?.ToObject<decimal?>();
            var fromAddress = jobj["FromAddress"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                if (scStateTreiRec.TokenDetails != null)
                {
                    var fromAccount = GetSpecificAccountStateTrei(fromAddress);

                    var tokenAccountFrom = fromAccount.TokenAccounts?.Where(x => x.SmartContractUID == scUID).FirstOrDefault();
                    if (tokenAccountFrom != null)
                    {
                        tokenAccountFrom.Balance -= amount.Value;
                        int fromIndex = fromAccount.TokenAccounts.FindIndex(a => a.SmartContractUID == scUID);
                        fromAccount.TokenAccounts[fromIndex] = tokenAccountFrom;
                        await stDB.UpdateSafeAsync(fromAccount);
                    }

                    scStateTreiRec.TokenDetails.CurrentSupply -= amount.Value;

                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                }
            }
        }

        private static void TokenizedWithdrawalRequest(Transaction tx)
        {
            try
            {
                var txData = tx.Data;
                var jobj = JObject.Parse(txData);

                var function = (string?)jobj["Function"];

                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var tw = jobj["TokenizedWithdrawal"]?.ToObject<TokenizedWithdrawals?>();

                if(tw != null)
                {
                    TokenizedWithdrawals.SaveTokenizedWithdrawals(tw);
                }
                else
                {
                    ErrorLogUtility.LogError($"Tokenized Withdrawal was NULL.", "StateData.TokenizedWithdrawalRequest()");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to save TW From Arb. ERROR: {ex}", "StateData.TokenizedWithdrawalRequest()");
            }
        }

        private static void TokenizedWithdrawalComplete(Transaction tx)
        {
            try
            {
                var txData = tx.Data;
                var jobj = JObject.Parse(txData);

                var function = (string?)jobj["Function"];

                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var uniqueId = jobj["UniqueId"]?.ToObject<string?>();
                var txHash = jobj["TransactionHash"]?.ToObject<string?>();
                
                if (uniqueId != null && scUID != null && txHash != null)
                {
                    // Get the withdrawal record to find the amount before completing it
                    var tw = TokenizedWithdrawals.GetTokenizedRecord(tx.FromAddress, uniqueId, scUID);
                    
                    if (tw != null)
                    {
                        // Mark withdrawal as completed
                        TokenizedWithdrawals.CompleteTokenizedWithdrawals(tx.FromAddress, uniqueId, scUID, txHash);
                        
                        // CRITICAL FIX: Decrement vBTC balance from smart contract state
                        // This prevents users from withdrawing the same funds multiple times
                        var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                        
                        if (scStateTreiRec != null)
                        {
                            // Create debit entry to subtract withdrawn amount from user's balance
                            // This mirrors the pattern used in TransferCoin() for vBTC transfers
                            List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                            {
                                new SmartContractStateTreiTokenizationTX
                                {
                                    Amount = tw.Amount * -1.0M,  // Negative amount = debit/withdrawal
                                    FromAddress = tx.FromAddress,
                                    ToAddress = "-"  // "-" indicates withdrawal/burn
                                }
                            };

                            if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                            {
                                scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                            }
                            else
                            {
                                scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;
                            }

                            SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                        }
                    }
                    else
                    {
                        ErrorLogUtility.LogError($"Could not find withdrawal record for UniqueId: {uniqueId}", "StateData.TokenizedWithdrawalComplete()");
                    }
                }
                else
                {
                    ErrorLogUtility.LogError($"Tokenized Withdrawal was NULL.", "StateData.TokenizedWithdrawalComplete()");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to complete TW. ERROR: {ex}", "StateData.TokenizedWithdrawalComplete()");
            }
        }



        private static void StartSaleSmartContract(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var txData = tx.Data;

            var jobj = JObject.Parse(txData);
            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var toAddress = jobj["NextOwner"]?.ToObject<string?>();
            var amountSoldFor = jobj["SoldFor"]?.ToObject<decimal?>();
            var locator = jobj["Locators"]?.ToObject<string?>();
            //var locator = jobj["Locators"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                scStateTreiRec.NextOwner = toAddress;
                scStateTreiRec.IsLocked = true;
                scStateTreiRec.Nonce += 1;
                scStateTreiRec.PurchaseAmount = amountSoldFor;
                scStateTreiRec.Locators = !string.IsNullOrWhiteSpace(locator) ? locator : scStateTreiRec.Locators;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }

        }

        private static async Task CompleteSaleSmartContract(Transaction tx, Block block)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var accStTrei = GetAccountStateTrei();

            var txData = tx.Data;

            var jobj = JObject.Parse(txData);
            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();
            var royalty = jobj["Royalty"]?.ToObject<bool?>();
            var royaltyAmount = jobj["RoyaltyAmount"]?.ToObject<decimal?>();
            var royaltyPayTo = jobj["RoyaltyPayTo"]?.ToObject<string?>();
            var transactions = jobj["Transactions"]?.ToObject<List<Transaction>?>();
            var keySign = jobj["KeySign"]?.ToObject<string?>();

            //var locator = jobj["Locators"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            // Fund-loss audit item 2: validation refuses a completion without a record from this height; the apply agrees
            // and moves nothing (before it, the inner payments were applied against no contract).
            if (scStateTreiRec == null && block.Height >= Globals.SaleCompleteContractRulesHeight)
                return;
            if (scStateTreiRec != null)
            {
                scStateTreiRec.NextOwner = null;
                scStateTreiRec.IsLocked = false;
                scStateTreiRec.PurchaseAmount = null;
                CarryOwnerPosition(scStateTreiRec, tx.FromAddress);
                scStateTreiRec.OwnerAddress = tx.FromAddress;
                if (scStateTreiRec.PurchaseKeys != null)
                {
                    scStateTreiRec.PurchaseKeys.Add(keySign);
                }
                else
                {
                    scStateTreiRec.PurchaseKeys = new List<string> { keySign };
                }

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }

            var from = GetSpecificAccountStateTrei(tx.FromAddress);
            if(royalty != null)
            {
                if(royalty.Value)
                {
                    if(transactions != null)
                    {
                        var txToSeller = transactions.Where(x => x.Data.Contains("1/2")).FirstOrDefault();
                        var txToRoyaltyPayee = transactions.Where(x => x.Data.Contains("2/2")).FirstOrDefault();
                        if(txToSeller != null)
                        {
                            var toSeller = GetSpecificAccountStateTrei(txToSeller.ToAddress);
                            if (toSeller == null)
                            {
                                var acctStateTreiTo = new AccountStateTrei
                                {
                                    Key = txToSeller.ToAddress,
                                    Nonce = 0,
                                    Balance = 0.0M,
                                    StateRoot = block.StateRoot
                                };

                                acctStateTreiTo.Balance += txToSeller.Amount;
                                await accStTrei.InsertSafeAsync(acctStateTreiTo);
                            }
                            else
                            {
                                toSeller.StateRoot = block.StateRoot;
                                toSeller.Balance += txToSeller.Amount;
                                
                                await accStTrei.UpdateSafeAsync(toSeller);
                            }

                            // Read again from the path-audit rules: the payee written just above may be the buyer itself (a creator buying
                            // back under its own royalty, a self-sale), and the copy read before overwrote the credit (round 30).
                            if (block.Height >= Globals.PathAuditRulesHeight) from = GetSpecificAccountStateTrei(tx.FromAddress) ?? from;
                            from.Nonce += 1;
                            from.StateRoot = block.StateRoot;
                            from.Balance -= (txToSeller.Amount + txToSeller.Fee);

                            await accStTrei.UpdateSafeAsync(from);
                        }
                        if (txToRoyaltyPayee != null)
                        {
                            var toRoyalty = GetSpecificAccountStateTrei(txToRoyaltyPayee.ToAddress);
                            if (toRoyalty == null)
                            {
                                var acctStateTreiTo = new AccountStateTrei
                                {
                                    Key = txToRoyaltyPayee.ToAddress,
                                    Nonce = 0,
                                    Balance = 0.0M,
                                    StateRoot = block.StateRoot
                                };

                                acctStateTreiTo.Balance += txToRoyaltyPayee.Amount;
                                await accStTrei.InsertSafeAsync(acctStateTreiTo);
                            }
                            else
                            {
                                toRoyalty.StateRoot = block.StateRoot;
                                toRoyalty.Balance += txToRoyaltyPayee.Amount;

                                await accStTrei.UpdateSafeAsync(toRoyalty);
                            }

                            // Read again from the path-audit rules: the payee written just above may be the buyer itself (a creator buying
                            // back under its own royalty, a self-sale), and the copy read before overwrote the credit (round 30).
                            if (block.Height >= Globals.PathAuditRulesHeight) from = GetSpecificAccountStateTrei(tx.FromAddress) ?? from;
                            from.Nonce += 1;
                            from.StateRoot = block.StateRoot;
                            from.Balance -= (txToRoyaltyPayee.Amount + txToRoyaltyPayee.Fee);

                            await accStTrei.UpdateSafeAsync(from);
                        }

                    }
                }
                else
                {
                    if (transactions != null)
                    {
                        var txToSeller = transactions.FirstOrDefault();
                        if (txToSeller != null)
                        {
                            var toSeller = GetSpecificAccountStateTrei(txToSeller.ToAddress);
                            if (toSeller == null)
                            {
                                var acctStateTreiTo = new AccountStateTrei
                                {
                                    Key = txToSeller.ToAddress,
                                    Nonce = 0,
                                    Balance = 0.0M,
                                    StateRoot = block.StateRoot
                                };

                                acctStateTreiTo.Balance += txToSeller.Amount;
                                await accStTrei.InsertSafeAsync(acctStateTreiTo);
                            }
                            else
                            {
                                toSeller.StateRoot = block.StateRoot;
                                toSeller.Balance += txToSeller.Amount;

                                await accStTrei.UpdateSafeAsync(toSeller);
                            }

                            // Read again from the path-audit rules: the payee written just above may be the buyer itself (a creator buying
                            // back under its own royalty, a self-sale), and the copy read before overwrote the credit (round 30).
                            if (block.Height >= Globals.PathAuditRulesHeight) from = GetSpecificAccountStateTrei(tx.FromAddress) ?? from;
                            from.Nonce += 1;
                            from.StateRoot = block.StateRoot;
                            from.Balance -= (txToSeller.Amount + txToSeller.Fee);

                            await accStTrei.UpdateSafeAsync(from);
                        }

                    }
                }
            }
            else
            {
                if (transactions != null)
                {
                    var txToSeller = transactions.FirstOrDefault();
                    if (txToSeller != null)
                    {
                        var toSeller = GetSpecificAccountStateTrei(txToSeller.ToAddress);
                        if (toSeller == null)
                        {
                            var acctStateTreiTo = new AccountStateTrei
                            {
                                Key = txToSeller.ToAddress,
                                Nonce = 0,
                                Balance = 0.0M,
                                StateRoot = block.StateRoot
                            };

                            acctStateTreiTo.Balance += txToSeller.Amount;
                            await accStTrei.InsertSafeAsync(acctStateTreiTo);
                        }
                        else
                        {
                            toSeller.StateRoot = block.StateRoot;
                            toSeller.Balance += txToSeller.Amount;

                            await accStTrei.UpdateSafeAsync(toSeller);
                        }

                        // Read again from the path-audit rules: the payee written just above may be the buyer itself (a creator buying
                        // back under its own royalty, a self-sale), and the copy read before overwrote the credit (round 30).
                        if (block.Height >= Globals.PathAuditRulesHeight) from = GetSpecificAccountStateTrei(tx.FromAddress) ?? from;
                        from.Nonce += 1;
                        from.StateRoot = block.StateRoot;
                        from.Balance -= (txToSeller.Amount + txToSeller.Fee);

                        await accStTrei.UpdateSafeAsync(from);
                    }
                }
            }
        }

        private static void CancelSaleSmartContract(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var txData = tx.Data;

            var jobj = JObject.Parse(txData);
            var function = (string?)jobj["Function"];

            var scUID = jobj["ContractUID"]?.ToObject<string?>();

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                scStateTreiRec.NextOwner = null;
                scStateTreiRec.IsLocked = false;
                scStateTreiRec.Nonce += 1;
                scStateTreiRec.PurchaseAmount = null;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            }

        }

        private static void TransferCoin(Transaction tx)
        {
            string scUID = "";
            string function = "";
            bool skip = false;
            JToken? scData = null;
            decimal? amountVal = null;

            try
            {
                var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
                scData = scDataArray[0];

                function = (string?)scData["Function"];
                scUID = (string?)scData["ContractUID"];
                amountVal = (decimal?)scData["Amount"];
                skip = true;
            }
            catch { }

            try
            {
                if (!skip)
                {
                    var jobj = JObject.Parse(tx.Data);
                    scUID = jobj["ContractUID"]?.ToObject<string?>();
                    function = jobj["Function"]?.ToObject<string?>();
                    amountVal = jobj["Amount"]?.ToObject<decimal?>();
                }
            }
            catch { }

            if (amountVal.HasValue)
            {
                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);

                if(scStateTreiRec != null)
                {
                    List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                    {
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = amountVal.Value,
                            FromAddress = "+",
                            ToAddress = tx.ToAddress
                        },
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = amountVal.Value * -1.0M,
                            FromAddress = tx.FromAddress,
                            ToAddress = "-"
                        }
                    };

                    if(scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                    {
                        scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                    }
                    else
                    {
                        scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;
                    }

                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                }
            }
        }

        private static void TransferCoinMulti(Transaction tx)
        {
            var txData = tx.Data;
            var jobj = JObject.Parse(txData);

            var function = (string?)jobj["Function"];

            var signatureInput = jobj["SignatureInput"]?.ToObject<string?>();
            var amount = jobj["Amount"]?.ToObject<decimal?>();
            var inputs = jobj["Inputs"]?.ToObject<List<VBTCTransferInput>?>();

            if(inputs != null)
            {
                foreach(var input in inputs)
                {
                    var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(input.SCUID);

                    if (scStateTreiRec != null)
                    {
                        List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                    {
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = input.Amount,
                            FromAddress = "+",
                            ToAddress = tx.ToAddress
                        },
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = input.Amount * -1.0M,
                            FromAddress = input.FromAddress,
                            ToAddress = "-"
                        }
                    };

                        if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                        {
                            scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                        }
                        else
                        {
                            scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;
                        }

                        SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                    }
                }
            }
        }

        private static void TransferVBTC(Transaction tx)
        {
            string scUID = "";
            string function = "";
            bool skip = false;
            JToken? scData = null;
            decimal? amountVal = null;

            try
            {
                var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
                scData = scDataArray[0];

                function = (string?)scData["Function"];
                scUID = (string?)scData["ContractUID"];
                amountVal = (decimal?)scData["Amount"];
                skip = true;
            }
            catch { }

            try
            {
                if (!skip)
                {
                    var jobj = JObject.Parse(tx.Data);
                    scUID = jobj["ContractUID"]?.ToObject<string?>();
                    function = jobj["Function"]?.ToObject<string?>();
                    amountVal = jobj["Amount"]?.ToObject<decimal?>();
                }
            }
            catch { }

            if (amountVal.HasValue)
            {
                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);

                if(scStateTreiRec != null)
                {
                    List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                    {
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = amountVal.Value,
                            FromAddress = "+",
                            ToAddress = tx.ToAddress
                        },
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = amountVal.Value * -1.0M,
                            FromAddress = tx.FromAddress,
                            ToAddress = "-"
                        }
                    };

                    if(scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                    {
                        scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                    }
                    else
                    {
                        scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;
                    }

                    SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                }
            }
        }

        // DO NOT RE-ENABLE. Legacy TKNZ-era handler, unreachable for new TXs — the validator
        // rejects Function "TransferVBTCMulti()" outright (deprecated, no balance check, and it
        // trusts input.FromAddress from tx.Data, so re-enabling it would be an unvalidated
        // mint/spoof path). Kept only for deterministic replay of any historical block that might
        // predate the rejection. The live V2 multi path is TransferVBTCV2Multi below
        // (Function "TransferVBTCMultiV2()", height-gated).
        private static void TransferVBTCMulti(Transaction tx)
        {
            var txData = tx.Data;
            var jobj = JObject.Parse(txData);

            var function = (string?)jobj["Function"];

            var signatureInput = jobj["SignatureInput"]?.ToObject<string?>();
            var amount = jobj["Amount"]?.ToObject<decimal?>();
            var inputs = jobj["Inputs"]?.ToObject<List<VBTCTransferInput>?>();

            if(inputs != null)
            {
                foreach(var input in inputs)
                {
                    var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(input.SCUID);

                    if (scStateTreiRec != null)
                    {
                        List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                        {
                            new SmartContractStateTreiTokenizationTX
                            {
                                Amount = input.Amount,
                                FromAddress = "+",
                                ToAddress = tx.ToAddress
                            },
                            new SmartContractStateTreiTokenizationTX
                            {
                                Amount = input.Amount * -1.0M,
                                FromAddress = input.FromAddress,
                                ToAddress = "-"
                            }
                        };

                        if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                        {
                            scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                        }
                        else
                        {
                            scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;
                        }

                        SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
                    }
                }
            }
        }

        private static void TransferVBTCV2(Transaction tx)
        {
            try
            {
                // Parse transaction data for ContractUID and Amount only
                var jobj = JObject.Parse(tx.Data);
                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var amount = jobj["Amount"]?.ToObject<decimal?>();

                // CONSENSUS SAFETY: Use tx.FromAddress and tx.ToAddress as the authoritative
                // sender/receiver - these are bound to the transaction signer and cannot be spoofed.
                // Do NOT trust FromAddress/ToAddress embedded in tx.Data.
                var fromAddress = tx.FromAddress;
                var toAddress = tx.ToAddress;

                if (string.IsNullOrEmpty(scUID) || !amount.HasValue || amount.Value <= 0)
                {
                    ErrorLogUtility.LogError($"VBTC-TRACE [4-StateLedger]: TransferVBTCV2 failed: Missing required fields or invalid amount. TX: {tx.Hash}", "StateData.TransferVBTCV2()");
                    return;
                }

                ApplyVBTCV2LedgerPair(scUID, fromAddress, toAddress, amount.Value, tx.Hash);
            }
            catch (Exception ex)
            {
                NoteApplyStepFailed(tx.Height, ex); // a fault on this node only: flagged, not left as other state than its peers' (fourth review, round 3)
                ErrorLogUtility.LogError($"TransferVBTCV2 error: {ex.Message}", "StateData.TransferVBTCV2()");
            }
        }

        /// <summary>
        /// vBTC V2 multi-contract transfer apply (Function "TransferVBTCMultiV2()", height-gated
        /// at the dispatch site): writes the same credit/debit ledger pair as a single transfer
        /// for EVERY input contract, so all downstream balance math (owner ledger formula,
        /// non-owner received+sent, withdrawal add-back) is unchanged per contract.
        /// CONSENSUS SAFETY: sender/recipient come from tx.FromAddress/tx.ToAddress (bound to the
        /// signer) — never from addresses embedded in tx.Data.
        /// </summary>
        private static void TransferVBTCV2Multi(Transaction tx)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var inputs = jobj["Inputs"]?.ToObject<List<VBTCV2MultiTransferInput>?>();

                if (inputs == null || !inputs.Any())
                {
                    ErrorLogUtility.LogError($"VBTC-TRACE [4-StateLedger]: TransferVBTCV2Multi failed: Missing inputs. TX: {tx.Hash}", "StateData.TransferVBTCV2Multi()");
                    return;
                }

                foreach (var input in inputs)
                {
                    if (string.IsNullOrEmpty(input.SCUID) || input.Amount <= 0)
                    {
                        ErrorLogUtility.LogError($"VBTC-TRACE [4-StateLedger]: TransferVBTCV2Multi skipped invalid input. SCUID: {input.SCUID}, Amount: {input.Amount}. TX: {tx.Hash}", "StateData.TransferVBTCV2Multi()");
                        continue;
                    }

                    ApplyVBTCV2LedgerPair(input.SCUID, tx.FromAddress, tx.ToAddress, input.Amount, tx.Hash);
                }
            }
            catch (Exception ex)
            {
                NoteApplyStepFailed(tx.Height, ex); // a fault on this node only: flagged, not left as other state than its peers' (fourth review, round 3)
                ErrorLogUtility.LogError($"TransferVBTCV2Multi error: {ex.Message}", "StateData.TransferVBTCV2Multi()");
            }
        }

        /// <summary>
        /// Writes the vBTC V2 credit/debit ledger pair for a transfer. Shared by the immediate
        /// apply path (TransferVBTCV2), the reserve finalize path (UpdateTreiFromReserve), and
        /// reserve recovery (redirect/sweep).
        /// </summary>
        // ── A reserve account's delayed moves are judged again when they fall due (second path audit, 14 October 2026) ──
        //
        // A reserve account's transfer is mined now and takes effect at its unlock time. Nothing is taken from the
        // sender in between: a vBTC transfer is only netted against the sender's own later spends, and a contract
        // transfer only sets the contract's lock. Both were then applied at the unlock time without looking again.

        /// <summary>
        /// Whether a reserve row that falls due at <paramref name="timestamp"/> is under these rules: it falls due in
        /// a block after Globals.PathAuditRulesHeight. A row falls due in the first block whose time is past it, so
        /// "at or after that block's time" is the same on a live node and on a replay.
        /// </summary>
        internal static bool PathAuditRulesAtTime(long timestamp)
        {
            if (Globals.PathAuditRulesHeight < 0 || Globals.PathAuditRulesHeight > (Globals.LastBlock?.Height ?? 0) + 1_000_000_000L) return false;
            var gate = BlockchainData.GetBlockByHeight(Globals.PathAuditRulesHeight);
            return gate != null && timestamp >= gate.Timestamp;
        }

        /// <summary>Whether state still waits for this delayed contract transfer: the contract is its sender's and marked for its recipient.</summary>
        internal static bool DeferredTransferStillStands(SmartContractStateTrei contract, ReserveTransactions row) =>
            contract.OwnerAddress == row.FromAddress && contract.NextOwner == row.ToAddress;

        /// <summary>
        /// Whether a reserve account's delayed vBTC transfer is still covered by what its sender holds on the contract.
        /// The sender's balance is read as a transfer's own rule reads it. While the sender owned the vault its
        /// balance was the vault's Bitcoin less everyone else's vBTC; once the vault has a new owner that balance is
        /// the new owner's, and the old one has only its own ledger rows. A delayed transfer made as owner and applied
        /// after the vault had changed hands gave the recipient vBTC the new owner could also spend: one Bitcoin,
        /// claimed twice. (The owner of a vault of the earlier kind is judged as the owner only: its deposit is not
        /// in state, as for its transfers in a block.)
        /// </summary>
        internal static bool DeferredVbtcTransferStillCovered(string scUID, string fromAddress, decimal amount)
        {
            var contract = SmartContractStateTrei.GetSmartContractState(scUID);
            if (contract == null) return false;
            if (contract.OwnerAddress == fromAddress)
                return !VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(scUID)
                    || Bitcoin.Services.VBTCService.AttestedOwnerSpendable(contract, fromAddress, long.MaxValue) >= amount;
            var held = contract.SCStateTreiTokenizationTXes?.Where(x => x.FromAddress == fromAddress || x.ToAddress == fromAddress).Sum(x => x.Amount) ?? 0M;
            return held >= amount;
        }

        /// <summary>
        /// A consensus write of a reserve's settlement, call-back or recovery. The safe writers log a failure and answer
        /// false (an update) or null (an insert): the row was read just before, so that is a fault of this node's storage.
        /// Swallowed, a settlement whose status write failed was settled again a block later on this node, a second credit,
        /// and nothing was flagged (fourth review, round 8). Thrown, it is caught where the step runs and flags the state.
        /// </summary>
        private static async Task Written(Task<bool> write, string what)
        {
            if (!await write) throw new System.IO.IOException($"{what} was not written on this node.");
        }

        private static async Task Written(Task<LiteDB.BsonValue> insert, string what)
        {
            if (await insert == null) throw new System.IO.IOException($"{what} was not written on this node.");
        }

        /// <summary>
        /// A reserve's call-back or recovery writing a contract's state: a watched vault through its own writer, which
        /// compacts its ledger and throws a failed write instead of dropping it (fourth review, round 6); any other
        /// contract as before.
        /// </summary>
        private static async Task WriteContractStateAsync(LiteDB.ILiteCollection<SmartContractStateTrei> scDb, SmartContractStateTrei contract)
        {
            if (VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(contract.SmartContractUID))
                SmartContractStateTrei.UpdateSmartContract(contract);
            else
                await scDb.UpdateSafeAsync(contract);
        }

        /// <summary>
        /// Called where a contract's owner changes, before the new owner is written. On a vault the chain watches, what
        /// the outgoing owner sent out as owner (transfers, withdrawals in escrow, bridge locks) sits on its address as
        /// a position below zero, and the incoming owner's balance is the vault's Bitcoin less that. Left on the old
        /// address, anything that address received afterwards only made the debt smaller: the refund of its own
        /// withdrawal that did not go through, a bridge unlock, a payment from a holder all went to the new owner's
        /// balance, and the old owner could spend none of it (second path audit, 14 October 2026). The debt belongs
        /// with the vault: it moves to the incoming owner here, as a pair of ledger rows, and the outgoing owner is an
        /// ordinary holder from then on. Changes the list only; the caller saves the contract.
        /// </summary>
        internal static void CarryOwnerPosition(SmartContractStateTrei? contract, string? newOwner)
        {
            var oldOwner = contract?.OwnerAddress;
            if (contract != null && !string.IsNullOrEmpty(oldOwner) && !string.IsNullOrEmpty(newOwner) && oldOwner != newOwner
                && VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(contract.SmartContractUID))
                lock (_ownerChangesThisBlock) _ownerChangesThisBlock.Add((contract.SmartContractUID, oldOwner));
            var rows = contract?.SCStateTreiTokenizationTXes;
            if (rows == null || rows.Count == 0 || string.IsNullOrEmpty(oldOwner) || string.IsNullOrEmpty(newOwner) || oldOwner == newOwner) return;
            if (!VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(contract!.SmartContractUID)) return;
            var position = rows.Where(x => x.FromAddress == oldOwner || x.ToAddress == oldOwner).Sum(x => x.Amount);
            if (position >= 0M) return;
            rows.Add(new SmartContractStateTreiTokenizationTX { Amount = -position, FromAddress = "+", ToAddress = oldOwner });
            rows.Add(new SmartContractStateTreiTokenizationTX { Amount = position, FromAddress = newOwner, ToAddress = "-" });
            SCLogUtility.Log($"vBTC vault {contract.SmartContractUID}: owner changes from {oldOwner} to {newOwner}; the {-position} vBTC the outgoing owner had sent out as owner is carried to the incoming owner.", "StateData.CarryOwnerPosition()");
        }

        /// <summary>The watched vaults whose owner changed in the block being applied, with the outgoing owner.</summary>
        private static readonly List<(string Uid, string OldOwner)> _ownerChangesThisBlock = new();

        /// <summary>
        /// At the end of a block: an outgoing owner left with a debt by an owner debit placed after the change in the same
        /// block (judged as the owner, since validation reads the state before the block) has it carried to the owner of
        /// the end of the block, as at the change itself. Left on the outgoing owner, a refund of that debit (a withdrawal
        /// cancelled, expired or released) only cancelled it and the new owner's balance rose by it (fifth review, round
        /// 29). A holder that never owned the vault cannot go below zero, so only such debts are moved.
        /// </summary>
        private static void CarryDebtsLeftInTheBlock(long height)
        {
            List<(string Uid, string OldOwner)> changes;
            lock (_ownerChangesThisBlock) { changes = _ownerChangesThisBlock.ToList(); _ownerChangesThisBlock.Clear(); }
            if (height < Globals.PathAuditRulesHeight) return;
            foreach (var (uid, oldOwner) in changes.Distinct())
            {
                var contract = SmartContractStateTrei.GetSmartContractState(uid);
                var rows = contract?.SCStateTreiTokenizationTXes;
                if (contract == null || rows == null || contract.OwnerAddress == oldOwner || string.IsNullOrEmpty(contract.OwnerAddress)) continue;
                var position = rows.Where(x => x.FromAddress == oldOwner || x.ToAddress == oldOwner).Sum(x => x.Amount);
                if (position >= 0M) continue;
                rows.Add(new SmartContractStateTreiTokenizationTX { Amount = -position, FromAddress = "+", ToAddress = oldOwner });
                rows.Add(new SmartContractStateTreiTokenizationTX { Amount = position, FromAddress = contract.OwnerAddress, ToAddress = "-" });
                SmartContractStateTrei.UpdateSmartContract(contract);
                SCLogUtility.Log($"vBTC vault {uid}: {-position} vBTC sent out by {oldOwner} as owner after the vault changed hands in block {height} is carried to {contract.OwnerAddress}.", "StateData.CarryDebtsLeftInTheBlock()");
            }
        }

        private static void ApplyVBTCV2LedgerPair(string scUID, string fromAddress, string toAddress, decimal amount, string txHash)
        {
            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);

            if (scStateTreiRec != null)
            {
                // Create credit/debit pair for the transfer
                // Credit: Add tokens to recipient
                // Debit: Subtract tokens from sender
                List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX
                    {
                        Amount = amount,
                        FromAddress = "+",
                        ToAddress = toAddress
                    },
                    new SmartContractStateTreiTokenizationTX
                    {
                        Amount = amount * -1.0M,
                        FromAddress = fromAddress,
                        ToAddress = "-"
                    }
                };

                if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                {
                    scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                }
                else
                {
                    scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;
                }

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);

                SCLogUtility.Log($"VBTC-TRACE [4-StateLedger]: vBTC V2 ledger pair applied: {amount} vBTC from {fromAddress} to {toAddress} in contract {scUID}. TX: {txHash}",
                    "StateData.ApplyVBTCV2LedgerPair()");
            }
            else
            {
                ErrorLogUtility.LogError($"VBTC-TRACE [4-StateLedger]: vBTC V2 ledger pair failed: Contract not found - {scUID}. TX: {txHash}", "StateData.ApplyVBTCV2LedgerPair()");
            }
        }

        /// <summary>
        /// vBTC Base bridge lock: deduct transparent balance chain-wide (same ledger pattern as withdrawal burn).
        /// </summary>
        private static void ApplyVBTCBridgeLock(Transaction tx, long blockHeight = 0)
        {
            try
            {
                if (tx.FromAddress != tx.ToAddress)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeLock: expected self-transaction (from == to)", "StateData.ApplyVBTCBridgeLock()");
                    return;
                }

                var jobj = JObject.Parse(tx.Data);
                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var lockId = jobj["LockId"]?.ToObject<string?>();
                var amount = jobj["Amount"]?.ToObject<decimal?>();
                var amountSats = jobj["AmountSats"]?.ToObject<long?>();
                var evmDestination = jobj["EvmDestination"]?.ToObject<string?>();

                if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(lockId) || !amount.HasValue || !amountSats.HasValue || string.IsNullOrEmpty(evmDestination))
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeLock: missing required fields", "StateData.ApplyVBTCBridgeLock()");
                    return;
                }

                var expectedSats = (long)(amount.Value * 100_000_000M);
                if (expectedSats != amountSats.Value)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeLock: AmountSats mismatch for lock {lockId}", "StateData.ApplyVBTCBridgeLock()");
                    return;
                }

                if (VBTCBridgeLockState.GetByLockId(lockId) != null)
                    return;

                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scStateTreiRec == null)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeLock: contract not found {scUID}", "StateData.ApplyVBTCBridgeLock()");
                    return;
                }

                var tknTxList = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX
                    {
                        Amount = amount.Value * -1.0M,
                        FromAddress = tx.FromAddress,
                        ToAddress = "-"
                    }
                };

                if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                    scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                else
                    scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);

                if (!VBTCBridgeLockState.TryInsertFromLockTx(tx, scUID, lockId, amount.Value, amountSats.Value, evmDestination, blockHeight))
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeLock: failed to persist VBTCBridgeLockState for {lockId}", "StateData.ApplyVBTCBridgeLock()");
                    LogUtility.Log($"[BridgeAttest] CONSENSUS STATE: FAILED to insert VBTCBridgeLockState. lockId={lockId}, txHash={tx.Hash}", "StateData.ApplyVBTCBridgeLock");
                }
                else
                {
                    BridgeLockRecord.TryMarkVfxLockConfirmed(lockId, tx.Hash, tx.Height);
                    LogUtility.Log($"[BridgeAttest] CONSENSUS STATE: Bridge lock CONFIRMED on-chain. lockId={lockId}, owner={tx.FromAddress}, amount={amount.Value} BTC, amountSats={amountSats.Value}, evmDest={evmDestination}, txHash={tx.Hash}, blockHeight={tx.Height}, scUID={scUID}. Balance deducted from owner. Validators can now sign attestation requests for this lockId.", "StateData.ApplyVBTCBridgeLock");
                }

                SCLogUtility.Log($"ApplyVBTCBridgeLock: lockId={lockId}, amount={amount.Value} BTC, owner={tx.FromAddress}, scUID={scUID}",
                    "StateData.ApplyVBTCBridgeLock()");
            }
            catch (Exception ex)
            {
                NoteApplyStepFailed(tx.Height, ex); // a fault of this node's storage is flagged (fourth review, round 8)
                ErrorLogUtility.LogError($"ApplyVBTCBridgeLock error: {ex.Message}", "StateData.ApplyVBTCBridgeLock()");
            }
        }

        /// <summary>
        /// vBTC Base bridge unlock: restore transparent balance after burn on Base (proof hash carried in TX data).
        /// </summary>
        private static void ApplyVBTCBridgeUnlock(Transaction tx)
        {
            try
            {
                if (tx.FromAddress != tx.ToAddress)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeUnlock: expected self-transaction (from == to)", "StateData.ApplyVBTCBridgeUnlock()");
                    return;
                }

                var jobj = JObject.Parse(tx.Data);
                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var lockId = jobj["LockId"]?.ToObject<string?>();
                var amount = jobj["Amount"]?.ToObject<decimal?>();
                var amountSats = jobj["AmountSats"]?.ToObject<long?>();
                var exitBurnTxHash = jobj["ExitBurnTxHash"]?.ToObject<string?>();

                if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(lockId) || !amount.HasValue || !amountSats.HasValue || string.IsNullOrEmpty(exitBurnTxHash))
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeUnlock: missing required fields", "StateData.ApplyVBTCBridgeUnlock()");
                    return;
                }

                var rec = VBTCBridgeLockState.GetByLockId(lockId);
                if (rec == null || rec.IsUnlocked)
                    return;

                if (rec.OwnerAddress != tx.FromAddress || rec.SmartContractUID != scUID)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeUnlock: signer/contract mismatch for lock {lockId}", "StateData.ApplyVBTCBridgeUnlock()");
                    return;
                }

                if (rec.AmountSats != amountSats.Value)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeUnlock: AmountSats mismatch for lock {lockId}", "StateData.ApplyVBTCBridgeUnlock()");
                    return;
                }

                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scStateTreiRec == null)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeUnlock: contract not found {scUID}", "StateData.ApplyVBTCBridgeUnlock()");
                    return;
                }

                var tknTxList = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX
                    {
                        Amount = amount.Value,
                        FromAddress = "+",
                        ToAddress = tx.FromAddress
                    }
                };

                if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                    scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
                else
                    scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;

                SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);

                if (!VBTCBridgeLockState.TryFinalizeUnlock(tx, rec, exitBurnTxHash))
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeUnlock: failed to finalize VBTCBridgeLockState for {lockId}", "StateData.ApplyVBTCBridgeUnlock()");

                BridgeLockRecord.FinalizeFromChainUnlockIfPending(lockId);

                SCLogUtility.Log($"ApplyVBTCBridgeUnlock: lockId={lockId}, amount={amount.Value} BTC, owner={tx.FromAddress}, exitBurn={exitBurnTxHash}",
                    "StateData.ApplyVBTCBridgeUnlock()");
            }
            catch (Exception ex)
            {
                NoteApplyStepFailed(tx.Height, ex); // a fault of this node's storage is flagged (fourth review, round 8)
                ErrorLogUtility.LogError($"ApplyVBTCBridgeUnlock error: {ex.Message}", "StateData.ApplyVBTCBridgeUnlock()");
            }
        }

        /// <summary>
        /// V3 pool-based unlock: credit vBTC to a destination VFX address by consuming
        /// available bridge locks FIFO. The TX data contains a pre-computed allocation plan
        /// (list of lockId + unlockAmount pairs) so all nodes produce identical state.
        /// </summary>
        /// <summary>
        /// VX-01: debits <paramref name="amount"/> from <paramref name="address"/> on the contract's
        /// tokenization ledger ("-" sink row): the escrow at withdrawal REQUEST, or the legacy burn at
        /// COMPLETE. <paramref name="amount"/> is the POSITIVE quantity to remove; anything else is
        /// refused and nothing is written. Consensus-critical — runs on ALL nodes.
        /// </summary>
        /// <remarks>
        /// Replaces a single signed-amount writer whose callers passed <c>-amount</c>: a negative
        /// request amount turned the debit into a "+" credit row, minting unbacked vBTC. Debit and
        /// credit are now separate entry points that each reject a non-positive amount, so no caller
        /// can reintroduce the sign inversion.
        /// </remarks>
        private static bool WriteWithdrawalEscrowDebit(string scUID, string address, decimal amount)
        {
            if (amount <= 0M)
            {
                ErrorLogUtility.LogError($"WriteWithdrawalEscrowDebit refused non-positive amount {amount} for {address} on {scUID}", "StateData.WriteWithdrawalEscrowDebit()");
                return false;
            }
            return AppendWithdrawalLedgerRow(scUID, new SmartContractStateTreiTokenizationTX { Amount = -amount, FromAddress = address, ToAddress = "-" });
        }

        /// <summary>
        /// VX-01: credits <paramref name="amount"/> back to <paramref name="address"/> ("+" source row):
        /// the escrow refund on an approved cancellation — the ONLY path that returns escrow.
        /// <paramref name="amount"/> must be positive; anything else is refused and nothing is written.
        /// </summary>
        private static bool WriteWithdrawalRefundCredit(string scUID, string address, decimal amount)
        {
            if (amount <= 0M)
            {
                ErrorLogUtility.LogError($"WriteWithdrawalRefundCredit refused non-positive amount {amount} for {address} on {scUID}", "StateData.WriteWithdrawalRefundCredit()");
                return false;
            }
            return AppendWithdrawalLedgerRow(scUID, new SmartContractStateTreiTokenizationTX { Amount = amount, FromAddress = "+", ToAddress = address });
        }

        /// <summary>
        /// The escrow refund for a withdrawal on a watched vault that will never be paid (void, expired, or cancelled
        /// before it was pinned): decided by <see cref="VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals"/> during block application.
        /// </summary>
        internal static bool RefundVbtcWithdrawalEscrow(string scUID, string address, decimal amount) =>
            WriteWithdrawalRefundCredit(scUID, address, amount);

        private static bool AppendWithdrawalLedgerRow(string scUID, SmartContractStateTreiTokenizationTX row)
        {
            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec == null) return false;
            if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                scStateTreiRec.SCStateTreiTokenizationTXes.Add(row);
            else
                scStateTreiRec.SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX> { row };
            SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
            return true;
        }

        /// <summary>Credits a pool-unlock allocation to the destination on the lock's contract ledger.</summary>
        private static void CreditPoolUnlock(SmartContractStateTrei scStateTreiRec, decimal amount, string vfxDestinationAddress)
        {
            var tknTxList = new List<SmartContractStateTreiTokenizationTX>
            {
                new SmartContractStateTreiTokenizationTX
                {
                    Amount = amount,
                    FromAddress = "+",
                    ToAddress = vfxDestinationAddress
                }
            };

            if (scStateTreiRec.SCStateTreiTokenizationTXes?.Count() > 0)
                scStateTreiRec.SCStateTreiTokenizationTXes.AddRange(tknTxList);
            else
                scStateTreiRec.SCStateTreiTokenizationTXes = tknTxList;

            SmartContractStateTrei.UpdateSmartContract(scStateTreiRec);
        }

        private static void ApplyVBTCBridgePoolUnlock(Transaction tx)
        {
            try
            {
                if (tx.FromAddress != tx.ToAddress)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgePoolUnlock: expected self-transaction (from == to)", "StateData.ApplyVBTCBridgePoolUnlock()");
                    return;
                }

                var jobj = JObject.Parse(tx.Data);
                var totalAmount = jobj["TotalAmount"]?.ToObject<decimal?>();
                var totalAmountSats = jobj["TotalAmountSats"]?.ToObject<long?>();
                var vfxDestinationAddress = jobj["VfxDestinationAddress"]?.ToObject<string?>();
                var exitBurnTxHash = jobj["ExitBurnTxHash"]?.ToObject<string?>();
                var allocationsToken = jobj["Allocations"];

                if (!totalAmount.HasValue || !totalAmountSats.HasValue ||
                    string.IsNullOrEmpty(vfxDestinationAddress) || string.IsNullOrEmpty(exitBurnTxHash) ||
                    allocationsToken == null)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgePoolUnlock: missing required fields", "StateData.ApplyVBTCBridgePoolUnlock()");
                    return;
                }

                var allocations = allocationsToken.ToObject<List<PoolUnlockAllocation>>();
                if (allocations == null || allocations.Count == 0)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgePoolUnlock: empty allocations", "StateData.ApplyVBTCBridgePoolUnlock()");
                    return;
                }

                // Verify allocations sum matches total
                decimal allocSum = allocations.Sum(a => a.UnlockAmount);
                if (Math.Abs(allocSum - totalAmount.Value) > 0.00000001M)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgePoolUnlock: allocation sum {allocSum} != totalAmount {totalAmount.Value}", "StateData.ApplyVBTCBridgePoolUnlock()");
                    return;
                }

                // Height-gated ordering fix (BridgeIntraBlockGuardHeight): consume the burn BEFORE
                // crediting so a second unlock for the same burn in the same block credits nothing, and
                // debit each lock BEFORE crediting so a failed debit (lock already drawn by an earlier tx)
                // never leaves an uncovered credit. Pre-gate keeps the legacy order for history.
                var strictOrder = tx.Height >= Globals.BridgeIntraBlockGuardHeight;

                if (strictOrder && !VBTCBridgeConsumedBurn.TryMarkConsumed(exitBurnTxHash, "POOL_UNLOCK", tx.Hash, tx.Height))
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgePoolUnlock: burn {exitBurnTxHash} already consumed — refusing to credit (tx {tx.Hash})", "StateData.ApplyVBTCBridgePoolUnlock()");
                    return;
                }

                // Process each allocation: partial-unlock each lock, credit each lock's contract
                foreach (var alloc in allocations)
                {
                    var rec = VBTCBridgeLockState.GetByLockId(alloc.LockId);
                    if (rec == null || rec.IsUnlocked)
                    {
                        ErrorLogUtility.LogError($"ApplyVBTCBridgePoolUnlock: lock {alloc.LockId} not found or already fully unlocked", "StateData.ApplyVBTCBridgePoolUnlock()");
                        continue;
                    }

                    long allocSats = (long)(alloc.UnlockAmount * 100_000_000M);

                    var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(rec.SmartContractUID);
                    if (scStateTreiRec == null)
                    {
                        ErrorLogUtility.LogError($"ApplyVBTCBridgePoolUnlock: contract {rec.SmartContractUID} not found for lock {alloc.LockId}", "StateData.ApplyVBTCBridgePoolUnlock()");
                        continue;
                    }

                    if (strictOrder)
                    {
                        // Debit first; credit only when the debit succeeded.
                        if (!VBTCBridgeLockState.ApplyPartialUnlock(alloc.LockId, alloc.UnlockAmount, allocSats))
                        {
                            ErrorLogUtility.LogError($"ApplyVBTCBridgePoolUnlock: lock {alloc.LockId} could not cover {alloc.UnlockAmount} — refusing to credit (tx {tx.Hash})", "StateData.ApplyVBTCBridgePoolUnlock()");
                            continue;
                        }
                        CreditPoolUnlock(scStateTreiRec, alloc.UnlockAmount, vfxDestinationAddress);
                    }
                    else
                    {
                        CreditPoolUnlock(scStateTreiRec, alloc.UnlockAmount, vfxDestinationAddress);
                        if (!VBTCBridgeLockState.ApplyPartialUnlock(alloc.LockId, alloc.UnlockAmount, allocSats))
                        {
                            ErrorLogUtility.LogError($"ApplyVBTCBridgePoolUnlock: failed to apply partial unlock for lock {alloc.LockId}", "StateData.ApplyVBTCBridgePoolUnlock()");
                        }
                    }

                    BridgeLockRecord.FinalizeFromChainUnlockIfPending(alloc.LockId);
                }

                // Pre-gate: legacy position of the consumption mark (after credit).
                if (!strictOrder)
                    VBTCBridgeConsumedBurn.TryMarkConsumed(exitBurnTxHash, "POOL_UNLOCK", tx.Hash, tx.Height);

                SCLogUtility.Log($"ApplyVBTCBridgePoolUnlock: totalAmount={totalAmount.Value} BTC, dest={vfxDestinationAddress}, allocations={allocations.Count}, exitBurn={exitBurnTxHash}",
                    "StateData.ApplyVBTCBridgePoolUnlock()");
            }
            catch (Exception ex)
            {
                NoteApplyStepFailed(tx.Height, ex); // a fault of this node's storage is flagged (fourth review, round 8)
                ErrorLogUtility.LogError($"ApplyVBTCBridgePoolUnlock error: {ex.Message}", "StateData.ApplyVBTCBridgePoolUnlock()");
            }
        }

        private static void ApplyVBTCBridgeExitToBTC(Transaction tx)
        {
            try
            {
                if (tx.FromAddress != tx.ToAddress)
                    return;
                var jobj = JObject.Parse(tx.Data);
                var totalAmount = jobj["TotalAmount"]?.ToObject<decimal?>();
                var totalAmountSats = jobj["TotalAmountSats"]?.ToObject<long?>();
                var btcDestination = jobj["BtcDestination"]?.ToObject<string>();
                var baseBurnTxHash = jobj["BaseBurnTxHash"]?.ToObject<string>();
                var allocationsToken = jobj["Allocations"];
                var btcWithdrawalsToken = jobj["BtcWithdrawals"];

                // Support legacy format (single contract/lockId) for backward compatibility
                if (allocationsToken == null)
                {
                    // Legacy: single lock exit (no partial unlocks applied)
                    var scUID = jobj["ContractUID"]?.ToObject<string>();
                    var lockId = jobj["LockId"]?.ToObject<string>();
                    var amount = jobj["Amount"]?.ToObject<decimal?>();
                    var amountSats = jobj["AmountSats"]?.ToObject<long?>();
                    if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(lockId) || !amount.HasValue || !amountSats.HasValue ||
                        string.IsNullOrEmpty(btcDestination) || string.IsNullOrEmpty(baseBurnTxHash))
                        return;

                    var st = new VBTCBridgeBtcExitState
                    {
                        BaseBurnTxHash = baseBurnTxHash.Trim(),
                        LockId = lockId.Trim(),
                        SmartContractUID = scUID,
                        OwnerAddress = tx.FromAddress,
                        Amount = amount.Value,
                        AmountSats = amountSats.Value,
                        BtcDestination = btcDestination,
                        ExitTxHash = tx.Hash,
                        CreatedTimestamp = tx.Timestamp,
                        IsComplete = false,
                        AllocationsJson = JsonConvert.SerializeObject(new List<PoolUnlockAllocation>
                        {
                            new PoolUnlockAllocation { LockId = lockId.Trim(), SmartContractUID = scUID, UnlockAmount = amount.Value }
                        })
                    };
                    VBTCBridgeBtcExitState.TryInsert(st);
                    SCLogUtility.Log($"ApplyVBTCBridgeExitToBTC (legacy): burn={baseBurnTxHash}, lockId={lockId}", "StateData.ApplyVBTCBridgeExitToBTC()");
                    return;
                }

                // V3 format: FIFO allocation plan with partial unlocks
                if (!totalAmount.HasValue || !totalAmountSats.HasValue ||
                    string.IsNullOrEmpty(btcDestination) || string.IsNullOrEmpty(baseBurnTxHash))
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeExitToBTC: missing required fields in V3 format", "StateData.ApplyVBTCBridgeExitToBTC()");
                    return;
                }

                var allocations = allocationsToken.ToObject<List<PoolUnlockAllocation>>();
                if (allocations == null || allocations.Count == 0)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeExitToBTC: empty allocations", "StateData.ApplyVBTCBridgeExitToBTC()");
                    return;
                }

                // Verify allocations sum matches total
                decimal allocSum = allocations.Sum(a => a.UnlockAmount);
                if (Math.Abs(allocSum - totalAmount.Value) > 0.00000001M)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeExitToBTC: allocation sum {allocSum} != totalAmount {totalAmount.Value}", "StateData.ApplyVBTCBridgeExitToBTC()");
                    return;
                }

                // The exit's record first: when the burn already has one, nothing is drawn (fifth review, round 13: a
                // second copy in the same block drew its locks, found the record taken, and was never paid or restored).
                var recordFirst = VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.Recording(tx.Height);
                if (recordFirst && tx.Height >= Globals.BridgeIntraBlockGuardHeight)
                    VBTCBridgeBtcExitState.DeleteFailedRecord(baseBurnTxHash.Trim());
                if (recordFirst && VBTCBridgeBtcExitState.GetByBurnHash(baseBurnTxHash.Trim()) != null)
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeExitToBTC: burn {baseBurnTxHash} already has an exit; no lock drawn", "StateData.ApplyVBTCBridgeExitToBTC()");
                    return;
                }

                // Process each allocation: apply partial unlock to each lock
                foreach (var alloc in allocations)
                {
                    var rec = VBTCBridgeLockState.GetByLockId(alloc.LockId);
                    if (rec == null || rec.IsUnlocked)
                    {
                        ErrorLogUtility.LogError($"ApplyVBTCBridgeExitToBTC: lock {alloc.LockId} not found or already fully unlocked", "StateData.ApplyVBTCBridgeExitToBTC()");
                        continue;
                    }

                    long allocSats = (long)(alloc.UnlockAmount * 100_000_000M);

                    // Apply partial unlock to the lock state (reduces RemainingAmount, marks fully unlocked if 0)
                    if (!VBTCBridgeLockState.ApplyPartialUnlock(alloc.LockId, alloc.UnlockAmount, allocSats))
                    {
                        ErrorLogUtility.LogError($"ApplyVBTCBridgeExitToBTC: failed to apply partial unlock for lock {alloc.LockId}", "StateData.ApplyVBTCBridgeExitToBTC()");
                    }

                    BridgeLockRecord.FinalizeFromChainUnlockIfPending(alloc.LockId);
                }

                // Insert BTC exit state record for tracking (use first allocation's contract for backward compat)
                var firstAlloc = allocations.First();
                var exitState = new VBTCBridgeBtcExitState
                {
                    BaseBurnTxHash = baseBurnTxHash.Trim(),
                    LockId = string.Join(",", allocations.Select(a => a.LockId)),
                    SmartContractUID = firstAlloc.SmartContractUID,
                    OwnerAddress = tx.FromAddress,
                    Amount = totalAmount.Value,
                    AmountSats = totalAmountSats.Value,
                    BtcDestination = btcDestination,
                    ExitTxHash = tx.Hash,
                    CreatedTimestamp = tx.Timestamp,
                    IsComplete = false,
                    AllocationsJson = JsonConvert.SerializeObject(allocations)
                };
                if (VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.Recording(tx.Height))
                    exitState.ExitVfxHeight = tx.Height;
                // A prior FAILED record for this burn (locks already restored) is superseded by the re-exit.
                if (tx.Height >= Globals.BridgeIntraBlockGuardHeight)
                    VBTCBridgeBtcExitState.DeleteFailedRecord(baseBurnTxHash.Trim());
                // Vaults the chain watches: the Bitcoin transaction for each vault this exit draws on is fixed now, from state.
                if (VBTCBridgeBtcExitState.TryInsert(exitState))
                    VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.OnExitApplied(exitState, tx.Height);

                SCLogUtility.Log(
                    $"ApplyVBTCBridgeExitToBTC (V3): burn={baseBurnTxHash}, totalAmount={totalAmount.Value} BTC, " +
                    $"allocations={allocations.Count}, locks=[{string.Join(",", allocations.Select(a => $"{a.LockId}:{a.UnlockAmount}"))}]",
                    "StateData.ApplyVBTCBridgeExitToBTC()");
            }
            catch (Exception ex)
            {
                NoteApplyStepFailed(tx.Height, ex); // a fault of this node's storage is flagged (fourth review, round 8)
                ErrorLogUtility.LogError($"ApplyVBTCBridgeExitToBTC error: {ex.Message}", "StateData.ApplyVBTCBridgeExitToBTC()");
            }
        }

        private static void ApplyVBTCBridgeExitToBTCComplete(Transaction tx)
        {
            try
            {
                if (tx.FromAddress != tx.ToAddress)
                    return;
                // An exit on watched vaults is completed by the Bitcoin report that shows its last transaction.
                if (VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ExitRefusal(tx) != null)
                    return;
                var jobj = JObject.Parse(tx.Data);
                var baseBurnTxHash = jobj["BaseBurnTxHash"]?.ToObject<string>();
                var btcTxHash = jobj["BtcTxHash"]?.ToObject<string>();
                if (string.IsNullOrEmpty(baseBurnTxHash) || string.IsNullOrEmpty(btcTxHash))
                    return;
                VBTCBridgeBtcExitState.MarkComplete(baseBurnTxHash.Trim(), btcTxHash.Trim());
                // BTC left the pool for this burn: it is consumed for every bridge path from here on.
                if (tx.Height >= Globals.BridgeIntraBlockGuardHeight)
                    VBTCBridgeConsumedBurn.TryMarkConsumed(baseBurnTxHash, "BTC_EXIT_COMPLETE", tx.Hash, tx.Height);
                SCLogUtility.Log($"ApplyVBTCBridgeExitToBTCComplete: burn={baseBurnTxHash}, btcTx={btcTxHash}", "StateData.ApplyVBTCBridgeExitToBTCComplete()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"ApplyVBTCBridgeExitToBTCComplete error: {ex.Message}", "StateData.ApplyVBTCBridgeExitToBTCComplete()");
            }
        }

        /// <summary>
        /// Handle VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL: reverse partial unlocks for failed locks,
        /// blacklist them, and mark the BTC exit state as failed so a retry can occur.
        /// </summary>
        private static void ApplyVBTCBridgeExitToBTCFail(Transaction tx)
        {
            try
            {
                if (tx.FromAddress != tx.ToAddress)
                    return;

                var jobj = JObject.Parse(tx.Data);
                var baseBurnTxHash = jobj["BaseBurnTxHash"]?.ToObject<string>();
                var exitTxHash = jobj["ExitTxHash"]?.ToObject<string>();
                var reason = jobj["Reason"]?.ToObject<string>() ?? "Unknown";
                var failedAllocationsToken = jobj["FailedAllocations"];

                if (string.IsNullOrEmpty(baseBurnTxHash) || failedAllocationsToken == null)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeExitToBTCFail: missing required fields", "StateData.ApplyVBTCBridgeExitToBTCFail()");
                    return;
                }

                var failedAllocations = failedAllocationsToken.ToObject<List<PoolUnlockAllocation>>();
                if (failedAllocations == null || failedAllocations.Count == 0)
                {
                    ErrorLogUtility.LogError("ApplyVBTCBridgeExitToBTCFail: empty FailedAllocations", "StateData.ApplyVBTCBridgeExitToBTCFail()");
                    return;
                }
                // An exit on watched vaults is failed by rule, never by a transaction.
                if (VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ExitRefusal(tx) != null)
                    return;

                // For each failed allocation: restore the partial unlock and blacklist the lock
                foreach (var alloc in failedAllocations)
                {
                    long allocSats = (long)(alloc.UnlockAmount * 100_000_000M);

                    var blacklisted = VBTCBridgeLockState.BlacklistLock(
                        alloc.LockId,
                        alloc.UnlockAmount,
                        allocSats,
                        $"FROST fail: {reason}");

                    if (!blacklisted)
                    {
                        ErrorLogUtility.LogError(
                            $"ApplyVBTCBridgeExitToBTCFail: failed to blacklist lock {alloc.LockId}",
                            "StateData.ApplyVBTCBridgeExitToBTCFail()");
                    }
                }

                // Mark the BTC exit state as failed (if it exists)
                var exitState = VBTCBridgeBtcExitState.GetByBurnTxHash(baseBurnTxHash.Trim());
                // From the recording height a failure of part of an exit (the handler's, when some vaults could not sign
                // and the rest paid) leaves the exit open for its completion: marked failed, its completion was refused
                // if mined later, its burn was never consumed, and it could be exited again and the paid part paid
                // twice (fifth review, round 21). The failed parts leave the exit's plan.
                if (exitState != null && !exitState.IsComplete && VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.Recording(tx.Height))
                {
                    var planned = JsonConvert.DeserializeObject<List<PoolUnlockAllocation>>(exitState.AllocationsJson ?? "[]") ?? new List<PoolUnlockAllocation>();
                    var remaining = new List<PoolUnlockAllocation>(planned);
                    foreach (var failed in failedAllocations)
                    {
                        var match = remaining.FindIndex(a => a.LockId == failed.LockId && a.UnlockAmount == failed.UnlockAmount);
                        if (match >= 0) remaining.RemoveAt(match);
                    }
                    if (remaining.Count > 0 && remaining.Count < planned.Count)
                    {
                        exitState.AllocationsJson = JsonConvert.SerializeObject(remaining);
                        VBTCBridgeBtcExitState.Update(exitState);
                        SCLogUtility.Log($"ApplyVBTCBridgeExitToBTCFail: part of the exit for burn {baseBurnTxHash} failed ({failedAllocations.Count} allocation(s)); {remaining.Count} still open for completion.",
                            "StateData.ApplyVBTCBridgeExitToBTCFail()");
                        return;
                    }
                }
                if (exitState != null && !exitState.IsComplete)
                {
                    exitState.IsComplete = true; // Mark as "done" so the retry creates a new record
                    exitState.BtcTxHash = $"FAILED:{tx.Hash}";
                    VBTCBridgeBtcExitState.Update(exitState);
                }

                SCLogUtility.Log(
                    $"ApplyVBTCBridgeExitToBTCFail: burn={baseBurnTxHash}, failedLocks={failedAllocations.Count}, " +
                    $"locks=[{string.Join(",", failedAllocations.Select(a => $"{a.LockId}:{a.UnlockAmount}"))}], reason={reason}",
                    "StateData.ApplyVBTCBridgeExitToBTCFail()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"ApplyVBTCBridgeExitToBTCFail error: {ex.Message}", "StateData.ApplyVBTCBridgeExitToBTCFail()");
            }
        }

        private static void RequestVBTCV2Withdrawal(Transaction tx)
        {
            try
            {
                // Parse transaction data
                var jobj = JObject.Parse(tx.Data);
                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var btcAddress = jobj["BTCAddress"]?.ToObject<string?>();
                var amount = jobj["Amount"]?.ToObject<decimal?>();
                var feeRate = jobj["FeeRate"]?.ToObject<int?>();
                var uniqueId = jobj["UniqueId"]?.ToObject<string?>() ?? tx.Hash; // Use tx.Hash as fallback for uniqueId
                var originalRequestTime = jobj["OriginalRequestTime"]?.ToObject<long?>() ?? tx.Timestamp;
                var originalSignature = jobj["OriginalSignature"]?.ToObject<string?>() ?? "";

                // FIND-002 FIX: Bind requester to tx.FromAddress - DO NOT trust tx.Data.OwnerAddress
                var requesterAddress = tx.FromAddress;

                if (string.IsNullOrEmpty(scUID) || !amount.HasValue || !feeRate.HasValue || string.IsNullOrEmpty(btcAddress))
                {
                    ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal failed: Missing required fields", "StateData.RequestVBTCV2Withdrawal()");
                    return;
                }

                // VX-01 (defense in depth; VerifyTX rejects this): a non-positive request is never
                // applied — no request record, no escrow row.
                if (amount.Value <= 0M)
                {
                    ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal refused non-positive amount {amount.Value}. TX: {tx.Hash}", "StateData.RequestVBTCV2Withdrawal()");
                    return;
                }

                // FIND-002 FIX: Create per-user withdrawal request record
                // This allows tracking of who requested the withdrawal and prevents
                // unauthorized parties from completing another user's withdrawal
                var withdrawalRequest = new VBTCWithdrawalRequest
                {
                    RequestorAddress = requesterAddress, // BOUND to tx.FromAddress
                    SmartContractUID = scUID,
                    Amount = amount.Value,
                    BTCDestination = btcAddress,
                    FeeRate = feeRate.Value,
                    OriginalUniqueId = uniqueId,
                    OriginalRequestTime = originalRequestTime,
                    OriginalSignature = originalSignature,
                    Timestamp = tx.Timestamp,
                    TransactionHash = tx.Hash,
                    Status = VBTCWithdrawalStatus.Requested,
                    IsCompleted = false,
                    // S3C §0: stamp the mined block height — the consensus-deterministic value
                    // all nodes agree on. Drives the per-contract anti-grief expiry gate.
                    RequestBlockHeight = tx.Height
                };

                // Save the withdrawal request to the per-user tracking database
                // This is consensus-critical and must succeed on ALL nodes.
                // update: true — on the API node that served RequestWithdrawalRaw, a local
                // pre-registration row already exists under the same (Requestor, UniqueId, scUID)
                // composite key; the mined record must UPDATE it (stamping the real TransactionHash
                // and mined height) rather than fail the insert and leave the row incomplete forever.
                // A vault the chain watches: the debit first (its write throws when it fails), then the request's row, which
                // must then exist; only then is the withdrawal fixed on coins. The other order left a request that could be
                // pinned and paid when its debit had not been written (fourth review, round 5).
                var watchedVault = VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(scUID);
                if (watchedVault && VBTCWithdrawalRequest.EscrowAppliesTo(tx.Height) && !WriteWithdrawalEscrowDebit(scUID, requesterAddress, amount.Value))
                {
                    ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal: escrow debit could not be written for {scUID} (contract state missing)", "StateData.RequestVBTCV2Withdrawal()");
                    return;
                }

                var saved = VBTCWithdrawalRequest.Save(withdrawalRequest, update: true);
                if (watchedVault && (!saved || VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, scUID) == null))
                    throw new System.IO.IOException($"Withdrawal request {tx.Hash} on vault {scUID} was debited but its row was not written.");
                if (!saved)
                {
                    ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal failed: Could not save withdrawal request", "StateData.RequestVBTCV2Withdrawal()");
                    return;
                }

                // ESCROW (gated): debit the requester NOW. vBTC used to be burned only at COMPLETE, so a
                // holder could receive the BTC, never complete, keep the vBTC and repeat after expiry.
                // Refunded only by an approved cancellation — never by expiry.
                if (!watchedVault && VBTCWithdrawalRequest.EscrowAppliesTo(tx.Height))
                {
                    if (!WriteWithdrawalEscrowDebit(scUID, requesterAddress, amount.Value))
                        ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal: escrow debit could not be written for {scUID} (contract state missing)", "StateData.RequestVBTCV2Withdrawal()");
                }

                // A vault the chain watches: the Bitcoin transaction that will pay this request is fixed now, from state.
                VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.OnRequestApplied(scUID, tx.Hash, tx.Height);

                // Also update contract-level tracking for backward compatibility (local DB only — informational)
                // Remote nodes won't have VBTCContractV2 locally, so this is conditional.
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract != null)
                {
                    contract.WithdrawalStatus = VBTCWithdrawalStatus.Requested;
                    contract.ActiveWithdrawalRequestHash = tx.Hash;
                    contract.ActiveWithdrawalAmount = amount.Value;
                    contract.ActiveWithdrawalBTCDestination = btcAddress;
                    contract.ActiveWithdrawalFeeRate = feeRate.Value;
                    contract.ActiveWithdrawalRequestTime = tx.Timestamp;
                    VBTCContractV2.UpdateContract(contract);
                }

                SCLogUtility.Log($"RequestVBTCV2Withdrawal completed: SCUID={scUID}, Requester={requesterAddress}, Amount={amount.Value} BTC, Destination={btcAddress}, TxHash={tx.Hash}", 
                    "StateData.RequestVBTCV2Withdrawal()");
            }
            catch (Exception ex)
            {
                NoteWithdrawalStoreStepFailed(tx.Height, ex); // a fault on this node only: flagged, not left as other state than its peers' (fourth review, round 3)
                ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal error: {ex.Message}", "StateData.RequestVBTCV2Withdrawal()");
            }
        }

        /// <summary>
        /// vBTC V2 multi-contract withdrawal REQUEST apply (Function "VBTCWithdrawalRequestMultiV2()",
        /// height-gated at the dispatch site): mints one <see cref="VBTCWithdrawalRequest"/> row per
        /// input contract, ALL sharing this REQUEST tx hash, and writes the same per-contract escrow
        /// debit a single request writes. Every downstream per-contract rule (the active-request
        /// gate, the repeat cooldown, the owner add-back, cancellation, FROST authorization) then
        /// sees exactly what N single requests would have produced — the only difference is that the
        /// rows share a hash, which is why every per-contract reader looks rows up by
        /// (hash, contract) rather than hash alone.
        /// Each row is later paid out by its OWN Bitcoin transaction and finalized by its OWN
        /// single-shape WITHDRAWAL_COMPLETE.
        /// CONSENSUS SAFETY: the requester is tx.FromAddress (bound to the signer) — never an
        /// address embedded in tx.Data.
        /// </summary>
        private static void RequestVBTCV2WithdrawalMulti(Transaction tx)
        {
            try
            {
                var jobj = JObject.Parse(tx.Data);
                var btcAddress = jobj["BTCAddress"]?.ToObject<string?>();
                var feeRate = jobj["FeeRate"]?.ToObject<int?>();
                var uniqueId = jobj["UniqueId"]?.ToObject<string?>() ?? tx.Hash;
                var originalRequestTime = jobj["OriginalRequestTime"]?.ToObject<long?>() ?? tx.Timestamp;
                var originalSignature = jobj["OriginalSignature"]?.ToObject<string?>() ?? "";
                var inputs = jobj["Inputs"]?.ToObject<List<Bitcoin.Models.VBTCV2MultiWithdrawalInput>?>();

                var requesterAddress = tx.FromAddress;

                if (string.IsNullOrEmpty(btcAddress) || !feeRate.HasValue || inputs == null || !inputs.Any())
                {
                    ErrorLogUtility.LogError($"RequestVBTCV2WithdrawalMulti failed: Missing required fields. TX: {tx.Hash}", "StateData.RequestVBTCV2WithdrawalMulti()");
                    return;
                }

                foreach (var input in inputs)
                {
                    if (string.IsNullOrEmpty(input.SCUID) || input.Amount <= 0)
                    {
                        ErrorLogUtility.LogError($"RequestVBTCV2WithdrawalMulti skipped invalid input. SCUID: {input.SCUID}, Amount: {input.Amount}. TX: {tx.Hash}", "StateData.RequestVBTCV2WithdrawalMulti()");
                        continue;
                    }

                    var withdrawalRequest = new VBTCWithdrawalRequest
                    {
                        RequestorAddress = requesterAddress, // BOUND to tx.FromAddress
                        SmartContractUID = input.SCUID,
                        Amount = input.Amount,
                        BTCDestination = btcAddress,
                        FeeRate = feeRate.Value,
                        OriginalUniqueId = uniqueId,
                        OriginalRequestTime = originalRequestTime,
                        OriginalSignature = originalSignature,
                        Timestamp = tx.Timestamp,
                        TransactionHash = tx.Hash,
                        Status = VBTCWithdrawalStatus.Requested,
                        IsCompleted = false,
                        RequestBlockHeight = tx.Height
                    };

                    // A watched vault: the debit first, then the row, which must exist (as for the single shape).
                    var watchedVault = VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(input.SCUID);
                    if (watchedVault && VBTCWithdrawalRequest.EscrowAppliesTo(tx.Height) && !WriteWithdrawalEscrowDebit(input.SCUID, requesterAddress, input.Amount))
                    {
                        ErrorLogUtility.LogError($"RequestVBTCV2WithdrawalMulti: escrow debit could not be written for {input.SCUID} (contract state missing)", "StateData.RequestVBTCV2WithdrawalMulti()");
                        continue;
                    }
                    var savedRow = VBTCWithdrawalRequest.Save(withdrawalRequest, update: true);
                    if (watchedVault && (!savedRow || VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, input.SCUID) == null))
                        throw new System.IO.IOException($"Withdrawal request {tx.Hash} on vault {input.SCUID} was debited but its row was not written.");
                    if (!savedRow)
                    {
                        ErrorLogUtility.LogError($"RequestVBTCV2WithdrawalMulti failed: Could not save withdrawal request for {input.SCUID}. TX: {tx.Hash}", "StateData.RequestVBTCV2WithdrawalMulti()");
                        continue;
                    }

                    // ESCROW (gated): identical rule to the single shape, applied per contract.
                    if (!watchedVault && VBTCWithdrawalRequest.EscrowAppliesTo(tx.Height))
                    {
                        if (!WriteWithdrawalEscrowDebit(input.SCUID, requesterAddress, input.Amount))
                            ErrorLogUtility.LogError($"RequestVBTCV2WithdrawalMulti: escrow debit could not be written for {input.SCUID} (contract state missing)", "StateData.RequestVBTCV2WithdrawalMulti()");
                    }

                    VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.OnRequestApplied(input.SCUID, tx.Hash, tx.Height);

                    // Local-only informational contract tracking (remote nodes have no local record).
                    var contract = VBTCContractV2.GetContract(input.SCUID);
                    if (contract != null)
                    {
                        contract.WithdrawalStatus = VBTCWithdrawalStatus.Requested;
                        contract.ActiveWithdrawalRequestHash = tx.Hash;
                        contract.ActiveWithdrawalAmount = input.Amount;
                        contract.ActiveWithdrawalBTCDestination = btcAddress;
                        contract.ActiveWithdrawalFeeRate = feeRate.Value;
                        contract.ActiveWithdrawalRequestTime = tx.Timestamp;
                        VBTCContractV2.UpdateContract(contract);
                    }
                }

                SCLogUtility.Log($"RequestVBTCV2WithdrawalMulti completed: Inputs={inputs.Count}, Requester={requesterAddress}, Destination={btcAddress}, TxHash={tx.Hash}",
                    "StateData.RequestVBTCV2WithdrawalMulti()");
            }
            catch (Exception ex)
            {
                NoteWithdrawalStoreStepFailed(tx.Height, ex); // a fault on this node only: flagged, not left as other state than its peers' (fourth review, round 3)
                ErrorLogUtility.LogError($"RequestVBTCV2WithdrawalMulti error: {ex.Message}", "StateData.RequestVBTCV2WithdrawalMulti()");
            }
        }

        private static void CompleteVBTCV2Withdrawal(Transaction tx)
        {
            try
            {
                // A watched vault's withdrawals are completed by the Bitcoin report that shows their transaction.
                if (VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.IsWatched(tx))
                    return;

                // Parse transaction data
                var jobj = JObject.Parse(tx.Data);
                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var withdrawalRequestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
                var btcTxHash = jobj["BTCTransactionHash"]?.ToObject<string?>();

                if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(btcTxHash) || string.IsNullOrEmpty(withdrawalRequestHash))
                {
                    ErrorLogUtility.LogError($"CompleteVBTCV2Withdrawal failed: Missing required fields", "StateData.CompleteVBTCV2Withdrawal()");
                    return;
                }

                // FIND-002 FIX: Look up the withdrawal request by transaction hash
                // This ensures we use the stored request data, not untrusted tx.Data.
                // Contract-scoped: a multi-contract REQUEST mints one row per input under one hash,
                // and this COMPLETE finalizes exactly the row for its own ContractUID.
                var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                if (withdrawalRequest == null)
                {
                    // Store-divergence recovery: the request row is chain-derived (the REQUEST tx is
                    // mined), so a missing row means THIS node's local store is incomplete — e.g. a
                    // Databases folder that arrived by file copy. Bailing here would silently skip
                    // the consensus burn row below and permanently fork this node's state trei from
                    // every healthy node. Reconstruct the row from the mined REQUEST tx and proceed.
                    withdrawalRequest = Bitcoin.Services.VBTCWithdrawalStoreRebuildService.TryRecoverRequestRowFromChain(withdrawalRequestHash, tx.Height, scUID);
                    if (withdrawalRequest == null)
                    {
                        ErrorLogUtility.LogError($"CompleteVBTCV2Withdrawal failed: Withdrawal request not found for hash - {withdrawalRequestHash} (chain recovery also failed — run POST /frost/withdrawals/rebuild)", "StateData.CompleteVBTCV2Withdrawal()");
                        return;
                    }
                }

                // FIND-002 FIX: Validate that the person completing is the original requester
                if (withdrawalRequest.RequestorAddress != tx.FromAddress)
                {
                    ErrorLogUtility.LogError($"CompleteVBTCV2Withdrawal failed: tx.FromAddress ({tx.FromAddress}) does not match original requester ({withdrawalRequest.RequestorAddress})", "StateData.CompleteVBTCV2Withdrawal()");
                    return;
                }

                // Validate the request is not already completed
                if (withdrawalRequest.IsCompleted)
                {
                    ErrorLogUtility.LogError($"CompleteVBTCV2Withdrawal failed: Withdrawal request already completed - {withdrawalRequestHash}", "StateData.CompleteVBTCV2Withdrawal()");
                    return;
                }

                // FIND-002 FIX: Use the stored Amount from the request, NOT from tx.Data
                // This prevents an attacker from specifying a different burn amount
                var storedAmount = withdrawalRequest.Amount;

                if (storedAmount <= 0.0M)
                {
                    ErrorLogUtility.LogError($"CompleteVBTCV2Withdrawal failed: Stored amount is zero or less - {scUID}", "StateData.CompleteVBTCV2Withdrawal()");
                    return;
                }

                // Mark the withdrawal request as completed (consensus-critical — runs on ALL nodes)
                withdrawalRequest.Status = VBTCWithdrawalStatus.Completed;
                withdrawalRequest.IsCompleted = true;
                withdrawalRequest.BTCTxHash = btcTxHash;
                withdrawalRequest.CompletionTxHash = tx.Hash;          // informational (withdrawal history on any node)
                withdrawalRequest.CompletionTimestamp = tx.Timestamp;
                VBTCWithdrawalRequest.Save(withdrawalRequest, true);

                // Update local contract tracking if available (informational — only on nodes with local contract)
                // Remote nodes won't have VBTCContractV2 locally, so this is conditional.
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract != null)
                {
                    var historyEntry = new VBTCWithdrawalHistory
                    {
                        RequestHash = withdrawalRequestHash,
                        CompletionHash = tx.Hash,
                        BTCTransactionHash = btcTxHash,
                        Amount = storedAmount,
                        BTCDestination = withdrawalRequest.BTCDestination,
                        RequestTime = withdrawalRequest.Timestamp,
                        CompletionTime = tx.Timestamp,
                        FeeRate = withdrawalRequest.FeeRate
                    };

                    if (contract.WithdrawalHistory == null)
                        contract.WithdrawalHistory = new List<VBTCWithdrawalHistory>();
                    contract.WithdrawalHistory.Add(historyEntry);

                    contract.WithdrawalStatus = VBTCWithdrawalStatus.Completed;
                    contract.ActiveWithdrawalRequestHash = null;
                    contract.ActiveWithdrawalAmount = 0;
                    contract.ActiveWithdrawalBTCDestination = null;
                    contract.ActiveWithdrawalFeeRate = 0;
                    contract.ActiveWithdrawalRequestTime = 0;
                    VBTCContractV2.UpdateContract(contract);
                }

                // CONSENSUS-CRITICAL — must run on ALL nodes. Escrowed requests (mined at/after
                // WithdrawalEscrowHeight) were debited at REQUEST; COMPLETE only finalizes them. Legacy
                // requests are burned here, using the STORED amount (FIND-002), never tx.Data.
                if (!VBTCWithdrawalRequest.EscrowAppliesTo(withdrawalRequest.RequestBlockHeight))
                {
                    WriteWithdrawalEscrowDebit(scUID, withdrawalRequest.RequestorAddress, storedAmount);
                }

                SCLogUtility.Log($"CompleteVBTCV2Withdrawal completed: SCUID={scUID}, Requester={withdrawalRequest.RequestorAddress}, BTCTxHash={btcTxHash}, Amount={storedAmount} BTC, TxHash={tx.Hash}", 
                    "StateData.CompleteVBTCV2Withdrawal()");
            }
            catch (Exception ex)
            {
                NoteWithdrawalStoreStepFailed(tx.Height, ex); // a fault on this node only (a data fault stays a no-op everywhere)
                ErrorLogUtility.LogError($"CompleteVBTCV2Withdrawal error: {ex.Message}", "StateData.CompleteVBTCV2Withdrawal()");
            }
        }

        /// <summary>
        /// FIND-018 Fix: Handle VBTC_V2_WITHDRAWAL_CANCEL transaction
        /// Creates a cancellation request record and updates contract withdrawal status to Cancellation_Requested.
        /// Only the original withdrawal requestor (tx.FromAddress) can initiate cancellation.
        /// </summary>
        private static void CancelVBTCV2Withdrawal(Transaction tx)
        {
            try
            {
                // A watched vault: cancelled at once when nothing is pinned, never by vote.
                if (VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ApplyCancel(tx, tx.Height))
                    return;

                // VbtcCancellationVoteRulesHeight: the shared rules create the record (stamped with its mined height).
                if (Bitcoin.Services.VBTCCancellationVoting.RulesActive(tx.Height))
                {
                    if (Bitcoin.Services.VBTCCancellationVoting.ApplyCancel(tx, out var created) && created != null)
                    {
                        MarkLocalContractCancellationRequested(created.SmartContractUID);
                        SCLogUtility.Log($"CancelVBTCV2Withdrawal: Cancellation request created. CancellationUID={created.CancellationUID}, SCUID={created.SmartContractUID}, Requester={tx.FromAddress}, WithdrawalHash={created.WithdrawalRequestHash}, VotesCloseAt={created.RequestBlockHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS}",
                            "StateData.CancelVBTCV2Withdrawal()");
                    }
                    return;
                }

                var jobj = JObject.Parse(tx.Data);
                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var withdrawalRequestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
                var failureProof = jobj["FailureProof"]?.ToObject<string?>() ?? "";

                if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                {
                    ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal failed: Missing required fields", "StateData.CancelVBTCV2Withdrawal()");
                    return;
                }

                // Look up the withdrawal request. Contract-scoped: each contract's share of a
                // multi-contract withdrawal is cancelled independently.
                var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                if (withdrawalRequest == null)
                {
                    ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal failed: Withdrawal request not found - {withdrawalRequestHash}", "StateData.CancelVBTCV2Withdrawal()");
                    return;
                }

                // Only the original requestor can cancel (bound to tx.FromAddress)
                if (withdrawalRequest.RequestorAddress != tx.FromAddress)
                {
                    ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal failed: tx.FromAddress ({tx.FromAddress}) does not match requestor ({withdrawalRequest.RequestorAddress})", "StateData.CancelVBTCV2Withdrawal()");
                    return;
                }

                // Must be in Requested state (not already completed/cancelled)
                if (withdrawalRequest.IsCompleted)
                {
                    ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal failed: Withdrawal already completed/cancelled - {withdrawalRequestHash}", "StateData.CancelVBTCV2Withdrawal()");
                    return;
                }

                // Check for duplicate cancellation
                var existingCancellation = VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(withdrawalRequestHash, scUID);
                if (existingCancellation != null)
                {
                    ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal failed: Cancellation already exists for withdrawal - {withdrawalRequestHash} on contract {scUID}", "StateData.CancelVBTCV2Withdrawal()");
                    return;
                }

                // Create cancellation request
                var cancellationUID = $"CANCEL_{tx.Hash}";
                var cancellation = new VBTCWithdrawalCancellation
                {
                    CancellationUID = cancellationUID,
                    SmartContractUID = scUID,
                    OwnerAddress = tx.FromAddress,
                    WithdrawalRequestHash = withdrawalRequestHash,
                    BTCTxHash = "",
                    FailureProof = failureProof,
                    RequestTime = tx.Timestamp,
                    ValidatorVotes = new Dictionary<string, bool>(),
                    ApproveCount = 0,
                    RejectCount = 0,
                    IsApproved = false,
                    IsProcessed = false
                };

                VBTCWithdrawalCancellation.SaveCancellation(cancellation);

                // Update contract status to Cancellation_Requested
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract != null)
                {
                    contract.WithdrawalStatus = VBTCWithdrawalStatus.Cancellation_Requested;
                    VBTCContractV2.UpdateContract(contract);
                }

                SCLogUtility.Log($"CancelVBTCV2Withdrawal: Cancellation request created. CancellationUID={cancellationUID}, SCUID={scUID}, Requester={tx.FromAddress}, WithdrawalHash={withdrawalRequestHash}", 
                    "StateData.CancelVBTCV2Withdrawal()");
            }
            catch (Exception ex)
            {
                NoteWithdrawalStoreStepFailed(tx.Height, ex); // a fault on this node only (a data fault stays a no-op everywhere)
                ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal error: {ex.Message}", "StateData.CancelVBTCV2Withdrawal()");
            }
        }

        /// <summary>Local display only (the owner's node): the contract shows a cancellation in progress.</summary>
        private static void MarkLocalContractCancellationRequested(string scUID)
        {
            var contract = VBTCContractV2.GetContract(scUID);
            if (contract != null)
            {
                contract.WithdrawalStatus = VBTCWithdrawalStatus.Cancellation_Requested;
                VBTCContractV2.UpdateContract(contract);
            }
        }

        /// <summary>
        /// VbtcCancellationVoteRulesHeight: counts the vote through the shared rules and writes the escrow refund when
        /// this vote is the one that approved the cancellation (see VBTCCancellationVoting.RefundDueFor).
        /// </summary>
        private static void VoteOnVBTCV2CancellationByRules(Transaction tx)
        {
            var result = Bitcoin.Services.VBTCCancellationVoting.ApplyVote(tx);

            var refund = Bitcoin.Services.VBTCCancellationVoting.RefundDueFor(tx);
            if (refund != null)
            {
                if (!WriteWithdrawalRefundCredit(refund.SmartContractUID, refund.RequestorAddress, refund.Amount))
                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation: refund of {refund.Amount} to {refund.RequestorAddress} on {refund.SmartContractUID} could not be written (contract state missing). Vote: {tx.Hash}", "StateData.VoteOnVBTCV2Cancellation()");

                // Local display only: the contract no longer shows this withdrawal as active.
                var contract = VBTCContractV2.GetContract(refund.SmartContractUID);
                if (contract != null && contract.ActiveWithdrawalRequestHash == refund.TransactionHash)
                {
                    contract.WithdrawalStatus = VBTCWithdrawalStatus.None;
                    contract.ActiveWithdrawalRequestHash = null;
                    contract.ActiveWithdrawalAmount = 0;
                    contract.ActiveWithdrawalBTCDestination = null;
                    contract.ActiveWithdrawalFeeRate = 0;
                    contract.ActiveWithdrawalRequestTime = 0;
                    VBTCContractV2.UpdateContract(contract);
                }
            }

            if (result.Applied)
                SCLogUtility.Log($"VoteOnVBTCV2Cancellation: vote counted. CancellationUID={result.Cancellation?.CancellationUID}, Voter={tx.FromAddress}, Outcome={result.Outcome}, Approvals={result.ApproveCount}, Rejections={result.RejectCount}, ActiveVoters={result.ActiveVoters}, Required={Bitcoin.Services.VBTCCancellationVoting.RequiredApprovals(result.ActiveVoters)}" +
                    (refund != null ? $", Refunded={refund.Amount} to {refund.RequestorAddress}" : "") +
                    (!string.IsNullOrEmpty(result.Cancellation?.SignedBtcTxId) ? $", SignedBtcTx={result.Cancellation.SignedBtcTxId} (reported by {result.Cancellation.SignedTxReportedBy})" : ""),
                    "StateData.VoteOnVBTCV2Cancellation()");
        }

        /// <summary>
        /// FIND-018 Fix: Handle VBTC_V2_WITHDRAWAL_VOTE transaction
        /// Records a validator vote on a cancellation request.
        /// Legacy rules (below VbtcCancellationVoteRulesHeight): when 75% of the contract's full voter set approve,
        /// the withdrawal is cancelled and funds are unlocked. Only active vBTC validators (tx.FromAddress) can vote.
        /// </summary>
        private static void VoteOnVBTCV2Cancellation(Transaction tx)
        {
            if (VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.IsWatched(tx))
                return;
            try
            {
                if (Bitcoin.Services.VBTCCancellationVoting.RulesActive(tx.Height))
                {
                    VoteOnVBTCV2CancellationByRules(tx);
                    return;
                }

                var jobj = JObject.Parse(tx.Data);
                var cancellationUID = jobj["CancellationUID"]?.ToObject<string?>();
                var approve = jobj["Approve"]?.ToObject<bool?>() ?? false;

                if (string.IsNullOrEmpty(cancellationUID))
                {
                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation failed: Missing CancellationUID", "StateData.VoteOnVBTCV2Cancellation()");
                    return;
                }

                // Look up the cancellation request
                var cancellation = VBTCWithdrawalCancellation.GetCancellation(cancellationUID);
                if (cancellation == null)
                {
                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation failed: Cancellation not found - {cancellationUID}", "StateData.VoteOnVBTCV2Cancellation()");
                    return;
                }

                // Cannot vote on already processed cancellations
                if (cancellation.IsProcessed)
                {
                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation failed: Cancellation already processed - {cancellationUID}", "StateData.VoteOnVBTCV2Cancellation()");
                    return;
                }

                // S3C §7.4: voter must be active AND in the contract's DKG snapshot (or the public
                // set for legacy no-snapshot contracts). Public→public-only; S3C→S3C-only.
                // Every vote on this path is history (below VbtcCancellationVoteRulesHeight): it is read as live nodes read it
                // when they applied the block, whose tip was then the block itself (fourth review, round 4: the block
                // before differed at the window's edge), from committed blocks rather than the synced-only cache.
                var voterSet = Bitcoin.Services.VBTCService.ResolveCancellationVoterSet(cancellation.SmartContractUID, tx.Height);
                var validator = Bitcoin.Services.VBTCValidatorRegistry.GetValidatorAt(tx.FromAddress, tx.Height);
                if (validator == null || !validator.IsActive || !voterSet.Contains(tx.FromAddress))
                {
                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation failed: {tx.FromAddress} is not an eligible voter for contract {cancellation.SmartContractUID}", "StateData.VoteOnVBTCV2Cancellation()");
                    return;
                }

                // Prevent duplicate votes (first vote only, no overwrite)
                if (VBTCWithdrawalCancellation.HasValidatorVoted(cancellationUID, tx.FromAddress))
                {
                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation failed: Validator {tx.FromAddress} already voted on {cancellationUID}", "StateData.VoteOnVBTCV2Cancellation()");
                    return;
                }

                // Record the vote
                VBTCWithdrawalCancellation.AddVote(cancellationUID, tx.FromAddress, approve);

                // Check if 75% approval threshold reached. S3C §7.4: denominator = FULL snapshot
                // count (dead snapshot validators still count; only snapshot members can vote).
                var totalValidatorCount = voterSet.Count;
                
                if (totalValidatorCount > 0)
                {
                    // Re-read cancellation after vote was added
                    cancellation = VBTCWithdrawalCancellation.GetCancellation(cancellationUID);
                    if (cancellation != null)
                    {
                        var approvalPercentage = (int)((double)cancellation.ApproveCount / totalValidatorCount * 100);

                        if (approvalPercentage >= 75 && !cancellation.IsProcessed)
                        {
                            // Threshold reached - approve the cancellation
                            VBTCWithdrawalCancellation.MarkAsProcessed(cancellationUID, true);

                            // Cancel the original withdrawal request (unlock funds)
                            var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash, cancellation.SmartContractUID);
                            if (withdrawalRequest != null)
                            {
                                // At/after V2WithdrawalOwnerAddBackFixHeight: never flip a burn-backed
                                // Completed row to Cancelled. If the withdrawal already completed (BTC
                                // provably left the deposit and the burn row exists), a late vote
                                // threshold must not retire the row from the owner add-back — doing so
                                // would permanently understate the owner by the burned amount. Gated on
                                // the mined vote height so historical replay stays deterministic.
                                var voteFixActive = tx.Height >= Globals.V2WithdrawalOwnerAddBackFixHeight;
                                if (voteFixActive && withdrawalRequest.IsCompleted && withdrawalRequest.Status == VBTCWithdrawalStatus.Completed)
                                {
                                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation: threshold reached but withdrawal {cancellation.WithdrawalRequestHash} already completed with a burn — leaving row Completed.", "StateData.VoteOnVBTCV2Cancellation()");
                                }
                                else
                                {
                                    withdrawalRequest.Status = VBTCWithdrawalStatus.Cancelled;
                                    withdrawalRequest.IsCompleted = true;
                                    VBTCWithdrawalRequest.Save(withdrawalRequest, true);

                                    // Escrowed request: give the debited amount back (approved cancellation is
                                    // the ONLY path that returns escrow; expiry never does).
                                    if (VBTCWithdrawalRequest.EscrowAppliesTo(withdrawalRequest.RequestBlockHeight))
                                        WriteWithdrawalRefundCredit(cancellation.SmartContractUID, withdrawalRequest.RequestorAddress, withdrawalRequest.Amount);
                                }
                            }

                            // Reset contract withdrawal status back to None (funds unlocked)
                            var contract = VBTCContractV2.GetContract(cancellation.SmartContractUID);
                            if (contract != null)
                            {
                                contract.WithdrawalStatus = VBTCWithdrawalStatus.None;
                                contract.ActiveWithdrawalRequestHash = null;
                                contract.ActiveWithdrawalAmount = 0;
                                contract.ActiveWithdrawalBTCDestination = null;
                                contract.ActiveWithdrawalFeeRate = 0;
                                contract.ActiveWithdrawalRequestTime = 0;
                                VBTCContractV2.UpdateContract(contract);
                            }

                            SCLogUtility.Log($"VoteOnVBTCV2Cancellation: Cancellation APPROVED (75% threshold reached). CancellationUID={cancellationUID}, Approvals={cancellation.ApproveCount}/{totalValidatorCount}", 
                                "StateData.VoteOnVBTCV2Cancellation()");
                        }
                        else
                        {
                            SCLogUtility.Log($"VoteOnVBTCV2Cancellation: Vote recorded. CancellationUID={cancellationUID}, Voter={tx.FromAddress}, Approve={approve}, Progress={cancellation.ApproveCount}/{totalValidatorCount} ({approvalPercentage}%)", 
                                "StateData.VoteOnVBTCV2Cancellation()");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A vote this node could not judge (the validator set is read from committed blocks and a read error is
                // thrown, not answered "no") leaves its state unlike its peers': marked, so a restart rebuilds state.
                NoteWithdrawalStoreStepFailed(tx.Height, ex);
                ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation error: {ex.Message}", "StateData.VoteOnVBTCV2Cancellation()");
            }
        }

    }
}
