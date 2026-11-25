namespace VerifiedXCore.Tests
{
    /// <summary>
    /// What <see cref="NetworkSurfaceTests"/> finds failing today. Entries are removed as routes are bounded and are
    /// never added: a route that newly fails is a route to bound.
    /// </summary>
    internal static class NetworkSurfaceBaseline
    {
        /// <summary>"host METHOD route flag", as the test prints them.</summary>
        public static readonly HashSet<string> KnownFailures = new(StringComparer.Ordinal)
        {
        };

        /// <summary>"HubType.Method" for every SignalR hub method. The test does not probe these yet.</summary>
        public static readonly HashSet<string> KnownHubMethods = new(StringComparer.Ordinal)
        {
            "ConsensusServer.RequestMethodCode",
            "P2PAdjServer.FortisPool",
            "P2PAdjServer.IpAddresses",
            "P2PAdjServer.ReceiveTX",
            "P2PAdjServer.SignerInfo",
            "P2PBeaconServer.BeaconDataIsReady",
            "P2PBeaconServer.BeaconFileIsDownloaded",
            "P2PBeaconServer.BeaconIsFileReady",
            "P2PBeaconServer.ReceiveDownloadRequest",
            "P2PBeaconServer.ReceiveUploadRequest",
            "P2PBlockcasterServer.GetConsensusVersion",
            "P2PBlockcasterServer.ReceiveBlockVal",
            "P2PBlockcasterServer.SendBlockHeightForVals",
            "P2PMotherServer.SendMotherData",
            "P2PServer.GetBannedMasternodes",
            "P2PServer.GetMasternodes",
            "P2PServer.GetValidatorStatus",
            "P2PServer.GetWalletVersion",
            "P2PServer.MasternodeOnline",
            "P2PServer.PingBackPeer",
            "P2PServer.PingPeers",
            "P2PServer.ReceiveBlock",
            "P2PServer.SeedNodeCheck",
            "P2PServer.SendBlock",
            "P2PServer.SendBlockHeight",
            "P2PServer.SendBlockList",
            "P2PServer.SendBlockSpan",
            "P2PServer.SendLeadAdjudicator",
            "P2PServer.SendPeers",
            "P2PServer.SendTxToMempool",
            "P2PValidatorServer.FailedToReachConsensus",
            "P2PValidatorServer.GetConsensusVersion",
            "P2PValidatorServer.GetFinalizedWinnersList",
            "P2PValidatorServer.GetValidatorStatusVal",
            "P2PValidatorServer.GetWalletVersionVal",
            "P2PValidatorServer.GetWinningProofList",
            "P2PValidatorServer.ReceiveBlockVal",
            "P2PValidatorServer.ReceiveQueueBlockVal",
            "P2PValidatorServer.SendActiveVals",
            "P2PValidatorServer.SendBlockHeightForVals",
            "P2PValidatorServer.SendBlockVal",
            "P2PValidatorServer.SendLockedWinner",
            "P2PValidatorServer.SendNetworkValidatorList",
            "P2PValidatorServer.SendProofList",
            "P2PValidatorServer.SendQueuedBlock",
            "P2PValidatorServer.SendTxToMempoolVals",
            "P2PValidatorServer.SendWinningProofList",
            "P2PValidatorServer.SendWinningProofVote",
        };
    }
}
