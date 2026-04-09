using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
{
    public static class ConsensusCertificateVerifier
    {
        public static int RequiredAttestations(int activeCasterCount) =>
            activeCasterCount <= 0 ? int.MaxValue : Math.Max(1, activeCasterCount / 2 + 1);

        /// <summary>True if certificate is not required, or present and valid (M-of-N caster ECDSA on §12.1 payload).</summary>
        public static bool VerifyOrNotRequired(Block block)
        {
            if (Globals.IsBootstrapMode || block.Height < Globals.CertEnforceHeight || !ConsensusCertificateRules.SupportsConsensusCertificate(block.Version))
                return true;

            var cert = block.ConsensusCertificate;
            if (cert == null)
                return false;

            if (cert.BlockHeight != block.Height
                || ConsensusMessageFormatter.NormalizeHash(cert.BlockHash) != ConsensusMessageFormatter.NormalizeHash(block.Hash)
                || cert.WinnerAddress != block.Validator
                || ConsensusMessageFormatter.NormalizeHash(cert.PrevHash) != ConsensusMessageFormatter.NormalizeHash(block.PrevHash))
                return false;

            var casterSet = BuildCasterAddressSet();
            if (casterSet.Count == 0)
                return false;

            var need = RequiredAttestations(casterSet.Count);
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
