﻿using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using static ReserveBlockCore.Models.ConnectionHistory;

namespace ReserveBlockCore.P2P
{
    public class P2PAdjServer : Hub
    {
        #region Broadcast methods
        public override async Task OnConnectedAsync()
        {
            string lastArea = "";
            string peerIP = "";
            var startTime = DateTime.UtcNow;
            ConnectionHistory.ConnectionHistoryQueue conQueue = null;
            try
            {
                peerIP = GetIP(Context);
                if (Globals.BannedIPs.ContainsKey(peerIP))
                {
                    Context.Abort();
                    return;
                }

                conQueue = new ConnectionHistory.ConnectionHistoryQueue { IPAddress = peerIP };


                var httpContext = Context.GetHttpContext();
                if(httpContext == null)
                {
                    _ = EndOnConnect(peerIP, "1", startTime, conQueue, "httpcontext was null", "httpcontext was null");
                    return;
                }

                var address = httpContext.Request.Headers["address"].ToString();
                var time = httpContext.Request.Headers["time"].ToString();
                var uName = httpContext.Request.Headers["uName"].ToString();
                var signature = httpContext.Request.Headers["signature"].ToString();
                var walletVersion = httpContext.Request.Headers["walver"].ToString();

                conQueue.Address = address;
                var SignedMessage = address;
                var Now = TimeUtil.GetTime();
                SignedMessage = address + ":" + time;
                if (TimeUtil.GetTime() - long.Parse(time) > 300)
                {
                    await EndOnConnect(peerIP, "20", startTime, conQueue, "Signature Bad time.", "Signature Bad time.");
                    return;
                }

                if (!Globals.Signatures.TryAdd(signature, Now))
                {
                    await EndOnConnect(peerIP, "40", startTime, conQueue, "Reused signature.", "Reused signature.");
                    return;
                }
                                
                var walletVersionVerify = WalletVersionUtility.Verify(walletVersion);

                var fortisPool = Globals.FortisPool.Values;                
                if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(uName) || string.IsNullOrWhiteSpace(signature) || !walletVersionVerify) 
                {
                    _ = EndOnConnect(peerIP, "Z", startTime, conQueue,
                        "Connection Attempted, but missing field(s). Address, Unique name, and Signature required. You are being disconnected.",
                        "Connected, but missing field(s). Address, Unique name, and Signature required: " + address);
                    return;
                }
                
                var stateAddress = StateData.GetSpecificAccountStateTrei(address);
                if(stateAddress == null)
                {
                    _ = EndOnConnect(peerIP, "X", startTime, conQueue,
                        "Connection Attempted, But failed to find the address in trie. You are being disconnected.",
                        "Connection Attempted, but missing field Address: " + address + " IP: " + peerIP);
                    return;                    
                }

                if(stateAddress.Balance < ValidatorService.ValidatorRequiredAmount())
                {
                    _ = EndOnConnect(peerIP, "W", startTime, conQueue,
                        $"Connected, but you do not have the minimum balance of {ValidatorService.ValidatorRequiredAmount()} VFX. You are being disconnected.",
                        $"Connected, but you do not have the minimum balance of {ValidatorService.ValidatorRequiredAmount()} VFX: " + address);
                    return;
                }

                var verifySig = SignatureService.VerifySignature(address, SignedMessage, signature);
                if(!verifySig)
                {
                    _ = EndOnConnect(peerIP, "V", startTime, conQueue,
                        "Connected, but your address signature failed to verify. You are being disconnected.",
                        "Connected, but your address signature failed to verify with ADJ: " + address);
                    return;
                }

                var fortisPools = new FortisPool();
                fortisPools.IpAddress = peerIP;
                fortisPools.UniqueName = uName;
                fortisPools.ConnectDate = DateTime.UtcNow;
                fortisPools.Address = address;
                fortisPools.Context = Context;
                fortisPools.WalletVersion = walletVersion;

                UpdateFortisPool(fortisPools);

                lastArea = "A";
                if (Globals.OptionalLogging == true)                
                    LogUtility.Log($"Last Area Reached : '{lastArea}'. IP: {peerIP} ", "Adj Connection");                

                conQueue.ConnectionTime = (DateTime.UtcNow - startTime).Milliseconds;
                Globals.ConnectionHistoryDict.TryAdd(conQueue.IPAddress, conQueue);
            }
            catch (Exception ex)
            {
                Globals.FortisPool.TryRemoveFromKey1(peerIP, out _);
                Context?.Abort();
                ErrorLogUtility.LogError($"Unhandled exception has happend. Error : {ex.ToString()}", "P2PAdjServer.OnConnectedAsync()");
            }            
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var peerIP = GetIP(Context);
            Globals.P2PPeerDict.TryRemove(peerIP, out _);
            Globals.FortisPool.TryRemoveFromKey1(peerIP, out _);
            Context?.Abort();

