using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using LiteDB;

namespace ReserveBlockCore.Utilities
{
    public class BlockRollbackUtility
    {
        /// <summary>RE-ENTRANCY GUARD: Prevents ResetTreis from being triggered while already running.</summary>
        private static volatile bool _isResetTreisRunning = false;

        /// <summary>Public accessor so other services can check if a full state rebuild is in progress.</summary>
        public static bool IsResetTreisRunning => _isResetTreisRunning;

        /// <summary>
        /// Full chain rescan rollback — deletes blocks above newHeight, nukes all state,
        /// and replays the entire blockchain from genesis.
        /// 
        /// WARNING: This is O(N) over the entire chain and can take minutes/hours on long chains.
        /// For small rollbacks (1-3 blocks) during fork recovery, prefer <see cref="RollbackBlocksFast"/>.
        /// </summary>
        /// <param name="numBlocksRollback">Number of blocks to remove from the tip.</param>
        /// <param name="manageIsResyncing">If true (default), sets/clears Globals.IsResyncing.
        /// Set to false when the caller (e.g., ForkRecoveryUtility) manages the flag itself
        /// to prevent a premature clear during multi-step recovery operations.</param>
        public static async Task<bool> RollbackBlocks(int numBlocksRollback, bool manageIsResyncing = true)
        {
            if (manageIsResyncing)
            {
                Globals.IsResyncing = true;
                Globals.StopAllTimers = true;
            }
            try
            {
                var height = Globals.LastBlock.Height;
                var newHeight = height - (long)numBlocksRollback;

                var blocks = Block.GetBlocks();
                blocks.DeleteManySafe(x => x.Height > newHeight);
                DbContext.DB.Checkpoint();

                // FAST PATH: restore from a snapshot slot at/below the rollback target and replay
                // the tail — this is what upgrades every RollbackBlocksFast fallback (blocks with
                // NFT/token/reserve/SC TXs) from a 45-min genesis replay to seconds.
                var restored = await SnapshotRestoreUtility.TryRestoreAsync(newHeight, manageFlags: false);
                if (restored)
                    return true;

                return await ResetTreis();
            }
            catch {
                return default;
            }
            finally
            {
                if (manageIsResyncing)
                {
                    Globals.IsResyncing = false;
                    Globals.StopAllTimers = false;
                }
            }
        }

