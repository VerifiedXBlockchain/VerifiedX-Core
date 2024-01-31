﻿using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.DST;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Reflection.Metadata.Ecma335;
using System.Xml;
using System.Xml.Linq;

namespace ReserveBlockCore.Nodes
{
    public class ValidatorProcessor : IHostedService, IDisposable
    {
        public static IHubContext<P2PValidatorServer> HubContext;
        private readonly IHubContext<P2PValidatorServer> _hubContext;
        private readonly IHostApplicationLifetime _appLifetime;
        static SemaphoreSlim BroadcastNetworkValidatorLock = new SemaphoreSlim(1, 1);
        static SemaphoreSlim GenerateProofLock = new SemaphoreSlim(1, 1);

        public ValidatorProcessor(IHubContext<P2PValidatorServer> hubContext, IHostApplicationLifetime appLifetime)
        {
            _hubContext = hubContext;
            HubContext = hubContext;
            _appLifetime = appLifetime;
        }
        public Task StartAsync(CancellationToken stoppingToken)
        {
            //TODO: Create NetworkValidator Broadcast loop.
            _ = BroadcastNetworkValidators();
            _ = BlockHeightCheckLoopForVals();
            _ = GenerateProofs();

            return Task.CompletedTask;
        }
        public static async Task ProcessData(string message, string data, string ipAddress)
        {
            if (string.IsNullOrEmpty(message))
                return;
            
            switch(message)
            {
                case "1":
                    _ = IpMessage(data);
                    break;
                case "2":
                    _ = ValMessage(data);
                    break;
                case "3":
                    _ = NetworkValMessage(data);
                    break;
                case "4":
                    _ = ProofsMessage(data);
                    break;
                case "9999":
                    break;
            }
            
                                
        }

        #region Messages
        //4
        public static async Task ProofsMessage(string data)
        {
            if(string.IsNullOrEmpty(data)) return;

            var proofList = JsonConvert.DeserializeObject<List<Proof>>(data);

            if(proofList?.Count() > 0) return;

            await ProofUtility.SortProofs(proofList);

            var address = proofList.GroupBy(x => x.Address).OrderByDescending(x => x.Count()).FirstOrDefault();
            if(address != null)
            {
                if (Globals.ProofsBroadcasted.TryGetValue(address.Key, out var date))
                {
                    if(date < DateTime.UtcNow)
                    {
                        //broadcast
                        Globals.ProofsBroadcasted[address.Key] = DateTime.UtcNow.AddMinutes(20);
                        await Broadcast("4", data, "SendProofList");
                    }
                }
                else
                {
                    Globals.ProofsBroadcasted.TryAdd(address.Key, DateTime.UtcNow.AddMinutes(20));
                    //broadcast
                    await Broadcast("4", data, "SendProofList");
                }
            }

            
        }

        //3
        public static async Task NetworkValMessage(string data)
        {
            if (!string.IsNullOrEmpty(data))
            {
                var networkValList = JsonConvert.DeserializeObject<List<NetworkValidator>>(data);
                if (networkValList?.Count > 0)
                {
                    foreach (var networkValidator in networkValList)
                    {
                        if (Globals.NetworkValidators.TryGetValue(networkValidator.Address, out var networkValidatorVal))
                        {
                            var verifySig = SignatureService.VerifySignature(
                                networkValidator.Address,
                                networkValidator.SignatureMessage,
                                networkValidator.Signature);

                            //if(networkValidatorVal.PublicKey != networkValidator.PublicKey)

                            if (verifySig && networkValidator.Signature.Contains(networkValidator.PublicKey))
                                Globals.NetworkValidators[networkValidator.Address] = networkValidator;

                        }
                        else
                        {
                            Globals.NetworkValidators.TryAdd(networkValidator.Address, networkValidator);
                        }
                    }
                }
            }
        }

        //2
        private static async Task ValMessage(string data)
        {
            try
            {
                var netVal = JsonConvert.DeserializeObject<NetworkValidator>(data);
                if (netVal == null)
                    return;

                if (Globals.NetworkValidators.TryGetValue(netVal.Address, out var networkVal))
                {
                    if (networkVal != null)
                    {
                        Globals.NetworkValidators[networkVal.Address] = netVal;
                    }
                }
                else
                {
                    Globals.NetworkValidators.TryAdd(netVal.Address, netVal);
                }
            }
            catch (Exception ex)
            {

            }
        }

        //1
        private static async Task IpMessage(string data)
        {
            var IP = data.ToString();
            if (Globals.ReportedIPs.TryGetValue(IP, out int Occurrences))
                Globals.ReportedIPs[IP]++;
            else
                Globals.ReportedIPs[IP] = 1;
        }

