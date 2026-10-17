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
            Globals.TXHeightRule5 = Globals.IsTestNet ? 746313 : Globals.TXHeightRule5;
            Globals.VbtcPrivacyDisableHeight = Globals.IsTestNet ? 1 /* TODO set testnet disable height */ : 7_281_000L;
            Globals.V2WithdrawalExpiryFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2WithdrawalOwnerAddBackFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.VbtcLegacyTransferBypassFixHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.V2TransferMultiHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.BridgeBurnBindingHeight = Globals.IsTestNet ? 1 : 7_281_000L;
            Globals.BridgeIntraBlockGuardHeight = Globals.IsTestNet ? 1 : 7_296_200L;
            Globals.WithdrawalEscrowHeight = Globals.IsTestNet ? 1 : 7_296_200L;
            // TODO set both: testnet must be ABOVE its tip at deploy (block 1,017,408 holds a request the fee floor refuses).
            Globals.VbtcWithdrawalConcurrencyHeight = Globals.IsTestNet ? 1_018_000 : Globals.VbtcWithdrawalConcurrencyHeight;
            // Must be above the tip when every node runs this build (see Globals.VbtcCancellationVoteRulesHeight).
            Globals.VbtcCancellationVoteRulesHeight = Globals.IsTestNet ? 1_019_000 : Globals.VbtcCancellationVoteRulesHeight;
            Globals.VbtcV2DkgAttestationHeight = Globals.IsTestNet ? 1_002_979 : Globals.VbtcV2DkgAttestationHeight;
            Globals.VbtcV1RetirementHeight = Globals.IsTestNet ? 1_002_979 : Globals.VbtcV1RetirementHeight;
            Globals.VbtcVaultTxSizeHeight = Globals.IsTestNet ? 1 : Globals.VbtcVaultTxSizeHeight;
            // Fund-loss audit (Oct 2026): one activation for all its rules (Globals.FundLossAuditTestnetHeight, reset 2026-10-13).
            Globals.PrivateTxSupplyRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.PrivateTxSupplyRulesHeight;
            Globals.AddressFreezeHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.AddressFreezeHeight;
            Globals.SaleCompleteContractRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.SaleCompleteContractRulesHeight;
            Globals.ReserveRecoverRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.ReserveRecoverRulesHeight;
            Globals.VaultRewriteRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.VaultRewriteRulesHeight;
            Globals.VaultBurnRuleHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.VaultBurnRuleHeight;
            Globals.PathAuditRulesHeight = Globals.IsTestNet ? Globals.FundLossAuditTestnetHeight : Globals.PathAuditRulesHeight;
            Globals.RecipientIsAddressHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.RecipientIsAddressHeight;
            Globals.ReserveRecoverReadsEveryRowHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.ReserveRecoverReadsEveryRowHeight;
            Globals.ReserveNonTransferAmountHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.ReserveNonTransferAmountHeight;
            Globals.FeeBelowOneVfxHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.FeeBelowOneVfxHeight;
            Globals.OneMovePerContractHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.OneMovePerContractHeight;
            Globals.FractionalAmountRehashHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.FractionalAmountRehashHeight;
            Globals.RecipientNetworkByteHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.RecipientNetworkByteHeight;
            Globals.VoteTopicRulesHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.VoteTopicRulesHeight;
            Globals.LegacyNameReservedHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.LegacyNameReservedHeight;
            Globals.EvolveRulesHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.EvolveRulesHeight;
            Globals.ShieldSpendsNothingHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.ShieldSpendsNothingHeight;
            Globals.SignerAgeFromEpochZeroHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.SignerAgeFromEpochZeroHeight;
            Globals.ContractPayloadArrayHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.ContractPayloadArrayHeight;
            Globals.BtcNameOneRecordPerAddressHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.BtcNameOneRecordPerAddressHeight;
            Globals.BtcNameSignerOwnsAddressHeight = Globals.IsTestNet ? 999_999_999_999 : Globals.BtcNameSignerOwnsAddressHeight;
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
