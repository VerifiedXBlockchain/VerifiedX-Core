using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

namespace ReserveBlockCore.Services
{
    public class MempoolBroadcastService
    {
        static SemaphoreSlim MempoolBroadcastServiceLock = new SemaphoreSlim(1, 1);
        private static ConcurrentDictionary<string, int> RebroadcastDict = new ConcurrentDictionary<string, int>();
        public static async Task Run()
        {
            while(true)
            {
                var delay = Task.Delay(5000); // Changed from 1000ms to 5000ms to reduce broadcast frequency
                if (Globals.StopAllTimers && !Globals.IsChainSynced)
                {
                    await delay;
                    continue;
                }

                await MempoolBroadcastServiceLock.WaitAsync();

                try
                {
                    await StartupService.ClearStaleMempool();

                    var currentTimeMinusFiveMins = TimeUtil.GetTime(-20);

                    var mempoolList = TransactionData.GetMempool();
                    if (mempoolList != null)
                    {
                        var mempool = mempoolList.Where(x => x.Timestamp <= currentTimeMinusFiveMins).ToList();
                        if (mempool.Count() > 0)
                        {
                            foreach (var mempoolEntry in mempool)
                            {
                                if (RebroadcastDict.TryGetValue(mempoolEntry.Hash, out var rebr))
                                {
                                    if (rebr < 3)
                                    {
                                        RebroadcastDict[mempoolEntry.Hash] += 1;
                                        
                                        // Update last broadcast time before rebroadcasting
                                        Globals.TxLastBroadcastTime[mempoolEntry.Hash] = TimeUtil.GetTime();
                                        
                                        if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                                        {
                                            await P2PValidatorClient.SendTXMempool(mempoolEntry);//send directly to adjs
                                        }
                                        await P2PClient.SendTXMempool(mempoolEntry);//send out to mempool
                                        
                                    }
                                }
                                else
                                {
                                    RebroadcastDict.TryAdd(mempoolEntry.Hash, 1);
                                    
                                    // Update last broadcast time before rebroadcasting
                                    Globals.TxLastBroadcastTime[mempoolEntry.Hash] = TimeUtil.GetTime();
                                    
                                    if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                                    {
                                        await P2PValidatorClient.SendTXMempool(mempoolEntry);//send directly to adjs
                                    }
                                        
                                    await P2PClient.SendTXMempool(mempoolEntry);//send out to mempool

                                }
                            }
                        }
                    }
                }
                finally
                {
                    MempoolBroadcastServiceLock.Release();
                    
                    //clear old cache
                    var rebroadcastList = RebroadcastDict.Keys.ToList();
                    if(rebroadcastList.Any())
                    {
                        var mempoolList = TransactionData.GetMempool();
                        if(mempoolList != null)
                        {
                            foreach(var hash in rebroadcastList)
                            {
                                try
                                {
                                    var mempoolTx = mempoolList.Where(x => x.Hash == hash).FirstOrDefault();
                                    if (mempoolTx == null)
                                    {
                                        RebroadcastDict.TryRemove(hash, out _);
                                    }
                                }
                                catch
                                { }
                            }
                        }
                    }
                    
                    // Cleanup TxLastBroadcastTime for TXs no longer in mempool or very old
                    var broadcastList = Globals.TxLastBroadcastTime.Keys.ToList();
                    if(broadcastList.Any())
                    {
                        var mempoolList = TransactionData.GetMempool();
                        var currentTime = TimeUtil.GetTime();
                        
                        foreach(var hash in broadcastList)
                        {
                            try
                            {
                                // Remove if no longer in mempool
                                if(mempoolList != null)
                                {
                                    var mempoolTx = mempoolList.Where(x => x.Hash == hash).FirstOrDefault();
                                    if (mempoolTx == null)
                                    {
                                        Globals.TxLastBroadcastTime.TryRemove(hash, out _);
                                        continue;
                                    }
                                }
                                
                                // Remove if older than 10 minutes (600 seconds)
                                if (Globals.TxLastBroadcastTime.TryGetValue(hash, out var lastTime))
                                {
                                    if (currentTime - lastTime > 600)
                                    {
                                        Globals.TxLastBroadcastTime.TryRemove(hash, out _);
                                    }
                                }
                            }
                            catch
                            { }
                        }
                    }
                }

                await delay;
            }   
        }
    }
}
