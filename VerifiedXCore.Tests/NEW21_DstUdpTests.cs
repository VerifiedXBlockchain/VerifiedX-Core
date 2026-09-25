using System.Net;
using System.Net.Sockets;
using VerifiedXCore;
using VerifiedXCore.DST;
using VerifiedXCore.Models.DST;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-21 (found by the sixth independent review; medium, pre-existing): one spoofed KeepAlive datagram created a
    /// permanent DST client entry and started a keepalive loop toward the spoofed source (~100x reflection); the DST
    /// connection tables were never pruned or bounded.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW21_DstUdpTests
    {
        [Fact]
        public void NEW21_PoC_UnsolicitedKeepAliveCreatesNoClientAndNoLoop()
        {
            Globals.ConnectedClients.Clear();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var spoofed = new IPEndPoint(IPAddress.Parse("198.51.100.23"), 40000);
            MessageService.KeepAlive(new Message { Type = MessageType.KeepAlive, Data = "ka", IPAddress = spoofed.ToString() }, spoofed, udp);
            Assert.False(Globals.ConnectedClients.ContainsKey(spoofed.ToString()));
        }

        [Fact]
        public void NEW21_HeloStillRegistersAClient_AndTheTableIsBounded()
        {
            Globals.ConnectedClients.Clear();
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var self = (IPEndPoint)udp.Client.LocalEndPoint!;
                MessageService.ShopConnect(new Message { Type = MessageType.KeepAlive, Data = "helo", IPAddress = self.ToString() }, self, udp);
                Assert.True(Globals.ConnectedClients.ContainsKey(self.ToString()));               // control

                for (int i = Globals.ConnectedClients.Count; i < MessageService.MaxDstConnections; i++)
                    Globals.ConnectedClients[$"203.0.113.1:{i}"] = new DSTConnection { IPAddress = $"203.0.113.1:{i}" };
                var extra = new IPEndPoint(IPAddress.Loopback, 1);
                MessageService.ShopConnect(new Message { Data = "helo", IPAddress = extra.ToString() }, extra, udp);
                Assert.False(Globals.ConnectedClients.ContainsKey(extra.ToString()));
            }
            finally { Globals.ConnectedClients.Clear(); }
        }
    }
}