        private static async Task TxMessage(string data)
        {
            var transaction = JsonConvert.DeserializeObject<Transaction>(data);
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
                            }
                        }
                    }
                }

            }
        }

        #endregion

        #region Broadcast

        private static async Task Broadcast(string messageType, string data, string method = "")
        {
            await HubContext.Clients.All.SendAsync(messageType, data);

            if (method == "") return;

            if (!Globals.ValidatorNodes.Any()) return;

            var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

            foreach (var val in valNodeList)
            {
                var source = new CancellationTokenSource(2000);
                await val.Connection.InvokeCoreAsync("SendProofList", args: new object?[] { data }, source.Token);
            }
        }

        #endregion

        #region Services

        private async Task GenerateProofs()
        {
            while(true)
            {
                var delay = Task.Delay(new TimeSpan(0, 0, 30));
                if (Globals.StopAllTimers && !Globals.IsChainSynced)
                {
                    await delay;
                    continue;
                }

                if(Globals.ValidatorNodes.Any())
                {
                    await delay;
                    continue;
                }
                await GenerateProofLock.WaitAsync();
                try
                {
                    var account = AccountData.GetLocalValidator();
                    var validators = Validators.Validator.GetAll();
                    var validator = validators.FindOne(x => x.Address == account.Address);
                    if (validator == null)
                    {
                        await delay;
                        continue;
                    }

                    var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

                    if (Globals.LastProofBlockheight == 0)
                    {
                        var firstProof = Globals.LastBlock.Height == 0 ? false : true;
                        var proofs = await ProofUtility.GenerateProofs(Globals.ValidatorAddress, account.PublicKey, Globals.LastBlock.Height, firstProof);
                        await ProofUtility.SortProofs(proofs);
                        //send proofs
                        var proofsJson = JsonConvert.SerializeObject(proofs);
                        await _hubContext.Clients.All.SendAsync("4", proofsJson);

                        if (Globals.ValidatorNodes.Count == 0)
                            continue;

                        foreach (var val in valNodeList)
                        {
                            var source = new CancellationTokenSource(2000);
                            await val.Connection.InvokeCoreAsync("SendProofList", args: new object?[] { proofsJson }, source.Token);
                        }
                    }
                    else
                    {
                        if (Globals.LastBlock.Height + 72 >= Globals.LastProofBlockheight)
                        {
                            var proofs = await ProofUtility.GenerateProofs(Globals.ValidatorAddress, account.PublicKey, Globals.LastProofBlockheight, false);
                            await ProofUtility.SortProofs(proofs);
                            //send proofs
                            var proofsJson = JsonConvert.SerializeObject(proofs);
                            await _hubContext.Clients.All.SendAsync("4", proofsJson);

                            foreach (var val in valNodeList)
                            {
                                var source = new CancellationTokenSource(2000);
                                await val.Connection.InvokeCoreAsync("SendProofList", args: new object?[] { proofsJson }, source.Token);
                            }
                        }
                    }
                }
                finally
                {
                    GenerateProofLock.Release();
                    await delay;
                }
                
            }
        }

        private async Task BroadcastNetworkValidators()
        {
            while (true)
            {
                var delay = Task.Delay(new TimeSpan(0,5,0));
                if (Globals.StopAllTimers && !Globals.IsChainSynced)
                {
                    await delay;
                    continue;
                }
                await BroadcastNetworkValidatorLock.WaitAsync();
                try
                {
                    if (Globals.NetworkValidators.Count == 0)
                        continue;

                    var networkValsJson = JsonConvert.SerializeObject(Globals.NetworkValidators.Values.ToList());

                    await _hubContext.Clients.All.SendAsync("3", networkValsJson);

                    if (Globals.ValidatorNodes.Count == 0)
                        continue;

                    var valNodeList = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).ToList();

                    foreach (var val in valNodeList)
                    {
                        var source = new CancellationTokenSource(2000);
                        await val.Connection.InvokeCoreAsync("SendNetworkValidatorList", args: new object?[] { networkValsJson }, source.Token);
                    }
                }
                finally
                {
                    BroadcastNetworkValidatorLock.Release();
                    await delay;
                }
            }
        }

        private static async Task BlockHeightCheckLoopForVals()
        {
            bool dupMessageShown = false;

            while (true)
            {
                try
                {
                    while (!Globals.ValidatorNodes.Any())
                        await Task.Delay(20);

                    await P2PValidatorClient.UpdateNodeHeights();

                    var maxHeight = Globals.ValidatorNodes.Values.Select(x => x.NodeHeight).OrderByDescending(x => x).FirstOrDefault();
                    if (maxHeight > Globals.LastBlock.Height)
                    {
                        P2PValidatorClient.UpdateMaxHeight(maxHeight);
                        //TODO: Update this method for getting block sync
                        _ = BlockDownloadService.GetAllBlocks();
                    }
                    else
                        P2PValidatorClient.UpdateMaxHeight(maxHeight);

                    var MaxHeight = P2PValidatorClient.MaxHeight();
                    foreach (var node in Globals.ValidatorNodes.Values)
                    {
                        if (node.NodeHeight < MaxHeight - 3)
                            await P2PValidatorClient.RemoveNode(node);
                    }

                }
                catch { }

                await Task.Delay(10000);
            }
        }

        #endregion

        #region Deprecate

        public static async void RandomNumberTaskV3(long blockHeight)
        {
            if (string.IsNullOrWhiteSpace(Globals.ValidatorAddress))
                return;

            while (Globals.LastBlock.Height + 1 != blockHeight)
            {                
                await BlockDownloadService.GetAllBlocks();
            }

            if (TimeUtil.GetTime() - Globals.CurrentTaskNumberAnswerV3.Time < 4)
            {
                return;
            }

            if (Globals.CurrentTaskNumberAnswerV3.Height != blockHeight)
            {
                var num = TaskQuestionUtility.GenerateRandomNumber(blockHeight);                                
                Globals.CurrentTaskNumberAnswerV3 = (blockHeight, num, TimeUtil.GetTime());
            }

            //await P2PClient.SendTaskAnswerV3(Globals.CurrentTaskNumberAnswerV3.Answer + ":" + Globals.CurrentTaskNumberAnswerV3.Height);
        }

        #endregion

        #region Stop/Dispose
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {

        }

        #endregion
    }
}
