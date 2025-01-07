﻿using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Utilities;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

namespace ReserveBlockCore.Services
{
    public class BlockDownloadService
    {
        public static ConcurrentDictionary<long, (Block block, string IPAddress)> BlockDict = 
            new ConcurrentDictionary<long, (Block, string)>();

        public const int MaxDownloadBuffer = 52428800;
        public const long MaxBlockRequestBuffer = 1048576;

        public static async Task GetAllBlocksV2()
        {
            try
            {
                if (Globals.BlocksDownloadV2Slim.CurrentCount == 0)
                {
                    await Task.Delay(1000);
                    return;
                }

                var stopwatch1 = new Stopwatch();
                stopwatch1.Start();
                    
                await Globals.BlocksDownloadV2Slim.WaitAsync();
                var coolDownTime = TimeUtil.GetTime();
                long blockStart = 0;
                while (Globals.LastBlock.Height < P2PClient.MaxHeight() || P2PClient.MaxHeight() == -1)
                {
                    //set the  next block height
                    var heightToDownload = Globals.LastBlock.Height + 1;
                    var blockBag = new ConcurrentBag<(Block, string)>();
                    ConcurrentDictionary<NodeInfo, (long, long)?> NodeDict = new ConcurrentDictionary<NodeInfo, (long, long)?>();
                    //Get the nodes who have the height I need.
                    var heightsFromNodes = Globals.Nodes.Values.Where(x => x.NodeHeight >= heightToDownload && x.IsConnected).ToArray();

                    if (!heightsFromNodes.Any())
                    {
                        //Failed to find any heights above mine. Checking again and continuing. 
                        await Task.Delay(20);
                        P2PClient.UpdateMaxHeight(Globals.Nodes.Values.Max(x => (long?)x.NodeHeight) ?? -1);
                        continue;
                    }

                    foreach (var node in heightsFromNodes)
                    {
                        if (node.NodeIP == "142.147.96.212")
                        {
                            //test
                        }

                        if (blockStart != 0)
                        {
                            if(blockStart != Globals.LastBlock.Height)
                                blockStart = Globals.LastBlock.Height;
                            var maxBlockHeight = await P2PClient.GetBlockSpan(blockStart, MaxBlockRequestBuffer, node);
                            if (maxBlockHeight != null)
                            {
                                (long, long) blockSpan = (blockStart, maxBlockHeight.Value);
                                NodeDict.TryAdd(node, blockSpan);
                                blockStart = (blockSpan.Item2 + 1);
                            }
                        }
                        else
                        {
                            var maxBlockHeight = await P2PClient.GetBlockSpan(heightToDownload, MaxBlockRequestBuffer, node);
                            if (maxBlockHeight != null)
                            {
                                (long, long) blockSpan = (heightToDownload, maxBlockHeight.Value);
                                NodeDict.TryAdd(node, blockSpan);
                                blockStart = (blockSpan.Item2 + 1);
                            }
                        }
                    }

                    //NodeDict.ParallelLoop(async h =>
                    //{
                    //    if (h.Value.HasValue)
                    //    {
                    //        var blockList = await P2PClient.GetBlockList(h.Value.Value, h.Key);
                    //        if (blockList != null)
                    //        {
                    //            foreach (var block in blockList)
                    //            {
                    //                blockBag.Add((block, h.Key.NodeIP));
                    //            }
                    //        }
                    //    }
                    //    else
                    //    {
                    //        Console.WriteLine("No Value");
                    //    }
                    //});
                    var tasks = NodeDict.Select(async h =>
                    {
                        if (h.Value.HasValue)
                        {
                            var blockList = await P2PClient.GetBlockList(h.Value.Value, h.Key);
                            if (blockList != null)
                            {
                                foreach (var block in blockList)
                                {
                                    blockBag.Add((block, h.Key.NodeIP));
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("No Value");
                        }
                    }).ToList();

                    await Task.WhenAll(tasks);

                    if (blockBag.Count > 0)
                    {
                        var blockBagOrdered = blockBag.OrderBy(x => x.Item1.Height);

                        foreach (var block in blockBagOrdered)
                        {
                            BlockDict[block.Item1.Height] = (block.Item1, block.Item2);
                        }

                        var stopwatch2 = new Stopwatch();
                        stopwatch2.Start();
                        stopwatch1.Stop();
                        Console.WriteLine($"Block Download time: {stopwatch1.ElapsedMilliseconds}");

                        await BlockValidatorService.ValidateBlocks();

                        stopwatch2.Stop();

                        
                        Console.WriteLine($"Block Processing time: {stopwatch2.ElapsedMilliseconds}");

                        _ = P2PClient.DropLowBandwidthPeers();
                        var AvailableNode = Globals.Nodes.Values.Where(x => x.IsSendingBlock == 0).OrderByDescending(x => x.NodeHeight).FirstOrDefault();

                        if (AvailableNode != null)
                        {
                            var DownloadBuffer = BlockDict.AsParallel().Sum(x => x.Value.block.Size);
                            if (DownloadBuffer > MaxDownloadBuffer)
                            {
                                
                            }
                            else
                            {
                                
                            }
                        }

                        blockBag.Clear();
                    }
                }
            }
            catch(Exception ex)
            {
                if (Globals.PrintConsoleErrors == true)
                {
                    Console.WriteLine("Failure in GetAllBlocks Method");
                    Console.WriteLine(ex.ToString());
                }
            }
            finally 
            {
                try 
                { Globals.BlocksDownloadV2Slim.Release(); Globals.UseV2BlockDownload = false; } catch { }
                 
            }
        }
        public static async Task<bool> GetAllBlocks()
        {
            try
            {
                if (Globals.UseV2BlockDownload)
                {
                    await Task.Delay(1000);
                    return false;
                }
                await Globals.BlocksDownloadSlim.WaitAsync();
                try
                {
                    while (Globals.LastBlock.Height < P2PClient.MaxHeight() || P2PClient.MaxHeight() == -1)
                    {
                        var coolDownTime = TimeUtil.GetTime();
                        var taskDict = new ConcurrentDictionary<long, (Task<Block> task, string ipAddress)>();
                        var heightToDownload = Globals.LastBlock.Height + 1;

                        var heightsFromNodes = Globals.Nodes.Values.Where(x => x.NodeHeight >= heightToDownload && x.IsConnected).GroupBy(x => x.NodeHeight)
                            .OrderBy(x => x.Key).Select((x, i) => (node: x.First(), height: heightToDownload + i))
                             .Where(x => x.node.NodeHeight >= x.height).ToArray();

                        if (!heightsFromNodes.Any())
                        {
                            await Task.Delay(20);
                            P2PClient.UpdateMaxHeight(Globals.Nodes.Values.Max(x => (long?)x.NodeHeight) ?? -1);
                            continue;
                        }
                        heightToDownload += heightsFromNodes.Length;
                        heightsFromNodes.ParallelLoop(h =>
                        {
                            taskDict[h.height] = (P2PClient.GetBlock(h.height, h.node), h.node.NodeIP);
                        });                        

                        while (taskDict.Any())
                        {
                            var completedTask = await Task.WhenAny(taskDict.Values.Select(x => x.task));
                            var result = await completedTask;

                            if (result == null)
                            {
                                var badTasks = taskDict.Where(x => x.Value.task.Id == completedTask.Id &&
                                    x.Value.task.IsCompleted).ToArray();

                                foreach (var badTask in badTasks)
                                    taskDict.TryRemove(badTask.Key, out _);

                                heightToDownload = Math.Min(heightToDownload, badTasks.Min(x => x.Key));
                            }
                            else
                            {
                                var resultHeight = result.Height;
                                var (_, ipAddress) = taskDict[resultHeight];
                                BlockDict[resultHeight] = (result, ipAddress);
                                taskDict.TryRemove(resultHeight, out _);
                                _ = BlockValidatorService.ValidateBlocks(true);
                            }

                            _ = P2PClient.DropLowBandwidthPeers();
                            var AvailableNode = Globals.Nodes.Values.Where(x => x.IsSendingBlock == 0).OrderByDescending(x => x.NodeHeight).FirstOrDefault();
                            if (AvailableNode != null)
                            {
                                var DownloadBuffer = BlockDict.AsParallel().Sum(x => x.Value.block.Size);
                                if (DownloadBuffer > MaxDownloadBuffer)
                                {
                                    if (TimeUtil.GetTime() - coolDownTime > 30 && taskDict.Keys.Any())
                                    {
                                        var staleHeight = taskDict.Keys.Min();
                                        var staleTask = taskDict[staleHeight];
                                        if (Globals.Nodes.TryRemove(staleTask.ipAddress, out var staleNode) && staleNode.Connection != null)
                                            _ = staleNode.Connection.DisposeAsync();
                                        taskDict.TryRemove(staleHeight, out _);
                                        staleTask.task.Dispose();
                                        heightToDownload = Math.Min(heightToDownload, staleHeight);
                                        coolDownTime = TimeUtil.GetTime();
                                    }
                                }
                                else
                                {
                                    var nextHeightToValidate = Globals.LastBlock.Height + 1;
                                    if (!BlockDict.ContainsKey(nextHeightToValidate) && !taskDict.ContainsKey(nextHeightToValidate))
                                        heightToDownload = nextHeightToValidate;
                                    while (taskDict.ContainsKey(heightToDownload))
                                        heightToDownload++;
                                    if (heightToDownload > P2PClient.MaxHeight())
                                        continue;
                                    taskDict[heightToDownload] = (P2PClient.GetBlock(heightToDownload, AvailableNode),
                                        AvailableNode.NodeIP);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    //Error
                    if (Globals.PrintConsoleErrors == true)
                    {
                        Console.WriteLine("Failure in GetAllBlocks Method");
                        Console.WriteLine(ex.ToString());
                    }
                }
            }
            catch { }
            finally
            {
                try { Globals.BlocksDownloadSlim.Release(); } catch { }
            }
                        
            return false;
        }

        public static async Task BlockCollisionResolve(Block badBlock, Block goodBlock)
        {

        }

    }
}
