using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// VX-02: binds the identity declared INSIDE a minted/deployed contract body to the signed
    /// transaction that carries it.
    ///
    /// The TokenDeploy() apply used to decompile the attacker-supplied body and credit the token
    /// supply under the body's own SmartContractUID and to the body's own MinterAddress. Neither was
    /// compared to the transaction, so a deploy under a fresh UID could credit an arbitrary supply of
    /// an EXISTING token to any address. Every legitimate emitter builds the transaction from the same
    /// SmartContractMain (tx ContractUID = body UID, tx FromAddress = body MinterAddress), so binding
    /// them is invisible to honest use.
    /// </summary>
    public static class SmartContractDeployBinding
    {
        /// <summary>
        /// Upper bound on a TokenDeploy() initial supply (whole token units). This is the ceiling the
        /// platform already had in practice: the wallet emitter (Token.TotalSupply) and the decompiler
        /// (GetTokenDetails() is deserialized into Token.TotalSupply) both carry supply as a 32-bit int,
        /// so a larger value cannot decompile at all. Stated here so the bound is explicit.
        /// </summary>
        public const long MaxTokenSupply = int.MaxValue;

        /// <summary>Upper bound on token decimal places.</summary>
        public const int MaxTokenDecimalPlaces = 18;

        /// <summary>
        /// Extracts (Function, ContractUID, Data) from a mint/deploy transaction payload. Accepts the
        /// array shape every emitter produces (<c>[{"Function":..,"ContractUID":..,"Data":..}]</c>) and
        /// the single-object shape the dispatcher also tolerates, exactly as the validator and the
        /// StateData dispatcher read them. Never throws.
        /// </summary>
        public static (string? Function, string? ContractUID, string? Data, string? MD5List) ReadPayload(string? txData)
        {
            if (string.IsNullOrWhiteSpace(txData))
                return (null, null, null, null);

            try
            {
                var arr = JsonConvert.DeserializeObject<JArray>(txData);
                var first = arr?.Count > 0 ? arr[0] : null;
                if (first != null)
                    return ((string?)first["Function"], (string?)first["ContractUID"], (string?)first["Data"], (string?)first["MD5List"]);
            }
            catch { }

            try
            {
                var obj = JObject.Parse(txData);
                return (obj["Function"]?.ToObject<string?>(), obj["ContractUID"]?.ToObject<string?>(),
                        obj["Data"]?.ToObject<string?>(), obj["MD5List"]?.ToObject<string?>());
            }
            catch { }

            return (null, null, null, null);
        }

        private static readonly System.Text.RegularExpressions.Regex DeclaredUidLine =
            new(@"^\s*let\s+SmartContractUID\s*=\s*""([^""]*)""", System.Text.RegularExpressions.RegexOptions.Multiline);
        private static readonly System.Text.RegularExpressions.Regex DeclaredMinterLine =
            new(@"^\s*let\s+MinterAddress\s*=\s*""([^""]*)""", System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>
        /// Reads the FIRST <c>let SmartContractUID</c> / <c>let MinterAddress</c> declarations from a body's
        /// source text (base64 → GZip → UTF-16, the mint encoding). Used only when the decompiler cannot
        /// evaluate the body. Nulls when the body cannot be decoded or a declaration is absent. Never throws.
        /// </summary>
        public static (string? Uid, string? Minter) ReadDeclaredIdentity(string? body)
        {
            try
            {
                var bytes = Convert.FromBase64String(body ?? "");
                var text = System.Text.Encoding.Unicode.GetString(Utilities.SmartContractUtility.Decompress(bytes));
                var uid = DeclaredUidLine.Match(text);
                var minter = DeclaredMinterLine.Match(text);
                return (uid.Success ? uid.Groups[1].Value : null, minter.Success ? minter.Groups[1].Value : null);
            }
            catch { return (null, null); }
        }

        /// <summary>
        /// Consensus check for "Mint()" and "TokenDeploy()". Returns null when the body is bound to the
        /// transaction, else the rejection reason. <paramref name="decompiled"/> is the decompiled body
        /// when decompilation succeeded.
        /// </summary>
        public static string? Validate(string? body, string? txContractUid, string txFromAddress, bool isTokenDeploy, out SmartContractMain? decompiled)
        {
            decompiled = null;
            var label = isTokenDeploy ? "token deploy" : "mint";

            if (string.IsNullOrWhiteSpace(body))
                return $"Smart contract body is missing for {label}.";
            if (string.IsNullOrWhiteSpace(txContractUid))
                return $"ContractUID is missing for {label}.";

            try
            {
                decompiled = SmartContractMain.GenerateSmartContractInMemory(body);
            }
            catch
            {
                decompiled = null;
            }
            if (decompiled == null)
            {
                // TokenDeploy credits a supply read from the body, so a body that cannot be read is refused.
                if (isTokenDeploy)
                    return $"Smart contract body could not be decompiled for {label}.";

                // Mint(): legitimate historical mints exist whose bodies the decompiler cannot parse (the
                // AUDIT-PREP replay scan found four on testnet, heights 71287-71340: descriptions with line
                // breaks and emoji). Refusing them would halt any node replaying those blocks. Such a body can
                // feed no downstream reader either (they all decompile), so the binding falls back to the
                // identity the source DECLARES; a declared identity that disagrees with the TX is refused.
                var (declaredUid, declaredMinter) = ReadDeclaredIdentity(body);
                if (declaredUid != null && !string.Equals(declaredUid, txContractUid, StringComparison.Ordinal))
                    return $"Smart contract body UID does not match the transaction ContractUID for {label}.";
                if (declaredMinter != null && !string.Equals(declaredMinter, txFromAddress, StringComparison.Ordinal))
                    return $"Smart contract body MinterAddress does not match the transaction sender for {label}.";
                return null;
            }

            if (!string.Equals(decompiled.SmartContractUID, txContractUid, StringComparison.Ordinal))
                return $"Smart contract body UID does not match the transaction ContractUID for {label}.";

            if (!string.Equals(decompiled.MinterAddress, txFromAddress, StringComparison.Ordinal))
                return $"Smart contract body MinterAddress does not match the transaction sender for {label}.";

            if (isTokenDeploy)
            {
                var tokenFeature = decompiled.Features?
                    .Where(f => f != null && f.FeatureName == FeatureName.Token)
                    .Select(f => f.FeatureFeatures)
                    .OfType<TokenFeature>()
                    .FirstOrDefault();

                // A TokenDeploy() without a Token feature credits nothing but still marks the record
                // as a token; the TokenizationV2 shape is the one legitimate feature-less-token case.
                var hasTokenizationV2 = decompiled.Features?.Any(f => f != null && f.FeatureName == FeatureName.TokenizationV2) == true;
                if (tokenFeature == null && !hasTokenizationV2)
                    return "Token deploy body does not declare a Token feature.";

                if (tokenFeature != null)
                {
                    if (tokenFeature.TokenSupply < 0 || tokenFeature.TokenSupply > MaxTokenSupply)
                        return $"Token supply must be between 0 and {MaxTokenSupply} for token deploy.";
                    if (tokenFeature.TokenDecimalPlaces < 0 || tokenFeature.TokenDecimalPlaces > MaxTokenDecimalPlaces)
                        return $"Token decimal places must be between 0 and {MaxTokenDecimalPlaces} for token deploy.";
                }
            }

            return null;
        }
    }
}
