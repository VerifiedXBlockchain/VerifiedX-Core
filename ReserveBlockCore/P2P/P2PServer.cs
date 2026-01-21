using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;


namespace ReserveBlockCore.P2P
{
    // HAL-16 Fix: Message type enum for semaphore selection
    public enum SignalRMessageType
    {
        Block,        // Highest priority - consensus critical
        Transaction,  // Medium priority - mempool operations
        Query        // Low priority - general queries
    }

    public class P2PServer : Hub
    {
        #region Broadcast methods
        public override async Task OnConnectedAsync()
        {            
            var peerIP = GetIP(Context);
            if (Globals.BannedIPs.ContainsKey(peerIP))
            {
                Context.Abort();
                return;
            }

            Globals.P2PPeerDict[peerIP] = Context;

            var portOpen = PortUtility.IsPortOpen(peerIP, Globals.Port);

            //Save Peer here
            var peers = Peers.GetAll();
            var peerExist = peers.Find(x => x.PeerIP == peerIP).FirstOrDefault();
            if (peerExist == null)
            {
                Peers nPeer = new Peers
                {
                    FailCount = 0,
                    IsIncoming = true,
                    IsOutgoing = portOpen,
                    PeerIP = peerIP
                };

                peers.InsertSafe(nPeer);
            }

            if (Globals.P2PPeerDict.TryGetValue(peerIP, out var context) && context.ConnectionId != Context.ConnectionId)
            {
                context.Abort();
            }

            await Clients.Caller.SendAsync("GetMessage", "IP", peerIP);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var peerIP = GetIP(Context);
            Globals.P2PPeerDict.TryRemove(peerIP, out _);
        }

        #endregion

        #region GetConnectedPeerCount
        public static int GetConnectedPeerCount()
        {
            return Globals.P2PPeerDict.Count;
        }

        #endregion

        #region SignalR DOS Protection
       
        // HAL-16 Fix: New SignalRQueue with message type support
        public static async Task<T> SignalRQueue<T>(HubCallerContext context, int sizeCost, SignalRMessageType messageType, Func<Task<T>> func)
        {
            var now = TimeUtil.GetMillisecondTime();
            var ipAddress = GetIP(context);
            if(Globals.AdjudicateAccount == null)
            {
                if (Globals.AdjNodes.ContainsKey(ipAddress))
                    return await func();
            }
            else
            {
                if (Globals.Nodes.ContainsKey(ipAddress))
                    return await func();
            }

            // HAL-054 Fix: Check global resource limits to prevent distributed DoS attacks
            if (Globals.GlobalConnectionCount >= Globals.MaxGlobalConnections)
            {
                throw new HubException("Server at maximum capacity. Too many global connections.");
            }

            if (Globals.GlobalBufferCost + sizeCost > Globals.MaxGlobalBufferCost)
            {
                throw new HubException("Server at maximum capacity. Too much global buffer usage.");
            }

            if (Globals.MessageLocks.TryGetValue(ipAddress, out var Lock))
            {                               
                var prev = Interlocked.Exchange(ref Lock.LastRequestTime, now);               
                if (Lock.ConnectionCount > Globals.MaxConnectionsPerIP)
                    BanService.BanPeer(ipAddress, ipAddress + ": Connection count exceeded limit.  Peer failed to wait for responses before sending new requests.", func.Method.Name);                                        
                
                if (Lock.BufferCost + sizeCost > Globals.MaxBufferCostPerIP)
                {
                    throw new HubException("Too much buffer usage.  Message was dropped.");
                }
                if (now - prev < 1000)
                    Interlocked.Increment(ref Lock.DelayLevel);
                else
                {
                    Interlocked.CompareExchange(ref Lock.DelayLevel, 1, 0);
                    Interlocked.Decrement(ref Lock.DelayLevel);
                }

                return await SignalRQueue(Lock, sizeCost, messageType, func);
            }
            else
            {
                var newLock = new MessageLock { BufferCost = sizeCost, LastRequestTime = now, DelayLevel = 0, ConnectionCount = 0 };
                if (Globals.MessageLocks.TryAdd(ipAddress, newLock))
                    return await SignalRQueue(newLock, sizeCost, messageType, func);
                else
                {
                    Lock = Globals.MessageLocks[ipAddress];                    
                    var prev = Interlocked.Exchange(ref Lock.LastRequestTime, now);
                    if (now - prev < 1000)
                        Interlocked.Increment(ref Lock.DelayLevel);
                    else
                    {
                        Interlocked.CompareExchange(ref Lock.DelayLevel, 1, 0);
                        Interlocked.Decrement(ref Lock.DelayLevel);
                    }

                    return await SignalRQueue(Lock, sizeCost, messageType, func);
                }
            }
        }

