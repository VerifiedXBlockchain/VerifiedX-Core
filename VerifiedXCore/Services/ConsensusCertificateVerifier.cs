using System.Linq;
using VerifiedXCore.Models;

namespace VerifiedXCore.Services
{
    public static class ConsensusCertificateVerifier
    {
        public static int RequiredAttestations(int activeCasterCount) =>
            activeCasterCount <= 0 ? int.MaxValue : Math.Max(1, activeCasterCount / 2 + 1);

        /// <summary>Distinct validator addresses in <see cref="Globals.BlockCasters"/>; matches <see cref="ConsensusCertificateHelper"/> quorum basis.</summary>
        public static int OperationalBlockCasterCount() =>
            Globals.BlockCasters
                .Where(p => !string.IsNullOrEmpty(p.ValidatorAddress))
                .Select(p => p.ValidatorAddress!)
                .Distinct(StringComparer.Ordinal)
                .Count();

        /// <summary>
        /// Wave 3: height-aware quorum basis — the membership committee for the height when the
        /// record era is active, else the legacy live-bag count. Attach-need and verify-need are
        /// provably identical because both call this.
        /// </summary>
        public static int OperationalBlockCasterCount(long height)
        {
            var committee = CasterMembershipStore.GetCommitteeForHeight(height);
            return committee != null ? committee.Count : OperationalBlockCasterCount();
        }

        /// <summary>
        /// Wave 3: the set of addresses whose attestations COUNT for a block at this height —
        /// the membership committee when active, else the legacy BlockCasters ∪ KnownCasters view.
        /// Wave 4: during cooperative bootstrap the agreed seeds are always eligible attestors.
        /// </summary>
        public static HashSet<string> AttestorSetForHeight(long height)
        {
            var committee = CasterMembershipStore.GetCommitteeForHeight(height);
            var set = committee ?? BuildCasterAddressSet();
            if (Globals.IsBootstrapMode)
            {
                foreach (var seed in BootstrapCoordinationService.AgreedSeedAddresses)
                    if (!string.IsNullOrEmpty(seed))
                        set.Add(seed);
            }
            return set;
        }

        /// <summary>
        /// How many attestations a block at this height needs. ONE-QUORUM (Sep 2026): delegates to
        /// <see cref="ConsensusQuorum.RequiredForHeight"/> — the same number block-hash agreement
        /// uses. The former bootstrap branch (majority of the agreed seeds, floored at 2) is gone:
        /// only seeds evaluated it, so seeds and validators could approve DIFFERENT blocks at one
        /// height, which is how testnet 975,533 forked. Bootstrap seeds now meet the same majority.
        /// </summary>
        public static int RequiredAttestationsForHeight(long height) => ConsensusQuorum.RequiredForHeight(height);

        /// <summary>STRICT: true only when a certificate is required at this height, is present, and
        /// carries a valid M-of-N caster quorum. "Not required" is NOT a pass here. Used where the
        /// block's own certificate is the sole basis for adopting it (majority-block adoption in the
        /// caster block-fetch fallback).</summary>
        public static bool HasValidCertificate(Block? block)
        {
            if (block?.ConsensusCertificate == null)
                return false;
            if (block.Height < Globals.CertEnforceHeight || !ConsensusCertificateRules.SupportsConsensusCertificate(block.Version))
                return false;
            return VerifyOrNotRequired(block);
        }

        /// <summary>True if certificate is not required, or present and valid (M-of-N caster ECDSA on §12.1 payload).</summary>
        public static bool VerifyOrNotRequired(Block block)
        {
            // Wave 4: bootstrap no longer skips certs — bootstrap blocks carry ≥2 seed
            // attestations (RequiredAttestationsForHeight handles the reduced quorum).
            if (block.Height < Globals.CertEnforceHeight || !ConsensusCertificateRules.SupportsConsensusCertificate(block.Version))
                return true;

            var cert = block.ConsensusCertificate;
            if (cert == null)
                return false;

            if (cert.BlockHeight != block.Height
                || ConsensusMessageFormatter.NormalizeHash(cert.BlockHash) != ConsensusMessageFormatter.NormalizeHash(block.Hash)
                || cert.WinnerAddress != block.Validator
                || ConsensusMessageFormatter.NormalizeHash(cert.PrevHash) != ConsensusMessageFormatter.NormalizeHash(block.PrevHash))
                return false;

            // Wave 3: committee for the block's height when the record era is active; legacy view otherwise.
            var casterSet = AttestorSetForHeight(block.Height);
            if (casterSet.Count == 0)
                return false;

            // Wave 4: single shared need computation (matches the attach + top-up paths exactly).
            var need = RequiredAttestationsForHeight(block.Height);
            var validSigners = new HashSet<string>(StringComparer.Ordinal);

            if (cert.Attestations == null)
                return false;

            foreach (var a in cert.Attestations)
            {
                if (string.IsNullOrEmpty(a.CasterAddress) || string.IsNullOrEmpty(a.Signature))
                    continue;
                if (!casterSet.Contains(a.CasterAddress))
                    continue;

                var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
                if (!SignatureService.VerifySignature(a.CasterAddress, msg, a.Signature))
                    continue;

                validSigners.Add(a.CasterAddress);
            }

            return validSigners.Count >= need;
        }

        private static HashSet<string> BuildCasterAddressSet()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in Globals.BlockCasters)
            {
                if (!string.IsNullOrEmpty(p.ValidatorAddress))
                    set.Add(p.ValidatorAddress);
            }

            lock (Globals.KnownCastersLock)
            {
                foreach (var k in Globals.KnownCasters)
                {
                    if (!string.IsNullOrEmpty(k.Address))
                        set.Add(k.Address);
                }
            }

            return set;
        }
    }
}