        /// <summary>
        /// FORK-RECOVERY FAST PATH: Incremental rollback that undoes state changes for
        /// only the removed block(s) rather than replaying the entire chain from genesis.
        /// 
        /// This is O(T) where T = number of transactions in the removed blocks, compared to
        /// the full <see cref="RollbackBlocks"/> which is O(N) over the entire blockchain.
        /// 
        /// For a typical 1-block rollback during fork recovery, this completes in milliseconds
        /// instead of minutes/hours.
        /// 
        /// Falls back to full <see cref="RollbackBlocks"/> if it encounters transaction types
        /// that cannot be safely reversed incrementally (smart contracts, reserves, etc.).
        /// </summary>
        /// <param name="numBlocksRollback">Number of blocks to remove from the tip (typically 1).</param>
        /// <param name="manageIsResyncing">If true, manages Globals.IsResyncing flag.</param>
        /// <returns>True if rollback succeeded, false otherwise.</returns>
        public static async Task<bool> RollbackBlocksFast(int numBlocksRollback, bool manageIsResyncing = true)
        {
            if (manageIsResyncing)
            {
                Globals.IsResyncing = true;
                Globals.StopAllTimers = true;
            }

            try
            {
                var currentHeight = Globals.LastBlock.Height;
                var targetHeight = currentHeight - (long)numBlocksRollback;

                if (targetHeight < 0)
                {
                    LogUtility.Log($"[RollbackFast] Cannot roll back below genesis. current={currentHeight} requested={numBlocksRollback}", "BlockRollback");
                    return false;
                }

                // Step 1: Load the blocks being removed (from tip downwards)
                var blocksToRemove = new List<Block>();
                for (long h = currentHeight; h > targetHeight; h--)
                {
                    var block = BlockchainData.GetBlockByHeight(h);
                    if (block == null)
                    {
                        LogUtility.Log($"[RollbackFast] Block at height {h} not found in DB. Falling back to full rollback.", "BlockRollback");
                        return await RollbackBlocks(numBlocksRollback, manageIsResyncing: false);
                    }
                    blocksToRemove.Add(block);
                }

                // Step 2: Check if all transactions can be incrementally reversed.
                // Only simple TX types are safe to undo. Complex types (smart contracts,
                // reserves, token operations, etc.) require a full state rebuild.
                foreach (var block in blocksToRemove)
                {
                    foreach (var tx in block.Transactions)
                    {
                        if (!CanIncrementallyReverse(tx))
                        {
                            LogUtility.Log(
                                $"[RollbackFast] Block {block.Height} contains non-reversible tx type {tx.TransactionType} " +
                                $"(hash={tx.Hash?[..Math.Min(12, tx.Hash?.Length ?? 0)]}). Falling back to full rollback.",
                                "BlockRollback");
                            return await RollbackBlocks(numBlocksRollback, manageIsResyncing: false);
                        }
                    }
                }

                // Step 3: Reverse state changes for each block (newest first)
                var accStTrei = StateData.GetAccountStateTrei();
                var txDataStore = TransactionData.GetAll();

                foreach (var block in blocksToRemove) // Already ordered newest first
                {
                    // Process transactions in reverse order for correctness
                    var txList = block.Transactions.ToList();
                    txList.Reverse();

                    foreach (var tx in txList)
                    {
                        try
                        {
                            // Reverse From address: restore balance (add back amount + fee), decrement nonce
                            if (tx.FromAddress != "Coinbase_TrxFees" && tx.FromAddress != "Coinbase_BlkRwd")
                            {
                                var from = StateData.GetSpecificAccountStateTrei(tx.FromAddress);
                                if (from != null)
                                {
                                    from.Balance += (tx.Amount + tx.Fee);
                                    from.Nonce = Math.Max(0, from.Nonce - 1);
                                    accStTrei.UpdateSafe(from);
                                }

                                // Also reverse local account balance
                                var fromAccount = AccountData.GetAccounts().FindOne(x => x.Address == tx.FromAddress);
                                if (fromAccount != null)
                                {
                                    AccountData.UpdateLocalBalanceAdd(tx.FromAddress, tx.Amount + tx.Fee);
                                }
                            }

                            // Reverse To address: remove received amount
                            if (tx.ToAddress != "Adnr_Base" &&
                                tx.ToAddress != "DecShop_Base" &&
                                tx.ToAddress != "Topic_Base" &&
                                tx.ToAddress != "Vote_Base" &&
                                tx.ToAddress != "Reserve_Base" &&
                                tx.ToAddress != "Token_Base")
                            {
                                var to = StateData.GetSpecificAccountStateTrei(tx.ToAddress);
                                if (to != null)
                                {
                                    to.Balance -= tx.Amount;
                                    // Don't go below zero due to floating point
                                    if (to.Balance < 0) to.Balance = 0;
                                    accStTrei.UpdateSafe(to);
                                }

                                // Also reverse local account balance
                                var toAccount = AccountData.GetAccounts().FindOne(x => x.Address == tx.ToAddress);
                                if (toAccount != null)
                                {
                                    await AccountData.UpdateLocalBalance(tx.ToAddress, tx.Amount);
                                }
                            }

                            // Remove transaction from local transaction store
                            txDataStore.DeleteManySafe(x => x.Hash == tx.Hash);
                        }
                        catch (Exception ex)
                        {
                            LogUtility.Log(
                                $"[RollbackFast] Error reversing tx {tx.Hash?[..Math.Min(12, tx.Hash?.Length ?? 0)]}: {ex.Message}. Falling back to full rollback.",
                                "BlockRollback");
                            return await RollbackBlocks(numBlocksRollback, manageIsResyncing: false);
                        }
                    }
                }

                // Step 4: Delete the blocks from the block store
                var blockStore = Block.GetBlocks();
                blockStore.DeleteManySafe(x => x.Height > targetHeight);
                DbContext.DB.Checkpoint();
                DbContext.DB_AccountStateTrei.Checkpoint();

                // Step 5 + 6: Update Globals.LastBlock to the new tip and clear stale in-memory
                // queues (shared with SnapshotRestoreUtility).
                RefreshInMemoryTip(targetHeight);

                LogUtility.Log(
                    $"[RollbackFast] SUCCESS: Rolled back {numBlocksRollback} block(s). " +
                    $"New tip: height={Globals.LastBlock.Height} hash={Globals.LastBlock.Hash?[..Math.Min(16, Globals.LastBlock.Hash?.Length ?? 0)]}",
                    "BlockRollback");

                return true;
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[RollbackFast] Exception: {ex.Message}. Falling back to full rollback.", "BlockRollback");
                try
                {
                    return await RollbackBlocks(numBlocksRollback, manageIsResyncing: false);
                }
                catch
                {
                    return false;
                }
            }
            finally
            {
                if (manageIsResyncing)
                {
                    Globals.IsResyncing = false;
                    Globals.StopAllTimers = false;
                }
            }
        }

