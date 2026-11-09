using LiteDB;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// This node's own record of the epoch's vBTCb change its key signed as a contract member (one row per kind: validator
    /// additions, validator removals, caster changes): the epoch and the whole change it computed then. One change of each
    /// kind per epoch is what keeps the set moving a third at a time. The change went through once the contract shows any
    /// of it (an address it adds is on the contract, or one it removes is gone): those were not so when it was computed,
    /// and the plan does not change until the contract does. Another admin action moving the nonce is not taken for it.
    /// Local key use, not chain state.
    /// </summary>
    public class BaseSignerSignLog
    {
        [BsonId]
        public string Contract { get; set; } = "";
        public long Epoch { get; set; }
        /// <summary>Kept for rows written before the change lists; not read.</summary>
        public string AdminNonce { get; set; } = "";
        public string[] Added { get; set; } = Array.Empty<string>();
        public string[] Removed { get; set; } = Array.Empty<string>();

        private const string CollectionName = "rsrv_base_signer_sign_log";

        public static BaseSignerSignLog? Get(string contract) =>
            DbContext.DB_vBTC.GetCollection<BaseSignerSignLog>(CollectionName).FindById(contract.Trim().ToLowerInvariant());

        public static void Save(string contract, long epoch, IEnumerable<string> added, IEnumerable<string> removed) =>
            DbContext.DB_vBTC.GetCollection<BaseSignerSignLog>(CollectionName).Upsert(new BaseSignerSignLog
            {
                Contract = contract.Trim().ToLowerInvariant(), Epoch = epoch, Added = added.ToArray(), Removed = removed.ToArray(),
            });

        /// <summary>
        /// Whether the change logged under <paramref name="contract"/> for <paramref name="epoch"/> went through: one it adds
        /// is now on <paramref name="addedTo"/>, or one it removes is no longer on <paramref name="removedFrom"/>.
        /// </summary>
        public static bool WentThrough(string contract, long epoch, IReadOnlyCollection<string> addedTo, IReadOnlyCollection<string> removedFrom) =>
            Get(contract) is { } log && log.Epoch == epoch
            && (log.Added.Any(a => addedTo.Contains(a, StringComparer.OrdinalIgnoreCase))
                || log.Removed.Any(r => !removedFrom.Contains(r, StringComparer.OrdinalIgnoreCase)));
    }
}
