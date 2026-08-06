namespace VerifiedXCore.Bitcoin.FROST.Models
{
    /// <summary>
    /// Why a FROST signing ceremony failed. Before this enum existed, every failure cause collapsed
    /// into a null result and surfaced as the same generic "FROST signing failed for input N"
    /// string, making production incidents undiagnosable from the caller side.
    /// </summary>
    public enum FrostCeremonyFailureCode
    {
        None = 0,
        /// <summary>Validators actively rejected /frost/sign/start (409 dedup, 403 bad leader signature, etc.).</summary>
        StartRejectedByValidators,
        /// <summary>Not enough validators responded to /frost/sign/start (unreachable / timeouts).</summary>
        StartInsufficientResponses,
        /// <summary>Round 1 collected fewer nonce commitments than the required threshold.</summary>
        Round1InsufficientNonces,
        /// <summary>Round 2 collected fewer signature shares than the required threshold.</summary>
        Round2InsufficientShares,
        /// <summary>Coordinator could not resolve the FROST group pubkey package via any lookup tier.</summary>
        PubkeyPackageNotFound,
        /// <summary>The FROST native library rejected the share aggregation.</summary>
        NativeAggregationFailed,
        /// <summary>The aggregated signature was not a 64-byte Schnorr signature.</summary>
        InvalidSignatureLength,
        /// <summary>The transaction rebuilt at execute time no longer matches what was prepared/pre-signed.</summary>
        InputCountMismatch,
        /// <summary>Unhandled exception in the coordinator.</summary>
        CoordinatorException
    }

    /// <summary>
    /// One validator's failure detail during a ceremony phase.
    /// </summary>
    public class FrostValidatorFailure
    {
        public string ValidatorAddress { get; set; } = "";
        public int HttpStatus { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Result of CoordinateSigningCeremony. Replaces the old nullable FrostSigningResult return so
    /// each distinct failure cause carries a code, the session id, response counts and per-validator
    /// rejection reasons end-to-end (logs, API responses, explorer).
    /// </summary>
    public class FrostCeremonyOutcome
    {
        /// <summary>Non-null on success.</summary>
        public FrostSigningResult? Result { get; set; }
        public FrostCeremonyFailureCode FailureCode { get; set; } = FrostCeremonyFailureCode.None;
        public string SessionId { get; set; } = "";
        public int InputIndex { get; set; }
        /// <summary>Validators that responded/produced data in the failing phase.</summary>
        public int Responded { get; set; }
        /// <summary>Threshold count required in the failing phase.</summary>
        public int Required { get; set; }
        /// <summary>Total validators contacted.</summary>
        public int Total { get; set; }
        public List<FrostValidatorFailure> ValidatorFailures { get; set; } = new();
        public string Detail { get; set; } = "";

        public bool Success => Result != null;

        /// <summary>
        /// True for failure modes that are expected to clear on their own (unreachable validators,
        /// timing) — callers must NOT take punitive action (e.g. contract blacklisting) on these.
        /// </summary>
        public bool IsRetryable => FailureCode == FrostCeremonyFailureCode.StartInsufficientResponses
            || FailureCode == FrostCeremonyFailureCode.StartRejectedByValidators
            || FailureCode == FrostCeremonyFailureCode.Round1InsufficientNonces
            || FailureCode == FrostCeremonyFailureCode.Round2InsufficientShares
            || FailureCode == FrostCeremonyFailureCode.InputCountMismatch;

        /// <summary>
        /// Compact operator-facing description: code, detail, session and response counts.
        /// </summary>
        public string Describe()
        {
            var counts = Total > 0 ? $", validators={Responded}/{Required}/{Total}" : "";
            var rejections = ValidatorFailures.Count > 0
                ? $", rejections=[{string.Join("; ", ValidatorFailures.Select(f => $"{f.ValidatorAddress}:HTTP {f.HttpStatus} {f.Message}"))}]"
                : "";
            var detail = !string.IsNullOrEmpty(Detail) ? $" — {Detail}" : "";
            return $"{FailureCode}{detail} (session={SessionId}{counts}){rejections}";
        }

        public static FrostCeremonyOutcome Ok(FrostSigningResult result, string sessionId, int inputIndex = 0)
        {
            return new FrostCeremonyOutcome { Result = result, SessionId = sessionId, InputIndex = inputIndex };
        }

        public static FrostCeremonyOutcome Fail(FrostCeremonyFailureCode code, string sessionId, string detail,
            int responded = 0, int required = 0, int total = 0, List<FrostValidatorFailure>? validatorFailures = null, int inputIndex = 0)
        {
            return new FrostCeremonyOutcome
            {
                FailureCode = code,
                SessionId = sessionId,
                Detail = detail,
                Responded = responded,
                Required = required,
                Total = total,
                ValidatorFailures = validatorFailures ?? new List<FrostValidatorFailure>(),
                InputIndex = inputIndex
            };
        }
    }
}
