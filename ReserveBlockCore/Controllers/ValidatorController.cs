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
        public ActionResult<string> Status([FromBody] NetworkValidator networkVal)
        {
            try
            {
                var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;

                // Convert it to a string if it's not null
                string peerIP = remoteIpAddress?.ToString().Replace("::ffff:", "");

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

                _ = Peers.UpdatePeerAsVal(peerIP.Replace("::ffff:", ""), networkVal.Address, networkVal.PublicKey);

                networkVal.IPAddress = peerIP.Replace("::ffff:", "");

                _ = NetworkValidator.AddValidatorToPool(networkVal);
            }
            catch (Exception ex)
            {

            }

            return Ok();
        }

        [HttpPost]
        [Route("ReceiveWinningProof")]
        public async Task<ActionResult<string>> ReceiveWinningProof([FromBody] Proof proof)
        {
            try
            {
                var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;

                // Convert it to a string if it's not null
                string peerIP = remoteIpAddress?.ToString();

                if (proof == null)
                    return BadRequest("Could not deserialize network val request");

                if (Globals.BannedIPs.ContainsKey(peerIP))
                {
                    return Unauthorized();
                }

                // Verify the proof and add it if valid
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
