﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReserveBlockCore.Utilities;
using ReserveBlockCore.Models;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Services;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Xml.Linq;

namespace ReserveBlockCore.Data
{
    internal class TransactionData
    {
        public static bool GenesisTransactionsCreated = false;
        public static void CreateGenesisTransction()
        {
            if (GenesisTransactionsCreated != true)
            {
                var trxPool = TransactionData.GetPool();
                trxPool.DeleteAllSafe();
                var timeStamp = TimeUtil.GetTime();

                var balanceSheet = GenesisBalanceUtility.GenesisBalances();
                foreach(var item in balanceSheet)
                {
                    var addr = item.Key;
                    var balance = item.Value;
                    var gTrx = new Transaction
                    {
                        Amount = balance,
                        Height = 0,
                        FromAddress = "rbx_genesis_transaction",
                        ToAddress = addr,
                        Fee = 0,
                        Hash = "", //this will be built down below. showing just to make this clear.
                        Timestamp = timeStamp,
                        Signature = "COINBASE_TX",
                        TransactionType = TransactionType.TX,
                        Nonce = 0
                    };

                    gTrx.Build();

                    AddToPool(gTrx);

                }

            }

        }
        public static void AddTxToWallet(Transaction transaction, bool subtract = false)
        {
            var txs = GetAll();
            var txCheck = txs.FindOne(x => x.Hash == transaction.Hash);
            if(txCheck== null)
            {
                Transaction tx = new Transaction { 
                    Height = transaction.Height,
                    Hash = transaction.Hash,
                    Amount = transaction.Amount,
                    FromAddress = transaction.FromAddress,
                    ToAddress = transaction.ToAddress,
                    Fee = transaction.Fee,
                    Data = transaction.Data,
                    Nonce = transaction.Nonce,
                    Signature = transaction.Signature,
                    Timestamp = transaction.Timestamp,
                    TransactionRating = transaction.TransactionRating,
                    TransactionStatus = transaction.TransactionStatus,
                    TransactionType = transaction.TransactionType,
                    UnlockTime = transaction.UnlockTime
                };
                if (subtract)
                {
                    tx.Amount = (tx.Amount * -1M);
                    tx.Fee = (tx.Fee * -1M);
                }
                    
                txs.InsertSafe(tx);
            }
        }

        public static void UpdateTxStatusAndHeightXXXX(Transaction transaction, TransactionStatus txStatus, long blockHeight, bool sameWalletTX = false, bool isReserveSend = false)
        {
            var txs = GetAll();
            var txCheck = txs.FindOne(x => x.Hash == transaction.Hash);
            if(!sameWalletTX)
            {
                if (txCheck == null)
                {
                    //posible sub needed
                    transaction.Id = new LiteDB.ObjectId();
                    transaction.TransactionStatus = txStatus;
                    transaction.Height = blockHeight;
                    txs.InsertSafe(transaction);
                    var account = AccountData.GetSingleAccount(transaction.FromAddress);
                    if (account != null)
                    {
                        var accountDb = AccountData.GetAccounts();
                        var stateTrei = StateData.GetSpecificAccountStateTrei(account.Address);
                        if (stateTrei != null)
                        {
                            account.Balance = stateTrei.Balance;
                            accountDb.UpdateSafe(account);
                        }
                    }
                }
                else
                {
                    txCheck.TransactionStatus = txStatus;
                    txCheck.Height = blockHeight;
                    txs.UpdateSafe(txCheck);
                }
            }
            else
            {
                if(txCheck != null)
                {
                    if(txCheck.Amount < 0)
                    {
                        transaction.Id = new LiteDB.ObjectId();
                        transaction.TransactionStatus = txStatus;
                        transaction.Height = blockHeight;
                        transaction.Amount = transaction.Amount < 0 ? transaction.Amount * -1.0M : transaction.Amount;
                        transaction.Fee = transaction.Fee < 0 ? transaction.Fee * -1.0M : transaction.Fee;
                        txs.InsertSafe(transaction);

                        var account = AccountData.GetSingleAccount(transaction.FromAddress);
                        if (account != null)
                        {
                            var accountDb = AccountData.GetAccounts();
                            var stateTrei = StateData.GetSpecificAccountStateTrei(account.Address);
                            if (stateTrei != null)
                            {
                                account.Balance = stateTrei.Balance;
                                accountDb.UpdateSafe(account);
                            }
                        }
                    }
                }
            }
            
        }

