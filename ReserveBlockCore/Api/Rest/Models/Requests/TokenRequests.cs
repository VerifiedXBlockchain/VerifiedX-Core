using System.ComponentModel.DataAnnotations;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Api.Rest.Models.Requests
{
    public class TransferTokenRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string ToAddress { get; set; } = "";
        [Required]
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
    }

    public class BurnTokenRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
    }

    public class MintTokenRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        [Range(0.00000001, (double)decimal.MaxValue, ErrorMessage = "Amount must be greater than zero.")]
        public decimal Amount { get; set; }
    }

    public class PauseTokenRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
    }

    public class BanAddressRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string BanAddress { get; set; } = "";
    }

    public class TransferOwnershipRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string ToAddress { get; set; } = "";
    }

    public class CreateTokenTopicRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public string TopicName { get; set; } = "";
        [Required]
        public string TopicDescription { get; set; } = "";
        [Required]
        public long MinimumVoteRequirement { get; set; }
        [Required]
        public VotingDays VotingEndDays { get; set; }
    }

    public class CastTokenVoteRequest
    {
        [Required]
        public string FromAddress { get; set; } = "";
        [Required]
        public VoteType VoteType { get; set; }
    }
}
