using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class TokensController : RestBaseController
    {
        /// <summary>
        /// Get token info by scUID, or all tokens if getAll query param is true
        /// </summary>
        [HttpGet("{scUID}")]
        public IActionResult GetToken(string scUID, [FromQuery] bool getAll = false)
        {
            if (getAll)
            {
                var tokenList = Globals.Tokens.Values.ToList();
                if (tokenList.Count == 0)
                    return Fail("NOT_FOUND", "No tokens found in memory. Please restart wallet and try again.", 404);

                return Ok(tokenList);
            }

            if (Globals.Tokens.TryGetValue(scUID, out var token))
                return Ok(token);

            var tokenState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (tokenState == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (tokenState.TokenDetails == null)
                return Fail("NOT_FOUND", "Could not locate the token details.", 404);

            Globals.Tokens.TryAdd(scUID, tokenState.TokenDetails);

            return Ok(tokenState.TokenDetails);
        }

        /// <summary>
        /// Transfer tokens
        /// </summary>
        [HttpPost("{scUID}/transfer")]
        public async Task<IActionResult> TransferToken(string scUID, [FromBody] TransferTokenRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails != null && sc.TokenDetails.IsPaused)
                return Fail("PAUSED", "Contract has been paused.", 409);

            var toAddress = request.ToAddress.Replace(" ", "").ToAddressNormalize();

            if (!request.FromAddress.StartsWith("xRBX"))
            {
                var account = AccountData.GetSingleAccount(request.FromAddress);
                if (account == null)
                    return Fail("NOT_FOUND", "Account does not exist locally.", 404);
            }
            else
            {
                var rAccount = ReserveAccount.GetReserveAccountSingle(request.FromAddress);
                if (rAccount == null)
                    return Fail("NOT_FOUND", "Reserve account does not exist locally.", 404);
            }

            var stateAccount = StateData.GetSpecificAccountStateTrei(request.FromAddress);
            if (stateAccount == null)
                return Fail("NOT_FOUND", "Account does not exist at the state level.", 404);

            if (stateAccount.TokenAccounts?.Count == 0)
                return Fail("NO_TOKENS", "Account does not have any token accounts.");

            var tokenAccount = stateAccount.TokenAccounts?.Where(x => x.SmartContractUID == scUID).FirstOrDefault();
            if (tokenAccount == null)
                return Fail("NO_TOKENS", $"Account does not own any of the token {sc.TokenDetails?.TokenName}.");

            if (tokenAccount.Balance < request.Amount)
                return Fail("INSUFFICIENT_BALANCE", $"Insufficient balance. Current: {tokenAccount.Balance} - Attempted: {request.Amount}");

            var result = await TokenContractService.TransferToken(sc, tokenAccount, request.FromAddress, toAddress, request.Amount);

            if (!result.Item1)
                return Fail("TRANSFER_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Burn tokens
        /// </summary>
        [HttpPost("{scUID}/burn")]
        public async Task<IActionResult> BurnToken(string scUID, [FromBody] BurnTokenRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails != null && sc.TokenDetails.IsPaused)
                return Fail("PAUSED", "Contract has been paused.", 409);

            var account = AccountData.GetSingleAccount(request.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "Account does not exist locally.", 404);

            var stateAccount = StateData.GetSpecificAccountStateTrei(request.FromAddress);
            if (stateAccount == null)
                return Fail("NOT_FOUND", "Account does not exist at the state level.", 404);

            if (stateAccount.TokenAccounts?.Count == 0)
                return Fail("NO_TOKENS", "Account does not have any token accounts.");

            var tokenAccount = stateAccount.TokenAccounts?.Where(x => x.SmartContractUID == scUID).FirstOrDefault();
            if (tokenAccount == null)
                return Fail("NO_TOKENS", $"Account does not own any of the token {sc.TokenDetails?.TokenName}.");

            if (tokenAccount.Balance < request.Amount)
                return Fail("INSUFFICIENT_BALANCE", $"Insufficient balance. Current: {tokenAccount.Balance} - Attempted: {request.Amount}");

            var result = await TokenContractService.BurnToken(sc, tokenAccount, request.FromAddress, request.Amount);

            if (!result.Item1)
                return Fail("BURN_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Mint new tokens (infinite supply only)
        /// </summary>
        [HttpPost("{scUID}/mint")]
        public async Task<IActionResult> MintToken(string scUID, [FromBody] MintTokenRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails == null)
                return Fail("NOT_FOUND", "Token details are null.", 404);

            if (sc.TokenDetails.IsPaused)
                return Fail("PAUSED", "Contract has been paused.", 409);

            var account = AccountData.GetSingleAccount(request.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "Account does not exist locally.", 404);

            if (account.Address != sc.TokenDetails.ContractOwner)
                return Fail("NOT_OWNER", "Account does not own this token contract.", 403);

            if (sc.TokenDetails.StartingSupply > 0.0M)
                return Fail("NOT_INFINITE", "Token supply was not set to infinite.");

            var result = await TokenContractService.TokenMint(sc, request.FromAddress, request.Amount);

            if (!result.Item1)
                return Fail("MINT_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Toggle pause on token contract
        /// </summary>
        [HttpPost("{scUID}/pause")]
        public async Task<IActionResult> PauseToken(string scUID, [FromBody] PauseTokenRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails == null)
                return Fail("NOT_FOUND", "Token details are null.", 404);

            var pause = !sc.TokenDetails.IsPaused;

            var account = AccountData.GetSingleAccount(request.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "Account does not exist locally.", 404);

            if (account.Address != sc.TokenDetails.ContractOwner)
                return Fail("NOT_OWNER", "Account does not own this token contract.", 403);

            var result = await TokenContractService.PauseTokenContract(sc, request.FromAddress, pause);

            if (!result.Item1)
                return Fail("PAUSE_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Ban an address from the token contract
        /// </summary>
        [HttpPost("{scUID}/ban")]
        public async Task<IActionResult> BanAddress(string scUID, [FromBody] BanAddressRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails == null)
                return Fail("NOT_FOUND", "Token details are null.", 404);

            var account = AccountData.GetSingleAccount(request.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "Account does not exist locally.", 404);

            if (account.Address != sc.TokenDetails.ContractOwner)
                return Fail("NOT_OWNER", "Account does not own this token contract.", 403);

            var banAddress = request.BanAddress.Replace(" ", "").ToAddressNormalize();

            var result = await TokenContractService.BanAddress(sc, request.FromAddress, banAddress);

            if (!result.Item1)
                return Fail("BAN_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Transfer token contract ownership
        /// </summary>
        [HttpPost("{scUID}/transfer-ownership")]
        public async Task<IActionResult> TransferOwnership(string scUID, [FromBody] TransferOwnershipRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails == null)
                return Fail("NOT_FOUND", "Token details are null.", 404);

            if (!request.FromAddress.StartsWith("xRBX"))
            {
                var account = AccountData.GetSingleAccount(request.FromAddress);
                if (account == null)
                    return Fail("NOT_FOUND", "Account does not exist locally.", 404);
                if (account.Address != sc.TokenDetails.ContractOwner)
                    return Fail("NOT_OWNER", "Account does not own this token contract.", 403);
            }
            else
            {
                var rAccount = ReserveAccount.GetReserveAccountSingle(request.FromAddress);
                if (rAccount == null)
                    return Fail("NOT_FOUND", "Reserve account does not exist locally.", 404);
                if (rAccount.Address != sc.TokenDetails.ContractOwner)
                    return Fail("NOT_OWNER", "Reserve account does not own this token contract.", 403);
            }

            var toAddress = request.ToAddress.Replace(" ", "").ToAddressNormalize();
            var result = await TokenContractService.ChangeTokenContractOwnership(sc, request.FromAddress, toAddress);

            if (!result.Item1)
                return Fail("TRANSFER_FAILED", result.Item2);

            return Ok(result.Item2);
        }

        /// <summary>
        /// Get votes for a token contract
        /// </summary>
        [HttpGet("{scUID}/votes")]
        public IActionResult GetVotes(string scUID)
        {
            var vote = TokenVote.GetSpecificTopicVotesBySCUID(scUID);
            if (vote == null)
                return Fail("NOT_FOUND", $"Could not find votes for scUID: {scUID}", 404);

            return Ok(vote);
        }

        /// <summary>
        /// Create a vote topic for a token community
        /// </summary>
        [HttpPost("{scUID}/topics")]
        public async Task<IActionResult> CreateTopic(string scUID, [FromBody] CreateTokenTopicRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails == null)
                return Fail("NOT_FOUND", "Token details are null.", 404);

            var account = AccountData.GetSingleAccount(request.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "Account does not exist locally.", 404);

            if (account.Address != sc.TokenDetails.ContractOwner)
                return Fail("NOT_OWNER", "Account does not own this token contract.", 403);

            var topic = new TokenVoteTopic
            {
                TopicName = request.TopicName,
                TopicDescription = request.TopicDescription,
                SmartContractUID = scUID,
                MinimumVoteRequirement = request.MinimumVoteRequirement,
            };

            var buildResult = topic.Build(request.VotingEndDays);
            if (!buildResult)
                return Fail("BUILD_FAILED", "Failed to create topic. Check name/description length constraints.");

            var result = await TokenContractService.CreateTokenVoteTopic(sc, request.FromAddress, topic);

            if (!result.Item1)
                return Fail("TOPIC_FAILED", result.Item2);

            return Created(new { Topic = topic, Message = result.Item2 });
        }

        /// <summary>
        /// Cast a vote on a token topic
        /// </summary>
        [HttpPost("{scUID}/topics/{topicUID}/vote")]
        public async Task<IActionResult> CastVote(string scUID, string topicUID, [FromBody] CastTokenVoteRequest request)
        {
            var sc = SmartContractStateTrei.GetSmartContractState(scUID);
            if (sc == null)
                return Fail("NOT_FOUND", "Could not locate the requested smart contract.", 404);

            if (sc.IsToken == null || sc.IsToken.Value == false)
                return Fail("NOT_TOKEN", "Smart contract is not a token contract.");

            if (sc.TokenDetails != null && sc.TokenDetails.IsPaused)
                return Fail("PAUSED", "Contract has been paused.", 409);

            var topic = sc.TokenDetails?.TokenTopicList?.Where(x => x.TopicUID == topicUID).FirstOrDefault();
            if (topic == null)
                return Fail("NOT_FOUND", "Topic was not found.", 404);

            var account = AccountData.GetSingleAccount(request.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "Account does not exist locally.", 404);

            var stateAccount = StateData.GetSpecificAccountStateTrei(request.FromAddress);
            if (stateAccount == null)
                return Fail("NOT_FOUND", "Account does not exist at the state level.", 404);

            if (stateAccount.TokenAccounts?.Count == 0)
                return Fail("NO_TOKENS", "Account does not have any token accounts.");

            var tokenAccount = stateAccount.TokenAccounts?.Where(x => x.SmartContractUID == scUID).FirstOrDefault();
            if (tokenAccount == null)
                return Fail("NO_TOKENS", $"Account does not own any of the token {sc.TokenDetails?.TokenName}.");

            if (tokenAccount.Balance < topic.MinimumVoteRequirement)
                return Fail("MINIMUM_NOT_MET", "You do not meet the minimum token balance required to vote.");

            var voteExist = TokenVote.CheckSpecificAddressTokenVoteOnTopic(request.FromAddress, topicUID);
            if (voteExist)
                return Fail("ALREADY_VOTED", "This address has already cast a vote.", 409);

            var result = await TokenContractService.CastTokenVoteTopic(sc, tokenAccount, request.FromAddress, topicUID, request.VoteType);

            if (!result.Item1)
                return Fail("VOTE_FAILED", result.Item2);

            return Ok(result.Item2);
        }
    }
}
