using VerifiedXCore.Bitcoin.ElectrumX;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>Why a transaction is being validated, as far as the Electrum deposit check is concerned.</summary>
    public enum ElectrumCheckMode
    {
        /// <summary>This node's own wallet/API is creating or sending it (the default).</summary>
        LocalSubmit,
        /// <summary>Received from a peer for the mempool.</summary>
        PeerAdmission,
        /// <summary>Picked from the mempool for a block this node is building.</summary>
        BlockProposal,
    }

    /// <summary>
    /// Ambient validation mode for the current call chain. Receiving and block-building code enters a mode around
    /// VerifyTX and the same-block debit guard; everything else validates as <see cref="ElectrumCheckMode.LocalSubmit"/>.
    /// Ambient rather than a parameter because the checks sit several calls deep (and inside the synchronous debit
    /// guard) on paths shared with block validation.
    /// </summary>
    public static class ElectrumCheckScope
    {
        private static readonly AsyncLocal<ElectrumCheckMode?> _mode = new();

        public static ElectrumCheckMode Current => _mode.Value ?? ElectrumCheckMode.LocalSubmit;

        public static IDisposable Enter(ElectrumCheckMode mode)
        {
            var previous = _mode.Value;
            _mode.Value = mode;
            return new Restore(previous);
        }

        /// <summary>
        /// Validators and casters enforce the owner deposit check. A plain node relaying a peer's transaction does
        /// not ask Electrum: it cannot enforce anything, and a failed lookup there only dropped valid transactions.
        /// </summary>
        public static bool ThisNodeValidates => !string.IsNullOrEmpty(Globals.ValidatorAddress) || Globals.IsBlockCaster;

        public static bool MayQueryElectrum => !(Current == ElectrumCheckMode.PeerAdmission && !ThisNodeValidates);

        private sealed class Restore : IDisposable
        {
            private readonly ElectrumCheckMode? _previous;
            public Restore(ElectrumCheckMode? previous) => _previous = previous;
            public void Dispose() => _mode.Value = _previous;
        }
    }

    public enum OwnerDepositStatus
    {
        /// <summary>DepositBalance is a real answer (or zero because no deposit is needed or there is no deposit address).</summary>
        Checked,
        /// <summary>Block validation: Electrum is never asked for mined transactions; the caller keeps its block rules.</summary>
        NotQueried,
        /// <summary>Not enforced here (a relaying node, or a validator admitting a peer's transaction while no server answers).</summary>
        Trusted,
        /// <summary>No server answered; the transaction must not be accepted or proposed on an unverified deposit.</summary>
        Unverifiable,
    }

    public readonly record struct OwnerDepositCheck(OwnerDepositStatus Status, decimal DepositBalance);

    /// <summary>
    /// The vBTC V2 owner deposit check shared by every owner-debit validation. An owner's balance is its deposit
    /// address's confirmed BTC plus its owner ledger; block validation trusts the block producer for the deposit part,
    /// so mempool admission and block proposal on validators are where it is enforced.
    ///
    /// Outcomes by mode when a deposit is needed:
    ///   block validation              -> NotQueried (unchanged block rules)
    ///   plain node relaying           -> Trusted, no query
    ///   validator admitting, no answer -> Trusted (block proposal checks again)
    ///   local submit / proposal, no answer -> Unverifiable ("could not verify, please retry"; proposal keeps it in the mempool)
    ///   a shortfall                   -> double-checked on a second server before it counts
    /// </summary>
    public static class VbtcOwnerDeposit
    {
        public const string UnverifiableReason = "Could not verify the vBTC deposit balance: no Bitcoin (Electrum) server answered. Please retry.";

        public static bool IsUnverifiable(string? reason) => reason != null && reason.StartsWith(UnverifiableReason, StringComparison.Ordinal);

        /// <param name="resolveDepositAddress">The vault's Bitcoin deposit address; called only when a query is needed
        /// (it may decompile the contract).</param>
        /// <param name="neededFromDeposit">What the deposit must cover once the owner ledger is counted (amount minus ledger).</param>
        /// <param name="blockDownloads">Block sync: Electrum is not asked.</param>
        /// <param name="blockVerify">Block validation: Electrum is not asked.</param>
        public static async Task<OwnerDepositCheck> CheckAsync(Func<string?> resolveDepositAddress, decimal neededFromDeposit, bool blockDownloads, bool blockVerify)
        {
            if (blockDownloads || blockVerify)
                return new OwnerDepositCheck(OwnerDepositStatus.NotQueried, 0M);

            // The ledger alone covers it: deposits are never negative.
            if (neededFromDeposit <= 0M)
                return new OwnerDepositCheck(OwnerDepositStatus.Checked, 0M);

            var mode = ElectrumCheckScope.Current;
            if (!ElectrumCheckScope.MayQueryElectrum)
                return new OwnerDepositCheck(OwnerDepositStatus.Trusted, 0M);

            var depositAddress = resolveDepositAddress();
            if (string.IsNullOrEmpty(depositAddress))
                return new OwnerDepositCheck(OwnerDepositStatus.Checked, 0M);

            var first = await DepositBalanceLookup.GetConfirmedBalanceAsync(depositAddress);
            if (!first.Answered)
                return NoAnswer(mode);

            if (first.ConfirmedBtc >= neededFromDeposit)
                return new OwnerDepositCheck(OwnerDepositStatus.Checked, first.ConfirmedBtc);

            // A shortfall counts only once a second server agrees: a lagging server under-reports. The higher answer
            // wins. With no other server configured, the one answer stands.
            var second = await DepositBalanceLookup.GetConfirmedBalanceAsync(depositAddress, bypassCache: true, excludeServer: first.Server);
            if (second.Answered)
                return new OwnerDepositCheck(OwnerDepositStatus.Checked, Math.Max(first.ConfirmedBtc, second.ConfirmedBtc));
            if (second.NoOtherServer)
                return new OwnerDepositCheck(OwnerDepositStatus.Checked, first.ConfirmedBtc);
            return NoAnswer(mode);
        }

        private static OwnerDepositCheck NoAnswer(ElectrumCheckMode mode)
        {
            return mode == ElectrumCheckMode.PeerAdmission
                ? new OwnerDepositCheck(OwnerDepositStatus.Trusted, 0M)
                : new OwnerDepositCheck(OwnerDepositStatus.Unverifiable, 0M);
        }
    }
}
