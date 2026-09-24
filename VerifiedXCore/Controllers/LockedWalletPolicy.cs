namespace VerifiedXCore.Controllers
{
    /// <summary>
    /// BB-3 (serves VX-03, VX-13, VX-18): default-deny policy for API actions while the wallet is
    /// encrypted and locked (<see cref="Globals.IsWalletEncrypted"/> with no password in memory).
    ///
    /// The previous gate named ~15 blocked actions; everything else was allowed, so every newly added
    /// route (and every Bitcoin read route that returned plaintext keys, and ReplaceByFee) was open.
    /// This policy inverts it: an action is permitted while locked ONLY if it is listed here as
    /// "ControllerName.ActionName". Everything unlisted — including any route added in future — is
    /// refused with 401 until the operator unlocks.
    ///
    /// An action belongs here only if it neither uses nor returns local private-key material and does
    /// not change wallet/key state. Three kinds qualify:
    ///  1. Pure reads (chain, mempool, balances, status, local history, shop browsing).
    ///  2. Raw / externally-signed transaction routes: the caller signs off-node and the node only
    ///     verifies the caller's signature and relays (exchange integrations run locked wallets).
    ///  3. The unlock / lock / encryption-status endpoints themselves.
    /// A reflection test (LockedWalletPolicyTests) asserts every entry names a real action, and
    /// pins the key-bearing actions that must never be listed.
    /// </summary>
    public static class LockedWalletPolicy
    {
        public static readonly IReadOnlySet<string> AllowedWhileLocked = new HashSet<string>(StringComparer.Ordinal)
        {
            // ── V1 ──────────────────────────────────────────────────────────────────────────
            "V1.Get", "V1.getabl", "V1.checkaddress", "V1.CheckStatus", "V1.Health", "V1.GetLatestReleaseFiles",
            "V1.GetAllAddresses", "V1.NetworkMetrics", "V1.GetValidatorAddresses", "V1.IsValidating",
            "V1.GetValidatorInfo", "V1.GetAddressInfo", "V1.GetChainBalance", "V1.SendBlock", "V1.GetLastBlock",
            "V1.GetRollbackBlocks", "V1.GetAllTransactions", "V1.ValidateAddress", "V1.GetMempool",
            "V1.GetMemBlockCluster", "V1.GetBlockByHeight", "V1.GetBlockByHash", "V1.GetMasternodesSent",
            "V1.GetMasternodes", "V1.GetBeaconPool", "V1.GetValidatorPoolInfo", "V1.GetMotherURL", "V1.MothersKids",
            "V1.Mother", "V1.Egg", "V1.GetPeerInfo", "V1.ListActiveVals", "V1.ListBannedPeers", "V1.GetDebugInfo",
            "V1.GetConnectionHistory", "V1.GetClientInfo", "V1.GetCLIVersion", "V1.ReadRBXLog", "V1.ReadValLog",
            "V1.GetWalletInfo", "V1.SyncBalances", "V1.ValidateSignature",
            // unlock / lock / encryption status
            "V1.UnlockWallet", "V1.LockWallet", "V1.GetDecryptWallet", "V1.GetEncryptedPassword", "V1.GetEncryptLock", "V1.GetIsWalletEncrypted",
            "V1.CheckPasswordNeeded", "V1.GetIsEncryptedPasswordStored",

            // ── V2 ──────────────────────────────────────────────────────────────────────────
            "V2.Get", "V2.GetBalances", "V2.GetStateBalance", "V2.ResolveAdnr", "V2.ResolveAddressAdnr",
            "V2.GetWinningProofs", "V2.ValidatorPool", "V2.ConsensusHeaderQueue", "V2.Producers", "V2.FailedProducers",
            "V2.BannedFailedProducers", "V2.GetBackupProofs", "V2.GetFinalizedProofs", "V2.GetNextValBlock",
            "V2.GetNetworkBlockQueue", "V2.GetValLists", "V2.GetCasterRounds", "V2.GetUncompressedByte",
            "V2.GetImageUncompressedBase",

            // ── TXV1 (local history reads + raw, externally signed TX relay) ────────────────
            "TXV1.GetTimestamp", "TXV1.GetSuccessfulLocalTX", "TXV1.GetReserveLocalTX", "TXV1.GetMinedLocalTX",
            "TXV1.GetAllLocalTX", "TXV1.GetPendingLocalTX", "TXV1.GetFailedLocalTX", "TXV1.GetLocalTxByHash",
            "TXV1.GetLocalTxByBlock", "TXV1.GetLocalTxBeforeBlock", "TXV1.GetLocalTxAfterBlock",
            "TXV1.GetLocalTxBeforeTimestamp", "TXV1.GetLocalTxAfterTimestamp", "TXV1.GetLocalTxByAddress",
            "TXV1.GetLocalTxByAddressLimit", "TXV1.GetLocalTxByAddressPaginated", "TXV1.GetAddressNonce",
            "TXV1.GetNetworkTXByHash", "TXV1.GetNetworkTXByHashSafe", "TXV1.GetLocalADNRTX", "TXV1.ValidateSignature",
            "TXV1.GetFortisBroadcastTx", "TXV1.GetRawTxFee", "TXV1.GetTxHash", "TXV1.VerifyRawTransaction",
            "TXV1.SendRawTransaction", "TXV1.GetNFTMintData", "TXV1.GetSCMintDeployData", "TXV1.GetNFTTransferData",
            "TXV1.GetNFTEvolveData", "TXV1.GetNFTBurnData",

            // ── SCV1 / TKV2 / VOV1 / BCV1 / ADJV1 / Integrations (reads) ────────────────────
            "SCV1.Get", "SCV1.GetCurrentSCOwner", "SCV1.GetSmartContractsByAddress", "SCV1.GetSmartContractsState",
            "SCV1.GetLastKnownLocators", "SCV1.GetNFTAssetLocation", "SCV1.GetSmartContractData",
            "SCV1.GetAllSmartContracts", "SCV1.GetMintedSmartContracts", "SCV1.GetSingleSmartContract",
            "SCV1.VerifyOwnership",
            "TKV2.Get", "TKV2.GetTokens", "TKV2.GetTokensUpdate", "TKV2.GetVoteBySmartContractUID",
            "TKV2.GetVoteByTopic", "TKV2.GetVotesByAddress", "TKV2.GetTokenOwnerVoteList",
            "VOV1.GetAllTopics", "VOV1.GetActiveTopics", "VOV1.GetInactiveTopics", "VOV1.GetTopicDetails",
            "VOV1.GetMyTopics", "VOV1.GetMyVotes", "VOV1.GetTopicVotes", "VOV1.GetAuditTopic", "VOV1.GetSearchTopics",
            "BCV1.GetBeacons", "BCV1.DecodeBeaconLocator", "BCV1.GetBeaconInfo", "BCV1.GetAssetQueue",
            "BCV1.GetBeaconRequest",
            "ADJV1.Get", "ADJV1.GetDups", "ADJV1.GetConsensusBroadcastTx", "ADJV1.GetFortisBroadcastTx",
            "ADJV1.GetMasternodes", "ADJV1.GetMasternodesSent", "ADJV1.GetAdjInfo",
            "IntegrationsV1.Network", "IntegrationsV1.Height", "IntegrationsV1.LastBlock",

            // ── Reserve (reads only; unlock/restore/sign stay locked) ───────────────────────
            "RSV1.GetAllReserveAccounts", "RSV1.GetReserveAccountInfo", "RSV1.GetReserveTransactions",

            // ── Decentralized shop browsing (reads + connect; no bids, saves, publishes) ────
            "DSTV1.Get", "DSTV1.GetCollection", "DSTV1.GetAllCollections", "DSTV1.GetDefaultCollection",
            "DSTV1.GetCollectionListings", "DSTV1.GetAuctionByListing", "DSTV1.GetDecShopByURL", "DSTV1.GetDecShop",
            "DSTV1.GetNetworkDecShopInfo", "DSTV1.GetDecShopStateTreiList", "DSTV1.ConnectToDecShop",
            "DSTV1.GetConnections", "DSTV1.GetShopInfo", "DSTV1.GetShopCollections", "DSTV1.GetShopListings",
            "DSTV1.GetShopAuctions", "DSTV1.GetShopListingsByCollection", "DSTV1.GetShopSpecificListing",
            "DSTV1.GetShopSpecificAuction", "DSTV1.CheckPingShop", "DSTV1.GetDetailedChatMessages",
            "DSTV1.GetSimpleChatMessages", "DSTV1.GetSpecificChatMessages", "DSTV1.GetMostRecentChatMessages",
            "DSTV1.GetSummaryChatMessages", "DSTV1.GetSimpleShopChatMessages", "DSTV1.GetDetailedShopChatMessages",
            "DSTV1.GetDetailedSpecificShopChatMessages", "DSTV1.GetSimpleSpecificShopChatMessages",
            "DSTV1.GetDecShopData", "DSTV1.GetNFTAssets", "DSTV1.GetListing", "DSTV1.GetBids", "DSTV1.GetListingBids",
            "DSTV1.GetBidsByStatus", "DSTV1.GetSingleBids", "DSTV1.GetShopListingBids",
            "WebShopV1.Get", "WebShopV1.ConnectToDecShop", "WebShopV1.CheckPingShop", "WebShopV1.GetDecShopData",
            "WebShopV1.GetConnections", "WebShopV1.GetShopSpecificAuction", "WebShopV1.GetShopListingBids",

            // ── Privacy (reads) ─────────────────────────────────────────────────────────────
            "PrivacyV1.GetPlonkStatus", "PrivacyV1.GetShieldedBalance", "PrivacyV1.GetShieldedPoolState",
            "PrivacyV1.GetShieldedVbtcBalance", "PrivacyV1.GetVbtcShieldedPoolState",

            // ── Bitcoin (reads; key-returning account routes are NOT listed) ─────────────────
            "BTCV2.Get", "BTCV2.GetDefaultAddressType", "BTCV2.GetAddressUTXOList", "BTCV2.GetAddressTXList",
            "BTCV2.GetBitcoinTXList", "BTCV2.GetLastAccounySync", "BTCV2.GetElectrumXState", "BTCV2.CalculateFee",
            "BTCV2.GetTokenizationDetails", "BTCV2.GetTokenizedBTCList", "BTCV2.SyncStatus", "BTCV2.GetvBTCBalance",
            "BTCV2.GetDefaultImageBase",

            // ── vBTC (reads + raw, externally signed flows) ─────────────────────────────────
            "VBTC.Get", "VBTC.GetValidatorList", "VBTC.GetValidatorStatus", "VBTC.GetMPCDepositAddress",
            "VBTC.GetFrostBlacklist", "VBTC.GetCeremonyStatus", "VBTC.GetS3CStatus", "VBTC.GetS3CAutoBridgeStatus",
            "VBTC.GetVBTCOwnershipTransferData", "VBTC.GetWithdrawalRequestShares", "VBTC.GetVBTCBalance",
            "VBTC.GetAllVBTCBalances", "VBTC.GetContractDetails", "VBTC.GetWithdrawalHistory",
            "VBTC.GetWithdrawalStatus", "VBTC.GetContractHealth", "VBTC.GetDefaultImageBase", "VBTC.GetContractList",
            "VBTC.GetShieldedVBTCBalance", "VBTC.GetShieldedVBTCPoolState", "VBTC.GetBridgeLockStatus",
            "VBTC.GetMintAttestation", "VBTC.GetBridgeLocks", "VBTC.GetBridgeLocksByOwner", "VBTC.GetBaseBalance",
            "VBTC.GetBridgeStatus", "VBTC.GetBridgeConfig",
            "VBTC.CreateVBTCContractRaw", "VBTC.PrepareMPCCeremonyRaw", "VBTC.ExecuteMPCCeremonyRaw",
            "VBTC.GetRawCreateContractTxData", "VBTC.SendRawCreateContractTx",
            "VBTC.GetRawTransferVBTCData", "VBTC.GetRawTransferVBTCMultiData", "VBTC.SendRawTransferVBTCTx",
            "VBTC.RequestWithdrawalRaw", "VBTC.GetRawRequestWithdrawalTxData", "VBTC.GetRawRequestWithdrawalMultiTxData",
            "VBTC.SendRawRequestWithdrawalTx", "VBTC.PrepareCompleteWithdrawalRaw", "VBTC.ExecuteCompleteWithdrawalRaw",
            "VBTC.GetRawCompleteWithdrawalTxData", "VBTC.SendRawCompleteWithdrawalTx", "VBTC.CancelWithdrawalRaw",
            "VBTC.GetRawCancelWithdrawalTxData", "VBTC.SendRawCancelWithdrawalTx",

            // ── Browser wallet (reads) and explorer ─────────────────────────────────────────
            "Wallet.Index", "Wallet.GetAccounts", "Wallet.GetTransactions", "Wallet.GetNFTs", "Wallet.GetBitcoinAccounts",
            "Wallet.GetBitcoinBaseBalances", "Wallet.GetVBTC", "Wallet.VBTCWithdrawStatus", "Wallet.VBTCBridgePreflight",
            "Wallet.VBTCBridgeLockStatus", "Wallet.VBTCBaseBalance", "Wallet.GetShieldedAddresses",
            "Wallet.GetShieldedBalance", "Wallet.GetPlonkStatus", "Wallet.GetShieldedPoolState",
            "Wallet.GetShieldedVbtcBalance", "Wallet.GetVbtcShieldedPoolState", "Wallet.GetBaseAddress",
            "Explorer.Index", "Explorer.GetStats", "Explorer.GetBlocks", "Explorer.GetBlock", "Explorer.GetBlockByHash",
            "Explorer.GetTransaction", "Explorer.GetAddress", "Explorer.GetMempoolTransactions", "Explorer.Search",
            "Explorer.Stream",
        };

        public static bool IsAllowedWhileLocked(string? controllerName, string? actionName) =>
            controllerName != null && actionName != null && AllowedWhileLocked.Contains($"{controllerName}.{actionName}");

        /// <summary>True when the wallet is encrypted and its password is not in memory.</summary>
        public static bool WalletIsLocked() =>
            Globals.IsWalletEncrypted && (Globals.EncryptPassword == null || Globals.EncryptPassword.Length == 0);
    }
}
