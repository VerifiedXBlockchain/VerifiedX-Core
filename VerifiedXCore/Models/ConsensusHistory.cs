using VerifiedXCore.Extensions;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Models
{
    public class ConsensusHistory
    {
        public long Id { get; set; }
        public long Height { get; set; }
        public int MethodCode { get; set; }
        public string SendingAddress { get; set; }
        public string MessageAddress { get; set; }
    }
}
