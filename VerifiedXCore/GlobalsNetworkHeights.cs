namespace VerifiedXCore
{
    public static partial class Globals
    {
        /// <summary>
        /// Sets every per-network activation height (and the network-specific lists) from <see cref="IsTestNet"/>. The one
        /// place they are set: Program.cs calls it at startup and the Warden calls it too, so the two can never disagree.
        /// </summary>
        public static void ApplyNetworkHeights()
        {
            //Globals.IsCustomTestNet = true;
            Globals.V4Height = Globals.IsTestNet ? 1 : 3_074_181;//change for mainnet.
            Globals.V2ValHeight = Globals.IsTestNet ? 0 : 3_074_180;//change for mainnet.
            Globals.SpecialBlockHeight = Globals.IsTestNet ? 2000 : 3_074_185;//change for mainnet.
            Globals.GenesisValidator = Globals.IsTestNet ? "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC" : "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH";
            Globals.TXHeightRule5 = Globals.IsTestNet ? 1 : Globals.TXHeightRule5;
            Globals.VbtcPrivacyDisableHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2WithdrawalExpiryFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2WithdrawalOwnerAddBackFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.VbtcLegacyTransferBypassFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2TransferMultiHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.BridgeBurnBindingHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.BridgeIntraBlockGuardHeight = Globals.IsTestNet ? 1 : 7_296_200L;
            Globals.WithdrawalEscrowHeight = Globals.IsTestNet ? 1 : 7_296_200L;
            // Testnet restarted from genesis on 2026-10-22 (the move to the VFX signet): the rules that were active on the
            // old chain start at block 1. The bonds and the Bitcoin attestation are staged at 300/400 (Globals.FundLossAuditTestnetHeight).
            Globals.VbtcWithdrawalConcurrencyHeight = Globals.IsTestNet ? 1 : Globals.VbtcWithdrawalConcurrencyHeight;
            Globals.VbtcCancellationVoteRulesHeight = Globals.IsTestNet ? 1 : Globals.VbtcCancellationVoteRulesHeight;
            Globals.VbtcV2DkgAttestationHeight = Globals.IsTestNet ? 1 : Globals.VbtcV2DkgAttestationHeight;
            Globals.VbtcV1RetirementHeight = Globals.IsTestNet ? 1 : Globals.VbtcV1RetirementHeight;
            Globals.VbtcVaultTxSizeHeight = Globals.IsTestNet ? 1 : Globals.VbtcVaultTxSizeHeight;
            // Fund-loss audit (Oct 2026): one activation for all its rules (Globals.FundLossAuditTestnetHeight, 400 since the 2026-10-22 reset).
            Globals.PrivateTxSupplyRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.PrivateTxSupplyRulesHeight;
            Globals.AddressFreezeHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.AddressFreezeHeight;
            Globals.SaleCompleteContractRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.SaleCompleteContractRulesHeight;
            Globals.SaleListingKeyHeight = Globals.IsTestNet ? Globals.SaleListingKeyTestnetHeight : Globals.SaleListingKeyHeight;
            Globals.ReserveRecoverRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.ReserveRecoverRulesHeight;
            Globals.VaultRewriteRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.VaultRewriteRulesHeight;
            Globals.VaultBurnRuleHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.VaultBurnRuleHeight;
            Globals.PathAuditRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.PathAuditRulesHeight;
            Globals.ContractBodyLimitsHeight = Globals.IsTestNet ? Globals.ContractBodyLimitsTestnetHeight : Globals.ContractBodyLimitsHeight;
            Globals.JsonCommentDataRuleHeight = Globals.IsTestNet ? Globals.JsonCommentDataRuleTestnetHeight : Globals.JsonCommentDataRuleHeight;
            Globals.WithdrawalDestinationRecheckHeight = Globals.IsTestNet ? Globals.WithdrawalDestinationRecheckTestnetHeight : Globals.WithdrawalDestinationRecheckHeight;
            Globals.EvolveSpecificNeedsContractHeight = Globals.IsTestNet ?Globals.EvolveSpecificNeedsContractTestnetHeight : Globals.EvolveSpecificNeedsContractHeight;
            Globals.BridgeVoidKeepsLocksHeight = Globals.IsTestNet ? Globals.BridgeVoidKeepsLocksTestnetHeight : Globals.BridgeVoidKeepsLocksHeight;
            Globals.BridgeFailKeepsLocksHeight = Globals.IsTestNet ? Globals.BridgeFailKeepsLocksTestnetHeight : Globals.BridgeFailKeepsLocksHeight;
            Globals.VaultMinimumPositionHeight = Globals.IsTestNet ? Globals.VaultMinimumPositionTestnetHeight : Globals.VaultMinimumPositionHeight;
            Globals.RawDataVerbatimHeight = Globals.IsTestNet ? Globals.RawDataVerbatimTestnetHeight : Globals.RawDataVerbatimHeight;
            Globals.ContractBodyKeySpellingHeight = Globals.IsTestNet ? Globals.ContractBodyKeySpellingTestnetHeight : Globals.ContractBodyKeySpellingHeight;
            Globals.ContractRunLimitsHeight = Globals.IsTestNet ? Globals.ContractRunLimitsTestnetHeight : Globals.ContractRunLimitsHeight;
            Globals.BridgeLockMinimumHeight = Globals.IsTestNet ? Globals.BridgeLockMinimumTestnetHeight : Globals.BridgeLockMinimumHeight;
            Globals.VaultLedgerHeight = Globals.IsTestNet ? Globals.VaultLedgerTestnetHeight : Globals.VaultLedgerHeight;
            Globals.BridgeAllocationsArrayHeight = Globals.IsTestNet ? Globals.BridgeAllocationsArrayTestnetHeight : Globals.BridgeAllocationsArrayHeight;
            Globals.ExitPartTakeoverHeight = Globals.IsTestNet ? Globals.ExitPartTakeoverTestnetHeight : Globals.ExitPartTakeoverHeight;
            Globals.BridgeBurnRefundHeight = Globals.IsTestNet ? Globals.BridgeBurnRefundTestnetHeight : Globals.BridgeBurnRefundHeight;
            Globals.ExitMinPayoutHeight = Globals.IsTestNet ? Globals.ExitMinPayoutTestnetHeight : Globals.ExitMinPayoutHeight;
            Globals.ExitPartRefundHeight = Globals.IsTestNet ? Globals.ExitPartRefundTestnetHeight : Globals.ExitPartRefundHeight;
            Globals.PoolUnlockCreditsAddressHeight = Globals.IsTestNet ? Globals.PoolUnlockCreditsAddressTestnetHeight : Globals.PoolUnlockCreditsAddressHeight;
            Globals.BridgeLockEvmAddressHeight = Globals.IsTestNet ? Globals.BridgeLockEvmAddressTestnetHeight : Globals.BridgeLockEvmAddressHeight;
            Globals.WithdrawalOwnScriptAttributionHeight = Globals.IsTestNet ? Globals.WithdrawalOwnScriptAttributionTestnetHeight : Globals.WithdrawalOwnScriptAttributionHeight;
            Globals.BridgeAllocationsSignableHeight = Globals.IsTestNet ? Globals.BridgeAllocationsSignableTestnetHeight : Globals.BridgeAllocationsSignableHeight;
            Globals.BridgeLockPartMinimumHeight = Globals.IsTestNet ? Globals.BridgeLockPartMinimumTestnetHeight : Globals.BridgeLockPartMinimumHeight;
            Globals.VrfMixHeight = Globals.IsTestNet ? Globals.VrfMixTestnetHeight : Globals.VrfMixHeight;
            Globals.ReserveRecoverReadsEveryRowHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.ReserveRecoverReadsEveryRowHeight;
            Globals.VbtcEnvelopeRulesHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.VbtcEnvelopeRulesHeight;
            Globals.ReserveNonTransferAmountHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.ReserveNonTransferAmountHeight;
            Globals.FeeBelowOneVfxHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.FeeBelowOneVfxHeight;
            Globals.OneMovePerContractHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.OneMovePerContractHeight;
            Globals.VrfSeedHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.VrfSeedHeight;
            Globals.TxHashGuardByTimeHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.TxHashGuardByTimeHeight;
            Globals.DataOwnerBoundHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.DataOwnerBoundHeight;
            Globals.TokenTransferRecipientFirstHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.TokenTransferRecipientFirstHeight;
            Globals.AdnrNameGrammarHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.AdnrNameGrammarHeight;
            Globals.TokenMintRulesHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.TokenMintRulesHeight;
            Globals.BlockTransactionCapHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.BlockTransactionCapHeight;
            Globals.FractionalAmountRehashHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.FractionalAmountRehashHeight;
            Globals.RecipientNetworkByteHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.RecipientNetworkByteHeight;
            Globals.VoteTopicRulesHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.VoteTopicRulesHeight;
            Globals.TopicAddDeterministicHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.TopicAddDeterministicHeight;
            Globals.LegacyNameReservedHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.LegacyNameReservedHeight;
            Globals.EvolveRulesHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.EvolveRulesHeight;
            Globals.ShieldSpendsNothingHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.ShieldSpendsNothingHeight;
            Globals.SignerAgeFromEpochZeroHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.SignerAgeFromEpochZeroHeight;
            Globals.ContractPayloadArrayHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.ContractPayloadArrayHeight;
            Globals.BtcNameOneRecordPerAddressHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.BtcNameOneRecordPerAddressHeight;
            Globals.BtcNameSignerOwnsAddressHeight = Globals.IsTestNet ? Globals.LedgerRulesBatchTestnetHeight : Globals.BtcNameSignerOwnsAddressHeight;
            Globals.ValidatorBondHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight - 1 : Globals.ValidatorBondHeight;
            Globals.ValidatorBondGrandfatherHeight = Globals.IsTestNet ? Globals.BitcoinAttestationObserveTestnetHeight : Globals.ValidatorBondGrandfatherHeight;
            Globals.SaleStartBidRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.SaleStartBidRulesHeight;
            Globals.SalePaymentSharesHeight = Globals.IsTestNet ? Globals.SalePaymentSharesTestnetHeight : Globals.SalePaymentSharesHeight;
            Globals.BridgeVoteOncePerCasterHeight = Globals.IsTestNet ? Globals.BridgeVoteOncePerCasterTestnetHeight : Globals.BridgeVoteOncePerCasterHeight;
            Globals.BridgeExitCarriesTotalsHeight = Globals.IsTestNet ? Globals.BridgeExitCarriesTotalsTestnetHeight : Globals.BridgeExitCarriesTotalsHeight;
            Globals.BridgeVotesBoundToAttemptHeight = Globals.IsTestNet ? Globals.BridgeVotesBoundToAttemptTestnetHeight : Globals.BridgeVotesBoundToAttemptHeight;
            Globals.CanonicalInputHeight = Globals.IsTestNet ? Globals.CanonicalInputTestnetHeight : Globals.CanonicalInputHeight;
            Globals.WithdrawalUniqueIdRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.WithdrawalUniqueIdRulesHeight;
            Globals.RoyaltyRewriteRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.RoyaltyRewriteRulesHeight;
            Globals.ReserveTokenRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.ReserveTokenRulesHeight;
            Globals.ForgedVfxBurnHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.ForgedVfxBurnHeight;
            if (Globals.IsTestNet) Globals.ForgedVfxBurns.Clear(); // the forged VFX exists on mainnet only
            // Privacy stage 2 on testnet: with the fund-loss rules (reset 2026-10-13 from 1,099,400, which had passed).
            Globals.PrivateTxProofRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.PrivateTxProofRulesHeight;
            Globals.PlonkCurrentRulesHeight = Globals.IsTestNet ? Globals.PlonkCurrentRulesTestnetHeight : Globals.PlonkCurrentRulesHeight;
            Globals.PlonkKeyBoundRulesHeight = Globals.IsTestNet ? Globals.PlonkKeyBoundRulesTestnetHeight : Globals.PlonkKeyBoundRulesHeight;
            Globals.StrictDataReaderHeight = Globals.IsTestNet ? Globals.StrictDataReaderTestnetHeight : Globals.StrictDataReaderHeight;
            // Bitcoin attestation (plan 8.4): observe from the tip of 2026-10-13, record with the fund-loss rules, enforce three days later.
            Globals.BitcoinAttestationObserveHeight = Globals.IsTestNet ? Globals.BitcoinAttestationObserveTestnetHeight : Globals.BitcoinAttestationObserveHeight;
            Globals.BitcoinAttestationRecordHeight = Globals.IsTestNet ? Globals.BitcoinAttestationRecordTestnetHeight : Globals.BitcoinAttestationRecordHeight;
            Globals.BitcoinAttestationHeight = Globals.IsTestNet ? Globals.BitcoinAttestationTestnetHeight : Globals.BitcoinAttestationHeight;
            Globals.VreeActivationHeight = Globals.IsTestNet ? Globals.VreeActivationTestnetHeight : Globals.VreeActivationHeight;
            Globals.BaseBridgeV4Height = Globals.IsTestNet ? Globals.BaseBridgeV4TestnetHeight : Globals.BaseBridgeV4Height;
            // The membership chain: its height and the record it starts from go together (Globals.MembershipAnchorRecordHash).
            Globals.MembershipChainHeight = Globals.IsTestNet ? Globals.MembershipChainTestnetHeight : Globals.MembershipChainHeight;
            Globals.MembershipAnchorRecordHash = Globals.IsTestNet ? Globals.MembershipAnchorRecordHashTestnet : Globals.MembershipAnchorRecordHash;
            if (Globals.IsTestNet) Globals.ShieldedSupplyCorrections.Clear(); // the testnet pool was never forged against
            if (Globals.IsCustomTestNet) ApplyCustomTestNetHeights();
        }

        /// <summary>
        /// The block by which every rule is active on a custom testnet (Marigold). Such a chain starts from genesis, so
        /// the heights the live testnet staged later (7,400, 107,700, 152,000, 205,615: that chain was already running)
        /// start here instead: the last of the genesis-staged heights (bonds 399, fund-loss rules 400, Bitcoin
        /// attestation 401). The heights at or below it keep their testnet values. VrfSeedHeight here puts the first
        /// epoch's anchor at block 301: a validator produces from 401 only if registered before block 301.
        /// </summary>
        public const long CustomTestNetRulesHeight = BitcoinAttestationTestnetHeight;

        /// <summary>
        /// Rules added after Marigold was already running take effect from this later block, not from
        /// <see cref="CustomTestNetRulesHeight"/>: a rule dated before the chain's tip would judge blocks that were
        /// produced under the old rules, so a node syncing Marigold from block 0 would refuse its own history. 8,500 was
        /// about 1,950 blocks past the tip (6,549) when the input-format rules reached devnet on 2026-11-24, room to
        /// upgrade every Marigold node first.
        /// </summary>
        public const long CustomTestNetInputRulesHeight = 8500;

        /// <summary>
        /// Rules added once Marigold had passed <see cref="CustomTestNetInputRulesHeight"/> take effect from this block: dated
        /// 8,500 they would judge blocks Marigold mined before its nodes ran them, and a node syncing from block 0 would
        /// refuse, or apply differently, its own history. Set on 2026-11-27 with Marigold's tip about 25,000, room to
        /// upgrade every Marigold node first.
        /// </summary>
        public const long CustomTestNetLateRulesHeight = 30_000;

        /// <summary>
        /// The membership chain on the live testnet (MembershipChain): not set yet. Set at release, at the tip plus room to
        /// upgrade every node, with <see cref="MembershipAnchorRecordHashTestnet"/> pinned the same day.
        /// </summary>
        public const long MembershipChainTestnetHeight = long.MaxValue;

        /// <summary>The record the live testnet's membership chain starts from (see Globals.MembershipAnchorRecordHash). Empty: not started.</summary>
        public const string MembershipAnchorRecordHashTestnet = "";

        /// <summary>
        /// The membership chain on Marigold. Added after Marigold passed <see cref="CustomTestNetLateRulesHeight"/>'s
        /// planning tip, so a later block again. PLACEHOLDER: to be set to Marigold's tip plus about 3,000 before this
        /// reaches devnet, with <see cref="CustomTestNetMembershipAnchorRecordHash"/> pinned the same day; until the anchor
        /// is set the chain's rules do not start here at any height.
        /// </summary>
        public const long CustomTestNetMembershipChainHeight = 60_000;

        /// <summary>The record Marigold's membership chain starts from. Empty: not started (pinned with the height).</summary>
        public const string CustomTestNetMembershipAnchorRecordHash = "";

        /// <summary>
        /// The custom testnet's heights, over the testnet ones. Config.ProcessConfig selects a custom testnet after
        /// Program.cs and the Warden first apply the heights, so it applies them again once it has.
        /// </summary>
        private static void ApplyCustomTestNetHeights()
        {
            const long h = CustomTestNetRulesHeight;
            Globals.TXHeightRule3 = h;
            // Testnet 7,400 (LedgerRulesBatchTestnetHeight)
            Globals.ReserveRecoverReadsEveryRowHeight = h;
            Globals.VbtcEnvelopeRulesHeight = h;
            Globals.ReserveNonTransferAmountHeight = h;
            Globals.FeeBelowOneVfxHeight = h;
            Globals.OneMovePerContractHeight = h;
            Globals.VrfSeedHeight = h;
            Globals.TxHashGuardByTimeHeight = h;
            Globals.DataOwnerBoundHeight = h;
            Globals.TokenTransferRecipientFirstHeight = h;
            Globals.AdnrNameGrammarHeight = h;
            Globals.TokenMintRulesHeight = h;
            Globals.BlockTransactionCapHeight = h;
            Globals.FractionalAmountRehashHeight = h;
            Globals.RecipientNetworkByteHeight = h;
            Globals.VoteTopicRulesHeight = h;
            Globals.TopicAddDeterministicHeight = h;
            Globals.LegacyNameReservedHeight = h;
            Globals.EvolveRulesHeight = h;
            Globals.ShieldSpendsNothingHeight = h;
            Globals.SignerAgeFromEpochZeroHeight = h;
            Globals.ContractPayloadArrayHeight = h;
            Globals.BtcNameOneRecordPerAddressHeight = h;
            Globals.BtcNameSignerOwnsAddressHeight = h;
            // Testnet 107,700
            Globals.VreeActivationHeight = h;
            // Testnet 152,000
            Globals.ContractBodyLimitsHeight = h;
            Globals.EvolveSpecificNeedsContractHeight = h;
            Globals.BridgeVoidKeepsLocksHeight = h;
            // Testnet 205,615
            Globals.SaleListingKeyHeight = h;
            Globals.JsonCommentDataRuleHeight = h;
            Globals.WithdrawalDestinationRecheckHeight = h;
            Globals.BridgeFailKeepsLocksHeight = h;
            Globals.VaultMinimumPositionHeight = h;
            Globals.SalePaymentSharesHeight = h;
            Globals.BridgeVoteOncePerCasterHeight = h;
            Globals.BridgeExitCarriesTotalsHeight = h;
            Globals.PlonkCurrentRulesHeight = h;
            // Not yet on the live testnet. Added while Marigold was running, so from a later block (see above).
            Globals.CanonicalInputHeight = CustomTestNetInputRulesHeight;
            Globals.RawDataVerbatimHeight = CustomTestNetInputRulesHeight;
            Globals.StrictDataReaderHeight = CustomTestNetInputRulesHeight;
            Globals.ContractBodyKeySpellingHeight = CustomTestNetInputRulesHeight;
            Globals.ContractRunLimitsHeight = CustomTestNetInputRulesHeight;
            Globals.VrfMixHeight = CustomTestNetInputRulesHeight;
            Globals.BridgeLockMinimumHeight = CustomTestNetLateRulesHeight;
            Globals.BridgeAllocationsArrayHeight = CustomTestNetLateRulesHeight;
            Globals.ExitPartTakeoverHeight = CustomTestNetLateRulesHeight;
            Globals.BridgeBurnRefundHeight = CustomTestNetLateRulesHeight;
            Globals.ExitMinPayoutHeight = CustomTestNetLateRulesHeight;
            Globals.ExitPartRefundHeight = CustomTestNetLateRulesHeight;
            Globals.PoolUnlockCreditsAddressHeight = CustomTestNetLateRulesHeight;
            Globals.BridgeLockEvmAddressHeight = CustomTestNetLateRulesHeight;
            Globals.WithdrawalOwnScriptAttributionHeight = CustomTestNetLateRulesHeight;
            Globals.VaultLedgerHeight = CustomTestNetLateRulesHeight;
            Globals.PlonkKeyBoundRulesHeight = CustomTestNetLateRulesHeight;
            Globals.BridgeVotesBoundToAttemptHeight = CustomTestNetLateRulesHeight;
            Globals.BridgeAllocationsSignableHeight = CustomTestNetLateRulesHeight;
            Globals.BridgeLockPartMinimumHeight = CustomTestNetLateRulesHeight;
            Globals.MembershipChainHeight = CustomTestNetMembershipChainHeight;
            Globals.MembershipAnchorRecordHash = CustomTestNetMembershipAnchorRecordHash;
        }
    }
}
