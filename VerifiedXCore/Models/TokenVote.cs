using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Models
{
    public class TokenVote
    {
        #region Variables
        public int Id { get; set; }
        public string SmartContractUID { get; set; }
        public string TopicUID { get; set; }
        public string Address { get; set; }
        public VoteType VoteType { get; set; }
        public string TransactionHash { get; set; }
        public long BlockHeight { get; set; }

        #endregion

        #region Get Votes DB
        public static LiteDB.ILiteCollection<TokenVote>? GetTokenVoteDB()
        {
            try
            {
                var votes = DbContext.DB_Wallet.GetCollection<TokenVote>(DbContext.RSRV_TOKEN_VOTE);
                return votes;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "TokenVote.GetTokenVoteDB()");
                return null;
            }

        }

        #endregion

        #region Save Vote
        public static bool SaveVote(TokenVote vote)
        {
            var votes = GetTokenVoteDB();
            if (votes == null)
            {
                ErrorLogUtility.LogError("GetTokenVoteDB() returned a null value.", "TokenVote.SaveVote()");
                return false;
            }
            else
            {
                var voteRecData = votes.Query().Where(x => x.TopicUID == vote.TopicUID && x.Address == vote.Address).ToList().OrderBy(x => x.Id).FirstOrDefault();
                if (voteRecData != null)
                {
                    return false;
                }
                else
                {
                    votes.InsertSafe(vote);
                    return true;
                }
            }

        }
        #endregion

        #region Get Specific Topic Votes by TUID
        public static TokenVote? GetSpecificTopicVotesByTUID(string tUID)
        {
            var votes = GetTokenVoteDB();
            if (votes != null)
            {
                var vote = votes.Query().Where(x => x.TopicUID == tUID).ToList().OrderBy(x => x.Id).FirstOrDefault();
                if (vote != null)
                {
                    return vote;
                }
            }
            return null;
        }
        #endregion

        #region Get Specific Topic Votes by SCUID
        public static TokenVote? GetSpecificTopicVotesBySCUID(string scUID)
        {
            var votes = GetTokenVoteDB();
            if (votes != null)
            {
                var vote = votes.Query().Where(x => x.SmartContractUID == scUID).ToList().OrderBy(x => x.Id).FirstOrDefault();
                if (vote != null)
                {
                    return vote;
                }
            }
            return null;
        }
        #endregion

        #region Get Specific Address Vote For Topic - Returns bool
        public static bool CheckSpecificAddressTokenVoteOnTopic(string address, string topicUID)
        {
            var votes = GetTokenVoteDB();
            if (votes != null)
            {
                var vote = votes.Query().Where(x => x.Address == address && x.TopicUID == topicUID).ToList().OrderBy(x => x.Id).FirstOrDefault();
                if (vote != null)
                {
                    return true;
                }
            }
            return false;
        }
        #endregion

        #region Get Specific Address Votes
        public static IEnumerable<TokenVote>? GetSpecificAddressVotes(string address)
        {
            var votes = GetTokenVoteDB();
            if (votes != null)
            {
                var voteList = votes.Query().Where(x => x.Address == address).ToList().OrderBy(x => x.Id);
                if (voteList?.Count() > 0)
                {
                    return voteList;
                }
            }
            return null;
        }
        #endregion

        #region Get Specific Address Votes
        public static IEnumerable<TokenVote>? GetOwnerVoteList(string tUID)
        {
            var votes = GetTokenVoteDB();
            if (votes != null)
            {
                var voteList = votes.Query().Where(x => x.TopicUID == tUID).ToList().OrderBy(x => x.Id);
                if (voteList?.Count() > 0)
                {
                    return voteList;
                }
            }
            return null;
        }
        #endregion
    }
}
