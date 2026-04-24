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

        #region vBTC V2 Bridge Endpoints

        /// <summary>
        /// Validators sign a mint attestation for a VFX bridge lock.
        /// Called by the user's node (or any node) to collect validator ECDSA signatures
        /// for the VBTCb <c>mintWithProof</c> call on Base.
        /// </summary>
        [HttpPost]
        [Route("SignMintAttestation")]
        public async Task<ActionResult<string>> SignMintAttestation([FromBody] Bitcoin.Models.MintAttestationRequest? request)
        {
            var reqIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            try
            {
                if (request == null)
                {
                    LogUtility.Log($"[BridgeAttest] SignMintAttestation: empty request body from IP={reqIp}", "ValidatorController.SignMintAttestation");
                    return BadRequest(JsonConvert.SerializeObject(new { success = false, error = "Empty request body" }));
                }

                LogUtility.Log($"[BridgeAttest] SignMintAttestation request from IP={reqIp}: lockId={request.LockId}, evmDest={request.EvmDestination}, amountSats={request.AmountSats}, nonce={request.Nonce}, chainId={request.ChainId}, contract={request.ContractAddress}, scUID={request.SmartContractUID}", "ValidatorController.SignMintAttestation");

                var (success, signatureHex, error) = await Bitcoin.Services.BaseBridgeAttestationService.HandleMintAttestationRequest(request);

                if (success)
                {
                    LogUtility.Log($"[BridgeAttest] SignMintAttestation SUCCESS for lockId={request.LockId}. Signature={signatureHex?.Substring(0, Math.Min(20, signatureHex?.Length ?? 0))}...", "ValidatorController.SignMintAttestation");
                    return Ok(JsonConvert.SerializeObject(new { success = true, signature = signatureHex }));
                }
                else
                {
                    LogUtility.Log($"[BridgeAttest] SignMintAttestation REJECTED for lockId={request.LockId}: {error}", "ValidatorController.SignMintAttestation");
                    return Ok(JsonConvert.SerializeObject(new { success = false, error = error ?? "Attestation failed" }));
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log($"[BridgeAttest] SignMintAttestation EXCEPTION for lockId={request?.LockId}: {ex.Message}", "ValidatorController.SignMintAttestation");
                return StatusCode(500, JsonConvert.SerializeObject(new { success = false, error = ex.Message }));
            }
        }

        /// <summary>
        /// Validators sign add/remove operations for the Base contract.
        /// Called by BaseValidatorSyncService when collecting signatures.
        /// </summary>
        [HttpPost]
        [Route("SignValidatorUpdate")]
        public ActionResult<string> SignValidatorUpdate([FromBody] object requestBody)
        {
            try
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = "Not a validator" }));

                var json = requestBody?.ToString() ?? "";
                var request = JsonConvert.DeserializeObject<dynamic>(json);
                string action = request?.Action;
                long vfxBlockHeight = request?.VfxBlockHeight ?? 0;

                // Get target addresses
                var targetAddresses = new List<string>();
                if (request?.TargetAddresses != null)
                {
                    foreach (var addr in request.TargetAddresses)
                        targetAddresses.Add((string)addr);
                }
                else if (request?.TargetAddress != null)
                {
                    targetAddresses.Add((string)request.TargetAddress);
                }

                if (string.IsNullOrEmpty(action) || !targetAddresses.Any())
                    return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = "Missing action or target address" }));

                var sig = Bitcoin.Services.BaseValidatorSyncService.SignValidatorUpdateLocally(
                    action, targetAddresses.ToArray(), vfxBlockHeight);

                if (sig == null)
                    return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = "Failed to sign" }));

                return Ok(JsonConvert.SerializeObject(new { Success = true, Signature = "0x" + Convert.ToHexString(sig).ToLowerInvariant() }));
            }
            catch (Exception ex)
            {
                return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = ex.Message }));
            }
        }

        /// <summary>
        /// Returns the current Base contract state: validators, nonce, thresholds.
        /// </summary>
        [HttpGet]
        [Route("GetBaseContractState")]
        public async Task<ActionResult<string>> GetBaseContractState()
        {
            try
            {
                var rpcUrl = Bitcoin.Services.BaseBridgeService.BaseRpcUrl;
                var contractAddr = Bitcoin.Services.BaseBridgeService.ContractAddress;

                if (string.IsNullOrEmpty(rpcUrl) || string.IsNullOrEmpty(contractAddr))
                    return Ok(JsonConvert.SerializeObject(new { Success = false, Message = "Base bridge not configured" }));

                var web3 = new Nethereum.Web3.Web3(rpcUrl);
                var abi = @"[
                    {""inputs"":[],""name"":""getValidators"",""outputs"":[{""internalType"":""address[]"",""name"":"""",""type"":""address[]""}],""stateMutability"":""view"",""type"":""function""},
                    {""inputs"":[],""name"":""getAdminNonce"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},
                    {""inputs"":[],""name"":""validatorCount"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},
                    {""inputs"":[],""name"":""requiredMintSignatures"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},
                    {""inputs"":[],""name"":""requiredRemoveSignatures"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""}
                ]";

                var contract = web3.Eth.GetContract(abi, contractAddr);
                var validators = await contract.GetFunction("getValidators").CallAsync<List<string>>();
                var adminNonce = await contract.GetFunction("getAdminNonce").CallAsync<System.Numerics.BigInteger>();
                var valCount = await contract.GetFunction("validatorCount").CallAsync<System.Numerics.BigInteger>();
                var mintSigs = await contract.GetFunction("requiredMintSignatures").CallAsync<System.Numerics.BigInteger>();
                var removeSigs = await contract.GetFunction("requiredRemoveSignatures").CallAsync<System.Numerics.BigInteger>();

                return Ok(JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Validators = validators,
                    AdminNonce = adminNonce.ToString(),
                    ValidatorCount = valCount.ToString(),
                    RequiredMintSignatures = mintSigs.ToString(),
                    RequiredRemoveSignatures = removeSigs.ToString(),
                    Network = Bitcoin.Services.BaseBridgeService.BaseNetworkDisplayName
                }));
            }
            catch (Exception ex)
            {
                return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = ex.Message }));
            }
        }

        /// <summary>
        /// Receives a BurnAlert from another caster.
        /// </summary>
        [HttpPost]
        [Route("BurnAlert")]
        public ActionResult<string> BurnAlert([FromBody] Bitcoin.Services.BurnExitConsensusService.BurnAlert alert)
        {
            try
            {
                if (alert == null || string.IsNullOrEmpty(alert.BaseBurnTxHash))
                    return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = "Invalid alert" }));

                _ = Bitcoin.Services.BurnExitConsensusService.HandleBurnAlert(alert);
                return Ok(JsonConvert.SerializeObject(new { Success = true }));
            }
            catch (Exception ex)
            {
                return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = ex.Message }));
            }
        }

        /// <summary>
        /// Receives a BurnExitProposal from another caster.
        /// </summary>
        [HttpPost]
        [Route("BurnExitProposal")]
        public ActionResult<string> BurnExitProposal([FromBody] Bitcoin.Services.BurnExitConsensusService.BurnExitProposal proposal)
        {
            try
            {
                if (proposal == null || string.IsNullOrEmpty(proposal.BaseBurnTxHash))
                    return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = "Invalid proposal" }));

                Bitcoin.Services.BurnExitConsensusService.HandleBurnExitProposal(proposal);
                return Ok(JsonConvert.SerializeObject(new { Success = true }));
            }
            catch (Exception ex)
            {
                return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = ex.Message }));
            }
        }

        /// <summary>
        /// Receives a BurnExitConfirmation from another caster.
        /// </summary>
        [HttpPost]
        [Route("BurnExitConfirmation")]
        public ActionResult<string> BurnExitConfirmation([FromBody] Bitcoin.Services.BurnExitConsensusService.BurnExitConfirmation confirmation)
        {
            try
            {
                if (confirmation == null || string.IsNullOrEmpty(confirmation.BaseBurnTxHash))
                    return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = "Invalid confirmation" }));

                Bitcoin.Services.BurnExitConsensusService.HandleBurnExitConfirmation(confirmation);
                return Ok(JsonConvert.SerializeObject(new { Success = true }));
            }
            catch (Exception ex)
            {
                return BadRequest(JsonConvert.SerializeObject(new { Success = false, Message = ex.Message }));
            }
        }

        #endregion

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
                    CasterLogUtility.Log($"SendWinningProof REJECTED — banned IP {peerIP} height={blockHeight}", "PROOFDIAG");
                    return Unauthorized();
                }

                // FIX C: If the requested height is close to our tip (within 2 blocks),
                // spin-wait up to 2 seconds for CasterRoundDict to populate instead of
                // returning "0" immediately. This handles the timing skew where a newly-joined
                // caster generates proofs before peers have entered the same round.
                const int PROOF_WAIT_MS = 2000;
                const int PROOF_WAIT_POLL_MS = 100;
                var myHeight = Globals.LastBlock.Height;
                var heightDelta = blockHeight - myHeight;

                if (heightDelta >= 0 && heightDelta <= 2)
                {
                    var waitSw = System.Diagnostics.Stopwatch.StartNew();
                    while (waitSw.ElapsedMilliseconds < PROOF_WAIT_MS)
                    {
                        if (Globals.CasterRoundDict.TryGetValue(blockHeight, out var waitRound)
                            && waitRound?.Proof != null)
                        {
                            CasterLogUtility.Log($"SendWinningProof → {peerIP} height={blockHeight} result=proof addr={waitRound.Proof.Address} VRF={waitRound.Proof.VRFNumber} (after {waitSw.ElapsedMilliseconds}ms wait)", "PROOFDIAG");
                            return Ok(JsonConvert.SerializeObject(waitRound.Proof));
                        }
                        await Task.Delay(PROOF_WAIT_POLL_MS);
                    }
                    CasterLogUtility.Log($"SendWinningProof → {peerIP} height={blockHeight} result=0 (wait expired after {PROOF_WAIT_MS}ms, myHeight={myHeight})", "PROOFDIAG");
                    return Ok("0");
                }

                if (Globals.CasterRoundDict.ContainsKey(blockHeight))
                {
                    var round = Globals.CasterRoundDict[blockHeight];
                    if (round == null)
                    {
                        CasterLogUtility.Log($"SendWinningProof → {peerIP} height={blockHeight} result=0 (round null)", "PROOFDIAG");
                        return Ok("0");
                    }
                    if (round.Proof == null)
                    {
                        CasterLogUtility.Log($"SendWinningProof → {peerIP} height={blockHeight} result=0 (proof null, validator={round.Validator ?? "?"})", "PROOFDIAG");
                        return Ok("0");
                    }

                    CasterLogUtility.Log($"SendWinningProof → {peerIP} height={blockHeight} result=proof addr={round.Proof.Address} VRF={round.Proof.VRFNumber}", "PROOFDIAG");
                    return Ok(JsonConvert.SerializeObject(round.Proof));
                }

                CasterLogUtility.Log($"SendWinningProof → {peerIP} height={blockHeight} result=0 (no CasterRoundDict entry, myHeight={myHeight}, delta={heightDelta})", "PROOFDIAG");
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
                // different hash), causing non-winners to end up with a different block than the winner ? FORK.
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
        /// CASTER-CONSENSUS-FIX: Winner vote exchange endpoint for mandatory agreement phase.
        /// Receives a peer caster's winner vote, stores it in CasterWinnerVoteDict,
        /// and returns all known votes for that height so peers can converge.
        /// </summary>
        [HttpPost]
        [Route("ExchangeWinnerVote")]
        public ActionResult<string> ExchangeWinnerVote([FromBody] WinnerVoteRequest? req)
        {
            try
            {
                if (req == null || req.BlockHeight <= 0 || string.IsNullOrEmpty(req.VoterAddress) || string.IsNullOrEmpty(req.WinnerAddress))
                    return BadRequest("0");

                // Only accept votes from known casters
                var casterList = Globals.BlockCasters.ToList();
                if (!casterList.Any(c => c.ValidatorAddress == req.VoterAddress))
                    return BadRequest("0");

                // Store the incoming vote
                var votesForHeight = Globals.CasterWinnerVoteDict.GetOrAdd(req.BlockHeight, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
                votesForHeight[req.VoterAddress] = req.WinnerAddress;

                // Also ensure our own vote is present (if we have one from CasterRoundDict)
                if (!string.IsNullOrEmpty(Globals.ValidatorAddress) && !votesForHeight.ContainsKey(Globals.ValidatorAddress))
                {
                    if (Globals.CasterRoundDict.TryGetValue(req.BlockHeight, out var round) && round?.Proof != null)
                    {
                        votesForHeight[Globals.ValidatorAddress] = round.Proof.Address;
                    }
                }

                // Return all votes for this height
                var result = new { BlockHeight = req.BlockHeight, Votes = votesForHeight.ToDictionary(kv => kv.Key, kv => kv.Value) };
                return Ok(JsonConvert.SerializeObject(result));
            }
            catch { return BadRequest("0"); }
        }

        /// <summary>Receives a signed promotion to join the caster pool (dynamic discovery).
        /// Returns "accepted" if the node accepts, or a rejection reason string.
        /// The promoter waits for this response before adding the node to its caster list.</summary>
        [HttpPost]
        [Route("PromoteToCaster")]
        public async Task<ActionResult<string>> PromoteToCaster([FromBody] CasterPromotionRequest? req)
        {
            var remoteIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "?";
            try
            {
                if (req == null || string.IsNullOrEmpty(req.PromotedAddress))
                {
                    CasterLogUtility.Log(
                        $"HTTP /PromoteToCaster from {remoteIp}: REJECT invalid request (null or missing PromotedAddress)",
                        "CasterFlow");
                    return BadRequest("rejected: invalid request");
                }

                CasterLogUtility.Log(
                    $"HTTP /PromoteToCaster from {remoteIp} | promoter={req.PromoterAddress} promoted={req.PromotedAddress} " +
                    $"height={req.BlockHeight} casterListCount={(req.CasterList?.Count ?? 0)} self={Globals.ValidatorAddress}",
                    "CasterFlow");
                ConsoleWriterService.OutputValCaster(
                    $"[CasterFlow] HTTP /PromoteToCaster inbound from {remoteIp} (promoter={req.PromoterAddress})");

                if (req.PromotedAddress != Globals.ValidatorAddress)
                {
                    CasterLogUtility.Log(
                        $"HTTP /PromoteToCaster from {remoteIp}: REJECT — PromotedAddress='{req.PromotedAddress}' does not match self='{Globals.ValidatorAddress}'",
                        "CasterFlow");
                    return BadRequest("rejected: not for us");
                }
                var result = await CasterDiscoveryService.HandlePromotion(req);
                CasterLogUtility.Log(
                    $"HTTP /PromoteToCaster from {remoteIp}: returning '{result}' | IsBlockCaster(now)={Globals.IsBlockCaster} BlockCasters.Count(now)={Globals.BlockCasters.Count}",
                    "CasterFlow");
                return Ok(result);
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log(
                    $"HTTP /PromoteToCaster from {remoteIp}: EXCEPTION {ex.GetType().Name}: {ex.Message}",
                    "CasterFlow");
                return BadRequest($"rejected: {ex.Message}");
            }
        }


        /// <summary>Graceful caster departure notice (signed by departing caster).</summary>
        [HttpPost]
        [Route("AnnounceCasterDeparture")]
        public async Task<ActionResult<string>> AnnounceCasterDeparture([FromBody] CasterDepartureNotice? notice)
        {
            try
            {
                if (notice == null || string.IsNullOrEmpty(notice.DepartingAddress))
                    return BadRequest("invalid");
                if (!Globals.BlockCasters.Any(c => c.ValidatorAddress == notice.DepartingAddress))
                    return BadRequest("not a known caster");
                await CasterDiscoveryService.HandleDeparture(notice);
                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        /// <summary>Caster demotion notice (signed by a peer caster that detected the issue).</summary>
        [HttpPost]
        [Route("AnnounceCasterDemotion")]
        public async Task<ActionResult<string>> AnnounceCasterDemotion([FromBody] CasterDemotionNotice? notice)
        {
            try
            {
                if (notice == null || string.IsNullOrEmpty(notice.DemotedAddress))
                    return BadRequest("invalid");
                await CasterDiscoveryService.HandleDemotion(notice);
                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        /// <summary>Diagnostics: top caster candidates by balance.</summary>
        [HttpGet]
        [Route("GetCasterCandidates")]
        public ActionResult<string> GetCasterCandidates()
        {
            try
            {
                var currentCasterAddresses = Globals.BlockCasters.ToList()
                    .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                    .Select(c => c.ValidatorAddress!)
                    .ToHashSet();

                var candidates = Globals.NetworkValidators.Values
                    .Where(v => !string.IsNullOrEmpty(v.Address)
                             && !currentCasterAddresses.Contains(v.Address)
                             && v.IsFullyTrusted)
                    .Select(v => new
                    {
                        Address = v.Address,
                        IP = v.IPAddress,
                        Balance = AccountStateTrei.GetAccountBalance(v.Address),
                        LastSeen = v.LastSeen,
                        FailCount = v.CheckFailCount
                    })
                    .OrderByDescending(x => x.Balance)
                    .Take(10)
                    .ToList();

                return Ok(JsonConvert.SerializeObject(new
                {
                    CurrentCasters = Globals.BlockCasters.Count,
                    MaxCasters = CasterDiscoveryService.MaxCasters,
                    MinBalance = CasterDiscoveryService.MinCasterBalance,
                    SlotsAvailable = Math.Max(0, CasterDiscoveryService.MaxCasters - Globals.BlockCasters.Count),
                    Candidates = candidates
                }));
            }
            catch { return Ok("0"); }
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
        /// Old nodes won't have this endpoint ? 404 ? rejected as winner.
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
                var activeValidators = Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidators();

                return JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "Active validators retrieved",
                    CurrentBlock = currentBlock,
                    TotalValidators = activeValidators?.Count ?? 0,
                    ActiveValidators = activeValidators
                });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex.Message}" });
            }
        }

        #region FIX 4 + FIX 5 — GetCasters + ProposePromotion endpoints

        /// <summary>
        /// FIX 4: Returns the current BlockCasters list as JSON.
        /// Used by non-casters for self-recovery heartbeat.
        /// </summary>
        [HttpGet]
        [Route("GetCasters")]
        public ActionResult<string> GetCasters()
        {
            try
            {
                var casters = Globals.BlockCasters.ToList()
                    .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                    .Select(c => new CasterInfo
                    {
                        Address = c.ValidatorAddress!,
                        PeerIP = (c.PeerIP ?? "").Replace("::ffff:", ""),
                        PublicKey = c.ValidatorPublicKey ?? ""
                    })
                    .ToList();

                return Ok(JsonConvert.SerializeObject(new
                {
                    Height = Globals.LastBlock?.Height ?? 0,
                    Casters = casters
                }));
            }
            catch (Exception ex)
            {
                return Ok(JsonConvert.SerializeObject(new { Height = 0, Casters = new List<CasterInfo>(), Error = ex.Message }));
            }
        }

        /// <summary>
        /// FIX 5: Receives a promotion proposal from a peer caster.
        /// </summary>
        [HttpPost]
        [Route("ProposePromotion")]
        public async Task<ActionResult<string>> ProposePromotion([FromBody] PromotionProposalRequest? proposal)
        {
            try
            {
                if (proposal == null || string.IsNullOrEmpty(proposal.CandidateAddress))
                {
                    return Ok(JsonConvert.SerializeObject(new PromotionProposalResponse
                    {
                        Accepted = false,
                        Reason = "Invalid request",
                        ResponderAddress = Globals.ValidatorAddress ?? ""
                    }));
                }

                var result = await CasterDiscoveryService.EvaluatePromotionProposal(proposal);
                CasterLogUtility.Log(
                    $"HTTP /ProposePromotion from {HttpContext.Connection.RemoteIpAddress}: candidate={proposal.CandidateAddress} accepted={result.Accepted} reason={result.Reason}",
                    "CasterFlow");
                return Ok(JsonConvert.SerializeObject(result));
            }
            catch (Exception ex)
            {
                return Ok(JsonConvert.SerializeObject(new PromotionProposalResponse
                {
                    Accepted = false,
                    Reason = $"Error: {ex.Message}",
                    ResponderAddress = Globals.ValidatorAddress ?? ""
                }));
            }
        }

        #endregion
    }
}
