using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Nodes
{
    public class NodeDataProcessor
    {
        public static async Task ProcessData(string message, string data, string ipAddress)
        {
            if (message == null || message == "")
            {
                return;
            }
            else
            {
                if (message == "blk")
                {
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
                                if (!BlockDownloadService.BlockDict.ContainsKey(currentHeight))
                                {
                                    BlockDownloadService.BlockDict[currentHeight] = (nextBlock, ipAddress);
                                    if (nextHeight == currentHeight)
                                        await BlockValidatorService.ValidateBlocks();
                                    if (nextHeight < currentHeight)                                            
                                        await BlockDownloadService.GetAllBlocks();                                                                                      
                                }
  
                            }
                        }
                                
                    }
                }

                if(message == "7777")
                {
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
