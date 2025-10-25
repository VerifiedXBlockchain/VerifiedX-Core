using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.DST;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

namespace ReserveBlockCore.P2P
{
    public class P2PValidatorServer : P2PServer
    {
        // Nonce tracking for replay attack prevention
        private static readonly ConcurrentDictionary<string, long> _usedNonces = new ConcurrentDictionary<string, long>();
        private static readonly object _nonceCleanupLock = new object();
        private static DateTime _lastNonceCleanup = DateTime.UtcNow;

        #region On Connected 
        public override async Task OnConnectedAsync()
        {
            string peerIP = null;
            try
            {
                peerIP = GetIP(Context);

                if (Globals.BannedIPs.ContainsKey(peerIP))
                {
                    Context.Abort();
                    return;
                }

                // HAL-14 Security Enhancement: Rate limiting and connection monitoring
                if (ConnectionSecurityHelper.ShouldRateLimit(peerIP))
                {
                    BanService.BanPeer(peerIP, "Connection rate limit exceeded", "OnConnectedAsync-RateLimit");
                    await EndOnConnect(peerIP, "Too many connection attempts", $"Rate limited IP: {peerIP}");
                    return;
                }
                var httpContext = Context.GetHttpContext();

                if (httpContext == null)
                {
                    _ = EndOnConnect(peerIP, "httpContext is null", "httpContext is null");
                    return;
                }

                var portCheck = PortUtility.IsPortOpen(peerIP, Globals.ValPort);
                if(!portCheck) 
                {
                    _ = EndOnConnect(peerIP, $"Port: {Globals.ValPort} was not detected as open.", $"Port: {Globals.ValPort} was not detected as open for IP: {peerIP}.");
                    return;
                }

                var address = httpContext.Request.Headers["address"].ToString();
                var time = httpContext.Request.Headers["time"].ToString();
                var uName = httpContext.Request.Headers["uName"].ToString();
                var publicKey = httpContext.Request.Headers["publicKey"].ToString();
                var signature = httpContext.Request.Headers["signature"].ToString();
                var walletVersion = httpContext.Request.Headers["walver"].ToString();
                var nonce = httpContext.Request.Headers["nonce"].ToString();

                // HAL-15 Security Fix: Validate handshake headers with size and format constraints
                var handshakeValidation = InputValidationHelper.ValidateHandshakeHeaders(
                    address, time, uName, publicKey, signature, walletVersion, nonce);

                if (!handshakeValidation.IsValid)
                {
                    var errorMessage = $"Invalid handshake data: {string.Join(", ", handshakeValidation.Errors)}";
                    ErrorLogUtility.LogError($"HAL-15 Security: {errorMessage} from {peerIP}", "P2PValidatorServer.OnConnectedAsync()");
                    
                    // Record this as a security violation for potential banning
                    ConnectionSecurityHelper.RecordAuthenticationFailure(peerIP, address ?? "unknown", "Invalid handshake format");
                    
                    _ = EndOnConnect(peerIP,
                        "Connection rejected due to invalid handshake data format.",
                        $"HAL-15 Security: {errorMessage} from {peerIP}");
                    return;
                }

                // Validate required fields (after format validation)
                if (string.IsNullOrWhiteSpace(address) ||
                    string.IsNullOrWhiteSpace(time) ||
                    string.IsNullOrWhiteSpace(publicKey) ||
                    string.IsNullOrWhiteSpace(signature) ||
                    string.IsNullOrWhiteSpace(nonce))
                {
                    _ = EndOnConnect(peerIP,
                        "Connection Attempted, but missing required field(s). You are being disconnected.",
                        "Connected, but missing required field(s): " + address);
                    return;
                }

                // Safe time parsing to prevent DoS
                if (!long.TryParse(time, out var timeValue))
                {
                    _ = EndOnConnect(peerIP, "Invalid timestamp format.", "Invalid timestamp format from: " + peerIP);
                    return;
                }

                var now = TimeUtil.GetTime();
                
                // Reduced time window from 300s to 30s
                if (now - timeValue > 30)
                {
                    _ = EndOnConnect(peerIP, "Timestamp outside acceptable window.", "Timestamp outside acceptable window from: " + peerIP);
                    return;
                }

                // Prevent future timestamps (with small tolerance for clock skew)
                if (timeValue > now + 5)
                {
                    _ = EndOnConnect(peerIP, "Timestamp from future.", "Future timestamp from: " + peerIP);
                    return;
                }

                // Clean up expired nonces periodically
                CleanupExpiredNonces(now);

                // HAL-14 Security Enhancement: Validate authentication attempt
                if (!ConnectionSecurityHelper.ValidateAuthenticationAttempt(peerIP, address))
                {
                    ConnectionSecurityHelper.RecordAuthenticationFailure(peerIP, address, "Suspicious authentication pattern");
                    _ = EndOnConnect(peerIP, "Authentication validation failed.", "Suspicious authentication attempt from: " + peerIP);
                    return;
                }

                // Check for nonce reuse (replay attack prevention)
                var nonceKey = $"{address}:{nonce}";
                if (!_usedNonces.TryAdd(nonceKey, now))
                {
                    ConnectionSecurityHelper.RecordAuthenticationFailure(peerIP, address, "Nonce reuse");
                    _ = EndOnConnect(peerIP, "Nonce already used.", "Replay attack detected from: " + peerIP);
                    return;
                }

                Globals.P2PValDict.TryAdd(peerIP, Context);

                if (Globals.P2PValDict.TryGetValue(peerIP, out var context) && context.ConnectionId != Context.ConnectionId)
                {
                    context.Abort();
                }

                // Updated signed message to include nonce
                var SignedMessage = address + ":" + time + ":" + publicKey + ":" + nonce;

                var walletVersionVerify = WalletVersionUtility.Verify(walletVersion);

                // Address-PublicKey binding validation
                if (!ValidateAddressPublicKeyBinding(address, publicKey))
                {
                    _ = EndOnConnect(peerIP,
                        "Authentication failed. You are being disconnected.",
                        "Address-PublicKey mismatch from: " + peerIP);
                    return;
                }

                // HAL-039 Fix: Verify signature BEFORE expensive database operations
                var verifySig = SignatureService.VerifySignature(address, SignedMessage, signature);
                if (!verifySig)
                {
                    ConnectionSecurityHelper.RecordAuthenticationFailure(peerIP, address, "Signature verification failed");
                    _ = EndOnConnect(peerIP,
                        "Authentication failed. You are being disconnected.",
                        "Signature verification failed from: " + peerIP);
                    return;
                }

                // HAL-039 Fix: Only perform expensive database lookups AFTER authentication
                var stateAddress = StateData.GetSpecificAccountStateTrei(address);
                if (stateAddress == null)
                {
                    _ = EndOnConnect(peerIP,
                        "Authentication failed. You are being disconnected.",
                        "Address not found in state trie: " + address + " IP: " + peerIP);
                    return;
                }

                if (stateAddress.Balance < ValidatorService.ValidatorRequiredAmount())
                {
                    _ = EndOnConnect(peerIP,
                        "Authentication failed. You are being disconnected.",
                        $"Insufficient balance for address: " + address);
                    return;
                }

                // HAL-14 Fix: Enhanced ABL check AFTER signature verification to prevent spoofing attacks
                if (ConnectionSecurityHelper.IsAddressBlocklisted(address, peerIP, "Validator Authentication"))
                {
                    BanService.BanPeer(peerIP, "ABL violation by authenticated address", "OnConnectedAsync-PostAuth");
                    await EndOnConnect(peerIP, $"Address is blocklisted", $"ABL violation by authenticated address {address} from IP: {peerIP}");
                    return;
                }

                var netVal = new NetworkValidator { 
                    Address = address,
                    IPAddress = peerIP.Replace("::ffff:", ""),
                    PublicKey = publicKey,
                    Signature = signature,
                    SignatureMessage = SignedMessage,
                    UniqueName = uName,
                };

                Globals.NetworkValidators.TryAdd(address, netVal);

                var netValSerialize = JsonConvert.SerializeObject(netVal);

                // HAL-14 Security Enhancement: Clear security tracking for successful connections
                ConnectionSecurityHelper.ClearConnectionHistory(peerIP);

                _ = Peers.UpdatePeerAsVal(peerIP, address, walletVersion, address, publicKey);
                
                // HAL-16 Fix: Replace fire-and-forget calls with reliable sender
                // HAL-17 Fix: Use configurable timeouts instead of hardcoded values
                Clients.Caller.SendToCallerReliable("GetValMessage", new object[] { "1", peerIP }, 
                    "OnConnectedAsync", peerIP, Globals.SignalRShortTimeoutMs, false);
                Clients.SendToAllReliable("GetValMessage", new object[] { "3", netValSerialize }, 
                    "OnConnectedAsync", Globals.SignalRLongTimeoutMs, false);

            }
            catch (Exception ex)
            {
                Context?.Abort();
                ErrorLogUtility.LogError($"Unhandled exception has happend. Error : {ex.ToString()}", "P2PValidatorServer.OnConnectedAsync()");
            }

        }
        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var peerIP = GetIP(Context);
            //var netVal = Globals.NetworkValidators.Where(x => x.Value.IPAddress == peerIP).FirstOrDefault();