        public static void UpdateTxStatusAndHeight(Transaction transaction, TransactionStatus txStatus, long blockHeight, bool sameWalletTX = false)
        {
            var txs = GetAll();
            var txCheck = txs.FindOne(x => x.Hash == transaction.Hash);
            if (!sameWalletTX)
            {
                if (txCheck == null)
                {
                    //posible sub needed
                    transaction.Id = new LiteDB.ObjectId();
                    transaction.TransactionStatus = txStatus;
                    transaction.Height = blockHeight;
                    txs.InsertSafe(transaction);
                    var account = AccountData.GetSingleAccount(transaction.FromAddress);
                    var rAccount = ReserveAccount.GetReserveAccountSingle(transaction.FromAddress);
                    if (account != null)
                    {
                        var accountDb = AccountData.GetAccounts();
                        var stateTrei = StateData.GetSpecificAccountStateTrei(account.Address);
                        if (stateTrei != null)
                        {
                            account.Balance = stateTrei.Balance;
                            accountDb.UpdateSafe(account);
                        }
                    }
                    if(rAccount != null)
                    {
                        var stateTrei = StateData.GetSpecificAccountStateTrei(rAccount.Address);
                        if (stateTrei != null)
                        {
                            rAccount.AvailableBalance = stateTrei.Balance;
                            rAccount.LockedBalance = stateTrei.LockedBalance;
                            ReserveAccount.SaveReserveAccount(rAccount);
                        }
                    }
                }
                else
                {
                    txCheck.TransactionStatus = txStatus;
                    txCheck.Height = blockHeight;
                    txs.UpdateSafe(txCheck);
                }
            }
            else
            {
                if (txCheck != null)
                {
                    if (txCheck.Amount < 0)
                    {
                        transaction.Id = new LiteDB.ObjectId();
                        transaction.TransactionStatus = txStatus;
                        transaction.Height = blockHeight;
                        transaction.Amount = transaction.Amount < 0 ? transaction.Amount * -1.0M : transaction.Amount;
                        transaction.Fee = transaction.Fee < 0 ? transaction.Fee * -1.0M : transaction.Fee;
                        txs.InsertSafe(transaction);

                        var account = AccountData.GetSingleAccount(transaction.FromAddress);
                        var rAccount = ReserveAccount.GetReserveAccountSingle(transaction.FromAddress);

                        if (account != null)
                        {
                            var accountDb = AccountData.GetAccounts();
                            var stateTrei = StateData.GetSpecificAccountStateTrei(account.Address);
                            if (stateTrei != null)
                            {
                                account.Balance = stateTrei.Balance;
                                accountDb.UpdateSafe(account);
                            }
                        }

                        if (rAccount != null)
                        {
                            var stateTrei = StateData.GetSpecificAccountStateTrei(rAccount.Address);
                            if (stateTrei != null)
                            {
                                rAccount.AvailableBalance = stateTrei.Balance;
                                rAccount.LockedBalance = stateTrei.LockedBalance;
                                ReserveAccount.SaveReserveAccount(rAccount);
                            }
                        }
                    }
                }
            }

        }

        public static async Task UpdateWalletTXTask()
        {
            var txs = GetAll();
            var txList = txs.Find(x => x.TransactionStatus == TransactionStatus.Pending).ToList();
            foreach(var tx in txList)
            {
                try
                {
                    var isTXCrafted = await HasTxBeenCraftedIntoBlock(tx);
                    if (isTXCrafted)
                    {
                        tx.TransactionStatus = TransactionStatus.Success;
                        txs.UpdateSafe(tx);
                    }
                    else
                    {
                        var isStale = await IsTxTimestampStale(tx);
                        if (isStale)
                        {
                            tx.TransactionStatus = TransactionStatus.Failed;
                            txs.UpdateSafe(tx);
                            var account = AccountData.GetSingleAccount(tx.FromAddress);
                            if (account != null)
                            {
                                var accountDb = AccountData.GetAccounts();
                                var stateTrei = StateData.GetSpecificAccountStateTrei(account.Address);
                                if (stateTrei != null)
                                {
                                    account.Balance = stateTrei.Balance;
                                    accountDb.UpdateSafe(account);
                                }
                            }
                        }

                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "TransactionData.UpdateWalletTXTask()");
                }
            }
        }

