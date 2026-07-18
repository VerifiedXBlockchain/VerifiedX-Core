using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class VotingController : RestBaseController
    {
        /// <summary>
        /// List topics with optional status filter and pagination
        /// </summary>
        [HttpGet("topics")]
        public IActionResult GetTopics([FromQuery] PaginationParams paging, [FromQuery] string? status = null)
        {
            List<TopicTrei> topicList;

            if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                topicList = TopicTrei.GetActiveTopics()?.ToList() ?? new List<TopicTrei>();
            }
            else if (string.Equals(status, "inactive", StringComparison.OrdinalIgnoreCase))
            {
                topicList = TopicTrei.GetInactiveTopics()?.ToList() ?? new List<TopicTrei>();
            }
            else
            {
                var topicsDb = TopicTrei.GetTopics();
                topicList = topicsDb?.FindAll().ToList() ?? new List<TopicTrei>();
            }

            var totalCount = topicList.Count;
            var paged = topicList
                .Skip((paging.Page - 1) * paging.PageSize)
                .Take(paging.PageSize)
                .ToList();

            return OkPaged(paged, paging.Page, paging.PageSize, totalCount);
        }

        /// <summary>
        /// Get topic details
        /// </summary>
        [HttpGet("topics/{topicUID}")]
        public IActionResult GetTopicDetails(string topicUID)
        {
            var topic = TopicTrei.GetSpecificTopic(topicUID);
            if (topic == null)
                return Fail("NOT_FOUND", $"Topic not found: {topicUID}", 404);

            return Ok(topic);
        }

        /// <summary>
        /// Create a new vote topic
        /// </summary>
        [HttpPost("topics")]
        public async Task<IActionResult> CreateTopic([FromBody] CreateTopicRequest request)
        {
            var topic = new TopicTrei
            {
                TopicName = request.TopicName,
                TopicDescription = request.TopicDescription,
            };

            if (request.VoteTopicCategory == VoteTopicCategories.AdjVoteIn)
            {
                var adjVoteReqs = JsonConvert.DeserializeObject<AdjVoteInReqs>(topic.TopicDescription);
                if (adjVoteReqs != null)
                {
                    var voteReqsResult = VoteValidatorService.ValidateAdjVoteIn(adjVoteReqs);
                    if (!voteReqsResult)
                        return Fail("VALIDATION_ERROR", "You did not meet the required specs or information was not completed. This topic has been cancelled.");
                }
                else
                {
                    return Fail("VALIDATION_ERROR", "For this topic you must complete the Adj Vote in Requirements.");
                }
            }

            topic.Build(request.VotingEndDays, request.VoteTopicCategory);

            var result = await TopicTrei.CreateTopicTx(topic);
            if (result.Item1 == null)
                return Fail("TOPIC_FAILED", result.Item2);

            return Created(new { TxHash = result.Item1.Hash, Topic = topic });
        }

        /// <summary>
        /// Cast a vote on a topic
        /// </summary>
        [HttpPost("topics/{topicUID}/vote")]
        public async Task<IActionResult> CastVote(string topicUID, [FromBody] CastVoteRequest request)
        {
            var topic = TopicTrei.GetSpecificTopic(topicUID);
            if (topic == null)
                return Fail("NOT_FOUND", $"Topic not found: {topicUID}", 404);

            var voteCreate = new Vote.VoteCreate
            {
                TopicUID = topicUID,
                VoteType = request.VoteType
            };

            var vote = new Vote();
            vote.Build(voteCreate);

            var result = await Vote.CreateVoteTx(vote);
            if (result.Item1 == null)
                return Fail("VOTE_FAILED", result.Item2);

            return Ok(new { TxHash = result.Item1.Hash });
        }

        /// <summary>
        /// Get votes for a specific topic
        /// </summary>
        [HttpGet("topics/{topicUID}/votes")]
        public IActionResult GetTopicVotes(string topicUID)
        {
            var topicVotes = Vote.GetSpecificTopicVotes(topicUID);
            if (topicVotes == null || !topicVotes.Any())
                return Ok(Array.Empty<object>());

            return Ok(topicVotes);
        }

        /// <summary>
        /// Get topics created by the current validator
        /// </summary>
        [HttpGet("my/topics")]
        public IActionResult GetMyTopics()
        {
            var address = Globals.ValidatorAddress;
            var topics = TopicTrei.GetSpecificTopicByAddress(address);
            if (topics == null || !topics.Any())
                return Ok(Array.Empty<object>());

            return Ok(topics);
        }

        /// <summary>
        /// Get votes cast by the current validator
        /// </summary>
        [HttpGet("my/votes")]
        public IActionResult GetMyVotes()
        {
            var address = Globals.ValidatorAddress;
            var myVotes = Vote.GetSpecificAddressVotes(address);
            if (myVotes == null || !myVotes.Any())
                return Ok(Array.Empty<object>());

            return Ok(myVotes);
        }
    }
}
