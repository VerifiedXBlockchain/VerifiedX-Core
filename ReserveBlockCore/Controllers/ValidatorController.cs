using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Net;

namespace ReserveBlockCore.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ValidatorController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> Get()
        {
            return "Hello from ValidatorController!";
        }

        [HttpGet]
        [Route("Status/{validatorAddress}/{validatorPublicKey}")]
        public ActionResult<string> Status(string validatorAddress, string validatorPublicKey)
        {
            var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;

            // Convert it to a string if it's not null
            string peerIP = remoteIpAddress?.ToString();

            if (Globals.BannedIPs.ContainsKey(peerIP))
            {
                return Unauthorized();
            }

            var portCheck = PortUtility.IsPortOpen(peerIP, Globals.ValPort);
            if (!portCheck)
            {
                return Unauthorized();
            }
            var ablList = Globals.ABL.ToList();

            if (ablList.Exists(x => x == validatorAddress))
            {
                BanService.BanPeer(peerIP, "Request malformed", "OnConnectedAsync");
                return Unauthorized();
            }

            _ = Peers.UpdatePeerAsVal(peerIP, validatorAddress, validatorPublicKey);

            return Ok();
        }

        [HttpGet]
        [Route("GetBlock/{blockHeight}")]
        public ActionResult<string?> GetBlock(long blockHeight)
        {
            if(Globals.LastBlock.Height == blockHeight)
                return Ok(JsonConvert.SerializeObject(Globals.LastBlock));

            return Ok("0");
        }

        [HttpGet]
        [Route("HeartBeat")]
        public ActionResult<string> HeartBeat()
        {
            return Ok();
        }

        [HttpGet]
        [Route("ValidatorInfo")]
        public ActionResult<string> ValidatorInfo()
        {
            return Ok($"{Globals.ValidatorAddress},{Globals.ValidatorPublicKey}");
        }
    }
}
