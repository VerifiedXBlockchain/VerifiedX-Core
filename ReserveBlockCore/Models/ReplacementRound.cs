namespace ReserveBlockCore.Models
{
    public class ReplacementRound
    {
        public string RoundId { get; set; }
        public string MissingCasterAddress { get; set; }  // Store just the address of missing caster
        public byte[] SharedRandomness { get; set; }
        public Dictionary<string, byte[]> RandomnessContributions { get; set; } = new Dictionary<string, byte[]>();
        public DateTime StartTime { get; set; }
    }
}
