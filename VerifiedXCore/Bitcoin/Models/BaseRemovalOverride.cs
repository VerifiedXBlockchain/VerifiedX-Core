using System.Text;
using LiteDB;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// This node's own record that its operator signed a vBTCb removal proposal for validators that still run on VFX
    /// but do not sign on Base (BaseAdminSigningService, NotSigningOnBase): the proposal's validators and casters as they
    /// were signed, in order, and the validators the operator named. While that proposal is pending, this node does not
    /// count the named validators as live, so it neither signs nor sends a veto of the proposal on their account
    /// (BaseValidatorSyncService.LiveTargetOf). The record belongs to one proposal: the first pending removal with
    /// exactly these lists that the node sees after the signature, held by the time it becomes executable; a later
    /// proposal with the same lists is judged as any other. Written only through the operator's own route. Local key
    /// use, not chain state.
    /// </summary>
    public class BaseRemovalOverride
    {
        [BsonId]
        public string Id { get; set; } = "";
        public string Contract { get; set; } = "";
        public string[] Validators { get; set; } = Array.Empty<string>();
        public string[] Casters { get; set; } = Array.Empty<string>();
        public string[] NotSigningOnBase { get; set; } = Array.Empty<string>();
        /// <summary>The pending proposal's eta once the node has seen it; empty until then.</summary>
        public string Eta { get; set; } = "";
        public long SignedAt { get; set; }

        private const string CollectionName = "rsrv_base_removal_override";

        private static ILiteCollection<BaseRemovalOverride> Rows => DbContext.DB_vBTC.GetCollection<BaseRemovalOverride>(CollectionName);

        private static string[] Norm(IEnumerable<string>? addresses) =>
            (addresses ?? Array.Empty<string>()).Select(a => (a ?? "").Trim().ToLowerInvariant()).ToArray();

        /// <summary>One id per contract and ordered lists (a hash: the lists are longer than a key may be).</summary>
        private static string IdOf(string? contract, string[] validators, string[] casters) =>
            Convert.ToHexString(VbtcbTypedData.Keccak(Encoding.UTF8.GetBytes(
                $"{(contract ?? "").Trim().ToLowerInvariant()}|{string.Join(",", validators)}|{string.Join(",", casters)}")));

        /// <summary>Records the operator's signature on a proposal with these lists; it is not bound to a pending proposal yet.</summary>
        public static void Save(string? contract, IEnumerable<string> validators, IEnumerable<string> casters, IEnumerable<string> notSigningOnBase)
        {
            var (v, c) = (Norm(validators), Norm(casters));
            Rows.Upsert(new BaseRemovalOverride
            {
                Id = IdOf(contract, v, c), Contract = (contract ?? "").Trim().ToLowerInvariant(), Validators = v, Casters = c,
                NotSigningOnBase = Norm(notSigningOnBase), Eta = "", SignedAt = VerifiedXCore.Utilities.TimeUtil.GetTime(),
            });
        }

        /// <summary>
        /// The validators the operator named for the pending proposal with these lists and this eta, lower-cased; none
        /// when the operator signed no such proposal, or signed for an earlier one with the same lists.
        /// </summary>
        public static IReadOnlyCollection<string> For(string? contract, IEnumerable<string> validators, IEnumerable<string> casters, string eta)
        {
            var (v, c) = (Norm(validators), Norm(casters));
            var row = Rows.FindById(IdOf(contract, v, c));
            // The store reads an empty text or list back as none.
            if (row == null || !Norm(row.Validators).SequenceEqual(v) || !Norm(row.Casters).SequenceEqual(c)) return Array.Empty<string>();
            if (string.IsNullOrEmpty(row.Eta))
            {
                row.Eta = eta;
                Rows.Update(row);
            }
            return row.Eta == eta ? Norm(row.NotSigningOnBase) : Array.Empty<string>();
        }
    }
}
