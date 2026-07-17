namespace ReserveBlockCore.Services
{
    /// <summary>Single place for block-version gating of consensus certificates (extend Max when v5 rules are defined).</summary>
    public static class ConsensusCertificateRules
    {
        public const int MinBlockVersion = 4;
        public const int MaxBlockVersionWithSameCertRules = 4;

        public static bool SupportsConsensusCertificate(int blockVersion) =>
            blockVersion >= MinBlockVersion && blockVersion <= MaxBlockVersionWithSameCertRules;
    }
}