            await base.OnDisconnectedAsync(ex);
        }

        private async Task SendAdjMessageSingle(string message, string data)
        {
            await Clients.Caller.SendAsync("GetAdjMessage", message, data, new CancellationTokenSource(1000).Token);
        }

        private async Task SendAdjMessageAll(string message, string data)
        {
            await Clients.All.SendAsync("GetAdjMessage", message, data, new CancellationTokenSource(6000).Token);
        }

        private async Task EndOnConnect(string ipAddress, string lastArea, DateTime startTime, ConnectionHistoryQueue queue, 
            string adjMessage, string loggMessage)
        {            
            await SendAdjMessageSingle("status", adjMessage);
            if (Globals.OptionalLogging == true)
            {
                LogUtility.Log(loggMessage, "Adj Connection");
                LogUtility.Log($"Last Area Reached : '{lastArea}'. IP: {ipAddress} ", "Adj Connection");
            }


            queue.ConnectionTime = (DateTime.UtcNow - startTime).Milliseconds;
            Globals.ConnectionHistoryDict.TryAdd(queue.IPAddress, queue);
            Context?.Abort();
        }

        private static void UpdateFortisPool(FortisPool pool)
        {
            var hasIpPool = Globals.FortisPool.TryGetFromKey1(pool.IpAddress, out var ipPool);
            var hasAddressPool = Globals.FortisPool.TryGetFromKey2(pool.Address, out var addressPool);

            if (hasIpPool && ipPool.Value.Context.ConnectionId != pool.Context.ConnectionId)
                ipPool.Value.Context.Abort();

            if (hasAddressPool && addressPool.Value.Context.ConnectionId != pool.Context.ConnectionId)
                addressPool.Value.Context.Abort();

            Globals.FortisPool[(pool.IpAddress, pool.Address)] = pool;
        }

        #endregion

        #region Get Connected Val Count

        public static async Task<int> GetConnectedValCount()
        {
            try
            {
                var peerCount = Globals.FortisPool.Count;
                return peerCount;
            }
            catch { }

            return -1;
        }

        #endregion

        #region Fortis Pool IPs
        public async Task<string> FortisPool()
        {
            return await P2PServer.SignalRQueue(Context, Globals.FortisPoolCache.Length, async () => Globals.FortisPoolCache);
        }
        #endregion

        #region Signer Seed Info
        public async Task<string> SignerInfo()
        {
            return await P2PServer.SignalRQueue(Context, Globals.SignerCache.Length, async () => Globals.SignerCache);
        }

        public async Task<string> IpAddresses()
        {
            return await P2PServer.SignalRQueue(Context, Globals.IpAddressCache.Length, async () => Globals.IpAddressCache);       
        }

        #endregion

