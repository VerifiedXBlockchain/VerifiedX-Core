using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.ComponentModel.DataAnnotations;
using System.Net;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class NetworkController : RestBaseController
    {
        /// <summary>
        /// Network overview
        /// </summary>
        [HttpGet]
        public IActionResult GetOverview()
        {
            var currentBlockHeight = Globals.LastBlock.Height;
            var hash = Globals.LastBlock.Hash;
            var lastTimeUTC = Globals.LastBlockAddedTimestamp.ToUTCDateTimeFromUnix();
            var cliVersion = Globals.CLIVersion;
            var gitVersion = Globals.GitHubLatestReleaseVersion;
            var blockVersion = Globals.LastBlock.Version;
            DateTime originDate = new DateTime(2022, 1, 1);
            DateTime currentDate = DateTime.Now;
            var dateDiff = (int)Math.Round((currentDate - originDate).TotalDays);
            var totalSupply = 200_000_000;

            var network = new
            {
                Height = currentBlockHeight,
                Hash = hash,
                LastBlockAddedTimeUTC = lastTimeUTC,
                CLIVersion = cliVersion,
                GitHubVersion = gitVersion,
                BlockVersion = blockVersion,
                NetworkAgeInDays = dateDiff,
                TotalSupply = totalSupply
            };

            return Ok(network);
        }

        /// <summary>
        /// Chain stats: height, circulating network supply, peer count, mempool size
        /// </summary>
        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            var height = -1L;
            var peerCount = 0;
            var mempoolCount = 0;
            decimal supply = 0;

            try { height = Globals.LastBlock?.Height ?? -1; } catch { }
            try { peerCount = Globals.Nodes?.Count ?? 0; } catch { }
            try { mempoolCount = Data.TransactionData.GetPool()?.Count() ?? 0; } catch { }
            try { supply = AccountStateTrei.GetNetworkTotal(); } catch { }

            return Ok(new
            {
                Height = height,
                NetworkSupply = supply,
                Peers = peerCount,
                Mempool = mempoolCount,
                IsTestNet = Globals.IsTestNet
            });
        }

        /// <summary>
        /// Block timing metrics
        /// </summary>
        [HttpGet("metrics")]
        public IActionResult GetMetrics()
        {
            var currentTime = TimeUtil.GetTime();
            var currentDiff = currentTime - Globals.LastBlockAddedTimestamp;

            var metrics = new
            {
                BlockDiffAvg = BlockDiffService.CalculateAverage().ToString("#.##"),
                BlockLastReceived = Globals.LastBlockAddedTimestamp.ToLocalDateTimeFromUnix(),
                BlockLastDelay = Globals.BlockTimeDiff,
                TimeSinceLastBlockSeconds = currentDiff,
                BlocksAveraged = $"{Globals.BlockDiffQueue.Count()}/3456"
            };

            return Ok(metrics);
        }

        /// <summary>
        /// Current block height
        /// </summary>
        [HttpGet("height")]
        public IActionResult GetHeight()
        {
            return Ok(new { Height = Globals.LastBlock.Height });
        }

        /// <summary>
        /// Connected peer info
        /// </summary>
        [HttpGet("peers")]
        public IActionResult GetPeers()
        {
            var nodeInfoList = Globals.Nodes.Select(x => new
            {
                x.Value.NodeIP,
                x.Value.NodeLatency,
                x.Value.NodeHeight,
                x.Value.NodeLastChecked
            }).ToArray();

            return Ok(nodeInfoList);
        }

        /// <summary>
        /// Add a peer by IP
        /// </summary>
        [HttpPost("peers")]
        public IActionResult AddPeer([FromBody] AddPeerRequest request)
        {
            if (!IPAddress.TryParse(request.IpAddress, out _))
                return Fail("INVALID_IP", $"Not a valid IP address: {request.IpAddress}");

            var peers = Peers.GetAll();
            var peerExist = peers.Exists(x => x.PeerIP == request.IpAddress);

            if (!peerExist)
            {
                var nPeer = new Peers
                {
                    IsIncoming = false,
                    IsOutgoing = true,
                    PeerIP = request.IpAddress,
                    FailCount = 0,
                    BanCount = 0
                };

                peers.InsertSafe(nPeer);

                if (nPeer.IsOutgoing)
                    _ = P2PClient.ManualConnectToPeers(nPeer);
            }
            else
            {
                var peerRec = peers.FindOne(x => x.PeerIP == request.IpAddress);
                if (peerRec.IsOutgoing)
                    _ = P2PClient.ManualConnectToPeers(peerRec);
            }

            return Ok($"Peer {request.IpAddress} has been added.");
        }

        /// <summary>
        /// List banned peers
        /// </summary>
        [HttpGet("peers/banned")]
        public IActionResult GetBannedPeers()
        {
            var bannedPeers = Peers.ListBannedPeers();
            return Ok(bannedPeers);
        }

        /// <summary>
        /// Ban a peer by IP
        /// </summary>
        [HttpPost("peers/{ip}/ban")]
        public IActionResult BanPeer(string ip)
        {
            BanService.BanPeer(ip, "Banned from REST API", "NetworkController.BanPeer()");
            return Ok($"Peer {ip} banned.");
        }

        /// <summary>
        /// Unban a peer by IP
        /// </summary>
        [HttpDelete("peers/{ip}/ban")]
        public IActionResult UnbanPeer(string ip)
        {
            BanService.UnbanPeer(ip);
            return Ok($"Peer {ip} unbanned.");
        }

        /// <summary>
        /// List masternodes (validator pool)
        /// </summary>
        [HttpGet("masternodes")]
        public IActionResult GetMasternodes()
        {
            var validators = Globals.FortisPool.Values.Select(x => new
            {
                x.Context.ConnectionId,
                x.ConnectDate,
                x.LastAnswerSendDate,
                x.IpAddress,
                x.Address,
                x.UniqueName,
                x.WalletVersion
            }).ToList();

            return Ok(validators);
        }
    }

    public class AddPeerRequest
    {
        [Required(ErrorMessage = "IP address is required")]
        public string IpAddress { get; set; } = string.Empty;
    }
}
