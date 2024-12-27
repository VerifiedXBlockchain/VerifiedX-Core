namespace ReserveBlockCore.Models
{
    public class ReplacementRound
    {
        public string RoundId { get; set; }
        public bool IsBootstrap { get; set; }  // Flag for bootstrap vs replacement
        public NodeInfo? MissingCaster { get; set; }  // Will be null during bootstrap
        public byte[] SharedRandomness { get; set; }
        public Dictionary<string, byte[]> RandomnessContributions { get; set; } = new Dictionary<string, byte[]>();
        public DateTime StartTime { get; set; }
    }
}