        #region Receive Rand Num and Task Answer V3
        public async Task<TaskAnswerResult> ReceiveTaskAnswerV3(string request)
        {
            if (Globals.AdjudicateAccount == null)
            {                
                return new TaskAnswerResult { AnswerCode = 4 }; //adjudicator is still booting up
            }

            var ipAddress = GetIP(Context);
            if (request?.Length > 30)
            {
                BanService.BanPeer(ipAddress, "request too big", "ReceiveTaskAnswerV3");
                return new TaskAnswerResult { AnswerCode = 5 };
            }

            try
            {
                return  await P2PServer.SignalRQueue(Context, request.Length, async () =>
                {
                    var taskAnsRes = new TaskAnswerResult();
                                        
                    var taskResult = request?.Split(':');
                    if (taskResult == null || taskResult.Length != 2)
                    {
                        taskAnsRes.AnswerCode = 5; // Task answer was null. Should not be possible.
                        return taskAnsRes;
                    }

                    var (Answer, Height) = (int.Parse(taskResult[0]), long.Parse(taskResult[1]));                                        

                    //This will result in users not getting their answers chosen if they are not in list.
                    var fortisPool = Globals.FortisPool.Values;
                    if (Globals.FortisPool.TryGetFromKey1(ipAddress, out var Pool))
                    {
                        if (Height != Globals.LastBlock.Height + 1 && Height != Globals.LastBlock.Height + 2)
                        {
                            taskAnsRes.AnswerCode = 6;
                            return taskAnsRes;
                        }
                        
                        if (!Globals.TaskAnswerDictV3.TryAdd((Pool.Key2, Height), (ipAddress, Pool.Key2, Answer)))
                        {
                            taskAnsRes.AnswerAccepted = true;
                            taskAnsRes.AnswerCode = 0;
                            return taskAnsRes;
                        }

                        taskAnsRes.AnswerCode = 7; // Answer was already submitted
                        return taskAnsRes;
                    }

                    Context.Abort();
                    taskAnsRes.AnswerCode = 3; //address is not pressent in the fortis pool
                    return taskAnsRes;
                });

            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error Processing Task - Error: {ex.ToString()}", "P2PAdjServer.ReceiveTaskAnswerV3()");
            }
            
            return new TaskAnswerResult {  AnswerCode = 1337 }; // Unknown Error
        }

        #endregion

        #region Receive Winning Task Block Answer V3
        public async Task<bool> ReceiveWinningBlockV3(string blockString)
        {
            try
            {
                if (blockString == null || Globals.AdjudicateAccount == null)
                    return false;
                
                var ipAddress = GetIP(Context);
                if (blockString.Length > 1048576 || !Globals.FortisPool.TryGetFromKey1(ipAddress, out var Pool))
                {
                    BanService.BanPeer(ipAddress, "block size too big", "ReceiveWinningBlockV3");
                    return false;
                }

                var block = JsonConvert.DeserializeObject<Block>(blockString);                
                var RBXAddress = Pool.Key2;
                if (!Globals.TaskSelectedNumbersV3.ContainsKey((RBXAddress, block.Height)))
                {
                    BanService.BanPeer(ipAddress, "unselected block was submitted", "ReceiveWinningBlockV3");
                    return false;
                }

                if (block.Height != Globals.LastBlock.Height + 1)
                    return false;

                return await P2PServer.SignalRQueue(Context, blockString.Length, async () =>
                {                                        
                    if (SignatureService.VerifySignature(RBXAddress, block.Hash, block.ValidatorSignature)
                        && RBXAddress == block.Validator && Globals.TaskWinnerDictV3.TryAdd((RBXAddress, block.Height), block))
                    {
                        return true;
                    }

                    return false;
                });
            }
            catch { }
            return false;
        }

        #endregion

