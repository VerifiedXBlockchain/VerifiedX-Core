namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// A2: strict consensus-version handshake check shared by P2PValidatorServer and
    /// P2PBlockcasterServer. A missing header means an old binary that predates the
    /// handshake — rejected, which is what locks stragglers out after a coordinated
    /// network restart.
    /// </summary>
    public static class ConsensusVersionGate
    {
        public static bool Check(string? consverHeader)
        {
            if (string.IsNullOrWhiteSpace(consverHeader))
                return false;
            return int.TryParse(consverHeader.Trim(), out var v) && v == Globals.ConsensusVersion;
        }
    }
}
