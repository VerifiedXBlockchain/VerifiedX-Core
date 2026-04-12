using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Nodes;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Linq;

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

                var peerIP = remoteIpAddress?.ToString()?.Replace("::ffff:", "") ?? "";

                if (networkVal == null)
                    return BadRequest("Could not deserialize network val request");

                if (!string.IsNullOrEmpty(peerIP) && Globals.BannedIPs.ContainsKey(peerIP))
                {
                    return Unauthorized();
                }

                var portCheck = PortUtility.IsPortOpen(peerIP.Replace("::ffff:", ""), Globals.ValAPIPort);
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
                string? peerIP = remoteIpAddress?.ToString();

                if (peerIP != null)
                {
                    peerIP = peerIP.Replace("::ffff:", "");
                }

                if (proof == null)
                    return BadRequest("Could not deserialize network val request");

                if (peerIP != null && Globals.BannedIPs.ContainsKey(peerIP))
                {
                    return Unauthorized();
                }

                if (string.IsNullOrEmpty(peerIP))
                    return BadRequest("Could not determine caller IP");

                // Verify the proof and add it if valid
                if (proof.VerifyProof())
                {
                    if (!Globals.CasterProofDict.ContainsKey(peerIP))
                    {
                        while (!Globals.CasterProofDict.TryAdd(peerIP, proof))
                        {
                            await Task.Delay(75);
                        }
                    }
                }

                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }


        [HttpGet]
        [Route("SendWinningProof/{blockHeight}")]
        public async Task<ActionResult<string>> SendWinningProof(long blockHeight)
        {
            try
            {
                var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;

                string? peerIP = remoteIpAddress?.ToString();
                if (peerIP != null)
                    peerIP = peerIP.Replace("::ffff:", "");

                if (!string.IsNullOrEmpty(peerIP) && Globals.BannedIPs.ContainsKey(peerIP))
                {
                    return Unauthorized();
                }

                if (Globals.CasterRoundDict.ContainsKey(blockHeight))
                {
                    var round = Globals.CasterRoundDict[blockHeight];
                    if (round == null)
                        return Ok("0");
                    if (round.Proof == null)
                        return Ok("0");

                    return Ok(JsonConvert.SerializeObject(round.Proof));
                }

                return Ok("0");
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        [HttpGet]
        [Route("GetCasterVote")]
        public async Task<ActionResult<string>> GetCasterVote()
        {
            try
            {
                if (BlockcasterNode._currentRound == null)
                    return BadRequest();

                if (BlockcasterNode._currentRound.IsFinalized)
                    return BadRequest();

                if (BlockcasterNode._currentRound.MyChosenCaster == null)
                    return BadRequest();

                return Ok(JsonConvert.SerializeObject(BlockcasterNode._currentRound.MyChosenCaster));
            }
            catch { return BadRequest(); }
        }

        [HttpGet]
        [Route("GetBlock/{blockHeight}")]
        public ActionResult<string?> GetBlock(long blockHeight)
        {
            if(Globals.CasterRoundDict.ContainsKey(blockHeight))
            {
                var round = Globals.CasterRoundDict[blockHeight];
                if(round == null)
                    return Ok("0");
                if(round.Block == null)
                    return Ok("0");

                // Do not publish attestations from this unauthenticated GET (DoS amplifier). Use authenticated RequestBlock / post-accept paths.
                return Ok(JsonConvert.SerializeObject(round.Block));
            }
                
            return Ok("0");
        }

        static bool IsCasterParticipantAddress(string? address)
        {
            if (string.IsNullOrEmpty(address))
                return false;
            if (Globals.BlockCasters.Any(x => x.ValidatorAddress == address))
                return true;
            lock (Globals.KnownCastersLock)
                return Globals.KnownCasters.Any(x => x.Address == address);
        }

        [HttpPost]
        [Route("SubmitAttestation")]
        public ActionResult<string> SubmitAttestation([FromBody] SubmitAttestationRequest? body)
        {
            try
            {
                var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString().Replace("::ffff:", "");
                if (remoteIp != null && Globals.BannedIPs.ContainsKey(remoteIp))
                    return Unauthorized();

                if (body == null
                    || string.IsNullOrWhiteSpace(body.BlockHash)
                    || string.IsNullOrWhiteSpace(body.WinnerAddress)
                    || string.IsNullOrWhiteSpace(body.PrevHash)
                    || string.IsNullOrWhiteSpace(body.CasterAddress)
                    || string.IsNullOrWhiteSpace(body.Signature))
                    return BadRequest("invalid body");

                if (!IsCasterParticipantAddress(body.CasterAddress))
                    return Unauthorized();

                var msg = ConsensusMessageFormatter.FormatAttestationV1(body.BlockHeight, body.BlockHash, body.WinnerAddress, body.PrevHash);
                if (!SignatureService.VerifySignature(body.CasterAddress, msg, body.Signature))
                    return Unauthorized();

                var att = new CasterAttestation
                {
                    CasterAddress = body.CasterAddress,
                    Signature = body.Signature,
                    Timestamp = TimeUtil.GetTime()
                };

                if (!ConsensusAttestationStore.TryAdd(body.BlockHeight, body.CasterAddress, att, out var err))
                    return Conflict(err);

                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        [HttpGet]
        [Route("GetAttestations/{blockHeight}")]
        public ActionResult<string> GetAttestations(long blockHeight)
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString().Replace("::ffff:", "");
            if (remoteIp != null && Globals.BannedIPs.ContainsKey(remoteIp))
                return Unauthorized();
            var list = ConsensusAttestationStore.GetForHeight(blockHeight);
            return Ok(JsonConvert.SerializeObject(list));
        }

        [HttpPost]
        [Route("RequestBlock")]
        public async Task<ActionResult<string>> RequestBlock([FromBody] RequestBlockRequest? req)
        {
            try
            {
                var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString().Replace("::ffff:", "");
                if (remoteIp != null && Globals.BannedIPs.ContainsKey(remoteIp))
                    return Unauthorized();

                if (req == null || string.IsNullOrWhiteSpace(req.CasterAddress) || string.IsNullOrWhiteSpace(req.WinnerAddress) || string.IsNullOrWhiteSpace(req.Signature))
                    return BadRequest();

                var now = TimeUtil.GetTime();
                if (Math.Abs(now - req.Timestamp) > 90)
                    return Unauthorized("timestamp");

                if (!IsCasterParticipantAddress(req.CasterAddress))
                    return Unauthorized();

                var msg = ConsensusMessageFormatter.FormatRequestBlockV1(req.BlockHeight, req.CasterAddress, req.WinnerAddress, req.Timestamp);
                if (!SignatureService.VerifySignature(req.CasterAddress, msg, req.Signature))
                    return Unauthorized();

                if (RequestBlockCache.TryGet(req.BlockHeight, req.WinnerAddress, out var cached) && cached != null)
                    return Ok(JsonConvert.SerializeObject(cached));

                if (req.BlockHeight != Globals.LastBlock.Height + 1)
                    return BadRequest("height");

                if (Globals.CasterRoundDict.TryGetValue(req.BlockHeight, out var cr))
                {
                    if (!string.IsNullOrEmpty(cr.Validator) && cr.Validator != req.WinnerAddress)
                        return BadRequest("winner mismatch");
                    if (cr.Block != null && cr.Block.Validator == req.WinnerAddress)
                    {
                        RequestBlockCache.Add(req.BlockHeight, req.WinnerAddress, cr.Block);
                        return Ok(JsonConvert.SerializeObject(cr.Block));
                    }
                }

                // FIX (CRITICAL): Do NOT craft a new block here.
                // The winning caster's consensus loop (iAmWinner path) is solely responsible for crafting
                // the block and storing it in CasterRoundDict. If RequestBlock independently crafts a block,
                // it races with the consensus loop and produces a DIFFERENT block (different timestamp/txs = 
                // different hash), causing non-winners to end up with a different block than the winner → FORK.
                // Return "0" (not found) so the requesting caster retries until the consensus loop stores the block.
                return Ok("0");
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        [HttpGet]
        [Route("GetCasterList")]
        public ActionResult<string> GetCasterList()
        {
            try
            {
                var acc = AccountData.GetLocalValidator();
                if (acc == null || acc.GetPrivKey == null)
                    return BadRequest();

                var list = Globals.BlockCasters
                    .Where(x => !string.IsNullOrEmpty(x.ValidatorAddress))
                    .Select(x => new CasterInfo
                    {
                        Address = x.ValidatorAddress!,
                        PeerIP = (x.PeerIP ?? "").Replace("::ffff:", ""),
                        PublicKey = x.ValidatorPublicKey ?? ""
                    })
                    .OrderBy(x => x.Address, StringComparer.Ordinal)
                    .ToList();

                var h = (int)Math.Min(int.MaxValue, Math.Max(0, Globals.LastBlock.Height));
                var canonical = ConsensusMessageFormatter.FormatSignedCasterListV1(h, list.Select(c => (c.Address, c.PeerIP, c.PublicKey)));
                var sig = SignatureService.CreateSignature(canonical, acc.GetPrivKey, acc.PublicKey);
                if (sig == "ERROR")
                    return BadRequest("sign failed");

                var resp = new SignedCasterListResponse
                {
                    AsOfBlockHeight = h,
                    Casters = list,
                    SignerAddress = acc.Address,
                    Signature = sig
                };
                return Ok(JsonConvert.SerializeObject(resp));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        [HttpPost]
        [Route("CasterInvitation")]
        public ActionResult<string> CasterInvitation([FromBody] CasterRotationBroadcast? _)
        {
            return Accepted("CasterInvitation reserved for signed rotation flow (plan §7.4).");
        }

        /// <summary>
        /// Returns this node's current block height as a plain number.
        /// Used by SyncHeightWithPeersAsync to check if peers are ahead.
        /// </summary>
        [HttpGet]
        [Route("GetBlockHeight")]
        public ActionResult<string> GetBlockHeight()
        {
            return Ok(Globals.LastBlock.Height.ToString());
        }

        /// <summary>
        /// Caster readiness check — returns this caster's current height and ready status.
        /// Used by the startup readiness barrier so all casters sync before beginning consensus.
        /// </summary>
        [HttpGet]
        [Route("CasterReadyCheck/{height}")]
        public ActionResult<string> CasterReadyCheck(long height)
        {
            try
            {
                var myHeight = Globals.LastBlock.Height;
                var ready = Globals.IsBlockCaster && !string.IsNullOrEmpty(Globals.ValidatorAddress);
                var result = new { Height = myHeight, Ready = ready, Address = Globals.ValidatorAddress ?? "" };
                return Ok(JsonConvert.SerializeObject(result));
            }
            catch { return BadRequest(); }
        }

        /// <summary>
        /// Returns the block hash this caster has for the given height.
        /// Used in the block-hash agreement phase to ensure all casters commit the same block.
        /// </summary>
        [HttpGet]
        [Route("GetBlockHash/{blockHeight}")]
        public ActionResult<string> GetBlockHash(long blockHeight)
        {
            try
            {
                // FIX: Return the COMMITTED block hash from the actual blockchain,
                // not stale CasterRoundDict data. CasterRoundDict may hold an outdated
                // block version from before hash-agreement or validation replaced it,
                // causing phantom mismatches and infinite HASHSYNC loops.
                
                // 1. If this is the current committed block, return Globals.LastBlock directly
                if (blockHeight == Globals.LastBlock.Height && !string.IsNullOrEmpty(Globals.LastBlock.Hash))
                {
                    var result = new { Hash = Globals.LastBlock.Hash, Validator = Globals.LastBlock.Validator, Height = blockHeight };
                    return Ok(JsonConvert.SerializeObject(result));
                }
                
                // 2. If this is a past block, look it up from the actual blockchain database
                if (blockHeight < Globals.LastBlock.Height)
                {
                    var block = BlockchainData.GetBlockByHeight(blockHeight);
                    if (block != null && !string.IsNullOrEmpty(block.Hash))
                    {
                        var result = new { Hash = block.Hash, Validator = block.Validator, Height = blockHeight };
                        return Ok(JsonConvert.SerializeObject(result));
                    }
                }
                
                // 3. Only for FUTURE blocks (being crafted), use CasterRoundDict
                if (blockHeight > Globals.LastBlock.Height && 
                    Globals.CasterRoundDict.TryGetValue(blockHeight, out var round) && round?.Block != null)
                {
                    var result = new { Hash = round.Block.Hash, Validator = round.Block.Validator, Height = blockHeight };
                    return Ok(JsonConvert.SerializeObject(result));
                }
                
                return Ok("0");
            }
            catch { return Ok("0"); }
        }

        [HttpGet]
        [Route("GetApproval/{blockHeight}")]
        public ActionResult<string?> GetApproval(long blockHeight)
        {
            if (Globals.CasterRoundDict.ContainsKey(blockHeight))
            {
                var casterRound = Globals.CasterRoundDict[blockHeight];

                if(string.IsNullOrEmpty(casterRound.Validator))
                    return Ok("1");

                return Ok(JsonConvert.SerializeObject(casterRound));
            }

            return Ok("0");
        }

        [HttpGet]
        [Route("SendApproval/{blockHeight}/{validatorAddress}")]
        public ActionResult<string?> SendApproval(long blockHeight, string validatorAddress)
        {
            var remoteIpAddress = HttpContext.Connection.RemoteIpAddress;
            // Convert it to a string if it's not null
            string? peerIP = remoteIpAddress?.ToString();

            if(peerIP != null)
            {
                peerIP = peerIP.Replace("::ffff:", "");
            }

            if (blockHeight >= Globals.LastBlock.Height)
            {
                _ = BlockcasterNode.GetApproval(peerIP, blockHeight, validatorAddress);
            }

            return Ok("0");
        }

        [HttpGet]
        [Route("HeartBeat/{address}")]
        public ActionResult<string> HeartBeat(string address)
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return BadRequest();

            if (!Globals.NetworkValidators.ContainsKey(address))
            {
                return Accepted();
            }

            return Ok();
        }

        [HttpGet]
        [Route("HeartBeat")]
        public ActionResult<string> HeartBeat()
        {
            return Ok();
        }

        /// <summary>
        /// Returns this node's wallet/CLI version string.
        /// Used by casters during VerifyWinnerAvailability to reject validators
        /// running outdated versions that can't participate in consensus properly.
        /// Old nodes won't have this endpoint → 404 → rejected as winner.
        /// </summary>
        [HttpGet]
        [Route("GetWalletVersion")]
        public ActionResult<string> GetWalletVersion()
        {
            return Ok(Globals.CLIVersion);
        }

        [HttpGet]
        [Route("SendSeedPart")]
        public ActionResult<string> SendSeedPart()
        {
            try
            {
                if (BlockcasterNode._currentRound == null)
                    return BadRequest();

                if (BlockcasterNode._currentRound.IsFinalized)
                    return BadRequest();

                var seedPart = BlockcasterNode._currentRound.SeedPiece.ToString();
                return Ok(seedPart);
            }
            catch { return BadRequest(); }
        }

        [HttpGet]
        [Route("VerifyBlock/{nextBlock}/{**proofHash}")]
        public async Task<ActionResult<string>> VerifyBlock(long nextBlock, string proofHash)
        {
            var block = Globals.NextValidatorBlock;

            if (block.Height != nextBlock)
            {
                await Task.Delay(2000);
                block = Globals.NextValidatorBlock;
            }

            if(block != null && block.Height == nextBlock)
                return Ok(JsonConvert.SerializeObject(block));
            
            return BadRequest();
        }

        [HttpGet]
        [Route("Blockcasters")]
        public ActionResult<string> Blockcasters()
        {
            var casterList = Globals.BlockCasters.ToList();
            if (casterList.Any())
                return Ok(JsonConvert.SerializeObject(casterList, Formatting.Indented));

            return Ok("0");
        }

        [HttpGet]
        [Route("ValidatorInfo")]
        public ActionResult<string> ValidatorInfo()
        {
            return Ok($"{Globals.ValidatorAddress},{Globals.ValidatorPublicKey}");
        }

        /// <summary>
        /// Get list of currently active vBTC V2 validators (based on heartbeat)
        /// Used by non-validators to fetch the latest validator list from the network
        /// </summary>
        /// <returns>Active validators</returns>
        [HttpGet]
        [Route("GetActiveValidators")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<string> GetActiveValidators()
        {
            try
            {
                // Get validators using blockchain-based staleness check
                var currentBlock = Globals.LastBlock.Height;
                var activeValidators = Bitcoin.Models.VBTCValidator.GetActiveValidatorsWithStalenessCheck(
                    currentBlock, Services.VBTCValidatorHeartbeatService.STALE_THRESHOLD);

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Active validators retrieved",
                    CurrentBlock = currentBlock,
                    TotalValidators = activeValidators?.Count ?? 0,
                    ActiveValidators = activeValidators ?? new List<Bitcoin.Models.VBTCValidator>()
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }
    }
}
