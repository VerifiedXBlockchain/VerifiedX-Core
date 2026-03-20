using Newtonsoft.Json;
using ReserveBlockCore.Privacy;

namespace ReserveBlockCore.Models.Privacy
{
    /// <summary>
    /// On-chain privacy payload stored in <see cref="Transaction.Data"/> as JSON or Base64(JSON).
    /// Ordering for rebuild: block height → tx order (see <see cref="Privacy.PrivateTransactionTypes"/>) → output <see cref="PrivateShieldedOutput.Index"/>.
    /// </summary>
    public sealed class PrivateTxPayload
    {
        [JsonProperty("v")]
        public int Version { get; set; } = 1;

        /// <summary>Logical kind, e.g. shield, unshield, private_transfer (optional cross-check vs <see cref="Transaction.TransactionType"/>).</summary>
        [JsonProperty("kind")]
        public string? Kind { get; set; }

        /// <summary>Asset key for pool state, e.g. VFX or a vBTC contract-specific id.</summary>
        [JsonProperty("asset")]
        public string Asset { get; set; } = "";

        [JsonProperty("outs")]
        public List<PrivateShieldedOutput> Outs { get; set; } = new();

        [JsonProperty("nulls")]
        public List<string> NullsB64 { get; set; } = new();

        /// <summary>Optional vBTC contract UID when asset is vBTC-derived.</summary>
        [JsonProperty("vbtc_uid")]
        public string? VbtcContractUid { get; set; }

        public bool TryValidateStructure([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
        {
            error = null;
            if (Version != 1)
            {
                error = "PrivateTxPayload version must be 1.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(Asset) || Asset.Length > 64)
            {
                error = "PrivateTxPayload asset is required (max 64 chars).";
                return false;
            }

            var seen = new HashSet<int>();
            foreach (var o in Outs)
            {
                if (o == null)
                {
                    error = "PrivateTxPayload outs contains a null entry.";
                    return false;
                }
                if (o.Index < 0)
                {
                    error = "PrivateTxPayload output index must be non-negative.";
                    return false;
                }
                if (!seen.Add(o.Index))
                {
                    error = "PrivateTxPayload duplicate output index.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(o.CommitmentB64))
                {
                    error = "PrivateTxPayload output commitment is required.";
                    return false;
                }
                byte[] g1;
                try
                {
                    g1 = Convert.FromBase64String(o.CommitmentB64);
                }
                catch
                {
                    error = "PrivateTxPayload output commitment is not valid Base64.";
                    return false;
                }
                if (g1.Length != PlonkNative.G1CompressedSize)
                {
                    error = $"PrivateTxPayload output commitment must decode to {PlonkNative.G1CompressedSize} bytes (G1 compressed).";
                    return false;
                }
            }

            for (var i = 0; i < NullsB64.Count; i++)
            {
                var nb = NullsB64[i];
                if (string.IsNullOrWhiteSpace(nb))
                {
                    error = "PrivateTxPayload nullifier entry is empty.";
                    return false;
                }
                try
                {
                    var n = Convert.FromBase64String(nb);
                    if (n.Length != PlonkNative.ScalarSize)
                    {
                        error = $"PrivateTxPayload nullifier must decode to {PlonkNative.ScalarSize} bytes.";
                        return false;
                    }
                }
                catch
                {
                    error = "PrivateTxPayload nullifier is not valid Base64.";
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class PrivateShieldedOutput
    {
        [JsonProperty("i")]
        public int Index { get; set; }

        /// <summary>G1 compressed Pedersen commitment, Base64.</summary>
        [JsonProperty("c")]
        public string CommitmentB64 { get; set; } = "";
    }
}