        // Legacy overload for backward compatibility - defaults to Query type
        public static async Task<T> SignalRQueue<T>(HubCallerContext context, int sizeCost, Func<Task<T>> func)
        {
            return await SignalRQueue(context, sizeCost, SignalRMessageType.Query, func);
        }

        private static async Task<T> SignalRQueue<T>(MessageLock Lock, int sizeCost, SignalRMessageType messageType, Func<Task<T>> func)
        {
            // HAL-16 Fix: Select appropriate semaphore based on message type
            // This prevents TXs from blocking blocks - critical for chain health
            var semaphore = messageType switch
            {
                SignalRMessageType.Block => Lock.BlockSemaphore,       // Highest priority - never blocked by TXs
                SignalRMessageType.Transaction => Lock.TxSemaphore,     // Allow 3 concurrent TXs
                SignalRMessageType.Query => Lock.QuerySemaphore,        // General operations
                _ => Lock.QuerySemaphore
            };

            T Result = default;
            try
            {
                await semaphore.WaitAsync();
                Interlocked.Increment(ref Lock.ConnectionCount);
                Interlocked.Add(ref Lock.BufferCost, sizeCost);
                
                // HAL-054 Fix: Track global resources
                Interlocked.Increment(ref Globals.GlobalConnectionCount);
                Interlocked.Add(ref Globals.GlobalBufferCost, sizeCost);

                var task = func();
                if (Lock.DelayLevel == 0)
                    return await task;

                var delayTask = Task.Delay(500 * (1 << (Lock.DelayLevel - 1)));
                await Task.WhenAll(delayTask, task);
                Result = await task;
            }
            catch { }
            finally
            {
                try 
                { 
                    Interlocked.Decrement(ref Lock.ConnectionCount);
                    Interlocked.Add(ref Lock.BufferCost, -sizeCost);
                    
                    // HAL-054 Fix: Release global resources
                    Interlocked.Decrement(ref Globals.GlobalConnectionCount);
                    Interlocked.Add(ref Globals.GlobalBufferCost, -sizeCost);
                    
                    semaphore.Release(); 
                } 
                catch { }
            }
                        
            return Result;            
        }

        #endregion

        #region Receive Block
        public async Task<bool> ReceiveBlock(Block nextBlock)
        {
            try
            {
                // HAL-16 Fix: Use Block semaphore to ensure blocks are NEVER blocked by TXs
                return await SignalRQueue(Context, (int)nextBlock.Size, SignalRMessageType.Block, async () =>
                {
                   
                    if (nextBlock.ChainRefId == BlockchainData.ChainRef)
                    {
                        var IP = GetIP(Context);
                        var nextHeight = Globals.LastBlock.Height + 1;
                        var currentHeight = nextBlock.Height;
                        
                        if (currentHeight >= nextHeight)
                        {
                            // HAL-066/HAL-072 Fix: Use AddOrUpdate to properly handle competing blocks list
                            BlockDownloadService.BlockDict.AddOrUpdate(
                                currentHeight,
                                new List<(Block, string)> { (nextBlock, IP) },
                                (key, existingList) =>
                                {
                                    existingList.Add((nextBlock, IP));
                                    return existingList;
                                });

                            await BlockValidatorService.ValidateBlocks();

                            if (nextHeight == currentHeight)
                            {
                                string data = "";
                                data = JsonConvert.SerializeObject(nextBlock);
                                await Clients.All.SendAsync("GetMessage", "blk", data);
                            }

                            if (nextHeight < currentHeight)
                                await BlockDownloadService.GetAllBlocks();

                            return true;
                        }
                    }

                    return false;
                });
            }
            catch { }

            return false;            
        }