        public static async Task<bool> HasTxBeenCraftedIntoBlock(Transaction tx)
        {
            if (Globals.MemBlocks.Any())
            {
                var txExist = Globals.MemBlocks.ContainsKey(tx.Hash);
                if (txExist == true)
                {
                    return true;
                }
            }
            if(!string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                if (Globals.NetworkBlockQueue.Any())
                {
                    foreach (var block in Globals.NetworkBlockQueue)
                    {
                        var txExist = block.Value.Transactions.Where(x => x.Hash == tx.Hash).FirstOrDefault();
                        if (txExist != null)
                            return true;
                    }
                }
            }

            return false;
        }

        public static async Task<bool> IsTxTimestampStale(Transaction tx)
        {
            var result = false;

            var currentTime = TimeUtil.GetTime();
            var timeDiff = currentTime - tx.Timestamp;
            var minuteDiff = timeDiff / 60M;

            if (minuteDiff > 60.0M)
            {
                result = true;
            }

            return result;
        }

        public static void AddToPool(Transaction transaction)
        {
            var TransactionPool = GetPool();
            TransactionPool.InsertSafe(transaction);
        }

        public static LiteDB.ILiteCollection<Transaction> GetPool()
        {
            try
            {
                var collection = DbContext.DB_Mempool.GetCollection<Transaction>(DbContext.RSRV_TRANSACTION_POOL);
                return collection;
            }
            catch(Exception ex)
            {
                DbContext.Rollback("TransactionData.GetPool()");
                return null;
            }
            
        }

        public static async Task ClearMempool()
        {
            var pool = GetPool();

            pool.DeleteAllSafe();
        }
        public static void PrintMemPool()
        {
            var pool = GetPool();
            if(pool.Count() != 0)
            {
                var txs = pool.FindAll().ToList();
                foreach(var tx in txs)
                {
                    var rating = tx.TransactionRating != null ? tx.TransactionRating.ToString() : "NA";
                    var txString = "From: " + tx.FromAddress + " | To: " + tx.ToAddress + " | Amount: " + tx.Amount.ToString() + " | Fee: " + tx.Fee.ToString()
                        + " | TX ID: " + tx.Hash + " | Timestamp: " + tx.Timestamp.ToString() + " | Rating: " + rating;
                    Console.WriteLine(txString);
                }
            }
            else
            {
                Console.WriteLine("No Transactions in your mempool");
            }
        }
        public static List<Transaction>? GetMempool()
        {
            var pool = GetPool();
            if (pool != null)
            {
                var txs = pool.FindAll().ToList();
                if(txs.Count() != 0)
                {
                    return txs;
                }
            }
            else
            {
                return null;
            }

            return null;
        }

