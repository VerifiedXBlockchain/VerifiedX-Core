using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Read-only views of vBTC V2 contracts built from chain data only: the state trei, the contract code stored in it, and
    /// the withdrawal tables every node writes while applying blocks. API routes that answer for ANY address or contract -
    /// web wallets through Spyglass, raw-transaction builders - use these. The local VBTCContractV2 table holds only the
    /// contracts this node's own wallet owns or holds, so a route that required it failed for every other user.
    /// </summary>
    public static class VBTCChainView
    {
        public sealed class Vault
        {
            public SmartContractStateTrei State { get; init; } = null!;
            public TokenizationV2Feature Feature { get; init; } = null!;
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public string SmartContractUID => State.SmartContractUID;
            public string OwnerAddress => State.OwnerAddress;
        }

        // Decompiling runs the contract code, so the parsed feature is cached per contract, keyed on its code digest.
        private static readonly ConcurrentDictionary<string, (string Digest, TokenizationV2Feature? Feature, string Name, string Description)> _cache = new();

        /// <summary>The on-chain vault, or null when the contract is not in the state trei or is not a vBTC V2 contract.</summary>
        public static Vault? GetVault(string? scUID) =>
            string.IsNullOrEmpty(scUID) ? null : FromState(SmartContractStateTrei.GetSmartContractState(scUID));

        public static Vault? FromState(SmartContractStateTrei? state)
        {
            if (state == null || string.IsNullOrEmpty(state.SmartContractUID) || string.IsNullOrEmpty(state.ContractData))
                return null;

            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(state.ContractData)));
            if (!_cache.TryGetValue(state.SmartContractUID, out var entry) || entry.Digest != digest)
            {
                TokenizationV2Feature? feature = null;
                string name = "", description = "";
                try
                {
                    var sc = SmartContractMain.GenerateSmartContractInMemory(state.ContractData);
                    var raw = sc?.Features?.FirstOrDefault(f => f != null && f.FeatureName == FeatureName.TokenizationV2)?.FeatureFeatures;
                    feature = raw as TokenizationV2Feature ?? (raw == null ? null : JsonConvert.DeserializeObject<TokenizationV2Feature>(raw.ToString() ?? ""));
                    name = sc?.Name ?? "";
                    description = sc?.Description ?? "";
                }
                catch { feature = null; }
                entry = (digest, feature, name, description);
                _cache[state.SmartContractUID] = entry;
            }

            return entry.Feature == null ? null : new Vault { State = state, Feature = entry.Feature, Name = entry.Name, Description = entry.Description };
        }

        /// <summary>Every vBTC V2 contract on chain.</summary>
        public static List<Vault> AllVaults() =>
            (SmartContractStateTrei.GetSCST()?.FindAll() ?? Enumerable.Empty<SmartContractStateTrei>())
                .Select(FromState).Where(v => v != null).Select(v => v!).ToList();

        /// <summary>The vBTC V2 contracts on chain that <paramref name="address"/> owns or has ledger rows on.</summary>
        public static List<Vault> VaultsFor(string address)
        {
            if (string.IsNullOrEmpty(address)) return new List<Vault>();
            return (SmartContractStateTrei.GetSCST()?.FindAll() ?? Enumerable.Empty<SmartContractStateTrei>())
                .Where(s => s.OwnerAddress == address
                         || (s.SCStateTreiTokenizationTXes?.Any(x => x.FromAddress == address || x.ToAddress == address) ?? false))
                .Select(FromState).Where(v => v != null).Select(v => v!).ToList();
        }

        /// <summary>The vBTC V2 contracts on chain owned by <paramref name="address"/>.</summary>
        public static List<Vault> VaultsOwnedBy(string address) =>
            string.IsNullOrEmpty(address) ? new List<Vault>()
                : (SmartContractStateTrei.GetSCST()?.Find(s => s.OwnerAddress == address) ?? Enumerable.Empty<SmartContractStateTrei>())
                    .Select(FromState).Where(v => v != null).Select(v => v!).ToList();

        // ── Withdrawals (tables written by every node from mined REQUEST / COMPLETE / CANCEL transactions) ─────────────

        /// <summary>Mined withdrawal rows for a contract (rows with no request hash exist only on the API node that served them).</summary>
        public static List<VBTCWithdrawalRequest> MinedWithdrawals(string scUID) =>
            (VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()?.Query().Where(x => x.SmartContractUID == scUID).ToList() ?? new List<VBTCWithdrawalRequest>())
                .Where(x => !string.IsNullOrEmpty(x.TransactionHash))
                .OrderBy(x => x.RequestBlockHeight).ThenBy(x => x.Timestamp).ToList();

        /// <summary>The contract's withdrawal that is still open (not completed, not expired), if any.</summary>
        public static VBTCWithdrawalRequest? ActiveWithdrawal(string scUID)
        {
            var height = Globals.LastBlock?.Height ?? 0;
            var now = TimeUtil.GetTime();
            return MinedWithdrawals(scUID).LastOrDefault(r => VBTCWithdrawalRequest.IsStillBlocking(r, height, now));
        }

        public static bool HasActiveWithdrawal(string scUID) => ActiveWithdrawal(scUID) != null;

        /// <summary>
        /// The contract's withdrawal status as the local record tracks it (Requested while open, Cancellation_Requested while a
        /// cancellation vote is pending, Completed after the last one completed, None otherwise), derived from chain data.
        /// </summary>
        public static VBTCWithdrawalStatus WithdrawalStatus(string scUID)
        {
            var active = ActiveWithdrawal(scUID);
            if (active != null)
                return VBTCWithdrawalCancellation.HasPendingCancellation(active.TransactionHash, TimeUtil.GetTime(), scUID)
                    ? VBTCWithdrawalStatus.Cancellation_Requested
                    : active.Status == VBTCWithdrawalStatus.None ? VBTCWithdrawalStatus.Requested : active.Status;
            var last = MinedWithdrawals(scUID).LastOrDefault();
            return last != null && last.Status == VBTCWithdrawalStatus.Completed ? VBTCWithdrawalStatus.Completed : VBTCWithdrawalStatus.None;
        }

        /// <summary>
        /// Completed withdrawals of a contract. The completing transaction and its time are recorded on the row from this
        /// release on; for older rows they come from this node's local record when it has one, else they are empty.
        /// </summary>
        public static List<VBTCWithdrawalHistory> WithdrawalHistory(string scUID, VBTCContractV2? local = null) =>
            MinedWithdrawals(scUID).Where(r => r.Status == VBTCWithdrawalStatus.Completed).Select(r =>
            {
                var known = local?.WithdrawalHistory?.FirstOrDefault(h => h.RequestHash == r.TransactionHash);
                return new VBTCWithdrawalHistory
                {
                    RequestHash = r.TransactionHash,
                    CompletionHash = r.CompletionTxHash ?? known?.CompletionHash ?? "",
                    BTCTransactionHash = r.BTCTxHash ?? known?.BTCTransactionHash ?? "",
                    Amount = r.Amount,
                    BTCDestination = r.BTCDestination,
                    RequestTime = r.Timestamp,
                    CompletionTime = r.CompletionTimestamp ?? known?.CompletionTime ?? 0,
                    FeeRate = r.FeeRate,
                };
            }).ToList();
    }
}