        /// <summary>
        /// Sets Globals.LastBlock from the block store and clears stale in-memory queues above
        /// the new tip. Without this, orphaned blocks referencing a removed block's hash can
        /// still pass validation via Block.GetPreviousHash() and create gaps in the chain.
        /// Shared by RollbackBlocksFast and SnapshotRestoreUtility.
        /// </summary>
        public static void RefreshInMemoryTip(long targetHeight)
        {
            var newLastBlock = BlockchainData.GetLastBlock();
            if (newLastBlock != null)
            {
                Globals.LastBlock = newLastBlock;
            }
            else
            {
                LogUtility.Log($"[RefreshInMemoryTip] WARNING: GetLastBlock returned null after rewind to height {targetHeight}.", "BlockRollback");
                // Fall back to genesis state
                Globals.LastBlock = new Block { Height = -1 };
            }

            var queueKeysToRemove = Globals.NetworkBlockQueue.Keys.Where(k => k > targetHeight).ToList();
            var removedQueueCount = 0;
            foreach (var key in queueKeysToRemove)
            {
                if (Globals.NetworkBlockQueue.TryRemove(key, out _))
                    removedQueueCount++;
            }

            var broadcastKeysToRemove = Globals.BlockQueueBroadcasted.Keys.Where(k => k > targetHeight).ToList();
            foreach (var key in broadcastKeysToRemove)
                Globals.BlockQueueBroadcasted.TryRemove(key, out _);

            var backupKeysToRemove = Globals.BackupProofs.Keys.Where(k => k > targetHeight).ToList();
            foreach (var key in backupKeysToRemove)
                Globals.BackupProofs.TryRemove(key, out _);

            var hashKeysToRemove = Globals.BlockHashes.Keys.Where(k => k > targetHeight).ToList();
            foreach (var key in hashKeysToRemove)
                Globals.BlockHashes.TryRemove(key, out _);

            LogUtility.Log(
                $"[RefreshInMemoryTip] Cleared in-memory queues above height {targetHeight}: " +
                $"NetworkBlockQueue={removedQueueCount}, BlockQueueBroadcasted={broadcastKeysToRemove.Count}, " +
                $"BackupProofs={backupKeysToRemove.Count}, BlockHashes={hashKeysToRemove.Count}",
                "BlockRollback");
        }

