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
            Globals.ReserveRecoverRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.ReserveRecoverRulesHeight;
            Globals.VaultRewriteRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.VaultRewriteRulesHeight;
            Globals.VaultBurnRuleHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.VaultBurnRuleHeight;
            Globals.PathAuditRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.PathAuditRulesHeight;
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
            Globals.WithdrawalUniqueIdRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.WithdrawalUniqueIdRulesHeight;
            Globals.RoyaltyRewriteRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.RoyaltyRewriteRulesHeight;
            Globals.ReserveTokenRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.ReserveTokenRulesHeight;
            Globals.ForgedVfxBurnHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.ForgedVfxBurnHeight;
            if (Globals.IsTestNet) Globals.ForgedVfxBurns.Clear(); // the forged VFX exists on mainnet only
            // Privacy stage 2 on testnet: with the fund-loss rules (reset 2026-10-13 from 1,099,400, which had passed).
            Globals.PrivateTxProofRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.PrivateTxProofRulesHeight;
            // Bitcoin attestation (plan 8.4): observe from the tip of 2026-10-13, record with the fund-loss rules, enforce three days later.
            Globals.BitcoinAttestationObserveHeight = Globals.IsTestNet ? Globals.BitcoinAttestationObserveTestnetHeight : Globals.BitcoinAttestationObserveHeight;
            Globals.BitcoinAttestationRecordHeight = Globals.IsTestNet ? Globals.BitcoinAttestationRecordTestnetHeight : Globals.BitcoinAttestationRecordHeight;
            Globals.BitcoinAttestationHeight = Globals.IsTestNet ? Globals.BitcoinAttestationTestnetHeight : Globals.BitcoinAttestationHeight;
            if (Globals.IsTestNet) Globals.ShieldedSupplyCorrections.Clear(); // the testnet pool was never forged against
        }
    }
}