        #region Receive TX to relay
        public async Task<bool> ReceiveTX(Transaction transaction)
        {
            try
            {
                return await P2PServer.SignalRQueue(Context, (transaction.Data?.Length ?? 0) + 1028, async () =>
                {
                    bool output = false;
                    if (Globals.BlocksDownloadSlim.CurrentCount != 0)
                    {
                        if (Globals.AdjudicateAccount != null)
                        {
                            if (transaction != null)
                            {
                                var isTxStale = await TransactionData.IsTxTimestampStale(transaction);
                                if (!isTxStale)
                                {
                                    var mempool = TransactionData.GetPool();
                                    if (mempool.Count() != 0)
                                    {
                                        var txFound = mempool.FindOne(x => x.Hash == transaction.Hash);
                                        if (txFound == null)
                                        {

                                            var txResult = await TransactionValidatorService.VerifyTX(transaction);
                                            if (txResult.Item1 == true)
                                            {
                                                var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                                                var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                                var rating = await TransactionRatingService.GetTransactionRating(transaction);
                                                transaction.TransactionRating = rating;

                                                if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                                {
                                                    mempool.InsertSafe(transaction);
                                                    var txOutput = "";
                                                    txOutput = JsonConvert.SerializeObject(transaction);
                                                    //await SendAdjMessageAll("tx", txOutput);//sends messages to all in fortis pool
                                                    Globals.BroadcastedTrxDict[transaction.Hash] = transaction;
                                                    if (!Globals.ConsensusBroadcastedTrxDict.TryGetValue(transaction.Hash, out _))
                                                    {
                                                        Globals.ConsensusBroadcastedTrxDict[transaction.Hash] = new TransactionBroadcast { Hash = transaction.Hash, IsBroadcastedToAdj = false, IsBroadcastedToVal = false, Transaction = transaction, RebroadcastCount = 0 };
                                                    }
                                                    output = true;
                                                }
                                                else
                                                {
                                                    Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                    Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                }
                                            }
                                            else
                                            {
                                                Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                            }

                                        }
                                        else
                                        {
                                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                            if (!isCraftedIntoBlock)
                                            {
                                                if (!Globals.BroadcastedTrxDict.TryGetValue(transaction.Hash, out _))
                                                {
                                                    var txOutput = "";
                                                    txOutput = JsonConvert.SerializeObject(transaction);
                                                    //await SendAdjMessageAll("tx", txOutput);
                                                    Globals.BroadcastedTrxDict[transaction.Hash] = transaction;
                                                    if (!Globals.ConsensusBroadcastedTrxDict.TryGetValue(transaction.Hash, out _))
                                                    {
                                                        Globals.ConsensusBroadcastedTrxDict[transaction.Hash] = new TransactionBroadcast { Hash = transaction.Hash, IsBroadcastedToAdj = false, IsBroadcastedToVal = false, Transaction = transaction, RebroadcastCount = 0 };
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                try
                                                {
                                                    mempool.DeleteManySafe(x => x.Hash == transaction.Hash);// tx has been crafted into block. Remove.
                                                    Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                    Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                }
                                                catch (Exception ex)
                                                {
                                                    //delete failed - may not be present
                                                    Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                    Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {

                                        var txResult = await TransactionValidatorService.VerifyTX(transaction);
                                        if (txResult.Item1 == true)
                                        {
                                            var dblspndChk = await TransactionData.DoubleSpendReplayCheck(transaction);
                                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(transaction);
                                            var rating = await TransactionRatingService.GetTransactionRating(transaction);
                                            transaction.TransactionRating = rating;

                                            if (dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                            {
                                                mempool.InsertSafe(transaction);
                                                var txOutput = "";
                                                txOutput = JsonConvert.SerializeObject(transaction);
                                                //await SendAdjMessageAll("tx", txOutput);//sends messages to all in fortis pool
                                                Globals.BroadcastedTrxDict[transaction.Hash] = transaction;
                                                if (!Globals.ConsensusBroadcastedTrxDict.TryGetValue(transaction.Hash, out _))
                                                {
                                                    Globals.ConsensusBroadcastedTrxDict[transaction.Hash] = new TransactionBroadcast { Hash = transaction.Hash, IsBroadcastedToAdj = false, IsBroadcastedToVal = false, Transaction = transaction, RebroadcastCount = 0 };
                                                }
                                                output = true;
                                            }
                                            else
                                            {
                                                Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                                Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                            }
                                        }
                                        else
                                        {
                                            Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                            Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                        }
                                    }
                                }
                                else
                                {
                                    Globals.BroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                    Globals.ConsensusBroadcastedTrxDict.TryRemove(transaction.Hash, out _);
                                }

                            }
                        }
                    }

                    return output;
                });
            }
            catch { } //incorrect TX received

            return false;
        }

        #endregion

        #region Get IP

        private static string GetIP(HubCallerContext context)
        {
            try
            {
                var peerIP = "NA";
                var feature = context.Features.Get<IHttpConnectionFeature>();
                if (feature != null)
                {
                    if (feature.RemoteIpAddress != null)
                    {
                        peerIP = feature.RemoteIpAddress.MapToIPv4().ToString();
                    }
                }

                return peerIP;
            }
            catch { }

            return "0.0.0.0";
        }

        #endregion
    }
}