        /// <summary>
        /// Determines if a transaction can be safely reversed incrementally.
        /// Only simple balance-transfer TX types qualify. Complex types (smart contracts,
        /// reserves, token operations, NFTs, etc.) require a full state rebuild.
        /// </summary>
        private static bool CanIncrementallyReverse(Transaction tx)
        {
            // Simple balance transfers and coinbase rewards can be reversed
            switch (tx.TransactionType)
            {
                case TransactionType.TX:
                    // Reserve addresses (xRBX prefix) have locked balance logic
                    // that is too complex for incremental reversal
                    if (tx.FromAddress != null && tx.FromAddress.StartsWith("xRBX"))
                        return false;
                    return true;

                // FORK-FIX: Zero-value lifecycle transactions can be safely reversed.
                // These transactions don't transfer any balance — they only update metadata
                // in the state trie (nonce, validator registry, etc.). The fast path can
                // handle them by just reversing the nonce increment without needing a full
                // chain replay via ResetTreis(). This prevents a catastrophic state wipe
                // when fork recovery rolls back a block containing these TX types.
                case TransactionType.VBTC_V2_VALIDATOR_REGISTER:
                case TransactionType.VBTC_V2_VALIDATOR_EXIT:
                case TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT:
                    return true;

                default:
                    // All other types (NFT, ADNR, VOTE, RESERVE, smart contract TXs, etc.)
                    // have complex state side-effects that can't be safely reversed
                    return false;
            }
        }

