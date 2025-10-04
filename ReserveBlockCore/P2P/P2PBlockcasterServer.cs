using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

namespace ReserveBlockCore.P2P
{
    public class P2PBlockcasterServer : P2PServer
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
                var httpContext = Context.GetHttpContext();

                if (httpContext == null)
                {
                    _ = EndOnConnect(peerIP, "httpContext is null", "httpContext is null");
                    return;
                }

                var portCheck = PortUtility.IsPortOpen(peerIP, Globals.ValPort);
                if (!portCheck)
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

                var ablList = Globals.ABL.ToList();

                if (ablList.Exists(x => x == address))
                {
                    BanService.BanPeer(peerIP, "Request malformed", "OnConnectedAsync");
                    await EndOnConnect(peerIP, $"ABL Detected", $"ABL Detected: {peerIP}.");
                    return;
                }

                // Validate required fields
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

                // Check for nonce reuse (replay attack prevention)
                var nonceKey = $"{address}:{nonce}";
                if (!_usedNonces.TryAdd(nonceKey, now))
                {
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
                        "Address and public key do not match. You are being disconnected.",
                        "Address-PublicKey mismatch from: " + peerIP);
                    return;
                }

                var stateAddress = StateData.GetSpecificAccountStateTrei(address);
                if (stateAddress == null)
                {
                    _ = EndOnConnect(peerIP,
                        "Connection Attempted, But failed to find the address in trie. You are being disconnected.",
                        "Connection Attempted, but missing field Address: " + address + " IP: " + peerIP);
                    return;
                }

                if (stateAddress.Balance < ValidatorService.ValidatorRequiredAmount())
                {
                    _ = EndOnConnect(peerIP,
                        $"Connected, but you do not have the minimum balance of {ValidatorService.ValidatorRequiredAmount()} VFX. You are being disconnected.",
                        $"Connected, but you do not have the minimum balance of {ValidatorService.ValidatorRequiredAmount()} VFX: " + address);
                    return;
                }

                var verifySig = SignatureService.VerifySignature(address, SignedMessage, signature);
                if (!verifySig)
                {
                    _ = EndOnConnect(peerIP,
                        "Connected, but your address signature failed to verify. You are being disconnected.",
                        "Connected, but your address signature failed to verify with Val: " + address);
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

                var netVal = new NetworkValidator
                {
                    Address = address,
                    IPAddress = peerIP.Replace("::ffff:", ""),
                    PublicKey = publicKey,
                    Signature = signature,
                    SignatureMessage = SignedMessage,
                    UniqueName = uName,
                };

                Globals.NetworkValidators.TryAdd(address, netVal);

                var netValSerialize = JsonConvert.SerializeObject(netVal);

                _ = Peers.UpdatePeerAsVal(peerIP, address, walletVersion, address, publicKey);
                _ = Clients.Caller.SendAsync("GetCasterMessage", "1", peerIP, new CancellationTokenSource(2000).Token);
                _ = Clients.All.SendAsync("GetCasterMessage", "3", netValSerialize, new CancellationTokenSource(6000).Token);

            }
            catch (Exception ex)
            {
                Context?.Abort();
                ErrorLogUtility.LogError($"Unhandled exception has happend. Error : {ex.ToString()}", "P2PValidatorServer.OnConnectedAsync()");
            }

        }

        private async Task SendCasterMessageSingle(string message, string data)
        {
            await Clients.Caller.SendAsync("GetCasterMessage", message, data, new CancellationTokenSource(1000).Token);
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

        #region End on Connect
        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var peerIP = GetIP(Context);
            //var netVal = Globals.NetworkValidators.Where(x => x.Value.IPAddress == peerIP).FirstOrDefault();

            Globals.P2PValDict.TryRemove(peerIP, out _);
            Globals.FortisPool.TryRemoveFromKey1(peerIP, out _);
            Context?.Abort();

            await base.OnDisconnectedAsync(ex);
        }

        private async Task EndOnConnect(string ipAddress, string adjMessage, string logMessage)
        {
            //await SendCasterMessageSingle("9999", adjMessage);
            if (Globals.OptionalLogging == true)
            {
                LogUtility.Log(logMessage, "Validator Connection");
                LogUtility.Log($"IP: {ipAddress} ", "Validator Connection");
            }

            Context?.Abort();
        }

        #endregion

        #region Receive Block - Receives Block and then Broadcast out.
        public async Task<bool> ReceiveBlockVal(Block nextBlock)
        {
            try
            {
                //return await SignalRQueue(Context, (int)nextBlock.Size, async () =>
                //{
                if (nextBlock.ChainRefId == BlockchainData.ChainRef)
                {
                    var IP = GetIP(Context);
                    var nextHeight = Globals.LastBlock.Height + 1;
                    var currentHeight = nextBlock.Height;

                    if (currentHeight >= nextHeight && BlockDownloadService.BlockDict.TryAdd(currentHeight, (nextBlock, IP)))
                    {
                        await Task.Delay(2000);

                        if (Globals.LastBlock.Height < nextBlock.Height)
                            await BlockValidatorService.ValidateBlocks();

                        if (nextHeight == currentHeight)
                        {
                            string data = "";
                            data = JsonConvert.SerializeObject(nextBlock);
                            await Clients.All.SendAsync("GetCasterMessage", "blk", data);
                        }

                        if (nextHeight < currentHeight)
                            await BlockDownloadService.GetAllBlocks();

                        return true;
                    }
                }

                return false;
                //});
            }
            catch { }

            return false;
        }

        #endregion

        #region Send Block Height
        public async Task<long> SendBlockHeightForVals()
        {
            return Globals.LastBlock.Height;
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
                // The signature verification later will ensure the address owns the private key
                // This method can be enhanced with more specific address-pubkey validation
                if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(publicKey))
                    return false;

                // Additional validation could include:
                // - Deriving address from public key and comparing
                // - Checking against known address-pubkey mappings
                // For this implementation, we rely on signature verification for binding validation
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
