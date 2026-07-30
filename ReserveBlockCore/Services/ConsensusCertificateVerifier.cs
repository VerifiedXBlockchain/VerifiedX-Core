using System.Linq;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
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
        /// Wave 4: single source of truth for how many attestations a block at this height needs.
        /// Bootstrap: majority of the AGREED seeds, floored at 2 — the first post-restart blocks
        /// carry ≥2 seed attestations instead of none. Normal: majority of the committee.
        /// </summary>
        public static int RequiredAttestationsForHeight(long height)
        {
            if (Globals.IsBootstrapMode)
                return Math.Max(2, BootstrapCoordinationService.AgreedSeedCount / 2 + 1);
            var operational = OperationalBlockCasterCount(height);
            if (operational > 0)
                return RequiredAttestations(operational);
            return RequiredAttestations(AttestorSetForHeight(height).Count);
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
