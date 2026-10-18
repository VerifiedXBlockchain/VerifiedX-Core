using VerifiedXCore.Utilities;

namespace VerifiedXCore.Models.DST
{
    public class DSTConnection
    {
        public string IPAddress { get; set; }
        public bool IsConnected { get { return GetConnectionStatus(); } }
        public long ConnectDate { get; set; }
        public long LastSentMessage { get; set; }
        public long LastReceiveMessage { get; set; }
        public bool KeepAliveStarted { get; set; }
        public bool AttemptReconnect { get; set; }
        public string ShopURL { get; set; }
        public string ConnectionId { get; set; }
        public Message InitialMessage { get; set; }
        public Message? LastMessageSent { get; set; }
        /// <summary>STUN side: the secret issued to this shop at registration, and whether the shop has returned it.</summary>
        public string? StunSecret { get; set; }
        public bool StunSecretConfirmed { get; set; }

        public bool GetConnectionStatus()
        {
            var currentTime = TimeUtil.GetTime();
            if (currentTime - LastReceiveMessage > 65)
                return false;

            return true;
        }
    }
}