        /// <summary>
        /// Wipes every chain-derived state collection (rebuildable by replaying blocks through
        /// StateData.UpdateTreis) while preserving local data that CANNOT be rebuilt from the chain:
        /// FROST validator keys/peer backups (rsrv_frost_validator_keys / rsrv_frost_peer_backups),
        /// arbiter secret shares (DB_Shares), local bridge mint tracking (BridgeLockRecord), the
        /// Base exit-scan cursor (BridgeExitSyncState), shielded wallets, reserve account keys,
        /// and all wallet/keystore/bitcoin local data.
        ///
        /// Does NOT touch the block store, local wallet transactions (rsrv_transactions), or the
        /// mempool — callers decide whether to wipe those (full genesis rebuild) or prune above a
        /// height (snapshot restore). Shared by ResetTreis and SnapshotRestoreUtility.
        /// </summary>
        public static void WipeChainDerivedState()
        {
            // Core state treis (state_trei_status in the same file is intentionally preserved —
            // it is managed by StateTreiStatusService, not derived from blocks)
            var stateTrei = StateData.GetAccountStateTrei();
            var worldTrei = WorldTrei.GetWorldTrei();
            stateTrei.DeleteAllSafe();
            worldTrei.DeleteAllSafe();

            // Smart contract state — clear all collections (preserve structure)
            try
            {
                if (DbContext.DB_SmartContractStateTrei != null)
                {
                    foreach (var name in DbContext.DB_SmartContractStateTrei.GetCollectionNames().ToList())
                    {
                        var coll = DbContext.DB_SmartContractStateTrei.GetCollection(name);
                        coll.DeleteAll();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe SmartContractStateTrei: {ex.Message}");
            }

            // DecShop state — clear all collections (preserve structure)
            try
            {
                if (DbContext.DB_DecShopStateTrei != null)
                {
                    foreach (var name in DbContext.DB_DecShopStateTrei.GetCollectionNames().ToList())
                    {
                        var coll = DbContext.DB_DecShopStateTrei.GetCollection(name);
                        coll.DeleteAll();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe DecShopStateTrei: {ex.Message}");
            }

            // Topic trei
            try
            {
                var topicTrei = TopicTrei.GetTopics();
                if (topicTrei != null) topicTrei.DeleteAllSafe();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe TopicTrei: {ex.Message}");
            }

            // Vote
            try
            {
                var votes = Vote.GetVotes();
                if (votes != null) votes.DeleteAllSafe();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe Vote: {ex.Message}");
            }

            // Token governance votes — chain-derived (written by TokenVoteTopicCast during
            // UpdateTreis) but stored in the wallet DB file; previously missed, which left
            // duplicate votes behind after every replay.
            try
            {
                DbContext.DB_Wallet.GetCollection(DbContext.RSRV_TOKEN_VOTE).DeleteAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe TokenVote: {ex.Message}");
            }

            // DNR (Domain Name Records) — VFX and Bitcoin ADNRs share this file. The Bitcoin
            // collection was previously missed, leaving stale BTC ADNRs to collide with replay.
            try
            {
                var dnr = Adnr.GetAdnr();
                if (dnr != null) dnr.DeleteAllSafe();
                DbContext.DB_DNR.GetCollection(DbContext.RSRV_BITCOIN_ADNR).DeleteAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe DNR/ADNR: {ex.Message}");
            }

            // Reserve transactions (chain-derived) — rsrv_reserve_account (local keys) is preserved
            try
            {
                var reserveTxDb = ReserveTransactions.GetReserveTransactionsDb();
                if (reserveTxDb != null) reserveTxDb.DeleteAllSafe();
                DbContext.DB_Reserve.GetCollection(DbContext.RSRV_RESERVE_TRANSACTIONS_CALLED_BACK).DeleteAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe ReserveTransactions: {ex.Message}");
            }

            // vBTC V2 consensus state — named collections ONLY. This file also holds FROST
            // signing keys, peer key backups, local bridge mint tracking, and the Base exit-scan
            // cursor, none of which can be rebuilt from the chain. The previous wipe-all loop
            // destroyed validator signing material on every rebuild.
            try
            {
                if (DbContext.DB_vBTC != null)
                {
                    DbContext.DB_vBTC.GetCollection(DbContext.RSRV_VBTC_V2_CONTRACTS).DeleteAll();
                    DbContext.DB_vBTC.GetCollection(DbContext.RSRV_VBTC_V2_CANCELLATIONS).DeleteAll();
                    DbContext.DB_vBTC.GetCollection(DbContext.RSRV_VBTC_V2_VALIDATORS).DeleteAll();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe DB_vBTC consensus collections: {ex.Message}");
            }

            // Tokenized withdrawals
            try
            {
                if (DbContext.DB_TokenizedWithdrawals != null)
                    DbContext.DB_TokenizedWithdrawals.GetCollection(DbContext.RSRV_TOKENIZED_WITHDRAWALS).DeleteAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe DB_TokenizedWithdrawals: {ex.Message}");
            }

            // vBTC withdrawal requests + bridge lock/exit consensus state (all written from StateData)
            try
            {
                if (DbContext.DB_VBTCWithdrawalRequests != null)
                {
                    DbContext.DB_VBTCWithdrawalRequests.GetCollection(DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS).DeleteAll();
                    DbContext.DB_VBTCWithdrawalRequests.GetCollection(VBTCBridgeLockState.CollectionName).DeleteAll();
                    DbContext.DB_VBTCWithdrawalRequests.GetCollection(VBTCBridgeBtcExitState.CollectionName).DeleteAll();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe DB_VBTCWithdrawalRequests: {ex.Message}");
            }

            // NOTE: DB_Shares (arbiter secret shares) is deliberately NOT wiped — it is local
            // secret material, not chain-derived state, and was previously destroyed here in error.

            // Shielded pool chain state — replay re-applies private TXs, and commitments insert
            // blindly (no dedup), so skipping this wipe duplicates commitments and corrupts merkle
            // roots. ShieldedWallets (local viewing/spend material) is intentionally preserved.
            try
            {
                if (DbContext.DB_Privacy != null)
                {
                    DbContext.DB_Privacy.GetCollection(PrivacyDbContext.PRIV_COMMITMENTS).DeleteAll();
                    DbContext.DB_Privacy.GetCollection(PrivacyDbContext.PRIV_NULLIFIERS).DeleteAll();
                    DbContext.DB_Privacy.GetCollection(PrivacyDbContext.PRIV_POOL_STATE).DeleteAll();
                    DbContext.DB_Privacy.GetCollection(PrivacyDbContext.PRIV_MERKLE_NODES).DeleteAll();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WipeChainDerivedState] Warning: Could not wipe shielded pool state: {ex.Message}");
            }

            // Checkpoint all touched databases
            DbContext.DB_AccountStateTrei.Checkpoint();
            DbContext.DB_WorldStateTrei.Checkpoint();
            try { DbContext.DB_SmartContractStateTrei.Checkpoint(); } catch { }
            try { DbContext.DB_DecShopStateTrei.Checkpoint(); } catch { }
            try { DbContext.DB_TopicTrei.Checkpoint(); } catch { }
            try { DbContext.DB_Vote.Checkpoint(); } catch { }
            try { DbContext.DB_DNR.Checkpoint(); } catch { }
            try { DbContext.DB_Reserve.Checkpoint(); } catch { }
            try { DbContext.DB_Wallet.Checkpoint(); } catch { }
            try { DbContext.DB_vBTC?.Checkpoint(); } catch { }
            try { DbContext.DB_TokenizedWithdrawals?.Checkpoint(); } catch { }
            try { DbContext.DB_VBTCWithdrawalRequests?.Checkpoint(); } catch { }
            try { DbContext.DB_Privacy?.Checkpoint(); } catch { }
        }

        /// <summary>
        /// Full chain state rebuild — wipes ALL chain-derived state databases and replays
        /// every block through StateData.UpdateTreis() (the standard commit path that handles
        /// ALL transaction types: TX, NFT, tokens, ADNR, votes, reserves, vBTC, privacy, etc.)
        /// 
        /// After replay, resyncs local wallet balances from the rebuilt AccountStateTrei and
        /// re-populates local TransactionData for wallet addresses.
        /// 
        /// Does NOT wipe user data (wallet, HD wallet, peers, config, keystore, beacon, DST).
        /// </summary>
        public static async Task<bool> ResetTreis()
        {
            // RE-ENTRANCY GUARD: If ResetTreis is already running, reject the new request.
            // This prevents the infinite wipe→replay→wipe loop that occurs when TX-validation
            // failures keep triggering ResetTreis while a previous ResetTreis is still replaying 6.6M blocks.
            if (_isResetTreisRunning)
            {
                Console.WriteLine("[ResetTreis] BLOCKED: ResetTreis is already running. Ignoring duplicate request.");
                return false;
            }

            _isResetTreisRunning = true;
            // CRITICAL FIX: Set IsResyncing to block ALL block validation during the entire
            // state rebuild. Without this, ResetTreis wipes the state trie (Step 1) but
            // ValidateBlock keeps running on incoming P2P blocks, sees empty state → 
            // "new account with no balance" → triggers MORE recovery attempts → infinite loop.
            // The state trie stays EMPTY because concurrent validation corrupts the replay.
            var wasResyncing = Globals.IsResyncing;
            var wasStopTimers = Globals.StopAllTimers;
            Globals.IsResyncing = true;
            Globals.StopAllTimers = true;
            try
            {
                Console.WriteLine("[ResetTreis] Starting full chain state rebuild...");
                Console.WriteLine("[ResetTreis] IsResyncing=true — all block validation suspended during rebuild.");

                // ═══════════════════════════════════════════════════════════════
                // STEP 1: Wipe all chain-derived state databases
                // ═══════════════════════════════════════════════════════════════
                Console.WriteLine("[ResetTreis] Step 1: Wiping chain-derived state databases...");

                // Local wallet transactions — full genesis replay rebuilds these in Step 4b.
                var transactions = TransactionData.GetAll();
                transactions.DeleteAllSafe();

                // Mempool
                try
                {
                    var mempool = TransactionData.GetPool();
                    if (mempool != null) mempool.DeleteAllSafe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe Mempool: {ex.Message}");
                }

                WipeChainDerivedState();

                DbContext.DB.Checkpoint();
                try { DbContext.DB_Mempool.Checkpoint(); } catch { }

                Console.WriteLine("[ResetTreis] Step 1 complete — all state databases wiped.");

                // ═══════════════════════════════════════════════════════════════
                // STEP 2: Reset local account balances to 0
                // ═══════════════════════════════════════════════════════════════
                Console.WriteLine("[ResetTreis] Step 2: Resetting local account balances...");
                var accounts = AccountData.GetAccounts();
                var accountList = accounts.FindAll().ToList();
                foreach (var account in accountList)
                {
                    account.Balance = 0M;
                    accounts.UpdateSafe(account);
                }

                // ═══════════════════════════════════════════════════════════════
                // STEP 3: Replay each block through UpdateTreis (the standard commit path)
                // ═══════════════════════════════════════════════════════════════
                var blockCollection = BlockchainData.GetBlocks();
                var totalBlocks = blockCollection.Count();
                Console.WriteLine($"[ResetTreis] Step 3: Replaying {totalBlocks:N0} blocks through UpdateTreis...");
                LogUtility.Log($"[ResetTreis] Step 3: Starting replay of {totalBlocks:N0} blocks.", "BlockRollbackUtility.ResetTreis");
                
                // CRITICAL: Stream blocks lazily from LiteDB ordered by height.
                // DO NOT use .FindAll().OrderBy().ToList() — that loads ALL 6.6M blocks into memory
                // at once, causing OutOfMemory or hour-long delays before the loop even starts.
                // LiteDB's Query.All("Height", Ascending) returns an IEnumerable that reads one block
                // at a time from disk, keeping memory usage flat regardless of chain length.
                int processedCount = 0;
                int failCount = 0;
                var rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew();

                foreach (var block in blockCollection.Find(LiteDB.Query.All("Height", LiteDB.Query.Ascending)))
                {
                    try
                    {
                        // UpdateTreis is the comprehensive state commit path — it handles ALL TX types:
                        // TX, NFT, tokens, ADNR, votes, topics, dec shops, reserves, vBTC V2,
                        // privacy/shielded TXs, bridge locks/unlocks, etc.
                        await StateData.UpdateTreis(block);
                        processedCount++;

                        // Progress display: log every 10,000 blocks with percentage + ETA
                        // Uses Console.WriteLine (not \r overwrite) so output is reliable on all terminals
                        if (processedCount % 10000 == 0)
                        {
                            double pct = (double)processedCount / totalBlocks * 100.0;
                            var elapsed = rebuildStopwatch.Elapsed;
                            var blocksPerSecond = processedCount / Math.Max(elapsed.TotalSeconds, 1);
                            var remainingBlocks = totalBlocks - processedCount;
                            var etaSeconds = remainingBlocks / Math.Max(blocksPerSecond, 1);
                            var eta = TimeSpan.FromSeconds(etaSeconds);

                            var progressMsg = $"[ResetTreis] Rebuilding state: {pct:F1}% ({processedCount:N0}/{totalBlocks:N0}) — " +
                                $"Elapsed: {elapsed:hh\\:mm\\:ss} — ETA: ~{eta:hh\\:mm\\:ss}";
                            Console.WriteLine(progressMsg);
                            LogUtility.Log(progressMsg, "BlockRollbackUtility.ResetTreis");
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        ErrorLogUtility.LogError($"[ResetTreis] Error replaying block {block.Height}: {ex.Message}", "BlockRollbackUtility.ResetTreis");
                    }
                }

                rebuildStopwatch.Stop();
                var totalTime = rebuildStopwatch.Elapsed;
                ConsoleWriterService.Output(
                    $"\r[ResetTreis] Step 3 complete — replayed {processedCount:N0} blocks ({failCount} failures) in {totalTime:hh\\:mm\\:ss}.");
                LogUtility.Log(
                    $"[ResetTreis] Step 3 complete — replayed {processedCount:N0} blocks ({failCount} failures) in {totalTime:hh\\:mm\\:ss}.",
                    "BlockRollbackUtility.ResetTreis");

                // ═══════════════════════════════════════════════════════════════
                // STEP 4: Resync local wallet from rebuilt state
                // ═══════════════════════════════════════════════════════════════
                Console.WriteLine("[ResetTreis] Step 4: Resyncing local wallet balances and transactions...");

                // 4a: Update local account balances from rebuilt AccountStateTrei
                var walletAccounts = accounts.FindAll().ToList();
                var walletAddresses = new HashSet<string>(walletAccounts.Select(a => a.Address));

                foreach (var account in walletAccounts)
                {
                    var stateEntry = StateData.GetSpecificAccountStateTrei(account.Address);
                    if (stateEntry != null)
                    {
                        account.Balance = stateEntry.Balance;
                    }
                    else
                    {
                        account.Balance = 0M;
                    }
                    accounts.UpdateSafe(account);
                }

                // 4b: Re-populate local TransactionData for wallet addresses
                // Stream blocks lazily again — do NOT load all into memory
                if (walletAddresses.Count > 0)
                {
                    Console.WriteLine($"[ResetTreis] Step 4b: Scanning {totalBlocks:N0} blocks for wallet transactions ({walletAddresses.Count} addresses)...");
                    var txData = TransactionData.GetAll();
                    int scanCount = 0;
                    int insertedTxCount = 0;
                    var step4Stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    foreach (var block in blockCollection.Find(LiteDB.Query.All("Height", LiteDB.Query.Ascending)))
                    {
                        foreach (var tx in block.Transactions)
                        {
                            bool isRelevant = walletAddresses.Contains(tx.ToAddress) || walletAddresses.Contains(tx.FromAddress);
                            if (isRelevant)
                            {
                                var existing = txData.FindOne(x => x.Hash == tx.Hash);
                                if (existing == null)
                                {
                                    txData.InsertSafe(tx);
                                    insertedTxCount++;
                                }
                            }
                        }
                        scanCount++;
                        if (scanCount % 100000 == 0)
                        {
                            double pct = (double)scanCount / totalBlocks * 100.0;
                            Console.WriteLine($"[ResetTreis] Step 4b: Wallet TX scan {pct:F1}% ({scanCount:N0}/{totalBlocks:N0}) — found {insertedTxCount} TXs so far");
                        }
                    }

                    step4Stopwatch.Stop();
                    Console.WriteLine($"[ResetTreis] Step 4b complete — scanned {scanCount:N0} blocks, inserted {insertedTxCount} wallet TXs in {step4Stopwatch.Elapsed:hh\\:mm\\:ss}.");
                }
                else
                {
                    Console.WriteLine("[ResetTreis] Step 4b: No wallet addresses — skipping TX resync.");
                }

                // 4c: Clear mempool (already wiped in Step 1, but safety check)
                try
                {
                    var mempool = TransactionData.GetPool();
                    if (mempool != null && mempool.Count() > 0)
                    {
                        mempool.DeleteAllSafe();
                    }
                }
                catch { }

                Console.WriteLine($"[ResetTreis] Step 4 complete — wallet resynced for {walletAccounts.Count} accounts.");
                Console.WriteLine($"[ResetTreis] Full chain state rebuild COMPLETE. Processed {processedCount} blocks with {failCount} failures.");

                // ═══════════════════════════════════════════════════════════════
                // STEP 5: Write StateTreiStatus flag — this MUST be the last step.
                // If the node crashes before this point, the flag stays missing,
                // and the next startup will detect the dirty state and re-run ResetTreis.
                // ═══════════════════════════════════════════════════════════════
                if (failCount == 0)
                {
                    StateTreiStatusService.SetSynced(Globals.LastBlock.Height);
                    Console.WriteLine($"[ResetTreis] Step 5: StateTreiStatus set to SYNCED at height {Globals.LastBlock.Height}.");
                }
                else
                {
                    StateTreiStatusService.SetFailed($"ResetTreis completed with {failCount} block replay failures.");
                    Console.WriteLine($"[ResetTreis] Step 5: StateTreiStatus set to FAILED ({failCount} replay errors).");
                }

                return failCount == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ResetTreis] CRITICAL ERROR: {ex.Message}");
                Console.WriteLine($"[ResetTreis] Stack trace: {ex.StackTrace}");
                StateTreiStatusService.SetFailed($"ResetTreis exception: {ex.Message}");
                return false;
            }
            finally
            {
                _isResetTreisRunning = false;
                // Restore IsResyncing — allow block validation to resume now that state is rebuilt
                Globals.IsResyncing = wasResyncing;
                Globals.StopAllTimers = wasStopTimers;
                Console.WriteLine("[ResetTreis] IsResyncing restored — block validation can resume.");
            }
        }
    }
}
