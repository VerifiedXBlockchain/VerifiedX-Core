using System.Numerics;
using LiteDB;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Models
{
    /// <summary>
    /// This node's own record that its operator signed a vBTCb removal proposal for validators that still run on VFX
    /// but do not sign on Base (BaseAdminSigningService, NotSigningOnBase): the digest that was signed, the admin nonce
    /// it was signed at, and the validators the operator named. While that proposal is pending, this node does not
    /// count the named validators as live, so it neither signs nor sends a veto of the proposal on their account
    /// (BaseValidatorSyncService.LiveTargetOf).
    ///
    /// The record belongs to the proposal that was signed and to no other. A signature is good at one admin nonce, and the
    /// proposal made with it is the change that moves the contract from that nonce to the next. So:
    /// <list type="bullet">
    /// <item>while the contract is still at the signed nonce, nothing made with the signature can be pending: the record
    /// waits;</item>
    /// <item>the first time the node sees the contract one nonce on, the pending removal is the signed one when it was
    /// not already pending at signing and its validators, casters and VFX block height give the signed digest at the
    /// signed nonce; the record is then tied to that proposal's eta;</item>
    /// <item>in every other case the nonce was used by something else (or the node cannot tell), the signature can no
    /// longer make a proposal, and the record is removed. So is a record whose proposal is no longer the pending one.</item>
    /// </list>
    /// Written only through the operator's own route. Local key use, not chain state.
    /// </summary>
    public class BaseRemovalOverride
    {
        /// <summary>The signed ProposeRemoval digest, in hex.</summary>
        [BsonId]
        public string Id { get; set; } = "";
        public string Contract { get; set; } = "";
        /// <summary>The contract's admin nonce the proposal was signed at.</summary>
        public string AdminNonce { get; set; } = "";
        public string[] NotSigningOnBase { get; set; } = Array.Empty<string>();
        /// <summary>The eta of the removal that was pending when the operator signed ("0": none): that one is not the proposal signed.</summary>
        public string PendingEtaAtSigning { get; set; } = "0";
        /// <summary>The signed proposal's eta once the node has seen it pending; none until then.</summary>
        public string? Eta { get; set; }
        public long SignedAt { get; set; }

        private const string CollectionName = "rsrv_base_removal_override";

        private static ILiteCollection<BaseRemovalOverride> Rows => DbContext.DB_vBTC.GetCollection<BaseRemovalOverride>(CollectionName);

        private static string Norm(string? contract) => (contract ?? "").Trim().ToLowerInvariant();

        /// <summary>Records the operator's signature on <paramref name="digest"/> at <paramref name="adminNonce"/>; it is tied to no pending proposal yet.</summary>
        public static void Save(string? contract, byte[] digest, BigInteger adminNonce, IEnumerable<string> notSigningOnBase, BigInteger pendingEtaAtSigning)
        {
            Rows.Upsert(new BaseRemovalOverride
            {
                Id = Convert.ToHexString(digest), Contract = Norm(contract), AdminNonce = adminNonce.ToString(),
                NotSigningOnBase = notSigningOnBase.Select(a => (a ?? "").Trim().ToLowerInvariant()).ToArray(),
                PendingEtaAtSigning = pendingEtaAtSigning.ToString(), Eta = null, SignedAt = VerifiedXCore.Utilities.TimeUtil.GetTime(),
            });
        }

        /// <summary>
        /// The validators the operator named when signing the proposal that is now pending, lower-cased; none when the
        /// pending proposal is not one the operator signed with names (see the class remarks). Records that can no longer
        /// apply are removed.
        /// </summary>
        public static IReadOnlyCollection<string> For(string? contract, long chainId, BaseBridgeService.PendingRemoval pending, BigInteger adminNonce)
        {
            var at = Norm(contract);
            var eta = pending.Eta.ToString();
            IReadOnlyCollection<string> named = Array.Empty<string>();
            foreach (var row in Rows.Find(r => r.Contract == at).ToList())
            {
                if (!BigInteger.TryParse(row.AdminNonce, out var signedAt)) { Rows.Delete(row.Id); continue; }
                if (adminNonce <= signedAt) continue;                         // the signature is unused so far
                if (string.IsNullOrEmpty(row.Eta))
                {
                    if (adminNonce == signedAt + 1 && eta != row.PendingEtaAtSigning && IsTheProposalSigned(row, at, chainId, pending, signedAt))
                    {
                        row.Eta = eta;
                        Rows.Update(row);
                    }
                    else { Rows.Delete(row.Id); continue; }                   // the nonce went to something else
                }
                if (row.Eta == eta) named = row.NotSigningOnBase ?? Array.Empty<string>();
                else Rows.Delete(row.Id);                                     // its proposal is no longer the pending one
            }
            return named;
        }

        /// <summary>Whether the pending removal's validators, casters and VFX block height give the digest signed, at the nonce it was signed at.</summary>
        private static bool IsTheProposalSigned(BaseRemovalOverride row, string contract, long chainId, BaseBridgeService.PendingRemoval pending, BigInteger signedAt)
        {
            if (pending.VfxBlockHeight is not { } height || height < 0 || height > long.MaxValue) return false;
            try
            {
                var digest = VbtcbTypedData.ProposeRemovalDigest(pending.Validators, pending.Casters, (long)height, signedAt, chainId, contract);
                return string.Equals(Convert.ToHexString(digest), row.Id, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