        public static async Task<List<Transaction>> ProcessTxPool()
        {
            var collection = DbContext.DB_Mempool.GetCollection<Transaction>(DbContext.RSRV_TRANSACTION_POOL);

            var memPoolTxList = collection.FindAll().ToList();
            //Size the pool to 1mb
            var sizedMempoolList = MempoolSizeUtility.SizeMempoolDown(memPoolTxList);

            var approvedMemPoolList = new List<Transaction>();
            var queuedMempoolTxList = new List<Transaction>();

            queuedMempoolTxList = Globals.NetworkBlockQueue.Values.SelectMany(x => x.Transactions).ToList();

            var adnrNameList = new List<string>();

            if(sizedMempoolList.Count() > 0)
            {
                sizedMempoolList.ForEach(async tx =>
                {
                    try
                    {
                        var txExist = approvedMemPoolList.Exists(x => x.Hash == tx.Hash);
                        var queuedTxExist = queuedMempoolTxList.Exists(x => x.Hash == tx.Hash);

                        if (!txExist && !queuedTxExist)
                        {
                            var reject = false;

                            var fromAddress = tx.FromAddress;
                            if (Globals.ABL.Exists(x => x == fromAddress))
                                reject = true;

                            if (tx.TransactionType != TransactionType.TX &&
                                tx.TransactionType != TransactionType.ADNR &&
                                tx.TransactionType != TransactionType.VOTE_TOPIC &&
                                tx.TransactionType != TransactionType.VOTE && 
                                tx.TransactionType != TransactionType.DSTR &&
                                tx.TransactionType != TransactionType.RESERVE &&
                                tx.TransactionType != TransactionType.NFT_SALE)
                            {
                                var scInfo = TransactionUtility.GetSCTXFunctionAndUID(tx);
                                if (!scInfo.Item1)
                                    reject = true;

                                string scUID = scInfo.Item3;
                                string function = scInfo.Item4;
                                JArray? scDataArray = scInfo.Item5;
                                bool skip = scInfo.Item2;

                                if (scDataArray != null && skip)
                                {
                                    
                                    if (!string.IsNullOrWhiteSpace(function))
                                    {
                                        switch(function)
                                        {
                                            case "Transfer()":
                                                {
                                                    var otherTxs = approvedMemPoolList.Where(x => x.FromAddress == tx.FromAddress && x.Hash != tx.Hash).ToList();
                                                    if (otherTxs.Count() > 0)
                                                    {
                                                        foreach (var otx in otherTxs)
                                                        {
                                                            if (otx.TransactionType == TransactionType.NFT_TX ||
                                                            otx.TransactionType == TransactionType.NFT_BURN ||
                                                            otx.TransactionType == TransactionType.NFT_MINT)
                                                            {
                                                                if (otx.Data != null)
                                                                {
                                                                    var memscInfo = TransactionUtility.GetSCTXFunctionAndUID(tx);
                                                                    if (memscInfo.Item2)
                                                                    {
                                                                        var ottxDataArray = JsonConvert.DeserializeObject<JArray>(otx.Data);
                                                                        if (ottxDataArray != null)
                                                                        {
                                                                            var ottxData = ottxDataArray[0];

                                                                            var ottxFunction = (string?)ottxData["Function"];
                                                                            var ottxscUID = (string?)ottxData["ContractUID"];
                                                                            if (!string.IsNullOrWhiteSpace(ottxFunction))
                                                                            {
                                                                                if (ottxscUID == scUID)
                                                                                {
                                                                                    //FAIL
                                                                                    reject = true; break;
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                                break;
                                            case "Burn()":
                                                {
                                                    var otherTxs = approvedMemPoolList.Where(x => x.FromAddress == tx.FromAddress && x.Hash != tx.Hash).ToList();
                                                    if (otherTxs.Count() > 0)
                                                    {
                                                        foreach (var otx in otherTxs)
                                                        {
                                                            if (otx.TransactionType == TransactionType.NFT_TX ||
                                                            otx.TransactionType == TransactionType.NFT_BURN ||
                                                            otx.TransactionType == TransactionType.NFT_MINT)
                                                            {
                                                                if (otx.Data != null)
                                                                {
                                                                    var memscInfo = TransactionUtility.GetSCTXFunctionAndUID(tx);
                                                                    if (memscInfo.Item2)
                                                                    {
                                                                        var ottxDataArray = JsonConvert.DeserializeObject<JArray>(otx.Data);
                                                                        if (ottxDataArray != null)
                                                                        {
                                                                            var ottxData = ottxDataArray[0];

                                                                            var ottxFunction = (string?)ottxData["Function"];
                                                                            var ottxscUID = (string?)ottxData["ContractUID"];
                                                                            if (!string.IsNullOrWhiteSpace(ottxFunction))
                                                                            {
                                                                                if (ottxscUID == scUID)
                                                                                {
                                                                                    //FAIL
                                                                                    reject = true; break;
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                                break;
                                            case "TokenTransfer()":
                                                {
                                                    var otherTxs = approvedMemPoolList.Where(x => x.FromAddress == tx.FromAddress && x.Hash != tx.Hash).ToList();
                                                }
                                                break;
                                            case "TokenBurn()":
                                                {

                                                }
                                                break;
                                            default:
                                                break;
                                        }
                                        
                                    }
                                }
                            }
                            if (tx.TransactionType == TransactionType.ADNR)
                            {
                                var jobj = JObject.Parse(tx.Data);
                                if (jobj != null)
                                {
                                    var function = (string)jobj["Function"];
                                    if (!string.IsNullOrWhiteSpace(function))
                                    {
                                        var name = (string?)jobj["Name"];
                                        if (!string.IsNullOrWhiteSpace(name))
                                        {
                                            if (adnrNameList.Contains(name.ToLower()))
                                            {
                                                reject = true;
                                            }
                                            else
                                            {
                                                adnrNameList.Add(name.ToLower());
                                            }
                                        }
                                    }
                                }
                            }

                            if(tx.TransactionType == TransactionType.VOTE_TOPIC)
                            {
                                var signature = tx.Signature;
                                //the signature must be checked here to ensure someone isn't spamming bad TXs to invalidated votes/vote topics
                                var sigCheck = SignatureService.VerifySignature(tx.FromAddress, tx.Hash, signature);
                                if (sigCheck)
                                {
                                    var topicAlreadyExist = approvedMemPoolList.Exists(x => x.FromAddress == tx.FromAddress && x.TransactionType == TransactionType.VOTE_TOPIC);
                                    if (topicAlreadyExist)
                                        reject = true;
                                }
                            }

                            if (tx.TransactionType == TransactionType.VOTE)
                            {
                                var signature = tx.Signature;
                                //the signature must be checked here to ensure someone isn't spamming bad TXs to invalidated votes/vote topics
                                var sigCheck = SignatureService.VerifySignature(tx.FromAddress, tx.Hash, signature);
                                if (sigCheck)
                                {
                                    var topicAlreadyExist = approvedMemPoolList.Exists(x => x.FromAddress == tx.FromAddress && x.TransactionType == TransactionType.VOTE);
                                    if (topicAlreadyExist)
                                        reject = true;
                                }
                            }

                            if (tx.TransactionType == TransactionType.DSTR)
                            {
                                var signature = tx.Signature;
                                //the signature must be checked here to ensure someone isn't spamming bad TXs to invalidated votes/vote topics
                                var sigCheck = SignatureService.VerifySignature(tx.FromAddress, tx.Hash, signature);
                                if (sigCheck)
                                {
                                    var topicAlreadyExist = approvedMemPoolList.Exists(x => x.FromAddress == tx.FromAddress && x.TransactionType == TransactionType.DSTR);
                                    if (topicAlreadyExist)
                                        reject = true;
                                }
                            }

                            if (reject == false)
                            {
                                var signature = tx.Signature;
                                var sigCheck = SignatureService.VerifySignature(tx.FromAddress, tx.Hash, signature);
                                if (sigCheck)
                                {
                                    var balance = AccountStateTrei.GetAccountBalance(tx.FromAddress);

                                    var totalSend = (tx.Amount + tx.Fee);
                                    if (balance >= totalSend)
                                    {
                                        var dblspndChk = await DoubleSpendReplayCheck(tx);
                                        var isCraftedIntoBlock = await HasTxBeenCraftedIntoBlock(tx);
                                        var txVerify = await TransactionValidatorService.VerifyTX(tx);

                                        if (txVerify.Item1 && !dblspndChk && !isCraftedIntoBlock)
                                            approvedMemPoolList.Add(tx);
                                    }
                                    else
                                    {
                                        var txToDelete = collection.FindOne(t => t.Hash == tx.Hash);
                                        if (txToDelete != null)
                                        {
                                            try
                                            {
                                                collection.DeleteManySafe(x => x.Hash == txToDelete.Hash);
                                            }
                                            catch (Exception ex)
                                            {
                                                DbContext.Rollback("TransactionData.ProcessTxPool()");
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    var txToDelete = collection.FindOne(t => t.Hash == tx.Hash);
                                    if (txToDelete != null)
                                    {
                                        try
                                        {
                                            collection.DeleteManySafe(x => x.Hash == txToDelete.Hash);
                                        }
                                        catch (Exception ex)
                                        {
                                            DbContext.Rollback("TransactionData.ProcessTxPool()-2");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var txToDelete = collection.FindOne(t => t.Hash == tx.Hash);
                                if (txToDelete != null)
                                {
                                    try
                                    {
                                        collection.DeleteManySafe(x => x.Hash == txToDelete.Hash);
                                    }
                                    catch (Exception ex)
                                    {
                                        DbContext.Rollback("TransactionData.ProcessTxPool()-3");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var txToDelete = collection.FindOne(t => t.Hash == tx.Hash);
                        if (txToDelete != null)
                        {
                            try
                            {
                                collection.DeleteManySafe(x => x.Hash == txToDelete.Hash);
                            }
                            catch (Exception ex2)
                            {
                                DbContext.Rollback("TransactionData.ProcessTxPool()-4");
                            }
                        }
                    }
                });

            }

            return approvedMemPoolList;
        }

        public static async Task<bool> DoubleSpendReplayCheck(Transaction tx)
        {
            bool result = false;
            AccountStateTrei? stateTreiAcct = null;

            if (Globals.MemBlocks.Any())
            {
                var txExist = Globals.MemBlocks.ContainsKey(tx.Hash);
                if (txExist)
                {
                    result = true;//replay or douple spend has occured
                }
            }

            if(result)
            {
                return result;//replay or douple spend has occured
            }

            var mempool = GetPool();
            var txs = mempool.Find(x => x.FromAddress == tx.FromAddress && x.Hash != tx.Hash).ToList();

            if(txs.Count() > 0)
            {
                var amount = txs.Sum(x => x.Amount + x.Fee);
                stateTreiAcct = StateData.GetSpecificAccountStateTrei(tx.FromAddress);
                if(stateTreiAcct != null)
                {
                    var amountTotal = amount + tx.Amount + tx.Fee;
                    if (amountTotal > stateTreiAcct.Balance)
                    {
                        result = true; //douple spend or overspend has occured
                    }
                }
            }

            if (result)
            {
                return result;//replay or douple spend has occured
            }

            //double NFT transfer or burn check
            if (tx.TransactionType != TransactionType.TX && 
                tx.TransactionType != TransactionType.ADNR && 
                tx.TransactionType != TransactionType.VOTE_TOPIC && 
                tx.TransactionType != TransactionType.VOTE && 
                tx.TransactionType != TransactionType.DSTR &&
                tx.TransactionType != TransactionType.RESERVE &&
                tx.TransactionType != TransactionType.NFT_SALE)
            {
                if(tx.Data != null)
                {
                    var scInfo = TransactionUtility.GetSCTXFunctionAndUID(tx);
                    if (!scInfo.Item1)
                        return false;

                    string scUID = scInfo.Item3;
                    string function = scInfo.Item4;
                    JArray? scDataArray = scInfo.Item5;
                    bool skip = scInfo.Item2;

                    if (scDataArray != null && skip)
                    {
                        var scData = scDataArray[0];

                        function = (string?)scData["Function"];
                        scUID = (string?)scData["ContractUID"];
                        if (!string.IsNullOrWhiteSpace(function))
                        {
                            switch (function)
                            {
                                case "Transfer()":
                                    //do something
                                    var otherTransferTxs = mempool.Find(x => x.FromAddress == tx.FromAddress && x.Hash != tx.Hash).ToList();
                                    if(otherTransferTxs.Count() > 0)
                                    {
                                        foreach(var ottx in otherTransferTxs)
                                        {
                                            if(ottx.TransactionType == TransactionType.NFT_TX || ottx.TransactionType == TransactionType.NFT_BURN)
                                            {
                                                if(ottx.Data != null)
                                                {
                                                    var memscInfo = TransactionUtility.GetSCTXFunctionAndUID(tx);
                                                    if(memscInfo.Item1 && memscInfo.Item2)
                                                    {
                                                        var ottxDataArray = JsonConvert.DeserializeObject<JArray>(ottx.Data);
                                                        if (ottxDataArray != null)
                                                        {
                                                            var ottxData = ottxDataArray[0];

                                                            var ottxFunction = (string?)ottxData["Function"];
                                                            var ottxscUID = (string?)ottxData["ContractUID"];
                                                            if (!string.IsNullOrWhiteSpace(ottxFunction))
                                                            {
                                                                if (ottxscUID == scUID)
                                                                {
                                                                    //FAIL
                                                                    return false;
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    break;
                                case "Burn()":
                                    var otherBurnTxs = mempool.Find(x => x.FromAddress == tx.FromAddress && x.Hash != tx.Hash).ToList();
                                    if (otherBurnTxs.Count() > 0)
                                    {
                                        foreach (var obtx in otherBurnTxs)
                                        {
                                            if (obtx.TransactionType == TransactionType.NFT_TX || obtx.TransactionType == TransactionType.NFT_BURN)
                                            {
                                                if (obtx.Data != null)
                                                {
                                                    var memscInfo = TransactionUtility.GetSCTXFunctionAndUID(tx);
                                                    if(memscInfo.Item1 && memscInfo.Item2)
                                                    {
                                                        var ottxDataArray = JsonConvert.DeserializeObject<JArray>(obtx.Data);
                                                        if (ottxDataArray != null)
                                                        {
                                                            var ottxData = ottxDataArray[0];

                                                            var ottxFunction = (string?)ottxData["Function"];
                                                            var ottxscUID = (string?)ottxData["ContractUID"];
                                                            if (!string.IsNullOrWhiteSpace(ottxFunction))
                                                            {
                                                                if (ottxscUID == scUID)
                                                                {
                                                                    //FAIL
                                                                    return false;
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    break;
                                case string i when i == "TokenTransfer()" || i == "TokenBurn()":
                                    {
                                        var otherTxs = mempool.Find(x => x.FromAddress == tx.FromAddress && x.Hash != tx.Hash).ToList();
                                        if(otherTxs.Count() > 0)
                                        {
                                            decimal xferBurnAmount = 0.0M;
                                            var originaljobj = JObject.Parse(tx.Data);
                                            var tokenTicker = originaljobj["TokenTicker"]?.ToObject<string?>();
                                            var amount = originaljobj["Amount"]?.ToObject<decimal?>();

                                            if (amount == null)
                                                return false;

                                            var tokenAccount = stateTreiAcct.TokenAccounts?.Where(x => x.TokenTicker == tokenTicker).FirstOrDefault();

                                            if (tokenAccount == null)
                                                return false;

                                            xferBurnAmount += amount.Value;

                                            foreach (var otx in otherTxs)
                                            {
                                                if (otx.TransactionType == TransactionType.NFT_TX)
                                                {
                                                    if (otx.Data != null)
                                                    {
                                                        var memscInfo = TransactionUtility.GetSCTXFunctionAndUID(otx);
                                                        if(!memscInfo.Item2 && memscInfo.Item1)
                                                        {
                                                            var jobj = JObject.Parse(otx.Data);
                                                            var otscUID = jobj["ContractUID"]?.ToObject<string?>();
                                                            var otFunction = jobj["Function"]?.ToObject<string?>();

                                                            if (otscUID == scUID)
                                                            {
                                                                var otTokenTicker = jobj["TokenTicker"]?.ToObject<string?>();
                                                                var otAmount = jobj["Amount"]?.ToObject<decimal?>();
                                                                if (otFunction != null)
                                                                {
                                                                    if (otFunction == "TokenTransfer()" || otFunction == "TokenBurn()")
                                                                    {
                                                                        if (otAmount != null)
                                                                        {
                                                                            if (otTokenTicker == tokenTicker)
                                                                            {
                                                                                xferBurnAmount += otAmount.Value;
                                                                            }

                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }

                                            if(xferBurnAmount > tokenAccount.Balance) return false; //failed due to overspend/overburn
                                        }
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    
                }
            }
            return result;
        }

        public static async Task<Transaction?> GetNetworkTXByHash(string txHash, int startAtBlock = 0, bool startAtBeginning = false, bool forcedRun = false)
        {
            var output = "";
            var coreCount = Environment.ProcessorCount;
            Transaction? txResult = null;
            if (coreCount >= 4 || Globals.RunUnsafeCode || forcedRun)
            {
                if (!string.IsNullOrEmpty(txHash))
                {
                    try
                    {
                        txHash = txHash.Replace(" ", "");//removes any whitespace before or after in case left in.
                        var blocks = BlockchainData.GetBlocks();
                        var height = Convert.ToInt32(Globals.LastBlock.Height) - startAtBlock;
                        bool resultFound = false;

                        var integerList = startAtBeginning ? Enumerable.Range(startAtBlock, height + 1) : Enumerable.Range(startAtBlock, height + 1).Reverse();
                        Parallel.ForEach(integerList, new ParallelOptions { MaxDegreeOfParallelism = coreCount <= 4 ? 2 : 4 }, (blockHeight, loopState) =>
                        {
                            var block = blocks.Query().Where(x => x.Height == blockHeight).FirstOrDefault();
                            if (block != null)
                            {
                                var txs = block.Transactions.ToList();
                                var result = txs.Where(x => x.Hash == txHash).FirstOrDefault();
                                if (result != null)
                                {
                                    resultFound = true;
                                    txResult = result;
                                    loopState.Break();
                                }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        return txResult;
                    }
                }
            }
            else
            {
                return txResult;
            }

            return txResult;
        }

        public static LiteDB.ILiteCollection<Transaction> GetAll()
        {
            var collection = DbContext.DB_Wallet.GetCollection<Transaction>(DbContext.RSRV_TRANSACTIONS);
            return collection;
        }

        public static IEnumerable<Transaction> GetAllLocalTransactions(bool showFailed = false)
        {
            var transactions = GetAll().Query().Where(x => x.TransactionStatus != TransactionStatus.Failed).ToEnumerable();

            if (showFailed)
                transactions = GetAll().Query().Where(x => true).ToEnumerable();

            return transactions;
        }

        public static Transaction? GetTxByHash(string hash)
        {
            var transaction = GetAll().Query().Where(x => x.Hash == hash).FirstOrDefault();

            return transaction;
        }

        public static IEnumerable<Transaction> GetTxByBlock(long height)
        {
            var transactions = GetAll().Query().Where(x => x.Height == height).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetSuccessfulLocalTransactions(bool showFailed = false)
        {
            var transactions = GetAll().Query().Where(x => x.TransactionStatus == TransactionStatus.Success).ToEnumerable();

            if (showFailed)
                transactions = GetAll().Query().Where(x => true).ToEnumerable();

            return transactions;
        }
        public static IEnumerable<Transaction> GetReserveLocalTransactions(bool showFailed = false)
        {
            var transactions = GetAll().Query().Where(x => x.TransactionStatus == TransactionStatus.Reserved).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalMinedTransactions(bool showFailed = false)
        {
            var transactions = GetAll().Query().Where(x =>  x.FromAddress == "Coinbase_BlkRwd").ToEnumerable();

            if (showFailed)
                transactions = GetAll().Query().Where(x => true).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalPendingTransactions()
        {
            var transactions = GetAll().Query().Where(x => x.TransactionStatus == TransactionStatus.Pending).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalFailedTransactions()
        {
            var transactions = GetAll().Query().Where(x => x.TransactionStatus == TransactionStatus.Failed).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalTransactionsSinceBlock(long blockHeight)
        {
            var transactions = GetAll().Query().Where(x => x.Height >= blockHeight).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalTransactionsBeforeBlock(long blockHeight)
        {
            var transactions = GetAll().Query().Where(x => x.Height < blockHeight).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalTransactionsSinceDate(long timestamp)
        {
            var transactions = GetAll().Query().Where(x => x.Timestamp >= timestamp).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalTransactionsBeforeDate(long timestamp)
        {
            var transactions = GetAll().Query().Where(x => x.Timestamp < timestamp).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalVoteTransactions()
        {
            var transactions = GetAll().Query().Where(x => x.TransactionType == TransactionType.VOTE).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalVoteTopics()
        {
            var transactions = GetAll().Query().Where(x => x.TransactionType == TransactionType.VOTE_TOPIC).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetLocalAdnrTransactions()
        {
            var transactions = GetAll().Query().Where(x => x.TransactionType == TransactionType.ADNR).ToEnumerable();

            return transactions;
        }

        public static IEnumerable<Transaction> GetAllLocalTransactionsByAddress(string address)
        {
            var transactions = GetAll().Query().Where(x => x.FromAddress == address || x.ToAddress == address).ToEnumerable();

            return transactions;
        }
        public static IEnumerable<Transaction> GetAccountTransactionsLimit(string address, int limit = 50)
        {
            var transactions = GetAll();
            var query = transactions.Query()
                .OrderByDescending(x => x.Timestamp)
                .Where(x => x.FromAddress == address || x.ToAddress == address)
                .Limit(limit).ToEnumerable();
            return query;
        }

        public static IEnumerable<Transaction> GetTransactionsPaginated(int pageNumber, int resultPerPage, string? address = null)
        {
            var transactions = GetAll();
            if(address == null)
            {
                var query = transactions.Query()
                    .OrderByDescending(x => x.Timestamp)
                    .Offset((pageNumber - 1) * resultPerPage)
                    .Limit(resultPerPage).ToEnumerable();

                return query;
            }
            else
            {
                var query = transactions.Query()
                    .Where(x => x.FromAddress == address || x.ToAddress == address)
                    .OrderByDescending(x => x.Timestamp)
                    .Offset((pageNumber - 1) * resultPerPage)
                    .Limit(resultPerPage).ToEnumerable();

                return query;
            }
            
        }

    }

}
