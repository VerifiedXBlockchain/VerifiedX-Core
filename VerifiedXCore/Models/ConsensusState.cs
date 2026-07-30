using VerifiedXCore.Extensions;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Models
{
    public enum ConsensusStatus : byte
    {
        Processing,
        Finalized        
    }

    public class ConsensusState
    {       
        public long Id { get; set; }
        public long Height { get; set; }
        public int MethodCode { get; set; }
        public ConsensusStatus Status { get; set; }                
        public int RandomNumber { get; set; }
        public string EncryptedAnswer { get; set; }
        public bool IsUsed { get; set; }
    }
}
