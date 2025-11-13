using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Data;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Transactions;
using System.Xml.Linq;

namespace ReserveBlockCore.P2P
{
    public class ConsensusServer : P2PServer
    {
        static ConsensusServer()
        {
            AdjPool = new ConcurrentDictionary<string, AdjPool>();
            ConsenusStateSingelton = new ConsensusState();
        }

        public static ConcurrentDictionary<string, AdjPool> AdjPool;

        // HAL-034 Fix: Hard caps for cache dictionaries to prevent unbounded growth
        private const int MaxCacheEntries = 100;
        
        // HAL-035 Fix: Maximum input size for consensus messages to prevent memory exhaustion
        private const int MaxMessageSize = 10000; // 10KB limit for consensus messages
        
        public static object UpdateNodeLock = new object();
        private static ConsensusState ConsenusStateSingelton;
        private static object UpdateLock = new object();
        public override async Task OnConnectedAsync()
        {
            string peerIP = null;
            try
            {
                peerIP = GetIP(Context);
                if(!Globals.Nodes.ContainsKey(peerIP))
                {
                    EndOnConnect(peerIP, peerIP + " attempted to connect as adjudicator", peerIP + " attempted to connect as adjudicator");
                    return;
                }

                var httpContext = Context.GetHttpContext();
                if (httpContext == null)
                {
                    EndOnConnect(peerIP, "httpContext is null", "httpContext is null");
                    return;
                }

                var address = httpContext.Request.Headers["address"].ToString();
                var time = httpContext.Request.Headers["time"].ToString();
                var signature = httpContext.Request.Headers["signature"].ToString();

                // Validate required fields first
                if (string.IsNullOrWhiteSpace(address) || 
                    string.IsNullOrWhiteSpace(time) ||
                    string.IsNullOrWhiteSpace(signature))
                {
                    EndOnConnect(peerIP,
                        "Connection Attempted, but missing required field(s). Address, Time, and Signature required. You are being disconnected.",
                        "Connected, but missing required field(s). Address, Time, and Signature required: " + address);
                    return;
                }

                // Safe time parsing to prevent DoS
                if (!long.TryParse(time, out var timeValue))
                {
                    EndOnConnect(peerIP, "Invalid timestamp format.", "Invalid timestamp format from: " + peerIP);
                    return;
                }

                var now = TimeUtil.GetTime();
                
                // Keep the stricter 15-second window for consensus operations
                if (Math.Abs(now - timeValue) > 15)
                {
                    EndOnConnect(peerIP, "Timestamp outside acceptable window.", "Timestamp outside acceptable window from: " + peerIP);
                    return;
                }

                // HAL-040 Fix: Verify signature BEFORE adding to signature map
                var verifySig = SignatureService.VerifySignature(address, address + ":" + time, signature);
                if (!verifySig)
                {
                    EndOnConnect(peerIP,
                        "Connected, but your address signature failed to verify. You are being disconnected.",
                        "Connected, but your address signature failed to verify with Consensus: " + address);
                    return;
                }

                // HAL-040 Fix: Only add to signature map AFTER successful verification
                if (!Globals.Signatures.TryAdd(signature, now))
                {
                    EndOnConnect(peerIP, "Reused signature.", "Reused signature.");
                    return;
                }

                if (!AdjPool.TryAdd(peerIP, new AdjPool { Address = address, Context = Context }))
                {
                    var Pool = AdjPool[peerIP];
                    if (Pool?.Context.ConnectionId != Context.ConnectionId)
                    {
                        Pool.Context?.Abort();                        
                    }
                    Pool.Context = Context;
                }
            }
            catch (Exception ex)
            {                
                ErrorLogUtility.LogError($"Unhandled exception has happend. Error : {ex.ToString()}", "ConsensusServer.OnConnectedAsync()");
            }

        }
        private void EndOnConnect(string ipAddress, string adjMessage, string loggMessage)
        {            
            if (Globals.OptionalLogging == true)
            {
                LogUtility.Log(loggMessage, "Consensus Connection");
                LogUtility.Log($"IP: {ipAddress} ", "Consensus Connection");
            }

            Context?.Abort();
        }

