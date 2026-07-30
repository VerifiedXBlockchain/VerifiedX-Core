using VerifiedXCore.Extensions;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Models
{
    public class ConsensusHeader
    {
        public long Height { get; set; }
        public string WinningAddress { get; set; }
        public long Timestamp { get; set; }
        public int NetworkValidatorCount { get; set; }
        public List<string> ValidatorAddressReceiveList { get; set; } = new List<string>();
        public List<string> ValidatorAddressFailList { get; set; } = new List<string>();
        public int ReceiveCount { get { return ValidatorAddressReceiveList == null ? 0 : ValidatorAddressReceiveList.Count(); } }
        public int TotalConsensusCount { get { return ValidatorAddressReceiveList == null || ValidatorAddressFailList == null ? 0 : ValidatorAddressReceiveList.Count() + ValidatorAddressFailList.Count(); } }
    }
}
