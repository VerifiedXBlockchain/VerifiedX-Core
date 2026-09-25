using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.P2P
{
    public class P2PMotherServer : Hub
    {
        /// <summary>
        /// VX-17: one password check per IP every 2 s. The verifier is deliberately slow (PBKDF2, 600k), so unthrottled
        /// wrong-password connections would be a cheap way to burn the mother's CPU.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastAuthAttemptMs = new();
        internal const long AuthAttemptIntervalMs = 2000;

        internal static bool TryBeginAuthAttempt(string peerIP)
        {
            var now = Environment.TickCount64;
            while (true)
            {
                if (!_lastAuthAttemptMs.TryGetValue(peerIP, out var last))
                {
                    if (_lastAuthAttemptMs.TryAdd(peerIP, now)) return true;
                    continue;
                }
                if (now - last < AuthAttemptIntervalMs) return false;
                if (_lastAuthAttemptMs.TryUpdate(peerIP, now, last)) return true;
            }
        }

        #region Connect/Disconnect methods
        public override async Task OnConnectedAsync()
        {
            bool connected = false;

            var peerIP = GetIP(Context);
            
            // Check if there's an existing connection from this IP
            if (Globals.MothersKidsContext.TryGetValue(peerIP, out var existingContext) && existingContext.ConnectionId != Context.ConnectionId)
            {
                // Check if existing connection is still alive
                var connectionFeature = existingContext.Features.Get<IConnectionLifetimeFeature>();
                if (connectionFeature?.ConnectionClosed.IsCancellationRequested == false)
                {
                    // Old connection is still active - reject the new connection attempt to prevent hijacking
                    Context.Abort();
                    await base.OnConnectedAsync();
                    return;
                }
                // Old connection is dead, allow replacement by aborting it
                existingContext.Abort();
            }

            var httpContext = Context.GetHttpContext();

            if (httpContext != null)
            {
                var password = httpContext.Request.Headers["password"].ToString();
                var walletVersion = httpContext.Request.Headers["walver"].ToString();

                var walletVersionVerify = WalletVersionUtility.Verify(walletVersion);

                if (!string.IsNullOrWhiteSpace(password) && walletVersionVerify)
                {
                    var mother = Mother.GetMother();
                    if(mother == null)
                    {
                        //Mother is not present. Cannot continue
                        Context.Abort();
                    }
                    else if (!TryBeginAuthAttempt(peerIP))
                    {
                        // VX-17: throttled (see TryBeginAuthAttempt)
                        Context.Abort();
                    }
                    else
                    {
                        // VX-17: KDF-based verifier (legacy self-encrypted records are upgraded on success).
                        if (Mother.VerifyPassword(mother, password))
                        {
                            Globals.MothersKidsContext[peerIP] = Context;
                            connected = true;
                        }
                        else
                        {
                            //password attempt failed
                            Context.Abort();
                        }
                    }
                }
            }

            if (connected)
            {
                await SendMotherMessageSingle("status", "Connected");
            }
            else
            {
                await SendMotherMessageSingle("disconnect", "Failed to authenticate");
                Context.Abort();
            }


            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            var peerIP = GetIP(Context);
            Globals.MothersKidsContext.TryRemove(peerIP, out _);
            //Don't remove now
            //Globals.MothersKids.TryRemove(peerIP, out _);
        }
        private async Task SendMessageClient(string clientId, string method, string message)
        {
            await Clients.Client(clientId).SendAsync("GetMotherData", method, message);
        }

        private async Task SendMotherMessageSingle(string message, string data)
        {
            await Clients.Caller.SendAsync("GetMotherData", message, data);
        }

        private async Task SendMotherMessageAll(string message, string data)
        {
            await Clients.All.SendAsync("GetMotherData", message, data);
        }

        #endregion

        #region Mother Get Data
        public async Task<bool> SendMotherData(string data)
        {
            var peerIP = GetIP(Context);

            bool result = false;
            var payload = JsonConvert.DeserializeObject<Mother.DataPayload>(data);
            if (payload != null)
            {
                Globals.MothersKids.TryGetValue(payload.Address, out var kid);
                if (kid != null)
                {
                    // Validate that the submitting client is authorized to update this address
                    if (kid.IPAddress != peerIP)
                    {
                        // Reject updates from unauthorized IP addresses
                        return false;
                    }

                    kid.Address = payload.Address;
                    kid.IPAddress = peerIP;
                    kid.Balance = payload.Balance;
                    kid.IsValidating = payload.IsValidating;
                    kid.BlockHeight = payload.BlockHeight;
                    kid.LastTaskSent = payload.LastTaskSent;
                    kid.LastTaskBlockSent = payload.LastTaskBlockSent;
                    kid.ValidatorName = payload.ValidatorName;
                    kid.PeerCount = payload.PeerCount;
                    kid.LastDataSentTime = DateTime.Now;

                    Globals.MothersKids[payload.Address] = kid;
                    result = true;
                }
                else
                {
                    Mother.Kids nKid = new Mother.Kids { 
                        ConnectTime = DateTime.Now,
                        LastDataSentTime = DateTime.Now,
                        Address = payload.Address,
                        IPAddress = peerIP,
                        Balance = payload.Balance,
                        IsValidating = payload.IsValidating,
                        BlockHeight = payload.BlockHeight,
                        LastTaskSent = payload.LastTaskSent,
                        LastTaskBlockSent = payload.LastTaskBlockSent,
                        ValidatorName = payload.ValidatorName,
                        PeerCount = payload.PeerCount,
                    };

                    Globals.MothersKids[payload.Address] = nKid;
                    result = true;
                }
            }
            return result;
        }

        #endregion

        #region Get IP
        private static string GetIP(HubCallerContext context)
        {
            var feature = context.Features.Get<IHttpConnectionFeature>();
            var peerIP = VerifiedXCore.Utilities.RemoteIp.Text(feature.RemoteIpAddress)!;

            return peerIP;
        }

        #endregion
    }
}
