using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Utilities
{
    public class BlockRollbackUtility
    {
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

                // Step 5: Update Globals.LastBlock to the new tip
                var newLastBlock = BlockchainData.GetLastBlock();
                if (newLastBlock != null)
                {
                    Globals.LastBlock = newLastBlock;
                }
                else
                {
                    LogUtility.Log($"[RollbackFast] WARNING: GetLastBlock returned null after rollback to height {targetHeight}.", "BlockRollback");
                    // Fall back to genesis state
                    Globals.LastBlock = new Block { Height = -1 };
                }

                // Step 6: Clear stale in-memory queues to prevent orphaned blocks from
                // passing validation via Block.GetPreviousHash() after rollback.
                // Without this, blocks referencing the rolled-back block's hash can still
                // be validated and committed, creating gaps in the chain.
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

                // Also clear BlockHashes for removed heights
                var hashKeysToRemove = Globals.BlockHashes.Keys.Where(k => k > targetHeight).ToList();
                foreach (var key in hashKeysToRemove)
                    Globals.BlockHashes.TryRemove(key, out _);

                LogUtility.Log(
                    $"[RollbackFast] Cleared in-memory queues above height {targetHeight}: " +
                    $"NetworkBlockQueue={removedQueueCount}, BlockQueueBroadcasted={broadcastKeysToRemove.Count}, " +
                    $"BackupProofs={backupKeysToRemove.Count}, BlockHashes={hashKeysToRemove.Count}",
                    "BlockRollback");

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

                default:
                    // All other types (NFT, ADNR, VOTE, RESERVE, smart contract TXs, etc.)
                    // have complex state side-effects that can't be safely reversed
                    return false;
            }
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
            try
            {
                Console.WriteLine("[ResetTreis] Starting full chain state rebuild...");

                // ═══════════════════════════════════════════════════════════════
                // STEP 1: Wipe all chain-derived state databases
                // ═══════════════════════════════════════════════════════════════
                Console.WriteLine("[ResetTreis] Step 1: Wiping chain-derived state databases...");

                // Core state treis
                var transactions = TransactionData.GetAll();
                var stateTrei = StateData.GetAccountStateTrei();
                var worldTrei = WorldTrei.GetWorldTrei();

                transactions.DeleteAllSafe();
                stateTrei.DeleteAllSafe();
                worldTrei.DeleteAllSafe();

                // Smart contract state — drop all collections in the SC state DB
                try
                {
                    if (DbContext.DB_SmartContractStateTrei != null)
                    {
                        foreach (var name in DbContext.DB_SmartContractStateTrei.GetCollectionNames().ToList())
                        {
                            DbContext.DB_SmartContractStateTrei.DropCollection(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe SmartContractStateTrei: {ex.Message}");
                }

                // DecShop state — drop all collections in the DecShop state DB
                try
                {
                    if (DbContext.DB_DecShopStateTrei != null)
                    {
                        foreach (var name in DbContext.DB_DecShopStateTrei.GetCollectionNames().ToList())
                        {
                            DbContext.DB_DecShopStateTrei.DropCollection(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe DecShopStateTrei: {ex.Message}");
                }

                // Topic trei
                try
                {
                    var topicTrei = TopicTrei.GetTopics();
                    if (topicTrei != null) topicTrei.DeleteAllSafe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe TopicTrei: {ex.Message}");
                }

                // Vote
                try
                {
                    var votes = Vote.GetVotes();
                    if (votes != null) votes.DeleteAllSafe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe Vote: {ex.Message}");
                }

                // DNR (Domain Name Records)
                try
                {
                    var dnr = Adnr.GetAdnr();
                    if (dnr != null) dnr.DeleteAllSafe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe DNR/ADNR: {ex.Message}");
                }

                // Reserve transactions
                try
                {
                    var reserveTxDb = ReserveTransactions.GetReserveTransactionsDb();
                    if (reserveTxDb != null) reserveTxDb.DeleteAllSafe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe ReserveTransactions: {ex.Message}");
                }

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

                // vBTC / Bitcoin state databases — wipe all collections
                try
                {
                    if (DbContext.DB_vBTC != null)
                    {
                        foreach (var name in DbContext.DB_vBTC.GetCollectionNames().ToList())
                        {
                            DbContext.DB_vBTC.DropCollection(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe DB_vBTC: {ex.Message}");
                }

                try
                {
                    if (DbContext.DB_TokenizedWithdrawals != null)
                    {
                        foreach (var name in DbContext.DB_TokenizedWithdrawals.GetCollectionNames().ToList())
                        {
                            DbContext.DB_TokenizedWithdrawals.DropCollection(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe DB_TokenizedWithdrawals: {ex.Message}");
                }

                try
                {
                    if (DbContext.DB_VBTCWithdrawalRequests != null)
                    {
                        foreach (var name in DbContext.DB_VBTCWithdrawalRequests.GetCollectionNames().ToList())
                        {
                            DbContext.DB_VBTCWithdrawalRequests.DropCollection(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe DB_VBTCWithdrawalRequests: {ex.Message}");
                }

                // Shares
                try
                {
                    if (DbContext.DB_Shares != null)
                    {
                        foreach (var name in DbContext.DB_Shares.GetCollectionNames().ToList())
                        {
                            DbContext.DB_Shares.DropCollection(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ResetTreis] Warning: Could not wipe DB_Shares: {ex.Message}");
                }

                // Checkpoint all wiped databases
                DbContext.DB.Checkpoint();
                DbContext.DB_AccountStateTrei.Checkpoint();
                DbContext.DB_WorldStateTrei.Checkpoint();
                try { DbContext.DB_SmartContractStateTrei.Checkpoint(); } catch { }
                try { DbContext.DB_DecShopStateTrei.Checkpoint(); } catch { }
                try { DbContext.DB_TopicTrei.Checkpoint(); } catch { }
                try { DbContext.DB_Vote.Checkpoint(); } catch { }
                try { DbContext.DB_DNR.Checkpoint(); } catch { }
                try { DbContext.DB_Reserve.Checkpoint(); } catch { }
                try { DbContext.DB_Mempool.Checkpoint(); } catch { }
                try { DbContext.DB_vBTC?.Checkpoint(); } catch { }
                try { DbContext.DB_TokenizedWithdrawals?.Checkpoint(); } catch { }
                try { DbContext.DB_VBTCWithdrawalRequests?.Checkpoint(); } catch { }
                try { DbContext.DB_Shares?.Checkpoint(); } catch { }

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
                Console.WriteLine("[ResetTreis] Step 3: Replaying blocks through UpdateTreis...");
                var allBlocks = BlockchainData.GetBlocks().FindAll().OrderBy(b => b.Height).ToList();
                int processedCount = 0;
                int failCount = 0;
                var failBlocks = new List<Block>();

                foreach (var block in allBlocks)
                {
                    try
                    {
                        // UpdateTreis is the comprehensive state commit path — it handles ALL TX types:
                        // TX, NFT, tokens, ADNR, votes, topics, dec shops, reserves, vBTC V2,
                        // privacy/shielded TXs, bridge locks/unlocks, etc.
                        await StateData.UpdateTreis(block);
                        processedCount++;

                        if (processedCount % 1000 == 0)
                            Console.WriteLine($"[ResetTreis] Processed {processedCount}/{allBlocks.Count} blocks...");
                    }
                    catch (Exception ex)
                    {
                        failBlocks.Add(block);
                        failCount++;
                        Console.WriteLine($"[ResetTreis] Error replaying block {block.Height}: {ex.Message}");
                    }
                }

                Console.WriteLine($"[ResetTreis] Step 3 complete — replayed {processedCount} blocks ({failCount} failures).");

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
                var txData = TransactionData.GetAll();
                foreach (var block in allBlocks)
                {
                    foreach (var tx in block.Transactions)
                    {
                        bool isRelevant = walletAddresses.Contains(tx.ToAddress) || walletAddresses.Contains(tx.FromAddress);
                        if (isRelevant)
                        {
                            // Check if already inserted (avoid duplicates)
                            var existing = txData.FindOne(x => x.Hash == tx.Hash);
                            if (existing == null)
                            {
                                txData.InsertSafe(tx);
                            }
                        }
                    }
                }

                // 4c: Clear mempool of any TXs that were included in blocks
                try
                {
                    var mempool = TransactionData.GetPool();
                    if (mempool != null && mempool.Count() > 0)
                    {
                        foreach (var block in allBlocks)
                        {
                            foreach (var tx in block.Transactions)
                            {
                                mempool.DeleteManySafe(x => x.Hash == tx.Hash);
                            }
                        }
                    }
                }
                catch { }

                Console.WriteLine($"[ResetTreis] Step 4 complete — wallet resynced for {walletAccounts.Count} accounts.");
                Console.WriteLine($"[ResetTreis] Full chain state rebuild COMPLETE. Processed {processedCount} blocks with {failCount} failures.");

                return failCount == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ResetTreis] CRITICAL ERROR: {ex.Message}");
                Console.WriteLine($"[ResetTreis] Stack trace: {ex.StackTrace}");
                return false;
            }
        }
    }
}
