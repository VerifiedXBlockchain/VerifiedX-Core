using VerifiedXCore.Utilities;
using System.Security.Cryptography;
using System.Text;

namespace VerifiedXCore.Models
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

        /// <summary>
        /// VX-05: a proof is valid only if it is exactly what anyone could recompute from public data —
        /// PublicKey derives Address, and VRFNumber and ProofHash are the values the VRF yields for
        /// (PublicKey, BlockHeight, PreviousBlockHash). Before, only ProofHash was checked, so Address
        /// and VRFNumber were free fields (a forged proof with PublicKey "aa" and VRFNumber 0 won).
        /// Round binding and eligibility are checked at ingress by ProofUtility.ValidateIncomingProof.
        /// </summary>
        public bool VerifyProof()
        {
            try
            {
                return ProofUtility.VerifyProofBinding(this);
            }
            catch { return false; }
        }
    }
}