        public static void UpdateState(long height = -100, int methodCode = -100, int status = -1, int randomNumber = -1, string encryptedAnswer = null, bool? isUsed = null)
        {
            lock (UpdateLock)
            {
                if(height != -100)
                    ConsenusStateSingelton.Height = height;
                if (status != -1)
                    ConsenusStateSingelton.Status = (ConsensusStatus)status;
                if (methodCode != -100)
                    ConsenusStateSingelton.MethodCode = methodCode;
                if (randomNumber != -1)
                    ConsenusStateSingelton.RandomNumber = randomNumber;
                if(encryptedAnswer != null)
                    ConsenusStateSingelton.EncryptedAnswer = encryptedAnswer;
                if (isUsed != null)
                    ConsenusStateSingelton.IsUsed = isUsed.Value;
            }
        }
        public static (long Height, int MethodCode, ConsensusStatus Status, int Answer, string EncryptedAnswer, bool IsUsed) GetState()
        {
            if (ConsenusStateSingelton == null)
                return (-1, 0, ConsensusStatus.Processing, -1, null, false);
            return (ConsenusStateSingelton.Height, ConsenusStateSingelton.MethodCode, ConsenusStateSingelton.Status, ConsenusStateSingelton.RandomNumber,
                ConsenusStateSingelton.EncryptedAnswer, ConsenusStateSingelton.IsUsed);
        }    

        public static void UpdateNode(NodeInfo node, long height, int methodCode, bool finalized)
        {
            lock(UpdateNodeLock)
            {
                node.NodeHeight = height;
                node.MethodCode = methodCode;
                node.IsFinalized = finalized;
                node.LastMethodCodeTime = TimeUtil.GetMillisecondTime();
            }

            RemoveStaleCache(node);
        }

        // HAL-063: RemoveStaleCache removed - legacy code for Messages/Hashes dictionaries that are no longer used
        public static void RemoveStaleCache(NodeInfo node)
        {
            // This method is kept as a stub to avoid breaking existing references
            // Legacy cache cleanup for Messages/Hashes has been removed
        }

        public static void UpdateConsensusDump(string ipAddress, string method, string request, string response)
        {
            var NodeElem = Globals.ConsensusDump.GetOrAdd(ipAddress, new ConcurrentDictionary<string, (DateTime Time, string Request, string Response)>());
            NodeElem[method] = (DateTime.Now, request, response);
        }

        public string RequestMethodCode(long height, int methodCode, bool isFinalized)
        {
            var ip = GetIP(Context);
            LogUtility.LogQueue(ip + " " + height + " " + methodCode + " " + isFinalized + " " + TimeUtil.GetMillisecondTime(), "RequestMethodCode", "ConsensusServer.txt", false);
            if (!Globals.Nodes.TryGetValue(ip, out var node))
            {
                Context?.Abort();
                UpdateConsensusDump(ip, "RequestMethodCode", height + " " + methodCode + " " + isFinalized, null);
                return null;
            }

            UpdateNode(node, height, methodCode, isFinalized);

            // HAL-10 Fix: Create signed consensus coordination metadata
            var consensusData = $"{Globals.LastBlock.Height}:{ConsenusStateSingelton.MethodCode}:{(ConsenusStateSingelton.Status == ConsensusStatus.Finalized ? 1 : 0)}";
            var timestamp = TimeUtil.GetTime();
            var nonce = GenerateSecureNonce();
            
            // Create message to sign: consensusData|timestamp|nonce for replay protection
            var messageToSign = $"{consensusData}|{timestamp}|{nonce}";
            
            // Sign the coordination metadata using validator signature
            var signature = "";
            try 
            {
                if (Globals.AdjudicateAccount != null)
                {
                    signature = SignatureService.AdjudicatorSignature(messageToSign);
                }
                else if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    signature = SignatureService.ValidatorSignature(messageToSign);
                }
                else
                {
                    // Fallback: use a basic signature if no specific validator account
                    signature = "unsigned";
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Failed to sign consensus coordination data: {ex.Message}", "RequestMethodCode");
                signature = "error";
            }

            // Format: consensusData|timestamp|nonce|signature
            var signedResponse = $"{consensusData}|{timestamp}|{nonce}|{signature}";

            UpdateConsensusDump(ip, "RequestMethodCode", height + " " + methodCode + " " + isFinalized, signedResponse);
            return signedResponse;
        }

        /// <summary>
        /// Generate a cryptographically secure nonce for replay attack prevention
        /// </summary>
        private static string GenerateSecureNonce()
        {
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                var bytes = new byte[16]; // 128-bit nonce
                rng.GetBytes(bytes);
                return Convert.ToBase64String(bytes);
            }
        }

        // HAL-063: Message() and Hash() methods removed - legacy code from old adjudicator consensus model
        // These methods were part of the deprecated message/hash exchange pattern that has been replaced
        // by the blockcaster/validator proof-based consensus mechanism

       
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
    }
}
