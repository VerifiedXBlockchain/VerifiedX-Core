using ReserveBlockCore.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace ReserveBlockCore.Models
{
    public class Proof
    {
        public string Address { get; set; }
        public string PublicKey { get; set; }
        public long BlockHeight { get; set; }
        public string PreviousBlockHash { get; set; }
        public uint VRFNumber { get; set; }
        public string ProofHash { get; set; }
        public string IPAddress { get; set; }

        public bool VerifyProof()
        {
            try
            {
                var proofResult = ProofUtility.VerifyProof(PublicKey, BlockHeight, PreviousBlockHash, ProofHash);

                return proofResult;
            }
            catch { return false; }
        }
    }
}
