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
    /// The record belongs to the proposal that was signed and to no other. A signature is good at one admin nonce, so
    /// the signed proposal is the removal that was proposed under that nonce with the signed validators, casters and VFX
    /// block height; the nonce a pending removal was proposed under is read from its RemovalProposed event, whatever the
    /// contract's nonce is by now (BaseRemovalProposal: the veto loop looks for it, a signing request reads only what the
    /// loop has found). So, for the removal that is pending:
    /// <list type="bullet">
    /// <item>while the contract is still at the signed nonce, nothing made with the signature can be pending: the record
    /// waits;</item>
    /// <item>proposed under the signed nonce and giving the signed digest: it is the signed proposal, and the record is
    /// tied to it by everything the contract shows of it (its eta, VFX block height, validators and casters: an eta
    /// alone is not one proposal, since a proposal, its cancellation and another proposal fit in one Base block and
    /// then share it), after which no further read is needed;</item>
    /// <item>proposed under the signed nonce and giving another digest, or proposed under a later nonce: the signed nonce
    /// went to something else, or the signed proposal is no longer the pending one. The record is dead and is removed;</item>
    /// <item>proposed under an earlier nonce: an older proposal (or an old read). Not the signed one; the record stays;</item>
    /// <item>its nonce is not known yet (the event is still looked for, or cannot be read): nothing is proven, and the
    /// record stays. Meanwhile a pending removal that gives the signed digest and was not already pending at signing is
    /// taken for the signed one, so a failed read never has the node veto what its operator signed.</item>
    /// </list>
    /// A later proposal the same in every field the contract shows, eta included, cannot be told from the one a record
    /// is tied to without its event; it removes the same validators at the same height, and is taken for it.
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
        /// <summary>The removal that was pending when the operator signed (BaseRemovalProposal.IdentityOf; none: empty): that one is not the proposal signed.</summary>
        public string? PendingAtSigning { get; set; }
        /// <summary>The signed proposal (BaseRemovalProposal.IdentityOf) once the node has found it pending; none until then.</summary>
        public string? BoundTo { get; set; }
        public long SignedAt { get; set; }

        private const string CollectionName = "rsrv_base_removal_override";

        private static ILiteCollection<BaseRemovalOverride> Rows => DbContext.DB_vBTC.GetCollection<BaseRemovalOverride>(CollectionName);

        private static string Norm(string? contract) => (contract ?? "").Trim().ToLowerInvariant();

        /// <summary>Records the operator's signature on <paramref name="digest"/> at <paramref name="adminNonce"/>; it is tied to no pending proposal yet.</summary>
        public static void Save(string? contract, byte[] digest, BigInteger adminNonce, IEnumerable<string> notSigningOnBase, BaseBridgeService.PendingRemoval pendingAtSigning)
        {
            Rows.Upsert(new BaseRemovalOverride
            {
                Id = Convert.ToHexString(digest), Contract = Norm(contract), AdminNonce = adminNonce.ToString(),
                NotSigningOnBase = notSigningOnBase.Select(a => (a ?? "").Trim().ToLowerInvariant()).ToArray(),
                PendingAtSigning = pendingAtSigning.Eta == 0 ? null : BaseRemovalProposal.IdentityOf(contract, pendingAtSigning), BoundTo = null,
                SignedAt = VerifiedXCore.Utilities.TimeUtil.GetTime(),
            });
        }

        /// <summary>
        /// The validators the operator named when signing the proposal that is now pending, lower-cased; none when the
        /// pending proposal is not one the operator signed with names (see the class remarks). <paramref name="adminNonce"/>
        /// is the contract's now; <paramref name="proposedUnder"/> is the nonce the pending removal was proposed under,
        /// null while that is not known (BaseRemovalProposal: found by the veto loop, never read here). A record is removed
        /// only once it is proven dead.
        /// </summary>
        public static IReadOnlyCollection<string> For(string? contract, long chainId, BaseBridgeService.PendingRemoval pending,
            BigInteger adminNonce, BigInteger? proposedUnder)
        {
            var at = Norm(contract);
            var identity = BaseRemovalProposal.IdentityOf(contract, pending);
            IReadOnlyCollection<string> named = Array.Empty<string>();
            foreach (var row in Rows.Find(r => r.Contract == at).ToList())
            {
                if (!BigInteger.TryParse(row.AdminNonce, out var signedAt)) { Rows.Delete(row.Id); continue; }
                // The signature is unused so far. (A cancellation signed while the contract reads this nonce or a lower
                // one cannot cancel a proposal made with the signature either: it is good at that nonce only.)
                if (adminNonce <= signedAt) continue;
                var names = row.NotSigningOnBase ?? Array.Empty<string>();
                if (!string.IsNullOrEmpty(row.BoundTo) && row.BoundTo == identity) { named = names; continue; }   // tied to this very proposal

                var givesTheDigest = string.IsNullOrEmpty(row.BoundTo) && IsTheProposalSigned(row, at, chainId, pending, signedAt);
                if (proposedUnder is not { } nonce)
                {
                    // Not proven either way: not vetoed while it is not known, unless it is the one that was pending at signing.
                    if (givesTheDigest && identity != row.PendingAtSigning) named = names;
                    continue;
                }
                if (nonce < signedAt) continue;                               // an older proposal: not the one signed
                if (nonce == signedAt && givesTheDigest)
                {
                    row.BoundTo = identity;
                    Rows.Update(row);
                    named = names;
                    continue;
                }
                Rows.Delete(row.Id);                                          // the signed nonce went elsewhere, or a later proposal is the pending one
            }
            return named;
        }

        /// <summary>
        /// Whether a record waits on the nonce the pending removal was proposed under: one whose signature may have been
        /// used and that is not tied to this removal. Only then does the veto loop look for the removal's event on Base.
        /// </summary>
        public static bool WaitsOnTheProposalsNonce(string? contract, BaseBridgeService.PendingRemoval pending, BigInteger adminNonce)
        {
            var at = Norm(contract);
            var identity = BaseRemovalProposal.IdentityOf(contract, pending);
            return Rows.Find(r => r.Contract == at).ToList().Any(row =>
                BigInteger.TryParse(row.AdminNonce, out var signedAt) && adminNonce > signedAt && row.BoundTo != identity);
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