        #endregion

        #region Ping Peers
        public async Task<string> PingPeers()
        {
            return await SignalRQueue(Context, 1024, async () => {
                var peerIP = GetIP(Context);

                var peerDB = Peers.GetAll();

                var peer = peerDB.FindOne(x => x.PeerIP == peerIP);

                if (peer == null)
                {
                    //this does a ping back on the peer to see if it can also be an outgoing node.
                    var result = await P2PClient.PingBackPeer(peerIP);

                    Peers nPeer = new Peers
                    {
                        FailCount = 0,
                        IsIncoming = true,
                        IsOutgoing = result,
                        PeerIP = peerIP
                    };

                    peerDB.InsertSafe(nPeer);
                }
                return "HelloPeer";
            });
        }

        public async Task<string> PingBackPeer()
        {
            return await SignalRQueue(Context, 1024, async () =>
            {
                return "HelloBackPeer";
            });
        }

        #endregion

        #region Send Block Height
        public async Task<long> SendBlockHeight()
        {
            return Globals.LastBlock.Height;
        }

        #endregion

        #region Send Adjudicator
        public async Task<Adjudicators> SendLeadAdjudicator()
        {
            return await SignalRQueue(Context, 128, async () =>
            {
                var leadAdj = Globals.LeadAdjudicator;
                if (leadAdj == null)
                {
                    leadAdj = Adjudicators.AdjudicatorData.GetLeadAdjudicator();
                }

                return leadAdj;
            });
        }

        #endregion

        #region Send Block Span

        public async Task<long?> SendBlockSpan(long startHeight, long cumulativeBuffer)
        {
            var blockSpan = await Blockchain.GetBlockSpan(startHeight, cumulativeBuffer);

            if (blockSpan == null)
                return null;

            return blockSpan.Value.Item2;
        }

        #endregion

        #region Send Block List
        public async Task<string> SendBlockList(long startHeight, long endHeight)
        {
            var peerIP = GetIP(Context);
            var blockSpan = (startHeight, endHeight);
            var blockList = await Blockchain.GetBlockListFromSpan(blockSpan);
            if (blockList?.Count > 0)
            {
                var blockListJsonCompressed = JsonConvert.SerializeObject(blockList).ToCompress();
                return blockListJsonCompressed;
            }
            else
            {
                return "0";
            }
        }

        #endregion

        #region Send Block
        //Send Block to client from p2p server
        public async Task<Block?> SendBlock(long currentBlock)
        {
            try
            {
                //return await SignalRQueue(Context, 1179648, async () =>
                //{
                //    var peerIP = GetIP(Context);

                //    var message = "";
                //    var nextBlockHeight = currentBlock + 1;
                //    var nextBlock = BlockchainData.GetBlockByHeight(nextBlockHeight);

                //    if (nextBlock != null)
                //    {
                //        return nextBlock;
                //    }
                //    else
                //    {
                //        return null;
                //    }
                //});
                var peerIP = GetIP(Context);

                var message = "";
                var nextBlockHeight = currentBlock + 1;
                var nextBlock = BlockchainData.GetBlockByHeight(nextBlockHeight);

                if (nextBlock != null)
                {
                    return nextBlock;
                }
                else
                {
                    return null;
                }
            }
            catch { }

            return null;
            
        }

        #endregion

