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

        public static async Task<bool> ResetTreis()
        {
            var blockChain = BlockchainData.GetBlocks().FindAll();
            var failCount = 0;
            List<Block> failBlocks = new List<Block>();

            var transactions = TransactionData.GetAll();
            var stateTrei = StateData.GetAccountStateTrei();
            var worldTrei = WorldTrei.GetWorldTrei();

            transactions.DeleteAllSafe();//delete all local transactions
            stateTrei.DeleteAllSafe(); //removes all state trei data
            worldTrei.DeleteAllSafe();  //removes the state trei

            DbContext.DB.Checkpoint();
            DbContext.DB_AccountStateTrei.Checkpoint();
            DbContext.DB_WorldStateTrei.Checkpoint();

            var accounts = AccountData.GetAccounts();
            var accountList = accounts.FindAll().ToList();
            if (accountList.Count() > 0)
            {
                foreach (var account in accountList)
                {
                    account.Balance = 0M;
                    accounts.UpdateSafe(account);//updating local record with synced state trei
                }
            }

            foreach (var block in blockChain)
            {
                var result = await BlockchainRescanUtility.ValidateBlock(block, true);
                if (result != false)
                {
                    await StateData.UpdateTreis(block);

                    foreach (Transaction transaction in block.Transactions)
                    {
                        var mempool = TransactionData.GetPool();

                        var mempoolTx = mempool.FindAll().Where(x => x.Hash == transaction.Hash).FirstOrDefault();
                        if (mempoolTx != null)
                        {
                            mempool.DeleteManySafe(x => x.Hash == transaction.Hash);
                            TransactionData.ReleasePrivateMempoolNullifiersForTx(transaction.Hash);
                        }

                        var account = AccountData.GetAccounts().FindAll().Where(x => x.Address == transaction.ToAddress).FirstOrDefault();
                        if (account != null)
                        {
                            AccountData.UpdateLocalBalanceAdd(transaction.ToAddress, transaction.Amount);
                            var txdata = TransactionData.GetAll();
                            txdata.InsertSafe(transaction);
                        }

                        //Adds sent TX to wallet
                        var fromAccount = AccountData.GetAccounts().FindOne(x => x.Address == transaction.FromAddress);
                        if (fromAccount != null)
                        {
                            var txData = TransactionData.GetAll();
                            var fromTx = transaction;
                            fromTx.Amount = transaction.Amount * -1M;
                            fromTx.Fee = transaction.Fee * -1M;
                            txData.InsertSafe(fromTx);
                            await AccountData.UpdateLocalBalance(fromAccount.Address, (transaction.Amount + transaction.Fee));
                        }
                    }
                }
                else
                {
                    //issue with chain and must redownload
                    failBlocks.Add(block);
                    failCount++;
                }
            }

            if (failCount == 0)
            {
                return true;
            }
            else
            {
                //chain is invalid. Delete and redownload
                return false;
            }
        }
    }
}
