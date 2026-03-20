using Newtonsoft.Json;

namespace ReserveBlockCore.Models.Privacy
{
    /// <summary>
    /// Cleartext shielded note (Phase 3). Serialized to UTF-8 JSON, then sealed via ECDH/AES (<c>ShieldedNoteEncryption</c>).
    /// </summary>
    public sealed class ShieldedPlainNote
    {
        [JsonProperty("v")]
        public int Version { get; set; } = 1;

        [JsonProperty("amt")]
        public decimal Amount { get; set; }

        /// <summary>32-byte Pedersen randomness, Base64.</summary>
        [JsonProperty("r")]
        public string RandomnessB64 { get; set; } = "";

        [JsonProperty("asset")]
        public string AssetType { get; set; } = "";

        [JsonProperty("memo", NullValueHandling = NullValueHandling.Ignore)]
        public string? Memo { get; set; }
    }
}