        #region Send to Mempool
        public async Task<string> SendTxToMempool(Transaction txReceived)
        {
            try
            {
                // HAL-16 Fix: Use Transaction semaphore (allows 3 concurrent TXs, never blocks blocks)
                return await SignalRQueue(Context, (txReceived.Data?.Length ?? 0) + 1024, SignalRMessageType.Transaction, async () =>
                {
                    var result = "";

                    var data = JsonConvert.SerializeObject(txReceived);

                    var ablList = Globals.ABL.ToList();
                    if (ablList.Exists(x => x == txReceived.FromAddress))
                        return "TFVP";

                    var mempool = TransactionData.GetPool();
                    if (mempool.Count() != 0)
                    {
                        var txFound = mempool.FindOne(x => x.Hash == txReceived.Hash);
                        if (txFound == null)
                        {
                            var isTxStale = await TransactionData.IsTxTimestampStale(txReceived);
                            if (!isTxStale)
                            {
                                var twSkipVerify = txReceived.TransactionType == TransactionType.TKNZ_WD_OWNER ? true : false;
                                var txResult = !twSkipVerify ? await TransactionValidatorService.VerifyTX(txReceived) : await TransactionValidatorService.VerifyTX(txReceived, false, false, true); //sends tx to connected peers
                                if (txResult.Item1 == false)
                                {
                                    try
                                    {
                                        mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                    }
                                    catch (Exception ex)
                                    {
                                        //delete failed
                                    }
                                    return "TFVP";
                                }
                                var dblspndChk = await TransactionData.DoubleSpendReplayCheck(txReceived);
                                var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(txReceived);
                                var rating = await TransactionRatingService.GetTransactionRating(txReceived);
                                txReceived.TransactionRating = rating;

                                if (txResult.Item1 == true && dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                                {
                                    // HAL-071 Fix: Validate minimum fee and enforce mempool limits
                                    var feeValidation = MempoolEvictionUtility.ValidateMinimumFee(txReceived);
                                    if (!feeValidation.isValid)
                                    {
                                        return "TFVP"; // Transaction failed - fee below minimum
                                    }

                                    var stats = MempoolEvictionUtility.GetMempoolStats();
                                    var canAdd = MempoolEvictionUtility.CanAddToMempool(txReceived, stats.count, stats.sizeBytes);
                                    
                                    if (!canAdd.canAdd)
                                    {
                                        // Try to evict lower priority transactions to make room
                                        MempoolEvictionUtility.EvictLowestPriority(
                                            targetCount: (int)(Globals.MaxMempoolEntries * 0.95),
                                            targetSize: (long)(Globals.MaxMempoolSizeBytes * 0.95)
                                        );
                                        
                                        // Check again after eviction
                                        stats = MempoolEvictionUtility.GetMempoolStats();
                                        canAdd = MempoolEvictionUtility.CanAddToMempool(txReceived, stats.count, stats.sizeBytes);
                                        
                                        if (!canAdd.canAdd)
                                        {
                                            return "TFVP"; // Mempool full, cannot add
                                        }
                                    }

                                mempool.InsertSafe(txReceived);
                                
                                // HAL-16 Fix: Broadcast guard - check if we should broadcast
                                // Store broadcast decision but DON'T execute yet (release semaphore first)
                                bool shouldBroadcast = false;
                                if(!string.IsNullOrEmpty(Globals.ValidatorAddress))
                                {
                                    var now = TimeUtil.GetTime();
                                    
                                    if (Globals.TxLastBroadcastTime.TryGetValue(txReceived.Hash, out var lastBroadcastTime))
                                    {
                                        // Allow rebroadcast only if it's been > 30 seconds
                                        if (now - lastBroadcastTime > 30)
                                        {
                                            shouldBroadcast = true;
                                            Globals.TxLastBroadcastTime[txReceived.Hash] = now;
                                        }
                                        else
                                        {
                                            if (Globals.OptionalLogging)
                                            {
                                                LogUtility.Log($"TX {txReceived.Hash.Substring(0, 8)}... skip broadcast (last: {now - lastBroadcastTime}s ago)", 
                                                    "BroadcastThrottle");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // First time, broadcast it
                                        shouldBroadcast = true;
                                        Globals.TxLastBroadcastTime.TryAdd(txReceived.Hash, now);
                                    }
                                }
                                
                                // HAL-16 Fix: Schedule broadcast AFTER semaphore release (fire-and-forget)
                                // This prevents broadcasts from blocking TX processing or block reception
                                if (shouldBroadcast)
                                {
                                    var txToBroadcast = txReceived; // Capture for closure
                                    _ = Task.Run(() => ValidatorNode.Broadcast("7777", txToBroadcast, "SendTxToMempoolVals"));
                                }
                                
                                return "ATMP";//added to mempool
                                }
                                else
                                {
                                    try
                                    {
                                        mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                    }
                                    catch (Exception ex)
                                    {
                                        //delete failed
                                    }
                                    return "TFVP"; //transaction failed verification process
                                }
                            }


                        }
                        else
                        {
                            var isTxStale = await TransactionData.IsTxTimestampStale(txReceived);
                            if (!isTxStale)
                            {
                                var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(txReceived);
                                if (isCraftedIntoBlock)
                                {
                                    try
                                    {
                                        mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                    }
                                    catch (Exception ex)
                                    {
                                        //delete failed
                                    }
                                }

                                return "AIMP"; //already in mempool
                            }
                            else
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
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
                        var isTxStale = await TransactionData.IsTxTimestampStale(txReceived);
                        if (!isTxStale)
                        {
                            var txResult = await TransactionValidatorService.VerifyTX(txReceived);
                            if (!txResult.Item1)
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                }
                                catch { }

                                return "TFVP";
                            }
                            var dblspndChk = await TransactionData.DoubleSpendReplayCheck(txReceived);
                            var isCraftedIntoBlock = await TransactionData.HasTxBeenCraftedIntoBlock(txReceived);
                            var rating = await TransactionRatingService.GetTransactionRating(txReceived);
                            txReceived.TransactionRating = rating;

                            if (txResult.Item1 == true && dblspndChk == false && isCraftedIntoBlock == false && rating != TransactionRating.F)
                            {
                                // HAL-071 Fix: Validate minimum fee and enforce mempool limits
                                var feeValidation = MempoolEvictionUtility.ValidateMinimumFee(txReceived);
                                if (!feeValidation.isValid)
                                {
                                    return "TFVP"; // Transaction failed - fee below minimum
                                }

                                var stats = MempoolEvictionUtility.GetMempoolStats();
                                var canAdd = MempoolEvictionUtility.CanAddToMempool(txReceived, stats.count, stats.sizeBytes);
                                
                                if (!canAdd.canAdd)
                                {
                                    // Try to evict lower priority transactions to make room
                                    MempoolEvictionUtility.EvictLowestPriority(
                                        targetCount: (int)(Globals.MaxMempoolEntries * 0.95),
                                        targetSize: (long)(Globals.MaxMempoolSizeBytes * 0.95)
                                    );
                                    
                                    // Check again after eviction
                                    stats = MempoolEvictionUtility.GetMempoolStats();
                                    canAdd = MempoolEvictionUtility.CanAddToMempool(txReceived, stats.count, stats.sizeBytes);
                                    
                                    if (!canAdd.canAdd)
                                    {
                                        return "TFVP"; // Mempool full, cannot add
                                    }
                                }

                                mempool.InsertSafe(txReceived);
                                
                                // HAL-16 Fix: Broadcast guard - check if we should broadcast
                                // Store broadcast decision but DON'T execute yet (release semaphore first)
                                bool shouldBroadcast = false;
                                if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                                {
                                    var now = TimeUtil.GetTime();
                                    
                                    if (Globals.TxLastBroadcastTime.TryGetValue(txReceived.Hash, out var lastBroadcastTime))
                                    {
                                        // Allow rebroadcast only if it's been > 30 seconds
                                        if (now - lastBroadcastTime > 30)
                                        {
                                            shouldBroadcast = true;
                                            Globals.TxLastBroadcastTime[txReceived.Hash] = now;
                                        }
                                        else
                                        {
                                            if (Globals.OptionalLogging)
                                            {
                                                LogUtility.Log($"TX {txReceived.Hash.Substring(0, 8)}... skip broadcast (last: {now - lastBroadcastTime}s ago)", 
                                                    "BroadcastThrottle");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // First time, broadcast it
                                        shouldBroadcast = true;
                                        Globals.TxLastBroadcastTime.TryAdd(txReceived.Hash, now);
                                    }
                                }
                                
                                // HAL-16 Fix: Schedule broadcast AFTER semaphore release (fire-and-forget)
                                // This prevents broadcasts from blocking TX processing or block reception
                                if (shouldBroadcast)
                                {
                                    var txToBroadcast = txReceived; // Capture for closure
                                    _ = Task.Run(() => ValidatorNode.Broadcast("7777", txToBroadcast, "SendTxToMempoolVals"));
                                }
                                
                                return "ATMP";//added to mempool
                            }
                            else
                            {
                                try
                                {
                                    mempool.DeleteManySafe(x => x.Hash == txReceived.Hash);// tx has been crafted into block. Remove.
                                }
                                catch { }

                                return "TFVP"; //transaction failed verification process
                            }
                        }

                    }

                    return "";
                });
            }
            catch { }

            return "TFVP";
        }

        #endregion

        #region Get Masternodes
        public async Task<List<Validators>?> GetMasternodes(int valCount)
        {
            return await SignalRQueue(Context, 65536, async () =>
            {
                var validatorList = Validators.Validator.GetAll();
                var validatorListCount = validatorList.Count();

                if (validatorListCount == 0)
                {
                    return null;
                }
                else
                {
                    return validatorList.FindAll().ToList();
                }
            });
        }

        #endregion

        #region Send Banned Addresses
        public async Task<List<Validators>?> GetBannedMasternodes()
        {
            return await SignalRQueue(Context, 65536, async () =>
            {
                var validatorList = Validators.Validator.GetAll();
                var validatorListCount = validatorList.Count();

                if (validatorListCount == 0)
                {
                    return null;
                }
                else
                {
                    var bannedNodes = validatorList.FindAll().Where(x => x.FailCount >= 10).ToList();
                    if (bannedNodes.Count() > 0)
                    {
                        return bannedNodes;
                    }
                }

                return null;
            });
        }
        #endregion 

        #region Check Masternode
        public async Task<bool> MasternodeOnline()
        {
            return await SignalRQueue(Context, 128, async () =>
            {
                return true;
            });
        }

        #endregion

        #region Seed node check
        public async Task<string> SeedNodeCheck()
        {
            return await SignalRQueue(Context, 1024, async () =>
            {
                //do check for validator. if yes return val otherwise return Hello.
                var validators = Validators.Validator.GetAll();
                var isValidator = !string.IsNullOrEmpty(Globals.ValidatorAddress) ? true : false;

                if (isValidator)
                    return "HelloVal";

                return "Hello";
            });
        }
        #endregion

        #region Get IP

        private static string GetIP(HubCallerContext context)
        {
            var feature = context.Features.Get<IHttpConnectionFeature>();            
            var peerIP = feature.RemoteIpAddress.MapToIPv4().ToString();

            return peerIP;
        }

        #endregion

        #region Get Validator Status
        public async Task<bool> GetValidatorStatus()
        
        {
            return await SignalRQueue(Context, bool.FalseString.Length, async () => !string.IsNullOrEmpty(Globals.ValidatorAddress));
        }

        #endregion

        #region Get Wallet Version
        public async Task<string> GetWalletVersion()
        {
            return await SignalRQueue(Context, Globals.CLIVersion.Length, async () => Globals.CLIVersion);
        }

        #endregion

    }
}
