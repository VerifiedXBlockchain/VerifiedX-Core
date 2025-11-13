using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Nodes
{
    public class NodeDataProcessor
    {
        // Maximum allowed payload sizes to prevent memory exhaustion attacks
        private const int MAX_BLOCK_JSON_SIZE = 1179648;  // ~1.2 MB (matches P2PClient check)
        private const int MAX_TRANSACTION_JSON_SIZE = 524288;  // 512 KB for transaction data

        public static async Task ProcessData(string message, string data, string ipAddress, CancellationToken cancellationToken = default)
        {
            if (message == null || message == "")
            {
                return;
            }
            else
            {
                if (message == "blk")
                {
                    // Validate payload size before deserialization to prevent memory exhaustion
                    if (string.IsNullOrEmpty(data) || data.Length > MAX_BLOCK_JSON_SIZE)
                    {
                        ErrorLogUtility.LogError($"Oversized or invalid block payload from {ipAddress}. Size: {data?.Length ?? 0} bytes", "NodeDataProcessor.ProcessData");
                        return;
                    }

                    var nextBlock = JsonConvert.DeserializeObject<Block>(data);

                    if(nextBlock != null)
                    {
                        if(nextBlock.ChainRefId != BlockchainData.ChainRef)
                        {                                    
                            var nextHeight = Globals.LastBlock.Height + 1;
                            var currentHeight = nextBlock.Height;

                            if (currentHeight < nextHeight)
                            {                                        
                                await BlockValidatorService.ValidationDelay();
                                var checkBlock = BlockchainData.GetBlockByHeight(currentHeight);

                                if (checkBlock != null)
                                {
                                    var localHash = checkBlock.Hash;
                                    var remoteHash = nextBlock.Hash;

                                    if (localHash != remoteHash)
                                    {
                                        Console.WriteLine("Possible block differ");
                                    }
                                }
                            }
                            else
                            {
                                // HAL-072 Fix: Use AddOrUpdate to properly handle competing blocks list
                                BlockDownloadService.BlockDict.AddOrUpdate(
                                    currentHeight,
                                    new List<(Block, string)> { (nextBlock, ipAddress) },
                                    (key, existingList) =>
                                    {
                                        existingList.Add((nextBlock, ipAddress));
                                        return existingList;
                                    });
                                
                                if (nextHeight == currentHeight)
                                    await BlockValidatorService.ValidateBlocks();
                                if (nextHeight < currentHeight)                                            
                                    await BlockDownloadService.GetAllBlocks();
                            }
                        }
                                
                    }
                }

                if(message == "7777")
                {
                    // Validate payload size before deserialization to prevent memory exhaustion
                    if (string.IsNullOrEmpty(data) || data.Length > MAX_TRANSACTION_JSON_SIZE)
                    {
                        ErrorLogUtility.LogError($"Oversized or invalid transaction payload from {ipAddress}. Size: {data?.Length ?? 0} bytes", "NodeDataProcessor.ProcessData");
                        return;
                    }

                    var transaction = JsonConvert.DeserializeObject<Transaction>(data);
                    if (transaction != null)
                    {
                        var ablList = Globals.ABL.ToList();
                        if (ablList.Exists(x => x == transaction.FromAddress))
                        {
                            return;
                        }

                        var isTxStale = await TransactionData.IsTxTimestampStale(transaction);
                        if (!isTxStale)
                        {
                            var mempool = TransactionData.GetPool();

                            if (mempool.Count() != 0)
                            {
                                var txFound = mempool.FindOne(x => x.Hash == transaction.Hash);
                                if (txFound == null)
                                {
                                    var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER ? true : false;
                                    var txResult = !twSkipVerify ? await TransactionValidatorService.VerifyTX(transaction) : await TransactionValidatorService.VerifyTX(transaction, false, false, true);
                                    if (txResult.Item1 == true)
                                    {
                                        var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                                        var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                        var rating = await TransactionRatingService.GetTransactionRating(transaction);
                                        transaction.TransactionRating = rating;

                                        if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                        {
                                            mempool.InsertSafe(transaction);
                                        }
                                    }

                                }
                                else
                                {
                                    //TODO Add this to also check in-mem blocks
                                    var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                    if (isCraftedIntoBlock)
                                    {
                                        try
                                        {
                                            mempool.DeleteManySafe(x => x.Hash == transaction.Hash);// tx has been crafted into block. Remove.
                                        }
                                        catch (Exception ex)
                                        {
                                            //delete failed
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER ? true : false;
                                var txResult = !twSkipVerify ? await TransactionValidatorService.VerifyTX(transaction) : await TransactionValidatorService.VerifyTX(transaction, false, false, true);
                                if (txResult.Item1 == true)
                                {
                                    var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                                    var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                    var rating = await TransactionRatingService.GetTransactionRating(transaction);
                                    transaction.TransactionRating = rating;

                                    if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                    {
                                        mempool.InsertSafe(transaction);
                                    }
                                }
                            }
                        }

                    }
                }
            }            
        }
    }
}
