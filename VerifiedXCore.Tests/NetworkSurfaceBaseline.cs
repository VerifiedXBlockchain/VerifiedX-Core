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
            // These build their whole body into objects before they look at who sent it.
            "valapi POST /valapi/Validator/ProbeTip allocation",
            "valapi POST /valapi/Validator/AnnounceCasterResumed allocation",
            "valapi POST /valapi/Validator/AnnounceCasterResumed slow",
            "valapi POST /valapi/Validator/CasterInvitation allocation",
            "valapi POST /valapi/Validator/ExchangeValidatorList allocation",
            "valapi POST /valapi/Validator/PromoteToCaster allocation",
            // A body that is not the shape the route expects ends in an unhandled error.
            "valapi POST /valapi/Validator/CasterInvitation unhandled-error",
            "valapi POST /valapi/Validator/ReceiveCatchUpBlock unhandled-error",
            "frost POST /frost/backup/check unhandled-error",
            "frost POST /frost/backup/recover unhandled-error",
            "frost POST /frost/backup/store unhandled-error",
            "frost POST /frost/dkg/abort unhandled-error",
            "frost POST /frost/dkg/start unhandled-error",
            "frost POST /frost/sign/abort unhandled-error",
            "frost POST /frost/sign/start unhandled-error",
            "arbiter GET /depositaddress/x/x unhandled-error",
            // The Arbiter sets no body limit and reads the whole body before it checks the signature.
            "arbiter POST /getsignedmultisig allocation",
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