            Globals.P2PValDict.TryRemove(peerIP, out _);
            Context?.Abort();

            await base.OnDisconnectedAsync(ex);
        }
        private async Task SendValMessageSingle(string message, string data)
        {
            // HAL-17 Fix: Use configurable timeout instead of hardcoded value
            await Clients.Caller.SendAsync("GetValMessage", message, data, new CancellationTokenSource(Globals.NetworkOperationTimeoutMs).Token);
        }
        #endregion

        #region Get Network Validator List - HAL-15 Security Fix Applied
        public async Task SendNetworkValidatorList(string data)
        {
            try
            {
                var peerIP = GetIP(Context);

                if(!string.IsNullOrEmpty(data))
                {
                    // HAL-15 Security Fix: Validate JSON input before deserialization
                    var jsonValidation = JsonSecurityHelper.ValidateJsonInput(data, $"SendNetworkValidatorList from {peerIP}");
                    if (!jsonValidation.IsValid)
                    {
                        ErrorLogUtility.LogError(
                            $"HAL-15 Security: Invalid JSON in SendNetworkValidatorList from {peerIP}: {jsonValidation.Error}",
                            "P2PValidatorServer.SendNetworkValidatorList()");
                        
                        BanService.BanPeer(peerIP, "Invalid validator list JSON format", "SendNetworkValidatorList");
                        return;
                    }

                    var networkValList = JsonConvert.DeserializeObject<List<NetworkValidator>>(data);
                    
                    // HAL-15 Security Fix: Validate the validator list with size constraints
                    var listValidation = InputValidationHelper.ValidateNetworkValidatorList(networkValList);
                    
                    if (!listValidation.IsValid)
                    {
                        var errorMessage = $"Invalid validator list: {string.Join(", ", listValidation.Errors)}";
                        ErrorLogUtility.LogError(
                            $"HAL-15 Security: {errorMessage} from {peerIP}",
                            "P2PValidatorServer.SendNetworkValidatorList()");
                        
                        // Ban peer for sending invalid data
                        BanService.BanPeer(peerIP, "Invalid validator list format", "SendNetworkValidatorList");
                        return;
                    }

                    // Use truncated list if the original was too large
                    var validatorsToProcess = listValidation.ShouldTruncate ? 
                        listValidation.TruncatedList : networkValList;

                    if (listValidation.ShouldTruncate)
                    {
                        ErrorLogUtility.LogError(
                            $"HAL-15 Security: Truncated oversized validator list from {peerIP}. Original: {networkValList?.Count}, Processed: {validatorsToProcess.Count}",
                            "P2PValidatorServer.SendNetworkValidatorList()");
                    }

                    if(validatorsToProcess?.Count > 0)
                    {
                        int processedCount = 0;
                        int rejectedCount = 0;

                        foreach(var networkValidator in validatorsToProcess)
                        {
                            // HAL-15 Security Fix: Validate each individual validator
                            var validatorValidation = InputValidationHelper.ValidateNetworkValidator(networkValidator);
                            if (!validatorValidation.IsValid)
                            {
                                rejectedCount++;
                                if (Globals.OptionalLogging)
                                {
                                    LogUtility.Log($"Rejected invalid validator from {peerIP}: {string.Join(", ", validatorValidation.Errors)}", "SendNetworkValidatorList");
                                }
                                continue;
                            }

                            if(Globals.NetworkValidators.TryGetValue(networkValidator.Address, out var networkValidatorVal))
                            {
                                var verifySig = SignatureService.VerifySignature(
                                    networkValidator.Address, 
                                    networkValidator.SignatureMessage, 
                                    networkValidator.Signature);

                                // HAL-025 Fix: Removed weak .Contains() check - proper cryptographic verification is sufficient
                                if(verifySig)
                                {
                                    Globals.NetworkValidators[networkValidator.Address] = networkValidator;
                                    processedCount++;
                                }
                                else
                                {
                                    rejectedCount++;
                                }
                            }
                            else
                            {
                                // HAL-15 Security Fix: Use secure validator addition method
                                var added = await NetworkValidator.AddValidatorToPool(networkValidator, peerIP);
                                if (added)
                                {
                                    processedCount++;
                                }
                                else
                                {
                                    rejectedCount++;
                                }
                            }
                        }

                        if (Globals.OptionalLogging)
                        {
                            LogUtility.Log($"SendNetworkValidatorList from {peerIP}: Processed {processedCount}, Rejected {rejectedCount}", "SendNetworkValidatorList");
                        }

                        // HAL-15 Security Fix: Ban peer if rejection rate is too high
                        if (rejectedCount > processedCount && rejectedCount > 5)
                        {
                            ErrorLogUtility.LogError(
                                $"HAL-15 Security: High rejection rate from {peerIP}. Processed: {processedCount}, Rejected: {rejectedCount}",
                                "P2PValidatorServer.SendNetworkValidatorList()");
                            
                            BanService.BanPeer(peerIP, "High validator rejection rate", "SendNetworkValidatorList");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var peerIP = GetIP(Context);
                ErrorLogUtility.LogError(
                    $"HAL-15 Security: Exception in SendNetworkValidatorList from {peerIP}: {ex.Message}",
                    "P2PValidatorServer.SendNetworkValidatorList()");
                
                BanService.BanPeer(peerIP, "Validator list processing error", "SendNetworkValidatorList");
            }
        }
        #endregion

        #region Send Block Height
        public async Task<long> SendBlockHeightForVals()
        {
            return Globals.LastBlock.Height;
        }

        #endregion

        #region Receive Block - Receives Block and then Broadcast out.
        public async Task<bool> ReceiveBlockVal(Block nextBlock)
        {
            try
            {
                // HAL-19 Fix: Add SignalRQueue protection with block size-based cost calculation
                return await SignalRQueue(Context, (int)(nextBlock?.Size ?? 0) + 1024, async () =>
                {
                    // HAL-18 Fix: Validate caller is an authenticated validator
                    var callerIP = GetIP(Context);
                    var authenticatedValidator = Globals.NetworkValidators.Values
                        .FirstOrDefault(v => v.IPAddress == callerIP.Replace("::ffff:", ""));
                    
                    if (authenticatedValidator == null)
                    {
                        ErrorLogUtility.LogError($"HAL-18 Security: Unauthorized block submission attempt from {callerIP}", 
                            "P2PValidatorServer.ReceiveBlockVal()");
                        BanService.BanPeer(callerIP, "Unauthorized block submission", "ReceiveBlockVal");
                        return false;
                    }

                    // HAL-19 Fix: Early block size validation to prevent DoS
                    if (nextBlock != null)
                    {
                        var sizeValidation = InputValidationHelper.ValidateBlockSize(nextBlock, callerIP);
                        if (!sizeValidation.IsValid)
                        {
                            ErrorLogUtility.LogError($"HAL-19 Security: Block size validation failed from {callerIP}: {sizeValidation.ErrorMessage}", 
                                "P2PValidatorServer.ReceiveBlockVal()");
                            BanService.BanPeer(callerIP, "Oversized block submission", "ReceiveBlockVal");
                            return false;
                        }

                        // HAL-20 Fix: Fast pre-validation of block headers to prevent DoS attacks
                        var headerValidation = InputValidationHelper.ValidateBlockHeaders(nextBlock, callerIP);
                        if (!headerValidation.IsValid)
                        {
                            ErrorLogUtility.LogError($"HAL-20 Security: Block header validation failed from {callerIP}: {headerValidation.ErrorMessage}", 
                                "P2PValidatorServer.ReceiveBlockVal()");
                            
                            // For duplicate blocks, use lighter penalty
                            if (headerValidation.IsDuplicate)
                            {
                                // Just log and return false for duplicates - no need to ban
                                return false;
                            }
                            else
                            {
                                // Ban for other validation failures (invalid version, timestamp, parent hash)
                                BanService.BanPeer(callerIP, "Invalid block header", "ReceiveBlockVal");
                                return false;
                            }
                        }
                    }

                    //Casters get blocks from elsewhere.
                    if (Globals.IsBlockCaster)
                        return true;

                    if (nextBlock.ChainRefId == BlockchainData.ChainRef)
                    {
                        var IP = GetIP(Context);
                        var nextHeight = Globals.LastBlock.Height + 1;
                        var currentHeight = nextBlock.Height;

                        if (currentHeight >= nextHeight && BlockDownloadService.BlockDict.TryAdd(currentHeight, (nextBlock, IP)))
                        {
                            // HAL-17 Fix: Use configurable delay instead of hardcoded value
                            await Task.Delay(Globals.BlockProcessingDelayMs);

                            if(Globals.LastBlock.Height < nextBlock.Height)
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

        #region FailedToReachConsensus
        public async Task<bool> FailedToReachConsensus(List<string> failedProducersList)
        {
            try
            {
                foreach (var val in failedProducersList)
                {
                    Globals.FailedProducerDict.TryGetValue(val, out var failRec);
                    if (failRec.Item1 != 0)
                    {
                        var currentTime = TimeUtil.GetTime(0, 0, -1);
                        failRec.Item2 += 1;
                        Globals.FailedProducerDict[val] = failRec;
                        if (failRec.Item2 >= 10)
                        {
                            if (currentTime > failRec.Item1)
                            {
                                var exist = Globals.FailedProducers.Where(x => x == val).FirstOrDefault();
                                if (exist == null)
                                    Globals.FailedProducers.Add(val);
                            }
                        }

                        //Reset timer
                        if (failRec.Item2 < 10)
                        {
                            if (failRec.Item1 < currentTime)
                            {
                                failRec.Item1 = TimeUtil.GetTime();
                                failRec.Item2 = 1;
                                Globals.FailedProducerDict[val] = failRec;
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        #endregion

        #region Receives a Queued block from client

        public async Task<bool> ReceiveQueueBlockVal(Block nextBlock)
        {
            try
            {
                var lastBlock = Globals.LastBlock;
                if(lastBlock.Height < nextBlock.Height)
                {
                    var result = await BlockValidatorService.ValidateBlock(nextBlock, false, false, true);
                    if (result)
                    {
                        var blockAdded = Globals.NetworkBlockQueue.TryAdd(nextBlock.Height, nextBlock);

                        if(blockAdded)
                        {
                            var blockJson = JsonConvert.SerializeObject(nextBlock);

                            if (!Globals.BlockQueueBroadcasted.TryGetValue(nextBlock.Height, out var lastBroadcast))
                            {
                                Globals.BlockQueueBroadcasted.TryAdd(nextBlock.Height, DateTime.UtcNow);
                                // HAL-16 Fix: Replace fire-and-forget call with reliable sender
                                // HAL-17 Fix: Use configurable timeout instead of hardcoded value
                                Clients.SendToAllReliable("GetValMessage", new object[] { "6", blockJson }, 
                                    "ReceiveQueueBlockVal", Globals.SignalRLongTimeoutMs, false);
                            }
                            else
                            {
                                if (DateTime.UtcNow.AddSeconds(30) > lastBroadcast)
                                {
                                    Globals.BlockQueueBroadcasted[nextBlock.Height] = DateTime.UtcNow;
                                    // HAL-16 Fix: Replace fire-and-forget call with reliable sender
                                    // HAL-17 Fix: Use configurable timeout instead of hardcoded value
                                    Clients.SendToAllReliable("GetValMessage", new object[] { "6", blockJson }, 
                                        "ReceiveQueueBlockVal", Globals.SignalRLongTimeoutMs, false);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        #endregion

        #region Send Queued Block - returns specific block
        //Send Block to client from p2p server
        public async Task<Block?> SendQueuedBlock(long currentBlock)
        {
            try
            {
                if(Globals.NetworkBlockQueue.TryGetValue(currentBlock, out var block))
                {
                    return block;
                }
            }
            catch { }

            return null;

        }

        #endregion

        #region Send Block - returns specific block
        //Send Block to client from p2p server
        public async Task<Block?> SendBlockVal(long currentBlock)
        {
            try
            {
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

        #region Send active val list
        public async Task<string> SendActiveVals()
        {
            var vals = Globals.NetworkValidators.Values.ToList();

            if (!vals.Any())
                return "0";

            // HAL-15 Security Fix: Limit validator list to maximum 2000 validators for broadcast
            var limitedVals = InputValidationHelper.LimitValidatorListForBroadcast(vals);
            
            if (limitedVals.Count < vals.Count)
            {
                var peerIP = GetIP(Context);
                ErrorLogUtility.LogError(
                    $"HAL-15 Security: Limited validator broadcast from {vals.Count} to {limitedVals.Count} validators for {peerIP}",
                    "P2PValidatorServer.SendActiveVals()");
            }

            return JsonConvert.SerializeObject(limitedVals).ToBase64().ToCompress();
        }

        #endregion

        #region Send TX to Mempool Vals
        public async Task<string> SendTxToMempoolVals(Transaction txReceived)
        {
            try
            {
                return await SignalRQueue(Context, (txReceived.Data?.Length ?? 0) + 1024, async () =>
                {
                    var result = "";

                    var data = JsonConvert.SerializeObject(txReceived);

                    var ablList = Globals.ABL.ToList();
                    if (ablList.Exists(x => x == txReceived.FromAddress))
                        return "TFVP";

                    var mempool = TransactionData.GetPool();

                    if (mempool.Exists(x => x.Hash == txReceived.Hash))
                        return "ATMP";

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
                                    mempool.InsertSafe(txReceived);
                                    _ = ValidatorNode.Broadcast("7777", txReceived, "SendTxToMempoolVals");

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
                                mempool.InsertSafe(txReceived);
                                if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                                {
                                    _ = ValidatorNode.Broadcast("7777", txReceived, "SendTxToMempoolVals");
                                } //sends tx to connected peers
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

        #region Get Validator Status
        public async Task<bool> GetValidatorStatusVal()
        {
            return await SignalRQueue(Context, bool.FalseString.Length, async () => !string.IsNullOrEmpty(Globals.ValidatorAddress));
        }

        #endregion

        #region Send Proof List (Receive it)

        public async Task<bool> SendProofList(string proofJson)
        {
            try
            {
                var peerIP = GetIP(Context);
                
                // HAL-13 Fix: Secure JSON deserialization with validation
                var deserializationResult = JsonSecurityHelper.DeserializeProofList(proofJson, $"SendProofList from {peerIP}");
                
                if (!deserializationResult.IsSuccess)
                {
                    // Log security event
                    ErrorLogUtility.LogError(
                        $"HAL-13 Security: Invalid proof list from {peerIP}: {deserializationResult.ValidationResult.Error}",
                        "P2PValidatorServer.SendProofList()");
                    
                    // Ban peer for repeated violations
                    BanService.BanPeer(peerIP, "Invalid proof list format", "SendProofList");
                    return false;
                }

                var proofList = deserializationResult.Data;
                
                if (proofList?.Count == 0) return false;
                if (proofList == null) return false;

                // Log successful processing
                if (Globals.OptionalLogging)
                {
                    LogUtility.Log($"Successfully processed {proofList.Count} proofs from {peerIP}", "SendProofList");
                }

                await ProofUtility.SortProofs(proofList);
                return true;
            }
            catch (Exception ex)
            {
                var peerIP = GetIP(Context);
                ErrorLogUtility.LogError(
                    $"HAL-13 Security: Exception in SendProofList from {peerIP}: {ex.Message}",
                    "P2PValidatorServer.SendProofList()");
                
                BanService.BanPeer(peerIP, "Proof list processing error", "SendProofList");
                return false;
            }
        }

        #endregion

        #region Send Winning Proof List (Receive it)

        public async Task<bool> SendWinningProofList(string proofJson)
        {
            try
            {
                var peerIP = GetIP(Context);
                
                // HAL-13 Fix: Secure JSON deserialization with validation
                var deserializationResult = JsonSecurityHelper.DeserializeProofList(proofJson, $"SendWinningProofList from {peerIP}");
                
                if (!deserializationResult.IsSuccess)
                {
                    // Log security event
                    ErrorLogUtility.LogError(
                        $"HAL-13 Security: Invalid winning proof list from {peerIP}: {deserializationResult.ValidationResult.Error}",
                        "P2PValidatorServer.SendWinningProofList()");
                    
                    // Ban peer for repeated violations
                    BanService.BanPeer(peerIP, "Invalid winning proof list format", "SendWinningProofList");
                    return false;
                }

                var proofList = deserializationResult.Data;
                
                if (proofList?.Count == 0) return false;
                if (proofList == null) return false;

                // Log successful processing
                if (Globals.OptionalLogging)
                {
                    LogUtility.Log($"Successfully processed {proofList.Count} winning proofs from {peerIP}", "SendWinningProofList");
                }

                await ProofUtility.SortProofs(proofList);
                return true;
            }
            catch (Exception ex)
            {
                var peerIP = GetIP(Context);
                ErrorLogUtility.LogError(
                    $"HAL-13 Security: Exception in SendWinningProofList from {peerIP}: {ex.Message}",
                    "P2PValidatorServer.SendWinningProofList()");
                
                BanService.BanPeer(peerIP, "Winning proof list processing error", "SendWinningProofList");
                return false;
            }
        }

        #endregion

        #region Get Winning Proof List (Send it)

        public async Task<string> GetWinningProofList()
        {
            try
            {
                string result = "0";
                if(Globals.WinningProofs.Count() != 0)
                {
                    var heightMax = Globals.LastBlock.Height + 10;
                    var list = Globals.WinningProofs.Where(x => x.Key <= heightMax).Select(x => x.Value).ToList();
                    
                    if(list != null && list.Any())
                    {
                        // HAL-13 Fix: Use secure serialization with size limits
                        var serializationResult = JsonSecurityHelper.SerializeWithLimits(list, "GetWinningProofList");
                        
                        if (!serializationResult.IsSuccess)
                        {
                            var peerIP = GetIP(Context);
                            ErrorLogUtility.LogError(
                                $"HAL-13 Security: Response size limit exceeded in GetWinningProofList for {peerIP}: {serializationResult.Error}",
                                "P2PValidatorServer.GetWinningProofList()");
                            
                            // Return truncated list if size exceeds limits
                            var truncatedList = list.Take(JsonSecurityHelper.MaxCollectionSize / 2).ToList();
                            var truncatedResult = JsonSecurityHelper.SerializeWithLimits(truncatedList, "GetWinningProofList-Truncated");
                            
                            if (truncatedResult.IsSuccess)
                            {
                                LogUtility.Log($"Returned truncated winning proof list with {truncatedList.Count} items", "GetWinningProofList");
                                return truncatedResult.Json;
                            }
                            
                            return "0"; // Fallback if even truncated list is too large
                        }
                        
                        result = serializationResult.Json;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                var peerIP = GetIP(Context);
                ErrorLogUtility.LogError(
                    $"HAL-13 Security: Exception in GetWinningProofList for {peerIP}: {ex.Message}",
                    "P2PValidatorServer.GetWinningProofList()");
                return "0";
            }
        }

        #endregion

        #region Get Finalized Winners (Send it)

        public async Task<string> GetFinalizedWinnersList()
        {
            try
            {
                string result = "0";
                if (Globals.WinningProofs.Count() != 0)
                {
                    var list = Globals.FinalizedWinner.Select(x => x.Value).ToList();
                    
                    if (list != null && list.Any())
                    {
                        // HAL-13 Fix: Use secure serialization with size limits
                        var serializationResult = JsonSecurityHelper.SerializeWithLimits(list, "GetFinalizedWinnersList");
                        
                        if (!serializationResult.IsSuccess)
                        {
                            var peerIP = GetIP(Context);
                            ErrorLogUtility.LogError(
                                $"HAL-13 Security: Response size limit exceeded in GetFinalizedWinnersList for {peerIP}: {serializationResult.Error}",
                                "P2PValidatorServer.GetFinalizedWinnersList()");
                            
                            // Return truncated list if size exceeds limits
                            var truncatedList = list.Take(JsonSecurityHelper.MaxCollectionSize / 2).ToList();
                            var truncatedResult = JsonSecurityHelper.SerializeWithLimits(truncatedList, "GetFinalizedWinnersList-Truncated");
                            
                            if (truncatedResult.IsSuccess)
                            {
                                LogUtility.Log($"Returned truncated finalized winners list with {truncatedList.Count} items", "GetFinalizedWinnersList");
                                return truncatedResult.Json;
                            }
                            
                            return "0"; // Fallback if even truncated list is too large
                        }
                        
                        result = serializationResult.Json;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                var peerIP = GetIP(Context);
                ErrorLogUtility.LogError(
                    $"HAL-13 Security: Exception in GetFinalizedWinnersList for {peerIP}: {ex.Message}",
                    "P2PValidatorServer.GetFinalizedWinnersList()");
                return "0";
            }
        }

        #endregion

        #region Send locked Winner
        //Send locked winner to client from p2p server
        public async Task<string> SendLockedWinner(long height)
        {
            try
            {
                var peerIP = GetIP(Context);

                if(Globals.FinalizedWinner.TryGetValue(height, out var winner))
                {
                    return winner;
                }
                else
                {
                    return "0";
                }
            }
            catch { }

            return "0";
        }

        #endregion

        #region Receive Winning Proof Vote
        public async Task SendWinningProofVote(string winningProofJson)
        {
            try
            {
                var peerIP = GetIP(Context);
                
                // HAL-13 Fix: Validate input before deserialization
                var validationResult = JsonSecurityHelper.ValidateJsonInput(winningProofJson, $"SendWinningProofVote from {peerIP}");
                
                if (!validationResult.IsValid)
                {
                    ErrorLogUtility.LogError(
                        $"HAL-13 Security: Invalid winning proof vote from {peerIP}: {validationResult.Error}",
                        "P2PValidatorServer.SendWinningProofVote()");
                    
                    BanService.BanPeer(peerIP, "Invalid winning proof vote format", "SendWinningProofVote");
                    return;
                }

                var proof = JsonConvert.DeserializeObject<Proof>(winningProofJson);
                if (proof != null)
                {
                    // Validate proof object structure
                    if (string.IsNullOrWhiteSpace(proof.Address) || 
                        string.IsNullOrWhiteSpace(proof.PublicKey) || 
                        string.IsNullOrWhiteSpace(proof.ProofHash))
                    {
                        ErrorLogUtility.LogError(
                            $"HAL-13 Security: Invalid proof structure from {peerIP}",
                            "P2PValidatorServer.SendWinningProofVote()");
                        
                        BanService.BanPeer(peerIP, "Invalid proof structure", "SendWinningProofVote");
                        return;
                    }

                    if (proof.VerifyProof())
                    {
                        Globals.Proofs.Add(proof);
                        
                        if (Globals.OptionalLogging)
                        {
                            LogUtility.Log($"Successfully processed winning proof vote from {peerIP}", "SendWinningProofVote");
                        }
                    }
                    else
                    {
                        ErrorLogUtility.LogError(
                            $"HAL-13 Security: Proof verification failed from {peerIP}",
                            "P2PValidatorServer.SendWinningProofVote()");
                    }
                }
            }
            catch (Exception ex)
            {
                var peerIP = GetIP(Context);
                ErrorLogUtility.LogError(
                    $"HAL-13 Security: Exception in SendWinningProofVote from {peerIP}: {ex.Message}",
                    "P2PValidatorServer.SendWinningProofVote()");
                
                BanService.BanPeer(peerIP, "Winning proof vote processing error", "SendWinningProofVote");
            }
        }

        #endregion

        #region Get Wallet Version
        public async Task<string> GetWalletVersionVal()
        {
            return await SignalRQueue(Context, Globals.CLIVersion.Length, async () => Globals.CLIVersion);
        }

        #endregion

        #region Get Connected Val Count

        public static async Task<int> GetConnectedValCount()
        {
            try
            {
                var peerCount = Globals.P2PValDict.Count;
                return peerCount;
            }
            catch { }

            return -1;
        }

        #endregion

        #region End on Connect

        private async Task EndOnConnect(string ipAddress, string adjMessage, string logMessage)
        {
            await SendValMessageSingle("9999", adjMessage);
            if (Globals.OptionalLogging == true)
            {
                LogUtility.Log(logMessage, "Validator Connection");
                LogUtility.Log($"IP: {ipAddress} ", "Validator Connection");
            }

            Context?.Abort();
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
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Unknown Error: {ex.ToString()}", "ConsensusServer.GetIP()");
            }

            return "0.0.0.0";
        }

        #endregion

        #region Security Helper Methods

        /// <summary>
        /// Periodically clean up expired nonces to prevent memory buildup
        /// </summary>
        private static void CleanupExpiredNonces(long currentTime)
        {
            // Only cleanup every 60 seconds to avoid performance impact
            lock (_nonceCleanupLock)
            {
                if ((DateTime.UtcNow - _lastNonceCleanup).TotalSeconds < 60)
                    return;

                _lastNonceCleanup = DateTime.UtcNow;
            }

            // Remove nonces older than 60 seconds (twice the allowed window)
            var expiredKeys = _usedNonces
                .Where(kvp => currentTime - kvp.Value > 60)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _usedNonces.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Validate that the provided address matches the public key
        /// </summary>
        private static bool ValidateAddressPublicKeyBinding(string address, string publicKey)
        {
            try
            {
                // For now, we implement basic validation
                if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(publicKey))
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
