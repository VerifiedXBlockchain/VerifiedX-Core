using System.ComponentModel.DataAnnotations;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class CreateTopicRequest
    {
        [Required]
        public string TopicName { get; set; } = "";
        [Required]
        public string TopicDescription { get; set; } = "";
        [Required]
        public VotingDays VotingEndDays { get; set; }
        [Required]
        public VoteTopicCategories VoteTopicCategory { get; set; }
    }

    public class CastVoteRequest
    {
        [Required]
        public VoteType VoteType { get; set; }
    }
}
