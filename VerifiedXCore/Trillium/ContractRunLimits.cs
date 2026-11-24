using Trillium.CodeAnalysis;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;

namespace VerifiedXCore.Trillium
{
    /// <summary>
    /// What one run of contract code may do on this node: steps, the length of any one string, and the text it works
    /// through (<see cref="EvaluationLimits"/>). Every run the node makes goes through <see cref="TrilliumRepl"/>, which
    /// asks <see cref="For"/> for its limits, so no reader of a contract (consensus, the API or the wallet) runs code
    /// without them. Over a limit the run stops with <see cref="EvaluationLimitException"/>.
    /// </summary>
    public static class ContractRunLimits
    {
        /// <summary>
        /// The limits of a contract run. Mainnet's contract bodies run in at most a small part of each (the census of
        /// every mined body, 24 Nov 2026), and a body is at most a million characters, so these leave room for any
        /// contract the templates write, and stop a body that builds or reads far more text than it holds.
        /// </summary>
        public static readonly EvaluationLimits Contract = new(maxSteps: 1_000_000, maxStringLength: 4 * 1024 * 1024, maxTextWork: 32L * 1024 * 1024);

        /// <summary>
        /// The limits for a run: <see cref="Contract"/>, except while a block below Globals.ContractRunLimitsHeight is
        /// judged or applied, which runs as before the limits.
        /// </summary>
        public static EvaluationLimits For(long? blockHeight) =>
            blockHeight is long h && h < Globals.ContractRunLimitsHeight ? EvaluationLimits.None : Contract;

        /// <summary>
        /// Why a run of <paramref name="body"/> as the node reads it (<see cref="SmartContractMain.GenerateSmartContractInMemory"/>)
        /// stops at a limit; null when it does not. A body that fails to run for any other reason is not judged here.
        /// </summary>
        public static string? StoppedBy(string body)
        {
            try { SmartContractMain.GenerateSmartContractInMemory(body); }
            catch (EvaluationLimitException ex) { return ex.Message; }
            catch { }
            return null;
        }
    }
}
