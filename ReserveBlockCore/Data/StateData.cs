using ReserveBlockCore.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Utilities;
using ReserveBlockCore.Services;
using System.Collections.Concurrent;
using System.Xml.Linq;
using LiteDB;
using System.Net;
using System.Security.Principal;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Privacy;
using NBitcoin.JsonConverters;
using ReserveBlockCore.Models.DST;
using System;

namespace ReserveBlockCore.Data
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
                ShieldedStateRoot = global::ReserveBlockCore.Privacy.ShieldedStateRoot.Compute(),
            };

            var wTrei = DbContext.DB_WorldStateTrei.GetCollection<WorldTrei>(DbContext.RSRV_WSTATE_TREI);
            await wTrei.InsertSafeAsync(worldTrei);
            var aTrei = DbContext.DB_AccountStateTrei.GetCollection<AccountStateTrei>(DbContext.RSRV_ASTATE_TREI);
            await aTrei.InsertBulkSafeAsync(accStTrei);
        }

        public static async Task UpdateTreis(Block block)
        {
            Globals.TreisUpdating = true;
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
                        if (tx.FromAddress != "Coinbase_TrxFees" && tx.FromAddress != "Coinbase_BlkRwd"
                            && !PrivateTransactionTypes.IsZkAuthorizedPrivate(tx.TransactionType))
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
                                        ConfirmTimestamp = (long)tx.UnlockTime,
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

                                    ReserveTransactions.SaveReserveTx(rTx);
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
                                        break;
                                    case "Update()":
                                        UpdateSmartContract(tx);
                                        break;
                                    case "Transfer()":
                                        TransferSmartContract(tx);
                                        break;
                                    case "Burn()":
                                        BurnSmartContract(tx);
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
                                        DeployTokenContract(tx, block);
                                        break;
                                    case "TokenTransfer()":
                                        TokenTransfer(tx, block);
                                        break;
                                    case "TokenMint()":
                                        TokenMint(tx);
                                        break;
                                    case "TokenBurn()":
                                        TokenBurn(tx);
                                        break;
                                    case "TokenPause()":
                                        TokenPause(tx);
                                        break;
                                    case "TokenBanAddress()":
                                        TokenBanAddress(tx);
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
                                            CompleteSaleSmartContract(tx, block);
                                            break;
                                        case "M_Sale_Complete()":
                                            CompleteSaleSmartContract(tx, block);
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
                                            TransferAdnr(tx);
                                            break;
                                        case "AdnrDelete()":
                                            DeleteAdnr(tx);
                                            break;
                                        case "BTCAdnrCreate()":
                                            AddNewBTCAdnr(tx);
                                            break;
                                        case "BTCAdnrTransfer()":
                                            TransferBTCAdnr(tx);
                                            break;
                                        case "BTCAdnrDelete()":
                                            DeleteBTCAdnr(tx);
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
                                            DeleteDecShop(tx);
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
                            TransferVBTCV2(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_REQUEST)
                        {
                            RequestVBTCV2Withdrawal(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE)
                        {
                            CompleteVBTCV2Withdrawal(tx);
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
                            ApplyVBTCBridgeLock(tx);
                        }

                        if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_UNLOCK)
                        {
                            ApplyVBTCBridgeUnlock(tx);
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
                                            RegisterReserveAccount(tx);
                                            break;
                                        case "CallBack()":
                                            var callBackHash = (string?)jobj["Hash"];
                                            CallBackReserveAccountTx(callBackHash);
                                            break;
                                        case "Recover()":
                                            string recoveryAddress = jobj["RecoveryAddress"].ToObject<string>();
                                            string recoverySigScript = jobj["RecoverySigScript"].ToObject<string>();
                                            RecoverReserveAccountTx(recoveryAddress, tx.FromAddress, block.StateRoot);
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

                    txTreiUpdateSuccessCount += 1;
                }
                catch(Exception ex)
                {
                    txFailList.Add(tx);
                    var txJson = JsonConvert.SerializeObject(tx);
                    ErrorLogUtility.LogError($"Error Updating State Treis. Error: {ex.ToString()}", "StateData.UpdateTreis() - Part 1");
                    ErrorLogUtility.LogError($"TX Info. TX: {txJson}", "StateData.UpdateTreis() - Part 2");
                }
            }

            if(txTreiUpdateSuccessCount != txCount)
            {
                var txFailListJson = JsonConvert.SerializeObject(txFailList);
                ErrorLogUtility.LogError($"TX Success Count Failed to match tx Count. TX Fail List: {txFailListJson}", "StateData.UpdateTreis() - Part 3");
            }

            WorldTrei.UpdateWorldTrei(block);
            Globals.TreisUpdating = false;
        }

        public static async Task UpdateTreiFromReserve(List<ReserveTransactions> txList)
        {
            var accStTrei = GetAccountStateTrei();
            var rtxDb = ReserveTransactions.GetReserveTransactionsDb();
            var txDb = Transaction.GetAll();

            foreach(var rtx in  txList)
            {
                try
                {
                    // HAL-070 Fix: Idempotence guard - skip if already confirmed
                    if (rtx.ReserveTransactionStatus == ReserveTransactionStatus.Confirmed)
                    {
                        ErrorLogUtility.LogError($"UpdateTreiFromReserve skipped for hash {rtx.Hash} - already Confirmed", "StateData.UpdateTreiFromReserve()");
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
                                await accStTrei.UpdateSafeAsync(from);
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

                                        await accStTrei.UpdateSafeAsync(to);
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
                                if (scStateTreiRec != null)
                                {
                                    
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

                    var rtxRec = rtxDb.Query().Where(x => x.Id == rtx.Id).FirstOrDefault();
                    var hash = rtx.Hash;

                    if (rtxRec != null)
                    {
                        rtx.ReserveTransactionStatus = ReserveTransactionStatus.Confirmed;
                        await rtxDb.UpdateSafeAsync(rtx);
                    }

                    var txRec = TransactionData.GetTxByHash(hash);
                    if (txRec != null)
                    {
                        txRec.TransactionStatus = TransactionStatus.Success;
                        await txDb.UpdateSafeAsync(txRec);
                    }
                }
                catch {  }
            }
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
            var account = scTrei.FindOne(x => x.SmartContractUID == scUID);
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

        private static async Task CallBackReserveAccountTx(string? callBackHash)
        {
            try
            {
                if(callBackHash != null)
                {
                    var rTX = ReserveTransactions.GetTransactions(callBackHash);
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
                                    await stDb.UpdateSafeAsync(stateTreiFrom);

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
                                    await stDb.UpdateSafeAsync(stateTreiTo);

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

                        if(rTX.TransactionType == TransactionType.NFT_TX)
                        {
                            var scDataArray = JsonConvert.DeserializeObject<JArray>(rTX.Data);
                            var scData = scDataArray[0];
                            var function = (string?)scData["Function"];
                            var scUID = (string?)scData["ContractUID"];

                            if(scUID != null)
                            {
                                var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
                                if(scStateTrei != null)
                                {
                                    var scDb = SmartContractStateTrei.GetSCST();
                                    if(scDb != null)
                                    {
                                        scStateTrei.NextOwner = null;
                                        scStateTrei.IsLocked = false;
                                        await scDb.UpdateSafeAsync(scStateTrei);
                                    }
                                }
                            }
                        }
                        

                        var localTx = TransactionData.GetTxByHash(rTX.Hash);
                        if(localTx != null)
                        {
                            //Change TX status to CalledBack
                            var txDB = Transaction.GetAll();
                            localTx.TransactionStatus = TransactionStatus.CalledBack;
                            rTX.ReserveTransactionStatus = ReserveTransactionStatus.CalledBack;
                            if (txDB != null)
                                await txDB.UpdateSafeAsync(localTx);
                        }

                        if (rtxDb != null)
                            await rtxDb.UpdateSafeAsync(rTX);
                    }
                }
            }
            catch { }
        }
        private static async Task RecoverReserveAccountTx(string? _recoveryAddress, string _fromAddress, string stateRoot)
        {
            try
            {
                var stDb = GetAccountStateTrei();
                var rTXList = ReserveTransactions.GetTransactionList(_fromAddress);

                if(rTXList?.Count() > 0)
                {
                    // HAL-070 Fix: Filter to only Pending transactions for recovery
                    var pendingTXList = rTXList.Where(x => x.ReserveTransactionStatus == ReserveTransactionStatus.Pending).ToList();
                    
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
                                        await stDb.UpdateSafeAsync(stateTreiFrom);

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
                                            await stDb.UpdateSafeAsync(stateTreiRecovery);
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
                                            await stDb.InsertSafeAsync(acctStateTreiTo);

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
                                    await stDb.UpdateSafeAsync(stateTreiTo);
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
                                    if (scDb != null)
                                    {
                                        if (recoveryAddress != null)
                                        {
                                            scStateTrei.OwnerAddress = recoveryAddress;
                                            scStateTrei.NextOwner = null;
                                            scStateTrei.IsLocked = false;
                                            await scDb.UpdateSafeAsync(scStateTrei);
                                        }

                                    }
                                }
                            }
                        }

                        var localTx = TransactionData.GetTxByHash(rTX.Hash);
                        if (localTx != null)
                        {
                            //Change TX status to CalledBack
                            var txDB = Transaction.GetAll();
                            localTx.TransactionStatus = TransactionStatus.Recovered;
                            rTX.ReserveTransactionStatus = ReserveTransactionStatus.Recovered;
                            if (txDB != null)
                                await txDB.UpdateSafeAsync(localTx);
                        }

                        if (rtxDb != null)
                            await rtxDb.UpdateSafeAsync(rTX);
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
                        await stDb.UpdateSafeAsync(_stateTreiRecovery);
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
                        await stDb.InsertSafeAsync(acctStateTreiTo);
                }

                rsrvAccount.Balance = 0.0M;
                rsrvAccount.LockedBalance = 0.0M;

                await stDb.UpdateSafeAsync(rsrvAccount);

                var _scDb = SmartContractStateTrei.GetSCST();

                if(_scDb != null)
                {
                    var scList = _scDb.Query().Where(x => x.OwnerAddress == _fromAddress).ToList();
                    if(scList?.Count > 0 ) 
                    {
                        foreach(var sc in scList)
                        {
                            sc.OwnerAddress = _recoveryAddress;
                            sc.NextOwner = null;
                            sc.IsLocked = false;
                            await _scDb.UpdateSafeAsync(sc);
                        }
                    }
                }

            }
            catch { }
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

        private static async Task TransferBTCAdnr(Transaction tx)
        {
            bool complete = false;
            try
            {
                while (!complete)
                {
                    if (tx.Data != null)
                    {
                        var jobj = JObject.Parse(tx.Data);
                        var BTCToAddress = (string?)jobj["BTCToAddress"];
                        var BTCFromAddress = (string?)jobj["BTCFromAddress"];

                        var adnrs = BitcoinAdnr.GetBitcoinAdnr();
                        if (adnrs != null)
                        {
                            var adnr = adnrs.FindOne(x => x.BTCAddress == BTCFromAddress);
                            if (adnr != null)
                            {
                                adnr.BTCAddress = BTCToAddress;
                                adnr.RBXAddress = tx.ToAddress;
                                adnr.TxHash = tx.Hash;
                                await adnrs.UpdateSafeAsync(adnr);
                                complete = true;
                            }
                        }
                    }
                    else
                    {
                        complete = true;
                    }
                    
                }
            }
            catch { complete = true; }
        }

        private static void DeleteBTCAdnr(Transaction tx)
        {
            try
            {
                if(tx.Data != null)
                {
                    var jobj = JObject.Parse(tx.Data);
                    var BTCFromAddress = (string?)jobj["BTCFromAddress"];
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
        private static async Task TransferAdnr(Transaction tx)
        {
            bool complete = false;
            while(!complete)
            {
                var adnrs = Adnr.GetAdnr();
                if (adnrs != null)
                {
                    var adnr = adnrs.FindOne(x => x.Address == tx.FromAddress);
                    if (adnr != null)
                    {
                        adnr.Address = tx.ToAddress;
                        adnr.TxHash = tx.Hash;
                        await adnrs.UpdateSafeAsync(adnr);
                        complete = true;
                    }
                }
            }
            
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
        private static void TransferSmartContract(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
            var scData = scDataArray[0];

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
        private static void BurnSmartContract(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
            var scData = scDataArray[0];
            var function = (string?)scData["Function"];
            var data = (string?)scData["Data"];
            var scUID = (string?)scData["ContractUID"];

            var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTreiRec != null)
            {
                SmartContractStateTrei.DeleteSmartContract(scStateTreiRec);
            }

        }

        private static void EvolveSC(Transaction tx)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
            var scData = scDataArray[0];

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
            var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
            var scData = scDataArray[0];

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
            var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
            var scData = scDataArray[0];

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

        private static async Task DeployTokenContract(Transaction tx, Block block)
        {
            SmartContractStateTrei scST = new SmartContractStateTrei();
            var scDataArray = JsonConvert.DeserializeObject<JArray>(tx.Data);
            var scData = scDataArray[0];
            var stDb = GetAccountStateTrei();
            if (scData != null)
            {
                var function = (string?)scData["Function"];
                var data = (string?)scData["Data"];
                var scUID = (string?)scData["ContractUID"];
                var md5List = (string?)scData["MD5List"];


                scST.ContractData = data;
                scST.MinterAddress = tx.FromAddress;
                scST.OwnerAddress = tx.FromAddress;
                scST.SmartContractUID = scUID;
                scST.Nonce = 0;
                scST.MD5List = md5List;
                scST.IsToken = true;

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
                                scST.TokenDetails = tokenDetails;

                                if(tokenFeature.TokenSupply > 0)
                                {
                                    var toAddress = GetSpecificAccountStateTrei(sc.MinterAddress);
                                    if(toAddress != null)
                                    {
                                        var tokenAccount = TokenAccount.CreateTokenAccount(sc.SmartContractUID, tokenFeature.TokenName,
                                            tokenFeature.TokenTicker, tokenFeature.TokenSupply, tokenFeature.TokenDecimalPlaces);

                                        if(toAddress.TokenAccounts?.Count > 0)
                                        {
                                            toAddress.TokenAccounts.Add(tokenAccount);
                                        }
                                        else
                                        {
                                            List<TokenAccount> tokenAccounts = new List<TokenAccount>
                                            {
                                                tokenAccount
                                            };

                                            toAddress.TokenAccounts = tokenAccounts;
                                        }

                                        await stDb.UpdateSafeAsync(toAddress);
                                    }
                                    else
                                    {
                                        var tokenAccount = TokenAccount.CreateTokenAccount(sc.SmartContractUID, tokenFeature.TokenName, 
                                            tokenFeature.TokenTicker, tokenFeature.TokenSupply, tokenFeature.TokenDecimalPlaces);

                                        List<TokenAccount> tokenAccounts = new List<TokenAccount>
                                        {
                                            tokenAccount
                                        };

                                        var acctStateTreiTo = new AccountStateTrei
                                        {
                                            Key = tx.ToAddress,
                                            Nonce = 0,
                                            Balance = 0.0M,
                                            StateRoot = block.StateRoot,
                                            LockedBalance = 0.0M,
                                            TokenAccounts = tokenAccounts
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
                                    ContractOwner = sc.MinterAddress,
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
            if (scStateTreiRec != null)
            {
                scStateTreiRec.NextOwner = null;
                scStateTreiRec.IsLocked = false;
                scStateTreiRec.PurchaseAmount = null;
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
                    ErrorLogUtility.LogError($"TransferVBTCV2 failed: Missing required fields or invalid amount", "StateData.TransferVBTCV2()");
                    return;
                }

                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);

                if (scStateTreiRec != null)
                {
                    // Create credit/debit pair for the transfer
                    // Credit: Add tokens to recipient (tx.ToAddress)
                    // Debit: Subtract tokens from sender (tx.FromAddress)
                    List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                    {
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = amount.Value,
                            FromAddress = "+",
                            ToAddress = toAddress
                        },
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = amount.Value * -1.0M,
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

                    SCLogUtility.Log($"TransferVBTCV2 completed: {amount.Value} vBTC from {fromAddress} to {toAddress} in contract {scUID}", 
                        "StateData.TransferVBTCV2()");
                }
                else
                {
                    ErrorLogUtility.LogError($"TransferVBTCV2 failed: Contract not found - {scUID}", "StateData.TransferVBTCV2()");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"TransferVBTCV2 error: {ex.Message}", "StateData.TransferVBTCV2()");
            }
        }

        /// <summary>
        /// vBTC Base bridge lock: deduct transparent balance chain-wide (same ledger pattern as withdrawal burn).
        /// </summary>
        private static void ApplyVBTCBridgeLock(Transaction tx)
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

                if (!VBTCBridgeLockState.TryInsertFromLockTx(tx, scUID, lockId, amount.Value, amountSats.Value, evmDestination))
                {
                    ErrorLogUtility.LogError($"ApplyVBTCBridgeLock: failed to persist VBTCBridgeLockState for {lockId}", "StateData.ApplyVBTCBridgeLock()");
                }
                else
                {
                    BridgeLockRecord.TryMarkVfxLockConfirmed(lockId, tx.Hash);
                }

                SCLogUtility.Log($"ApplyVBTCBridgeLock: lockId={lockId}, amount={amount.Value} BTC, owner={tx.FromAddress}, scUID={scUID}",
                    "StateData.ApplyVBTCBridgeLock()");
            }
            catch (Exception ex)
            {
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
                ErrorLogUtility.LogError($"ApplyVBTCBridgeUnlock error: {ex.Message}", "StateData.ApplyVBTCBridgeUnlock()");
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
                    IsCompleted = false
                };

                // Save the withdrawal request to the per-user tracking database
                // This is consensus-critical and must succeed on ALL nodes
                var saved = VBTCWithdrawalRequest.Save(withdrawalRequest);
                if (!saved)
                {
                    ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal failed: Could not save withdrawal request", "StateData.RequestVBTCV2Withdrawal()");
                    return;
                }

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
                ErrorLogUtility.LogError($"RequestVBTCV2Withdrawal error: {ex.Message}", "StateData.RequestVBTCV2Withdrawal()");
            }
        }

        private static void CompleteVBTCV2Withdrawal(Transaction tx)
        {
            try
            {
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
                // This ensures we use the stored request data, not untrusted tx.Data
                var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
                if (withdrawalRequest == null)
                {
                    ErrorLogUtility.LogError($"CompleteVBTCV2Withdrawal failed: Withdrawal request not found for hash - {withdrawalRequestHash}", "StateData.CompleteVBTCV2Withdrawal()");
                    return;
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

                // CRITICAL: Burn the withdrawn tokens in state trei (CONSENSUS-CRITICAL — must run on ALL nodes)
                // FIND-002 FIX: Use storedAmount (from request record), NOT tx.Data.Amount
                var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scStateTreiRec != null)
                {
                    List<SmartContractStateTreiTokenizationTX> tknTxList = new List<SmartContractStateTreiTokenizationTX>
                    {
                        new SmartContractStateTreiTokenizationTX
                        {
                            Amount = storedAmount * -1.0M,  // Negative amount = burn (USING STORED AMOUNT)
                            FromAddress = withdrawalRequest.RequestorAddress,  // FIND-002 FIX: Burn from requester's balance
                            ToAddress = "-"  // "-" indicates burn/withdrawal
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

                SCLogUtility.Log($"CompleteVBTCV2Withdrawal completed: SCUID={scUID}, Requester={withdrawalRequest.RequestorAddress}, BTCTxHash={btcTxHash}, Amount={storedAmount} BTC, TxHash={tx.Hash}", 
                    "StateData.CompleteVBTCV2Withdrawal()");
            }
            catch (Exception ex)
            {
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
                var jobj = JObject.Parse(tx.Data);
                var scUID = jobj["ContractUID"]?.ToObject<string?>();
                var withdrawalRequestHash = jobj["WithdrawalRequestHash"]?.ToObject<string?>();
                var failureProof = jobj["FailureProof"]?.ToObject<string?>() ?? "";

                if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(withdrawalRequestHash))
                {
                    ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal failed: Missing required fields", "StateData.CancelVBTCV2Withdrawal()");
                    return;
                }

                // Look up the withdrawal request
                var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash);
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
                var existingCancellation = VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(withdrawalRequestHash);
                if (existingCancellation != null)
                {
                    ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal failed: Cancellation already exists for withdrawal - {withdrawalRequestHash}", "StateData.CancelVBTCV2Withdrawal()");
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
                ErrorLogUtility.LogError($"CancelVBTCV2Withdrawal error: {ex.Message}", "StateData.CancelVBTCV2Withdrawal()");
            }
        }

        /// <summary>
        /// FIND-018 Fix: Handle VBTC_V2_WITHDRAWAL_VOTE transaction
        /// Records a validator vote on a cancellation request.
        /// When 75% approval threshold is reached, the withdrawal is cancelled and funds are unlocked.
        /// Only active vBTC validators (tx.FromAddress) can vote.
        /// </summary>
        private static void VoteOnVBTCV2Cancellation(Transaction tx)
        {
            try
            {
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

                // Verify voter is an active vBTC validator (tx.FromAddress is authoritative)
                var validator = VBTCValidator.GetValidator(tx.FromAddress);
                if (validator == null || !validator.IsActive)
                {
                    ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation failed: {tx.FromAddress} is not an active vBTC validator", "StateData.VoteOnVBTCV2Cancellation()");
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

                // Check if 75% approval threshold reached
                var activeValidators = VBTCValidator.GetActiveValidators();
                var totalValidatorCount = activeValidators?.Count ?? 0;
                
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
                            var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(cancellation.WithdrawalRequestHash);
                            if (withdrawalRequest != null)
                            {
                                withdrawalRequest.Status = VBTCWithdrawalStatus.Cancelled;
                                withdrawalRequest.IsCompleted = true;
                                VBTCWithdrawalRequest.Save(withdrawalRequest, true);
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
                ErrorLogUtility.LogError($"VoteOnVBTCV2Cancellation error: {ex.Message}", "StateData.VoteOnVBTCV2Cancellation()");
            }
        }

    }
}
