﻿using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;


namespace ReserveBlockCore.P2P
{
    public class P2PBeaconServer : Hub
    {
        #region Connect/Disconnect methods
        public override async Task OnConnectedAsync()
        {
            bool connected = false;
            bool pendingSends = false;
            bool pendingReceives = false;

            SCLogUtility.Log("Entered Beacon Connection", "CustomLogging");
            await Task.Delay(10);

            var peerIP = GetIP(Context);
            SCLogUtility.Log($"IP Found: {peerIP}", "CustomLogging");
            await Task.Delay(10);
            if (Globals.BeaconPeerDict.TryGetValue(peerIP, out var context) && context.ConnectionId != Context.ConnectionId)
                context.Abort();

            SCLogUtility.Log($"Beyond Context", "CustomLogging");
            await Task.Delay(10);
            Globals.BeaconPeerDict[peerIP] = Context;

            var httpContext = Context.GetHttpContext();
            if (httpContext != null)
            {
                SCLogUtility.Log($"HttpContext was good.", "CustomLogging");
                await Task.Delay(10);
                var beaconRef = httpContext.Request.Headers["beaconRef"].ToString();
                var walletVersion = httpContext.Request.Headers["walver"].ToString();
                var uplReq = httpContext.Request.Headers["uplReq"].ToString();
                var dwnlReq = httpContext.Request.Headers["dwnlReq"].ToString();

                SCLogUtility.Log($"beaconRef: {beaconRef} | walletVersion: {walletVersion} | uplReq: {uplReq} | dwnReq: {dwnlReq}", "CustomLogging");
                await Task.Delay(10);

                var walletVersionVerify = WalletVersionUtility.Verify(walletVersion);

                var beaconPool = Globals.BeaconPool.Values.ToList();

                SCLogUtility.Log($"Wallet Version Verift: {walletVersionVerify}", "CustomLogging");
                await Task.Delay(10);

                if (!string.IsNullOrWhiteSpace(beaconRef) && walletVersionVerify)
                {
                    SCLogUtility.Log($"Wal Version Good and Beacon Ref Good.", "CustomLogging");
                    var beaconData = BeaconData.GetBeaconData();
                    var beacon = BeaconData.GetBeacon();
                    
                    if(uplReq == "n")
                    {
                        if (beaconData != null)
                        {
                            var beaconSendData = beaconData.Where(x => x.Reference == beaconRef).ToList();
                            if (beaconSendData.Count() > 0)
                            {
                                var removeList = beaconSendData.Where(x => x.AssetExpireDate <= TimeUtil.GetTime());
                                //remove record and remove any data sent
                                if (beacon != null)
                                {
                                    beacon.DeleteManySafe(x => removeList.Contains(x));
                                }
                                beaconData = BeaconData.GetBeaconData();
                                if(beaconData != null)
                                {
                                    beaconSendData = beaconData.Where(x => x.Reference == beaconRef).ToList();
                                    if (beaconSendData.Count() > 0)
                                    {
                                        pendingSends = true;
                                    }
                                }    
                            }
                        }
                    }
                    else
                    {
                        pendingSends = true;
                    }

                    if(dwnlReq == "n")
                    {
                        if (beaconData != null)
                        {
                            var beaconRecData = beaconData.Where(x => x.NextOwnerReference == beaconRef).ToList();
                            if (beaconRecData.Count() > 0)
                            {
                                var removeList = beaconRecData.Where(x => x.AssetExpireDate <= TimeUtil.GetTime());
                                //remove record and remove any data sent
                                if (beacon != null)
                                {
                                    beacon.DeleteManySafe(x => removeList.Contains(x));
                                }
                                beaconData = BeaconData.GetBeaconData();
                                if (beaconData != null)
                                {
                                    beaconRecData = beaconData.Where(x => x.NextOwnerReference == beaconRef).ToList();
                                    if (beaconRecData.Count() > 0)
                                    {
                                        pendingReceives = true;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        pendingReceives = true;
                    }

                    if(pendingSends == true || pendingReceives == true)
                    {
                        SCLogUtility.Log($"Pending Sends: {pendingSends} | Pending Received: {pendingReceives}", "CustomLogging");
                        var conExist = beaconPool.Where(x => x.Reference == beaconRef || x.IpAddress == peerIP).FirstOrDefault();
                        if (conExist != null)
                        {
                            SCLogUtility.Log($"Con Exist", "CustomLogging");
                            var beaconCon = Globals.BeaconPool.Values.Where(x => x.Reference == beaconRef || x.IpAddress == peerIP).FirstOrDefault();
                            if (beaconCon != null)
                            {
                                SCLogUtility.Log($"BeaconCon was not null", "CustomLogging");
                                beaconCon.WalletVersion = walletVersion;
                                beaconCon.Reference = beaconRef;
                                beaconCon.ConnectDate = DateTime.Now;
                                beaconCon.ConnectionId = Context.ConnectionId;
                                beaconCon.IpAddress = peerIP;
                            }
                        }
                        else
                        {
                            SCLogUtility.Log($"Con did not exist, but thats ok.", "CustomLogging");
                            BeaconPool beaconConnection = new BeaconPool
                            {
                                WalletVersion = walletVersion,
                                Reference = beaconRef,
                                ConnectDate = DateTime.Now,
                                ConnectionId = Context.ConnectionId,
                                IpAddress = peerIP
                            };

                            Globals.BeaconPool[(peerIP, beaconRef)] = beaconConnection;
                        }

                        connected = true;
                    }

                }
            }

            if(connected)
            {
                await SendBeaconMessageSingle("status", "Connected");
            }
            else
            {
                await SendBeaconMessageSingle("disconnect", "No downloads at this time.");
                Context.Abort();
            }



            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var peerIP = GetIP(Context);
            Globals.BeaconPeerDict.TryRemove(peerIP, out _);
            Globals.BeaconPool.TryGetFromKey1(peerIP, out _);
        }
        private async Task SendMessageClient(string clientId, string method, string message)
        {
            await Clients.Client(clientId).SendAsync("GetBeaconData", method, message);
        }

        private async Task SendBeaconMessageSingle(string message, string data)
        {
            await Clients.Caller.SendAsync("GetBeaconData", message, data);
        }

        private async Task SendBeaconMessageAll(string message, string data)
        {
            await Clients.All.SendAsync("GetBeaconData", message, data);
        }

        #endregion

        #region  Beacon Receive Download Request - The receiver of the NFT Asset
        public async Task<bool> ReceiveDownloadRequest(BeaconData.BeaconDownloadData bdd)
        {
            //return await SignalRQueue(Context, 1024, async () =>
            //{
                bool result = false;
                var peerIP = GetIP(Context);
                var beaconPool = Globals.BeaconPool.Values.ToList();
                try
                {
                    if (bdd != null)
                    {
                        var scState = SmartContractStateTrei.GetSmartContractState(bdd.SmartContractUID);
                        if (scState == null)
                        {
                            return result; //fail
                        }

                        //var sigCheck = SignatureService.VerifySignature(scState, bdd.SmartContractUID, bdd.Signature);
                        //if (sigCheck == false)
                        //{
                        //    return result; //fail
                        //}

                        var beaconDatas = BeaconData.GetBeacon();
                        var beaconData = BeaconData.GetBeaconData();
                        foreach (var fileName in bdd.Assets)
                        {
                            if (beaconData != null)
                            {
                                var bdCheck = beaconData.Where(x => x.SmartContractUID == bdd.SmartContractUID && x.AssetName == fileName && (x.NextAssetOwnerAddress == scState.OwnerAddress || x.NextAssetOwnerAddress == scState.NextOwner)).FirstOrDefault();
                                if (bdCheck != null)
                                {
                                    if (beaconDatas != null)
                                    {
                                        bdCheck.DownloadIPAddress = peerIP;
                                        bdCheck.NextOwnerReference = bdd.Reference;
                                        beaconDatas.UpdateSafe(bdCheck);
                                    }
                                    else
                                    {
                                        return result;//fail
                                    }
                                }
                                else
                                {
                                    return result; //fail
                                }
                            }
                            else
                            {
                                return result; //fail
                            }

                            result = true; //success
                            //need to then call out to origin to process download
                            var beaconDataRec = beaconData.Where(x => x.SmartContractUID == bdd.SmartContractUID && x.AssetName == fileName && (x.NextAssetOwnerAddress == scState.OwnerAddress || x.NextAssetOwnerAddress == scState.NextOwner)).FirstOrDefault();
                            if(beaconDataRec != null)
                            {
                                var remoteUser = beaconPool.Where(x => x.Reference == beaconDataRec.Reference).FirstOrDefault();
                                string[] senddata = { beaconDataRec.SmartContractUID, beaconDataRec.AssetName };
                                var sendJson = JsonConvert.SerializeObject(senddata);
                                if(remoteUser != null)
                                {
                                    if(beaconDataRec.IsReady != true)
                                    {
                                        await SendMessageClient(remoteUser.ConnectionId, "send", sendJson);
                                    }
                                }
                            }
                            
                        }

                    }
                }
                catch (Exception ex)
                {
                    result = false; //just in case setting this to false
                    ErrorLogUtility.LogError($"Error Creating BeaconData. Error Msg: {ex.ToString()}", "P2PServer.ReceiveUploadRequest()");
                }

                return result;
            //});
        }

        #endregion

        #region Beacon Receive Upload Request - The sender of the NFT Asset
        public async Task<bool> ReceiveUploadRequest(BeaconData.BeaconSendData bsd)
        {
            //return await SignalRQueue(Context, 1024, async () =>
            //{
            bool result = false;
            var peerIP = GetIP(Context);
            SCLogUtility.Log($"Receive Upload IP : {peerIP}", "CustomLogging-2-ReceiveUploadRequest");
            try
            {
                SCLogUtility.Log($"Wal Version Good and Beacon Ref Good.", "CustomLogging-2-ReceiveUploadRequest");
                var beaconAuth = await BeaconService.BeaconAuthorization(bsd.CurrentOwnerAddress);
                if (!beaconAuth.Item1)
                    return result;

                SCLogUtility.Log($"Beacon Auth Good.", "CustomLogging-2-ReceiveUploadRequest");
                if (bsd != null)
                {
                    SCLogUtility.Log($"BSD was not null", "CustomLogging-2-ReceiveUploadRequest");
                    var scState = SmartContractStateTrei.GetSmartContractState(bsd.SmartContractUID);
                    if (scState == null)
                    {
                        SCLogUtility.Log($"SC State was null", "CustomLogging-2-ReceiveUploadRequest");
                        return result;
                    }

                    var sigCheck = SignatureService.VerifySignature(scState.OwnerAddress, bsd.SmartContractUID, bsd.Signature);
                    if (sigCheck == false)
                    {
                        SCLogUtility.Log($"Bad Signature. Owner | {scState.OwnerAddress} | SCUID: {bsd.SmartContractUID} | Signature: {bsd.Signature}", "CustomLogging-2-ReceiveUploadRequest");
                        return result;
                    }

                    var beaconData = BeaconData.GetBeaconData();
                    foreach (var fileName in bsd.Assets)
                    {
                        if (beaconData == null)
                        {
                            SCLogUtility.Log($"Beacon data was  not null", "CustomLogging-2-ReceiveUploadRequest");
                            var bd = new BeaconData
                            {
                                CurrentAssetOwnerAddress = bsd.CurrentOwnerAddress,
                                AssetExpireDate = TimeUtil.GetTimeForBeaconRelease(),
                                AssetReceiveDate = TimeUtil.GetTime(),
                                AssetName = fileName,
                                IPAdress = peerIP,
                                NextAssetOwnerAddress = bsd.NextAssetOwnerAddress,
                                SmartContractUID = bsd.SmartContractUID,
                                IsReady = false,
                                MD5List = bsd.MD5List,
                                Reference = bsd.Reference,
                                DeleteAfterDownload = beaconAuth.Item2
                            };

                            var beaconResult = BeaconData.SaveBeaconData(bd);
                            result = true;
                        }
                        else
                        {
                            SCLogUtility.Log($"Beacon data was null", "CustomLogging-2-ReceiveUploadRequest");
                            var bdCheck = beaconData.Where(x => x.SmartContractUID == bsd.SmartContractUID && 
                            x.AssetName == fileName && 
                            x.IPAdress == peerIP && 
                            x.IsReady != true && 
                            x.NextAssetOwnerAddress == bsd.NextAssetOwnerAddress).FirstOrDefault();

                            if (bdCheck == null)
                            {
                                SCLogUtility.Log($"BDcheck was not null", "CustomLogging-2-ReceiveUploadRequest");
                                var bd = new BeaconData
                                {
                                    CurrentAssetOwnerAddress = bsd.CurrentOwnerAddress,
                                    AssetExpireDate = TimeUtil.GetTimeForBeaconRelease(),
                                    AssetReceiveDate = TimeUtil.GetTime(),
                                    AssetName = fileName,
                                    IPAdress = peerIP,
                                    NextAssetOwnerAddress = bsd.NextAssetOwnerAddress,
                                    SmartContractUID = bsd.SmartContractUID,
                                    IsReady = false,
                                    MD5List = bsd.MD5List,
                                    Reference = bsd.Reference
                                };

                                var beaconResult = BeaconData.SaveBeaconData(bd);
                                result = true;
                            }
                            else
                            {
                                SCLogUtility.Log($"Beacon request failed to insert for: {bsd.SmartContractUID}. From: {bsd.CurrentOwnerAddress}. To: {bsd.NextAssetOwnerAddress}. PeerIP: {peerIP}", "CustomLogging-2-ReceiveUploadRequest");
                                ErrorLogUtility.LogError($"Beacon request failed to insert for: {bsd.SmartContractUID}. From: {bsd.CurrentOwnerAddress}. To: {bsd.NextAssetOwnerAddress}. PeerIP: {peerIP}", "P2PBeaconService.ReceiveUploadRequest()");
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Error Receive Upload Request. Error Msg: {ex.ToString()}", "CustomLogging-2-ReceiveUploadRequest");
                ErrorLogUtility.LogError($"Error Receive Upload Request. Error Msg: {ex.ToString()}", "P2PServer.ReceiveUploadRequest()");
                return false;
            }

            SCLogUtility.Log($"Result : {result}", "CustomLogging-2-ReceiveUploadRequest");
            return result;
            //});
        }

        #endregion

        #region Beacon Data IsReady Flag Set - Sets IsReady to true if file is present
        public async Task<bool> BeaconDataIsReady(string data)
        {
            var peerIP = GetIP(Context);
            bool output = false;
            try
            {
                var beaconPool = Globals.BeaconPool.Values.ToList();
                var payload = JsonConvert.DeserializeObject<string[]>(data);
                if (payload != null)
                {
                    var scUID = payload[0];
                    var assetName = payload[1];

                    var beacon = BeaconData.GetBeacon();
                    if (beacon != null)
                    {
                        var beaconData = beacon.FindOne(x => x.SmartContractUID == scUID && x.AssetName == assetName && x.IPAdress == peerIP && x.IsReady == false);
                        if (beaconData != null)
                        {
                            beaconData.IsReady = true;
                            beacon.UpdateSafe(beaconData);
                            output = true;

                            //send message to receiver.
                            var receiverRef = beaconData.NextOwnerReference;
                            var remoteUser = beaconPool.Where(x => x.Reference == receiverRef).FirstOrDefault();
                            if (remoteUser != null)
                            {
                                string[] senddata = { beaconData.SmartContractUID, beaconData.AssetName };
                                var sendJson = JsonConvert.SerializeObject(senddata);
                                await SendMessageClient(remoteUser.ConnectionId, "receive", sendJson);
                                SCLogUtility.Log($"Receive request was sent to: {remoteUser.IpAddress}. Information JSON sent: {sendJson}", "P2PBeaconServer.BeaconDataIsReady()");
                            }
                            else
                            {
                                SCLogUtility.Log($"Remote user was null. Ref: {receiverRef}", "P2PBeaconServer.BeaconDataIsReady()");
                            }
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                SCLogUtility.Log($"Error occurred when sending receive. Error: {ex.ToString()}", "P2PBeaconServer.BeaconDataIsReady()");
            }

            return output;
        }

        #endregion

        #region Beacon Is File Ready check for receiver
        public async Task<bool> BeaconIsFileReady(string data)
        {
            var peerIP = GetIP(Context);

            bool result = false;
            var payload = JsonConvert.DeserializeObject<string[]>(data);
            if (payload != null)
            {
                var scUID = payload[0];
                var assetName = payload[1];
                var beacon = BeaconData.GetBeacon();
                if (beacon != null)
                {
                    var beaconData = beacon.FindOne(x => x.SmartContractUID == scUID && x.AssetName == assetName && x.DownloadIPAddress == peerIP);
                    if (beaconData != null)
                    {
                        if (beaconData.IsReady)
                        {
                            result = true;
                            return result;
                        }
                        else
                        {
                            //attempt to call to person to get file.

                            if (Globals.BeaconPool.TryGetFromKey2(beaconData.Reference, out var remoteUser))
                            {
                                string[] senddata = { beaconData.SmartContractUID, beaconData.AssetName };
                                var sendJson = JsonConvert.SerializeObject(senddata);
                                if (remoteUser.Value != null)
                                {
                                    await SendMessageClient(remoteUser.Value.ConnectionId, "send", sendJson);
                                }
                            }
                        }
                            
                    }
                }
            }

            return result;
        }

        #endregion

        #region Beacon File Is Downloaded set
        public async Task<bool> BeaconFileIsDownloaded(string data)
        {
            var peerIP = GetIP(Context);

            bool result = false;
            var payload = JsonConvert.DeserializeObject<string[]>(data);
            if (payload != null)
            {
                var scUID = payload[0];
                var assetName = payload[1];
                var beacon = BeaconData.GetBeacon();
                if (beacon != null)
                {
                    var beaconData = beacon.FindOne(x => x.SmartContractUID == scUID && x.AssetName == assetName && x.DownloadIPAddress == peerIP);
                    if (beaconData != null)
                    {
                        beaconData.IsDownloaded = true;
                        beacon.UpdateSafe(beaconData);
                        if(beaconData.DeleteAfterDownload)
                            BeaconService.DeleteFile(beaconData.AssetName);
                    }

                    //remove all completed beacon request
                    beacon.DeleteManySafe(x => x.IsDownloaded == true);
                }
            }



            return result;
        }

        #endregion

        #region SignalR DOS Protection

        public static async Task<T> SignalRQueue<T>(HubCallerContext context, int sizeCost, Func<Task<T>> func)
        {
            var now = TimeUtil.GetMillisecondTime();
            var ipAddress = GetIP(context);
            if (Globals.MessageLocks.TryGetValue(ipAddress, out var Lock))
            {
                var prev = Interlocked.Exchange(ref Lock.LastRequestTime, now);
                if (Lock.ConnectionCount > 20)
                    BanService.BanPeer(ipAddress, "Connection count exceeded limit", "P2PBeaconServer.SignalRQueue()");

                if (Lock.BufferCost + sizeCost > 5000000)
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

                return await SignalRQueue(Lock, sizeCost, func);
            }
            else
            {
                var newLock = new MessageLock { BufferCost = sizeCost, LastRequestTime = now, DelayLevel = 0, ConnectionCount = 0 };
                if (Globals.MessageLocks.TryAdd(ipAddress, newLock))
                    return await SignalRQueue(newLock, sizeCost, func);
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

                    return await SignalRQueue(Lock, sizeCost, func);
                }
            }
        }

        private static async Task<T> SignalRQueue<T>(MessageLock Lock, int sizeCost, Func<Task<T>> func)
        {
            Interlocked.Increment(ref Lock.ConnectionCount);
            Interlocked.Add(ref Lock.BufferCost, sizeCost);
            T Result = default;
            try
            {
                await Lock.Semaphore.WaitAsync();
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
                try { Lock.Semaphore.Release(); } catch { }
            }

            Interlocked.Decrement(ref Lock.ConnectionCount);
            Interlocked.Add(ref Lock.BufferCost, -sizeCost);
            return Result;
        }

        public static async Task SignalRQueue(HubCallerContext context, int sizeCost, Func<Task> func)
        {
            var commandWrap = async () =>
            {
                await func();
                return 1;
            };
            await SignalRQueue(context, sizeCost, commandWrap);
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
    }
}
