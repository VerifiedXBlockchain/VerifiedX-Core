﻿using Newtonsoft.Json;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using ReserveBlockCore.P2P;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReserveBlockCore.Services;
using System.Numerics;
using ReserveBlockCore.EllipticCurve;
using System.Globalization;
using ReserveBlockCore.Extensions;
using System.Security.Principal;
using LiteDB;

namespace ReserveBlockCore.Data
{
    internal class BlockchainData
    {
        public IList<Transaction> PendingTransactions = new List<Transaction>();
        public Blockchain Chain { get; set; }
        public static string ChainRef { get; set; }

        public static int BlockVersion { get; set; }
        static SemaphoreSlim BlockAddLock = new SemaphoreSlim(1, 1);

        #region Initialize Chain
        internal static async Task InitializeChain()
        {
            var blocks = BlockData.GetBlocks();
            
            if (blocks.FindOne(x => true) == null)
            {
                var genesisTime = DateTime.UtcNow;

                DbContext.BeginTrans();
                TransactionData.CreateGenesisTransction();

                //Get all transaction in pool. This can be used to create multiple accounts to receive funds at start of chain
                var trxPool = TransactionData.GetPool();
                var transactions = trxPool.FindAll().ToList();

                var block = BlockData.CreateGenesisBlock(transactions);

                // add the genesis block to our new blockchain
                AddBlock(block);

                //Updates state trei
                StateData.CreateGenesisWorldTrei(block);

                // clear mempool
                trxPool.DeleteAllSafe();

                DbContext.Commit();
            }
        }

        #endregion

        #region Craft Block V2 and V3
        public static async Task<Block?> CraftNewBlock_New(string validator, int totalVals, string valAnswer)
        {
            try
            {
                Block block;
                int craftCount = 1;
                bool blockCrafted = false;
                do
                {
                    await BlockValidatorService.ValidationDelay();

                    var startCraftTimer = DateTime.UtcNow;
                    var validatorAccount = AccountData.GetSingleAccount(validator);

                    if (validatorAccount == null)
                    {
                        return null;
                    }

                    //Get tx's from Mempool                
                    var processedTxPool = await TransactionData.ProcessTxPool();
                    var txPool = TransactionData.GetPool();

                    var lastBlock = Globals.LastBlock;
                    var height = lastBlock.Height + 1;

                    //Need to get master node validator.
                    var timestamp = TimeUtil.GetTime();

                    if (timestamp <= Globals.LastBlock.Timestamp)
                        timestamp = Globals.LastBlock.Timestamp + 1;

                    var transactionList = new List<Transaction>();

                    var coinbase_tx2 = new Transaction
                    {
                        Amount = GetBlockReward(),
                        ToAddress = validator,
                        Fee = 0.00M,
                        Timestamp = timestamp,
                        FromAddress = "Coinbase_BlkRwd",
                        TransactionType = TransactionType.TX
                    };

                    if (processedTxPool.Count() > 0)
                    {
                        //commenting these out to test burning of fee.
                        //coinbase_tx.Amount = GetTotalFees(processedTxPool);
                        //coinbase_tx.Build();
                        coinbase_tx2.Build();

                        //transactionList.Add(coinbase_tx);
                        transactionList.Add(coinbase_tx2);

                        transactionList.AddRange(processedTxPool);

                        //need to only delete processed mempool tx's in event new ones get added while creating block.
                        //delete after block is added, so they can't  be re-added before block is over.
                        foreach (var tx in processedTxPool)
                        {
                            var txRec = txPool.FindOne(x => x.Hash == tx.Hash);
                            if (txRec != null)
                            {
                                //txPool.DeleteManySafe(x => x.Hash == tx.Hash);
                            }
                        }
                    }
                    else
                    {
                        coinbase_tx2.Build();
                        transactionList.Add(coinbase_tx2);
                    }

                    block = new Block
                    {
                        Height = height,
                        Timestamp = timestamp,
                        Transactions = GiveOtherInfos(transactionList, height),
                        Validator = validator,
                        ChainRefId = ChainRef,
                        TotalValidators = totalVals,
                        ValidatorAnswer = valAnswer
                    };
                    block.Build();

                    //Add validator signature
                    block.ValidatorSignature = SignatureService.ValidatorSignature(block.Hash);

                    //block size
                    var str = JsonConvert.SerializeObject(block);
                    block.Size = str.Length;

                    // get craft time    
                    var endTimer = DateTime.UtcNow;
                    var buildTime = endTimer - startCraftTimer;
                    block.BCraftTime = buildTime.Milliseconds;

                
                    blockCrafted = await BlockValidatorService.ValidateBlockForTask(block, true);
                    if (blockCrafted == true)
                    {
                        break;
                    }
                    else
                    {
                        craftCount += 1; //add count to attempts and retry.
                    }

                } while (craftCount != 5); // this will try up to 5 times to craft a block

                if (blockCrafted == true)
                {
                    return block;
                }


            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "BlockchainData.CraftNewBlock(string validator)");
            }
            // start craft time
            return null;
        }

