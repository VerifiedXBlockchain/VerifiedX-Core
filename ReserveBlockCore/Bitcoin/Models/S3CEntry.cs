namespace ReserveBlockCore.Bitcoin.Models
{
    /// <summary>
    /// One entry in a minting node's S3C= config: an IP:ValidatorAddress pair naming a
    /// private validator that exclusively handles this node's vBTC DKG + withdrawal signing.
    /// Validators are contacted at this configured IP (authoritative — §4.2); the registry is
    /// only used to verify the address is an active, IsS3C-flagged validator.
    /// </summary>
    public class S3CEntry
    {
        public string IPAddress { get; set; }
        public string ValidatorAddress { get; set; }
    }
}
