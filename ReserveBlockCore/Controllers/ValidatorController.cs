using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Models;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Net;

namespace ReserveBlockCore.Controllers
{
    [Route("valapi/[controller]")]
    [ApiController]
    public class ValidatorController : ControllerBase
    {
        [HttpGet]
        public ActionResult<string> Get()
        {
            return "Hello from ValidatorController!";
        }

        [HttpPost]
        [Route("Status")]
        public ActionResult<string> Status([FromBody] string networkValJson)
        {
            var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;

            // Convert it to a string if it's not null
            string peerIP = remoteIpAddress?.ToString();

            if(networkValJson == null)
                return BadRequest("Bad Json");

            var networkVal = JsonConvert.DeserializeObject<NetworkValidator>(networkValJson);

            if (networkVal == null)
                return BadRequest("Could not deserialize network val request");

            if (Globals.BannedIPs.ContainsKey(peerIP))
            {
                return Unauthorized();
            }

            var portCheck = PortUtility.IsPortOpen(peerIP.Replace("::ffff:", ""), Globals.ValPort);
            if (!portCheck)
            {
                return Unauthorized();
            }
            var ablList = Globals.ABL.ToList();

            if (ablList.Exists(x => x == networkVal.Address))
            {
                BanService.BanPeer(peerIP, "Request malformed", "OnConnectedAsync");
                return Unauthorized();
            }

            _ = Peers.UpdatePeerAsVal(peerIP, networkVal.Address, networkVal.PublicKey);

            networkVal.IPAddress = peerIP;

            _ = NetworkValidator.AddValidatorToPool(networkVal);

            return Ok();
        }
        [HttpPost]
        [Route("TestPost")]
        public async Task<string> TestPost([FromBody] string winningProof)
        {
            return "Ok";
        }

        [HttpPost]
        [Route("ReceiveWinningProof")]
        public async Task<ActionResult<string>> ReceiveWinningProof([FromBody] string winningProof)
        {
            try
            {
                var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;

                // Convert it to a string if it's not null
                string peerIP = remoteIpAddress?.ToString();

                if (winningProof == null)
                    return BadRequest("Bad Json");

                var proof = JsonConvert.DeserializeObject<Proof>(winningProof);

                if (proof == null)
                    return BadRequest("Could not deserialize network val request");

                if (Globals.BannedIPs.ContainsKey(peerIP))
                {
                    return Unauthorized();
                }

                if (proof.VerifyProof())
                    Globals.Proofs.Add(proof);

                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
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
        [Route("SendApproval/{blockHeight}")]
        public ActionResult<string?> SendApproval(long blockHeight)
        {
            var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;
            // Convert it to a string if it's not null
            string? peerIP = remoteIpAddress?.ToString();

            if (blockHeight >= Globals.LastBlock.Height)
            {
                _ = ValidatorNode.GetApproval(peerIP, blockHeight);
            }

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