        #endregion

        #region Craft Block V4
        public static async Task<Block?> CraftBlock_V4(string validator, int totalVals, string valAnswer, long height)
        {
            try
            {
                Block block;
                int craftCount = 1;
                bool blockCrafted = false;
                do
                {
                    await BlockValidatorService.ValidationDelay();

                    var startCraftTimer = DateTime.UtcNow;
                    var validatorAccount = AccountData.GetSingleAccount(validator);

                    if (validatorAccount == null)
                    {
                        return null;
                    }

                    //Get tx's from Mempool                
                    var processedTxPool = await TransactionData.ProcessTxPool();
                    var txPool = TransactionData.GetPool();

                    //Need to get master node validator.
                    var timestamp = TimeUtil.GetTime();

                    if (timestamp <= Globals.LastBlock.Timestamp)
                        timestamp = Globals.LastBlock.Timestamp + 1;

                    var transactionList = new List<Transaction>();

                    var coinbase_tx2 = new Transaction
                    {
                        Amount = GetBlockReward(),
                        ToAddress = validator,
                        Fee = 0.00M,
                        Timestamp = timestamp,
                        FromAddress = "Coinbase_BlkRwd",
                        TransactionType = TransactionType.TX
                    };

                    if (processedTxPool.Count() > 0)
                    {
                        //commenting these out to test burning of fee.
                        //coinbase_tx.Amount = GetTotalFees(processedTxPool);
                        //coinbase_tx.Build();
                        coinbase_tx2.Build();

                        //transactionList.Add(coinbase_tx);
                        transactionList.Add(coinbase_tx2);

                        transactionList.AddRange(processedTxPool);

                        //need to only delete processed mempool tx's in event new ones get added while creating block.
                        //delete after block is added, so they can't  be re-added before block is over.
                        foreach (var tx in processedTxPool)
                        {
                            var txRec = txPool.FindOne(x => x.Hash == tx.Hash);
                            if (txRec != null)
                            {
                                //txPool.DeleteManySafe(x => x.Hash == tx.Hash);
                            }
                        }
                    }
                    else
                    {
                        coinbase_tx2.Build();
                        transactionList.Add(coinbase_tx2);
                    }

                    block = new Block
                    {
                        Height = height,
                        Timestamp = timestamp,
                        Transactions = GiveOtherInfos(transactionList, height),
                        Validator = validator,
                        ChainRefId = ChainRef,
                        TotalValidators = totalVals,
                        ValidatorAnswer = valAnswer
                    };
                    block.Build();

                    if(block.PrevHash == "0")
                    {
                        if(Globals.OptionalLogging)
                            ErrorLogUtility.LogError("Block Previous Hash not available.", "BlockchainData.CraftBlock_V4()");

                        return null;
                    }    

                    //Add validator signature
                    block.ValidatorSignature = SignatureService.ValidatorSignature(block.Hash);

                    //block size
                    var str = JsonConvert.SerializeObject(block);
                    block.Size = str.Length;

                    // get craft time    
                    var endTimer = DateTime.UtcNow;
                    var buildTime = endTimer - startCraftTimer;
                    block.BCraftTime = buildTime.Milliseconds;


                    blockCrafted = await BlockValidatorService.ValidateBlock(block, true, false, true);
                    if (blockCrafted == true)
                    {
                        break;
                    }
                    else
                    {
                        craftCount += 1; //add count to attempts and retry.
                    }

                } while (craftCount != 5); // this will try up to 5 times to craft a block

                if (blockCrafted == true)
                {
                    return block;
                }


            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "BlockchainData.CraftBlock_V4()");
            }
            // start craft time
            return null;
        }
        #endregion

        #region Craft Block V5 - WIP
        public static async Task<Block?> CraftBlock_V5(string validator, int totalVals, string valAnswer, long height, bool skipTXs = false)
        {
            try
            {
                Block block;
                int craftCount = 1;
                bool blockCrafted = false;
                do
                {
                    await BlockValidatorService.ValidationDelay();

                    var startCraftTimer = DateTime.UtcNow;
                    var validatorAccount = AccountData.GetSingleAccount(validator);

                    if (validatorAccount == null)
                    {
                        return null;
                    }

                    if(height == Globals.SpecialBlockHeight)
                    {
                        var specialBlock = await CraftSpecialBlock(validator, totalVals, valAnswer, height, skipTXs);
                        if(specialBlock != null)
                            return specialBlock;
                    }

                    //Get tx's from Mempool                
                    var processedTxPool = await TransactionData.ProcessTxPool();
                    var txPool = TransactionData.GetPool();

                    //Need to get master node validator.
                    var timestamp = TimeUtil.GetTime();

                    if (timestamp <= Globals.LastBlock.Timestamp)
                        timestamp = Globals.LastBlock.Timestamp + 1;

                    var transactionList = new List<Transaction>();

                    var coinbase_tx2 = new Transaction
                    {
                        Amount = 0.00M,
                        ToAddress = validator,
                        Fee = 0.00M,
                        Timestamp = timestamp,
                        FromAddress = "Coinbase_BlkRwd",
                        TransactionType = TransactionType.TX
                    };

                    if (processedTxPool.Count() > 0)
                    {
                        //commenting these out to test burning of fee.
                        //coinbase_tx.Amount = GetTotalFees(processedTxPool);
                        //coinbase_tx.Build();
                        coinbase_tx2.Build();

                        //transactionList.Add(coinbase_tx);
                        transactionList.Add(coinbase_tx2);

                        //We will skip during proof generation
                        if(!skipTXs)
                            transactionList.AddRange(processedTxPool);
                    }
                    else
                    {
                        coinbase_tx2.Build();
                        transactionList.Add(coinbase_tx2);
                    }

                    block = new Block
                    {
                        Height = height,
                        Timestamp = timestamp,
                        Transactions = GiveOtherInfos(transactionList, height),
                        Validator = validator,
                        ChainRefId = ChainRef,
                        TotalValidators = totalVals,
                        ValidatorAnswer = valAnswer
                    };
                    block.Build();

                    if (block.PrevHash == "0")
                    {
                        if (Globals.OptionalLogging)
                            ErrorLogUtility.LogError("Block Previous Hash not available.", "BlockchainData.CraftBlock_V5()");

                        return null;
                    }

                    //Add validator signature
                    block.ValidatorSignature = SignatureService.ValidatorSignature(block.Hash);

                    //block size
                    var str = JsonConvert.SerializeObject(block);
                    block.Size = str.Length;

                    // get craft time    
                    var endTimer = DateTime.UtcNow;
                    var buildTime = endTimer - startCraftTimer;
                    block.BCraftTime = buildTime.Milliseconds;


                    blockCrafted = await BlockValidatorService.ValidateBlock(block, true, false, false, true);
                    if (blockCrafted == true)
                    {
                        break;
                    }
                    else
                    {
                        craftCount += 1; //add count to attempts and retry.
                    }

                } while (craftCount != 5);

                if (blockCrafted == true)
                {
                    return block;
                }
            }
            catch(Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "BlockchainData.CraftBlock_V5()");
            }

            return null;
        }

        private static async Task<Block?> CraftSpecialBlock(string validator, int totalVals, string valAnswer, long height, bool skipTXs = false)
        {
            Block block;
            int craftCount = 1;
            bool blockCrafted = false;
            try
            {
                do
                {
                    await BlockValidatorService.ValidationDelay();

                    var startCraftTimer = DateTime.UtcNow;
                    var validatorAccount = AccountData.GetSingleAccount(validator);

                    if (validatorAccount == null)
                    {
                        return null;
                    }

                    //Need to get master node validator.
                    var timestamp = TimeUtil.GetTime();

                    if (timestamp <= Globals.LastBlock.Timestamp)
                        timestamp = Globals.LastBlock.Timestamp + 1;

                    var postMineBalance = 200_000_000M - AccountStateTrei.GetNetworkTotal();

                    var transactionList = new List<Transaction>();

                    var coinbase_tx2 = new Transaction
                    {
                        Amount = 0.00M,
                        ToAddress = validator,
                        Fee = 0.00M,
                        Timestamp = timestamp,
                        FromAddress = "Coinbase_BlkRwd",
                        TransactionType = TransactionType.TX
                    };

                    var coinbase_special = new Transaction
                    {
                        Amount = postMineBalance,
                        ToAddress = Globals.GenesisValidator,
                        Fee = 0.00M,
                        Timestamp = timestamp,
                        FromAddress = "Coinbase_BlkRwd",
                        TransactionType = TransactionType.TX
                    };


                    coinbase_tx2.Build();
                    coinbase_special.Build();

                    transactionList.Add(coinbase_tx2);
                    transactionList.Add(coinbase_special);

                    block = new Block
                    {
                        Height = height,
                        Timestamp = timestamp,
                        Transactions = GiveOtherInfos(transactionList, height),
                        Validator = validator,
                        ChainRefId = ChainRef,
                        TotalValidators = totalVals,
                        ValidatorAnswer = valAnswer
                    };
                    block.Build();

                    if (block.PrevHash == "0")
                    {
                        if (Globals.OptionalLogging)
                            ErrorLogUtility.LogError("Block Previous Hash not available.", "BlockchainData.CraftBlock_V5()");

                        return null;
                    }

                    //Add validator signature
                    block.ValidatorSignature = SignatureService.ValidatorSignature(block.Hash);

                    //block size
                    var str = JsonConvert.SerializeObject(block);
                    block.Size = str.Length;

                    // get craft time    
                    var endTimer = DateTime.UtcNow;
                    var buildTime = endTimer - startCraftTimer;
                    block.BCraftTime = buildTime.Milliseconds;


                    blockCrafted = await BlockValidatorService.ValidateBlock(block, true, false, false, true);
                    if (blockCrafted == true)
                    {
                        break;
                    }
                    else
                    {
                        craftCount += 1; //add count to attempts and retry.
                    }

                } while (craftCount != 5);

                if (blockCrafted == true)
                {
                    return block;
                }
            }
            catch (Exception ex)
            {
            }

            return null;
        }

        #endregion

        public static decimal GetBlockReward()
        {
            var BlockReward = HalvingUtility.GetBlockReward();
            return BlockReward;
        }
        public static List<Transaction> GiveOtherInfos(List<Transaction> trxs, long height)
        {
            foreach (var trx in trxs)
            {
                trx.Height = height;
            }
            return trxs;
        }

        public static LiteDB.ILiteCollection<Block> GetBlocks()
        {
            try
            {
                var blocks = DbContext.DB.GetCollection<Block>(DbContext.RSRV_BLOCKS);
                return blocks;
            }
            catch(Exception ex)
            {                
                ErrorLogUtility.LogError(ex.ToString(), "BlockchainData.GetBlocks()");
                return null;
            }
            
        }
        public static Block GetGenesisBlock()
        {
            var block = GetBlocks().FindOne(x => true);
            return block;
        }
        public static Block? GetBlockByHeight(long height)
        {
            var blocks = GetBlocks();           
            var block = blocks.Query().Where(x => x.Height == height).FirstOrDefault();
            return block;
        }


        public static Block? GetBlockByHash(string hash)
        {
            var blocks = GetBlocks();
            var block = blocks.Query().Where(x => x.Hash == hash).FirstOrDefault();
            return block;
        }
        public static Block GetLastBlock()
        {
            var blockchain = GetBlocks();
            var block = blockchain.FindOne(LiteDB.Query.All(LiteDB.Query.Descending));
            return block;
        }
        public static long GetHeight()
        {
            var lastBlock = GetLastBlock();
            if(lastBlock != null)
            {
                return lastBlock.Height;
            }
            return -1;
            
        }
        public static bool ValidateSpecialBlock(Block block)
        {
            var txList = block.Transactions.ToList();

            var blkRwdCnt = txList.Where(x => x.FromAddress == "Coinbase_BlkRwd").Count();
            var feeRemovalCheck = txList.Exists(x => x.FromAddress == "Coinbase_TrxFees");

            if (feeRemovalCheck)
                return false;
            

            if (blkRwdCnt > 2)
                return false;
            

            var valRewardCheck = txList.Where(x => x.FromAddress == "Coinbase_BlkRwd" && x.ToAddress == block.Validator).FirstOrDefault();
            if(valRewardCheck?.Amount > 0.00M)
                return false;
            

            var specialRewardCheck = txList.Where(x => x.FromAddress == "Coinbase_BlkRwd" && x.ToAddress == Globals.GenesisValidator).FirstOrDefault();
            if(specialRewardCheck == null) 
                return false;

            var networkBalance = AccountStateTrei.GetNetworkTotal();

            //Leaving room for marginal error. Only 1k tho.
            if (networkBalance + specialRewardCheck.Amount > 200_001_000)
                return false;

            return true;
        }
        public static bool ValidateBlock(Block block)
        {
            if (block.Height == Globals.SpecialBlockHeight)
                return true;

            bool result = false;

            var txList = block.Transactions.ToList();

            var blkRwdCnt = txList.Where(x => x.FromAddress == "Coinbase_BlkRwd").Count();
            var feeRemovalCheck = txList.Exists(x => x.FromAddress == "Coinbase_TrxFees");

            if(feeRemovalCheck)
            {
                return result;
            }

            if(blkRwdCnt > 1)
            {
                return result;
            }

            foreach(var tx in txList)
            {
                if (tx.FromAddress == "Coinbase_TrxFees")
                {
                    //validating fees to ensure block is not malformed.
                    result = tx.Amount == txList.Sum(y => y.Fee) ? true : false;
                    if (result == false)
                    {
                        break;
                    }
                }
                if (tx.FromAddress == "Coinbase_BlkRwd")
                {
                    //validating block reward to ensure block is not malformed.
                    result = block.Version < 4 ? tx.Amount == GetBlockReward() ? true : false : tx.Amount == 0.0M ? true : false;
                    //ensures the reward person is the validator themselves.
                    result = result != true ? false : tx.ToAddress == block.Validator ? true : false;

                    if (result == false)
                    {
                        break;
                    }
                }
            }
            
            return result;
        }
        public static async Task AddBlock(Block block, bool notifyCLI = false)
        {
            while(Globals.TreisUpdating)
            {
                await Task.Delay(200);
                //prevents new block from being added while treis are updating
            }

            await BlockAddLock.WaitAsync();

            try
            {
                var blocks = GetBlocks();
                //only input block if null
                var blockCheck = blocks.Find(Query.All(Query.Descending)).Take(100).Where(x => x.Height == block.Height).FirstOrDefault();
                if (blockCheck == null)
                {
                    //Update in memory block.
                    Globals.LastBlock = block;
                    var currentTime = TimeUtil.GetTime();
                    Globals.BlockTimeDiff = currentTime - Globals.LastBlockAddedTimestamp;
                    Globals.LastBlockAddedTimestamp = currentTime;

                    Blockchain.AddBlockHeader(block);
                    if (Globals.ValidatorAddress == block.Validator)
                        Globals.LastWonBlock = block;

                    _ = BlockDiffService.UpdateQueue(Globals.BlockTimeDiff);
                    _ = ValidatorService.UpdateActiveValidators(block);


                    //insert block to db
                    blocks.InsertSafe(block);

                    if (notifyCLI)
                    {
                        if (!Globals.BasicCLI)
                        {
                            ConsoleWriterService.OutputSameLineMarked(($"Time: [yellow]{DateTime.Now}[/] | Block [green]({block.Height})[/] added from: [purple]{block.Validator}[/] | Delay: [aqua]{Globals.BlockTimeDiff}[/]/s"));
                        }
                        else
                        {
                            ConsoleWriterService.OutputSameLineMarked($"Time: [yellow]{DateTime.Now}[/] | Block [green]({block.Height})[/]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error Adding Block to chain. Error: {ex}", "BlockchainData.AddBlock()");
            }
            finally
            {
                BlockAddLock.Release();
            }
        }
        private static decimal GetTotalFees(List<Transaction> txs)
        {
            var totFee = txs.AsEnumerable().Sum(x => x.Fee);
            return totFee;
        }

        public static IEnumerable<Block> GetBlocksByValidator(string address)
        {

            var blocks = DbContext.DB.GetCollection<Block>(DbContext.RSRV_BLOCKS);            
            var query = blocks.Query()
                .OrderByDescending(x => x.Height)
                .Where(x => x.Validator == address)
                .Limit(20).ToEnumerable();
            return query;
        }

            public static void PrintBlock(Block block)
        {
            Console.WriteLine("\n===========\nBlock Info:");
            Console.WriteLine(" * Chain Reference.: {0}", ChainRef);
            Console.WriteLine(" * Block Height....: {0}", block.Height);
            Console.WriteLine(" * Version         : {0}", block.Version);
            Console.WriteLine(" * Previous Hash...: {0}", block.PrevHash);
            Console.WriteLine(" * Hash            : {0}", block.Hash);
            Console.WriteLine(" * Merkle Hash.....: {0}", block.MerkleRoot);
            Console.WriteLine(" * State Hash......: {0}",  block.StateRoot);
            Console.WriteLine(" * Timestamp       : {0}", TimeUtil.ToDateTime(block.Timestamp));
            Console.WriteLine(" * Chain Validator : {0}", block.Validator);
            Console.WriteLine(" * Validator Ans.  : {0}", block.ValidatorAnswer);

            Console.WriteLine(" * Number Of Tx(s) : {0}", block.NumOfTx);
            Console.WriteLine(" * Amount...........: {0}", block.TotalAmount);
            Console.WriteLine(" * Reward          : {0}", block.TotalReward);
            Console.WriteLine(" * Size............: {0}", block.Size);
            Console.WriteLine(" * Craft Time      : {0}", block.BCraftTime);

            Console.WriteLine($"");
            Console.WriteLine("\n===========\nBlock Metrics:");
            var currentTime = TimeUtil.GetTime();
            var currentDiff = (currentTime - Globals.LastBlockAddedTimestamp).ToString();
            Console.WriteLine($"Block Diff Avg: {BlockDiffService.CalculateAverage().ToString("#.##")} secs. Avg of: {Globals.BlockDiffQueue.Count()}/3456 Blocks.");
            Console.WriteLine($"Block Last Received: {Globals.LastBlockAddedTimestamp.ToLocalDateTimeFromUnix()}");
            Console.WriteLine($"Block Last Delay: {Globals.BlockTimeDiff}");
            Console.WriteLine($"Current block delay: {currentDiff}");
            

        }
    }
}
