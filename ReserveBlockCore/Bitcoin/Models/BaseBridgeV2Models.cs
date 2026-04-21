using ReserveBlockCore;

namespace ReserveBlockCore.Bitcoin.Models
{
    public class MintAttestationState
    {
        public string LockId { get; set; } = string.Empty;
        public string SmartContractUID { get; set; } = string.Empty;
        public string EvmDestination { get; set; } = string.Empty;
        public long AmountSats { get; set; }
        public long Nonce { get; set; }
        public long ChainId { get; set; }
        public string ContractAddress { get; set; } = string.Empty;
        public Dictionary<string, string> ValidatorSignatures { get; set; } = new();
        public int RequiredSignatures { get; set; }
        public bool IsReady => ValidatorSignatures.Count >= RequiredSignatures;
        public long CreatedAt { get; set; }
        public string Status { get; set; } = "Pending";
    }

    public class MintAttestationRequest
    {
        public string LockId { get; set; } = string.Empty;
        public string EvmDestination { get; set; } = string.Empty;
        public long AmountSats { get; set; }
        public long Nonce { get; set; }
        public long ChainId { get; set; }
        public string ContractAddress { get; set; } = string.Empty;
        public string SmartContractUID { get; set; } = string.Empty;
    }

    public class ValidatorUpdateSigningRequest
    {
        public string UpdateType { get; set; } = string.Empty;
        public List<string> BaseAddresses { get; set; } = new();
        public string ValidatorAddress { get; set; } = string.Empty;
        public long VfxBlockHeight { get; set; }
        public long AdminNonce { get; set; }
        public long ChainId { get; set; }
        public string ContractAddress { get; set; } = string.Empty;
        public CasterEndorsement? CasterEndorsement { get; set; }
    }

    public class CasterEndorsement
    {
        public string UpdateType { get; set; } = string.Empty;
        public List<string> BaseAddresses { get; set; } = new();
        public long VfxBlockHeight { get; set; }
        public long Timestamp { get; set; }
        public Dictionary<string, string> CasterSignatures { get; set; } = new();

        public int RequiredCasterSignatures => Math.Max(2, Globals.ActiveCasterCount / 2 + 1);

        public bool IsFresh(long nowUnix)
        {
            return CasterSignatures.Count >= RequiredCasterSignatures && nowUnix - Timestamp < 600;
        }
    }

    public class BurnAlert
    {
        public string BaseBurnTxHash { get; set; } = string.Empty;
        public string BurnType { get; set; } = "";
        public string BurnerAddress { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Destination { get; set; } = string.Empty;
        public long DetectedAtBaseBlock { get; set; }
        public string DetectedByCasterId { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public string Signature { get; set; } = string.Empty;
    }

    public class BurnExitProposal
    {
        public string BaseBurnTxHash { get; set; } = string.Empty;
        public string BurnType { get; set; } = "";
        public string ProposedHandler { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public string Signature { get; set; } = string.Empty;
    }

    public class BurnExitConfirmation
    {
        public string BaseBurnTxHash { get; set; } = string.Empty;
        public string AgreedHandler { get; set; } = string.Empty;
        public string ConfirmerAddress { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public string Signature { get; set; } = string.Empty;
    }

    public class BurnExitConsensusResult
    {
        public bool Success { get; set; }
        public string AgreedHandler { get; set; } = string.Empty;
        public Dictionary<string, string> ConfirmationSignatures { get; set; } = new();
        public bool IsThisNodeTheHandler { get; set; }
        public string? Error { get; set; }
    }

    public class CasterConsensusVote
    {
        public string CasterAddress { get; set; } = string.Empty;
        public string BaseBurnTxHash { get; set; } = string.Empty;
        public string BurnType { get; set; } = "";
        public long Timestamp { get; set; }
        public string Signature { get; set; } = string.Empty;
    }
}
