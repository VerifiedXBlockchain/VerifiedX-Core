using LiteDB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// A bridge exit (vBTC.b burned on Base, Bitcoin paid out of the vaults whose locks back it) on vaults the chain
    /// watches (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.6). It takes the same path as a withdrawal: when the exit
    /// transaction is applied, the Bitcoin transaction for each vault it draws on is worked out from state and pinned;
    /// the vault's signers sign exactly that; Bitcoin showing it settles that vault's part, and the exit is complete
    /// when every part is. No caster builds a Bitcoin transaction, reports a transaction id, or fails an exit.
    ///
    /// What differs from a withdrawal:
    /// <list type="bullet">
    /// <item><b>Whole or not at all.</b> An exit that draws on several vaults is pinned on all of them in the same
    /// block, or on none. So an exit with nothing pinned has no signature anywhere and can be failed as a whole; an
    /// exit with pins will be paid in full. There is no state in which one part can be paid and another given up.</item>
    /// <item><b>Failed by rule.</b> An exit that could not be pinned within the expiry window is failed by every node
    /// at the same block: its locks are restored and its record marked failed, which lets the casters make it again
    /// from other locks. The caster-sent failure and completion transactions are refused for these exits.</item>
    /// <item><b>Nothing was escrowed on the ledger.</b> The locker was debited when it locked; the exit reserved the
    /// locks. So an exit that fails restores its locks instead of refunding anybody.</item>
    /// <item><b>Its fee is raised by rule.</b> An exit has no requester on this chain who could raise the Bitcoin fee
    /// of a part that is not confirming, and a pinned part cannot be given up. So the chain raises it: a part whose
    /// newest transaction has been unconfirmed for <see cref="BtcAttestRules.StuckPinBlocks"/> blocks gets a
    /// replacement at twice the fee rate, on the same coins, the fee taken out of the part's amount like any
    /// withdrawal's (<see cref="RaiseStuckExitFees"/>).</item>
    /// <item><b>The payout is added back to the vault's owner.</b> When a part is paid the vault's Bitcoin falls and no
    /// ledger row moves; the vBTC behind it was already debited once, at the lock. Without the add-back the owner's
    /// figure would fall a second time and that much of the vault's Bitcoin would belong to nobody.</item>
    /// </list>
    /// </summary>
    public static partial class BtcWithdrawals
    {
        /// <summary>The fee rate of a bridge exit's Bitcoin transaction, sat/vB: the one exits have always paid.</summary>
        public const long ExitFeeRate = 10;

        /// <summary>How many times the rule can double a part's fee rate that every part must be able to bear (10 to 160 sat/vB).</summary>
        public const int ExitPartBearsDoublings = 4;

        /// <summary>
        /// The least one vault may be asked to pay for an exit: the fee of the smallest transaction after the rule has
        /// doubled it <see cref="ExitPartBearsDoublings"/> times, and an output above dust (25,186 sats). It used to be
        /// the fee at 10 sat/vB and dust (2,086 sats): such a part could never have its fee raised, nobody can release
        /// or take over an exit's part, and so it held the coin it was fixed on (in a vault with one coin, the whole
        /// vault) for as long as Bitcoin asked more than 10 sat/vB, at the price of a 2,100-sat burn (second path
        /// audit, 14 October 2026).
        /// </summary>
        public static readonly long MinExitPartSats = BtcCanonicalWithdrawal.VSize(1) * ExitFeeRate * (1L << ExitPartBearsDoublings) + BtcCanonicalWithdrawal.DustSats;

        /// <summary>
        /// The id of one vault's part of an exit. Keyed by the exit transaction and its block, so an exit made again
        /// after a failure has parts of its own (even if its transaction were to hash the same).
        /// </summary>
        public static string ExitRefOf(string exitTxHash, long exitVfxHeight, string scUID) => $"exit:{exitTxHash}@{exitVfxHeight}|{scUID}";

        public static string ExitRefOf(VBTCBridgeBtcExitState exit, string scUID) => ExitRefOf(exit.ExitTxHash, exit.ExitVfxHeight, scUID);

        /// <summary>Whether a part belongs to this attempt at the exit (and not to an earlier, failed one).</summary>
        private static bool IsPartOf(VaultWithdrawalRow part, VBTCBridgeBtcExitState exit) =>
            part.RequestHash == exit.ExitTxHash && part.RequestVfxHeight == exit.ExitVfxHeight;

        /// <summary>The reference the signing ceremony uses for one vault's part of an exit (as the casters have always named it).</summary>
        public static string ExitSigningReference(string burnHash, string scUID) =>
            $"{FROST.FrostSigningAuthorization.BtcExitPrefix}{burnHash[..Math.Min(16, burnHash.Length)]}_{scUID[..Math.Min(8, scUID.Length)]}";

        private static List<PoolUnlockAllocation> AllocationsOf(VBTCBridgeBtcExitState? exit)
        {
            if (exit == null || string.IsNullOrWhiteSpace(exit.AllocationsJson)) return new List<PoolUnlockAllocation>();
            try { return JsonConvert.DeserializeObject<List<PoolUnlockAllocation>>(exit.AllocationsJson) ?? new List<PoolUnlockAllocation>(); }
            catch { return new List<PoolUnlockAllocation>(); }
        }

        /// <summary>The vault a lock belongs to, from the lock's own record (never from what a transaction says).</summary>
        private static string? VaultOfLock(PoolUnlockAllocation allocation) =>
            VBTCBridgeLockState.GetByLockId(allocation.LockId)?.SmartContractUID ?? allocation.SmartContractUID;

        private static long SatsOf(decimal btc) => (long)(btc * BtcAttestBalance.SatsPerBtc);

        /// <summary>What each vault pays for a set of allocations, in vault order.</summary>
        private static List<(string Vault, long Sats)> PartsOf(IEnumerable<PoolUnlockAllocation> allocations) =>
            allocations.Select(a => (Vault: VaultOfLock(a) ?? "", Sats: SatsOf(a.UnlockAmount)))
                .GroupBy(a => a.Vault, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Sum(a => a.Sats)))
                .ToList();

        /// <summary>
        /// Whether a set of allocations is on watched vaults: true for all of them, false for none; null with the
        /// reason when they are mixed (never accepted: an exit is paid one way or the other).
        /// </summary>
        public static bool? ExitIsOnWatchedVaults(IEnumerable<PoolUnlockAllocation> allocations, out string? reason)
        {
            reason = null;
            var kinds = allocations.Select(a => BtcAttestBalance.IsAttestedVault(VaultOfLock(a))).Distinct().ToList();
            if (kinds.Count == 1) return kinds[0];
            if (kinds.Count == 0) return false;
            reason = "A bridge exit draws either on vaults the chain watches or on older vaults, not on both.";
            return null;
        }

        /// <summary>Whether the exit recorded for a burn is paid by pinned transactions (it draws on watched vaults).</summary>
        public static bool IsWatchedExit(VBTCBridgeBtcExitState? exit) =>
            exit != null && ExitIsOnWatchedVaults(AllocationsOf(exit), out _) == true;

        // ── Rules checked by VerifyTX ───────────────────────────────────────────────────────────────────────

        /// <summary>Why a bridge exit transaction is refused under these rules; null when it is not refused here.</summary>
        public static string? ExitRefusal(Transaction tx)
        {
            try
            {
                if (tx == null || string.IsNullOrWhiteSpace(tx.Data)) return null;
                switch (tx.TransactionType)
                {
                    case TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC:
                    {
                        var data = JObject.Parse(tx.Data);
                        var allocations = data["Allocations"]?.ToObject<List<PoolUnlockAllocation>>();
                        if (allocations == null || allocations.Count == 0)
                        {
                            // The single-lock shape of the first bridge version names its one contract directly.
                            return BtcAttestBalance.IsAttestedVault(Stored(data["ContractUID"]?.ToObject<string?>()))
                                ? "A bridge exit from this vault carries its allocations."
                                : null;
                        }
                        var watched = ExitIsOnWatchedVaults(allocations, out var mixed);
                        if (watched == null) return mixed;
                        if (watched == false) return null;
                        var destination = data["BtcDestination"]?.ToObject<string?>();
                        if (!BtcCanonicalWithdrawal.TryDestinationScript(destination, out var script))
                            return $"The exit's Bitcoin destination is not an address on this network: {destination}";
                        foreach (var (vault, sats) in PartsOf(allocations))
                        {
                            if (sats < MinExitPartSats)
                                return $"The exit asks vault {vault} for {sats} sats; a vault's part of an exit is at least {MinExitPartSats} sats (its Bitcoin fee and an output above dust).";
                            // As for a withdrawal: a payment to the vault's own address cannot be told from its change.
                            if (BtcAttestStore.ScriptRowForVault(vault)?.ScriptHex == script)
                                return $"The exit's Bitcoin destination is the deposit address of vault {vault}, which it would be paid from.";
                        }
                        return null;
                    }
                    case TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE:
                        return IsWatchedExit(VBTCBridgeBtcExitState.GetByBurnHash(JObject.Parse(tx.Data)["BaseBurnTxHash"]?.ToObject<string?>() ?? ""))
                            ? "This bridge exit is completed by the chain when Bitcoin shows its transactions; no completion transaction is accepted."
                            : null;
                    case TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL:
                        return IsWatchedExit(VBTCBridgeBtcExitState.GetByBurnHash(JObject.Parse(tx.Data)["BaseBurnTxHash"]?.ToObject<string?>() ?? ""))
                            ? $"This bridge exit is failed by the chain if it cannot be pinned within {VBTCWithdrawalRequest.EXPIRY_BLOCKS} blocks; no failure transaction is accepted."
                            : null;
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                return $"The bridge exit transaction could not be read: {ex.Message}";
            }
        }

        // ── Block application ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// An exit has just been applied (its locks reserved, its record written): record the part of every watched
        /// vault it draws on, and pin their Bitcoin transactions if all of them can be pinned now.
        /// </summary>
        public static void OnExitApplied(VBTCBridgeBtcExitState exit, long vfxHeight, LiteDatabase? db = null)
        {
            try
            {
                if (!IsWatchedExit(exit)) return;
                var hasDestination = BtcCanonicalWithdrawal.TryDestinationScript(exit.BtcDestination, out var script);
                VaultWithdrawalRow? first = null;
                foreach (var (vault, sats) in PartsOf(AllocationsOf(exit)))
                {
                    var reference = ExitRefOf(exit, vault);
                    if (BtcAttestStore.Withdrawal(reference, db) != null) continue;
                    var row = new VaultWithdrawalRow
                    {
                        Ref = reference, SmartContractUID = vault, RequestHash = exit.ExitTxHash, Requester = "",
                        AmountSats = sats, FeeRate = ExitFeeRate, RequestVfxHeight = vfxHeight,
                        State = VaultWithdrawalState.Waiting, IsBridgeExit = true, ExitBurnHash = exit.BaseBurnTxHash,
                        DestinationScriptHex = hasDestination ? script : "",
                        WaitingReason = hasDestination ? null : "the destination is not a Bitcoin address on this network",
                    };
                    BtcAttestStore.SaveWithdrawal(row, db);
                    first ??= row;
                }
                if (first != null) TryPinExit(first, vfxHeight, db);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Recording bridge exit {exit?.BaseBurnTxHash} failed: {ex}", "BtcWithdrawals.OnExitApplied()");
            }
        }

        /// <summary>
        /// Pins every part of an exit, or none: the transactions are worked out for all of its vaults first, and
        /// recorded only if every one of them can be built from that vault's free coins.
        /// </summary>
        private static bool TryPinExit(VaultWithdrawalRow anyPart, long vfxHeight, LiteDatabase? db)
        {
            var parts = BtcAttestStore.ExitParts(anyPart.RequestHash, anyPart.RequestVfxHeight, db).OrderBy(p => p.Ref, StringComparer.Ordinal).ToList();
            if (parts.Count == 0 || parts.Any(p => p.State != VaultWithdrawalState.Waiting)) return false;

            var built = new List<(VaultWithdrawalRow Row, BtcCanonicalTx? Tx, string? Reason)>();
            foreach (var part in parts)
            {
                string? reason = "the destination is not a Bitcoin address on this network";
                var tx = string.IsNullOrEmpty(part.DestinationScriptHex)
                    ? null
                    : BtcCanonicalWithdrawal.Build(part.SmartContractUID, part.DestinationScriptHex, part.AmountSats, part.FeeRate, out reason, db);
                built.Add((part, tx, reason));
            }

            if (built.Any(b => b.Tx == null))
            {
                foreach (var (row, tx, reason) in built)
                    NoteWaiting(row, tx == null ? reason : "the exit's other vaults cannot pay their parts yet", db);
                return false;
            }
            foreach (var (row, tx, _) in built)
                Pin(row, tx!, vfxHeight, db);
            return true;
        }

        /// <summary>
        /// Every <see cref="BtcAttestRules.TakeRetryBlocks"/> blocks: each pinned exit part whose newest transaction
        /// was fixed <see cref="BtcAttestRules.StuckPinBlocks"/> blocks ago or more gets a replacement at twice its fee
        /// rate (10, 20, 40 ... sat/vB, at most <see cref="MaxPins"/> transactions), on the same coins and with the
        /// larger fee taken out of the amount paid. A function of state and height: every node fixes the same
        /// transaction at the same block, and nobody decides anything. A part too small to bear the next fee keeps the
        /// transactions it has. Without this a part fixed at 10 sat/vB while Bitcoin asks for more would hold its
        /// coins and its locks for as long as that lasted, with the vBTC behind it already burned on Base.
        /// </summary>
        private static void RaiseStuckExitFees(long vfxHeight, LiteDatabase? db)
        {
            if (vfxHeight % BtcAttestRules.TakeRetryBlocks != 0) return;
            foreach (var row in BtcAttestStore.WithdrawalsInState(VaultWithdrawalState.Pinned, db)
                         .Where(r => r.IsBridgeExit && HasRoomForPayment(r))
                         .OrderBy(r => r.Ref, StringComparer.Ordinal))
            {
                var newest = row.Pins.LastOrDefault(p => !p.Dead);
                if (newest == null || vfxHeight < newest.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks) continue;
                long rate;
                try { rate = checked((newest.FeeRate > 0 ? newest.FeeRate : row.FeeRate) * 2); }
                catch (OverflowException) { continue; }
                if (rate > BtcCanonicalWithdrawal.MaxFeeRate) continue;
                var replacement = BtcCanonicalWithdrawal.BuildReplacement(row.SmartContractUID, row.DestinationScriptHex, row.AmountSats, rate, newest.Inputs, row.Ref, out _, db);
                if (replacement == null) continue; // the fee would leave too little to pay, or a coin is not there (a rewind)

                foreach (var earlier in row.Pins.Where(p => !p.Dead))
                    earlier.Superseded = true;
                row.Pins.Add(new VaultPin
                {
                    Txid = replacement.Txid, TxHex = replacement.TxHex, Inputs = replacement.Inputs, DestinationSats = replacement.DestinationSats,
                    ChangeSats = replacement.ChangeSats, FeeSats = replacement.FeeSats, FeeRate = rate, PinnedAtVfxHeight = vfxHeight,
                });
                BtcAttestStore.SaveWithdrawal(row, db);
                SCLogUtility.Log($"Bridge exit for burn {row.ExitBurnHash}: the part on vault {row.SmartContractUID} was unconfirmed for {BtcAttestRules.StuckPinBlocks} blocks; its Bitcoin fee is raised to {rate} sat/vB by rule, replacement {replacement.Txid} pinned ({replacement.FeeSats} sats, out of the amount paid).",
                    "BtcWithdrawals.RaiseStuckExitFees()");
            }
        }

        /// <summary>
        /// An exit that was never pinned and is past its window: its parts are closed, its locks restored (and left in
        /// use: nothing is wrong with them), and its record marked failed so that it can be made again.
        /// </summary>
        private static void ExpireExit(VaultWithdrawalRow anyPart, long vfxHeight, LiteDatabase? db)
        {
            var exitTxHash = anyPart.RequestHash;
            var parts = BtcAttestStore.ExitParts(exitTxHash, anyPart.RequestVfxHeight, db).OrderBy(p => p.Ref, StringComparer.Ordinal).ToList();
            if (parts.Count == 0 || parts.Any(p => p.State != VaultWithdrawalState.Waiting)) return;

            var why = $"it could not be pinned within {VBTCWithdrawalRequest.EXPIRY_BLOCKS} blocks ({string.Join("; ", parts.Select(p => $"{p.SmartContractUID}: {p.WaitingReason}"))})";
            foreach (var part in parts)
            {
                part.State = VaultWithdrawalState.Expired;
                part.ClosedAtVfxHeight = vfxHeight;
                part.WaitingReason = why;
                BtcAttestStore.SaveWithdrawal(part, db);
            }

            var exit = VBTCBridgeBtcExitState.GetByBurnHash(parts[0].ExitBurnHash ?? "");
            if (exit == null || exit.IsComplete || !IsPartOf(parts[0], exit))
            {
                ErrorLogUtility.LogError($"Bridge exit {exitTxHash} expired but its record is not open; no locks restored.", "BtcWithdrawals.ExpireExit()");
                return;
            }
            foreach (var allocation in AllocationsOf(exit))
                VBTCBridgeLockState.RestoreUnlock(allocation.LockId, allocation.UnlockAmount, SatsOf(allocation.UnlockAmount));
            exit.IsComplete = true;
            exit.BtcTxHash = $"{VBTCBridgeBtcExitState.FailedMarkerPrefix}expired@{vfxHeight}";
            VBTCBridgeBtcExitState.Update(exit);
            SCLogUtility.Log($"Bridge exit for burn {exit.BaseBurnTxHash} failed by rule at block {vfxHeight}: {why}. Its locks were restored.", "BtcWithdrawals.ExpireExit()");
        }

        /// <summary>
        /// Bitcoin shows one vault's part of an exit: the amount is added back to that vault's owner and the burn is
        /// used up for good; the exit is complete when every part is paid.
        /// </summary>
        private static void SettleExitPart(VaultWithdrawalRow row, string txid, Transaction report, long vfxHeight, LiteDatabase? db)
        {
            BtcAttestStore.AddSettledExit(row.SmartContractUID, row.AmountSats, db);
            // Bitcoin has left a vault for this burn: it can never back another exit or unlock, whatever happens to the rest.
            VBTCBridgeConsumedBurn.TryMarkConsumed(row.ExitBurnHash, "BTC_EXIT_COMPLETE", report?.Hash ?? "", vfxHeight);

            var exit = VBTCBridgeBtcExitState.GetByBurnHash(row.ExitBurnHash ?? "");
            if (exit == null || exit.IsComplete || !IsPartOf(row, exit)) return;
            var parts = BtcAttestStore.ExitParts(row.RequestHash, row.RequestVfxHeight, db).OrderBy(p => p.Ref, StringComparer.Ordinal).ToList();
            if (parts.Any(p => p.State != VaultWithdrawalState.Settled)) return;

            exit.IsComplete = true;
            exit.BtcTxHash = string.Join(",", parts.Select(p => p.SettledTxid));
            VBTCBridgeBtcExitState.Update(exit);
            SCLogUtility.Log($"Bridge exit for burn {exit.BaseBurnTxHash} completed by Bitcoin ({parts.Count} vault(s): {exit.BtcTxHash}; last reported in {report?.Hash}).", "BtcWithdrawals.SettleExitPart()");
        }

        /// <summary>
        /// One vault's part of an exit can never be paid: a coin pinned to it was spent by another transaction. That
        /// takes signatures the vault's signers do not give (they sign only what is pinned), so it is not expected;
        /// if it happens the part's locks are restored and taken out of use, and the exit is marked failed when no part
        /// of it was or will be paid, so that it can be made again from other locks.
        /// </summary>
        private static void VoidExitPart(VaultWithdrawalRow row, long vfxHeight, LiteDatabase? db, string why)
        {
            var exit = VBTCBridgeBtcExitState.GetByBurnHash(row.ExitBurnHash ?? "");
            if (exit == null || !IsPartOf(row, exit)) return;
            foreach (var allocation in AllocationsOf(exit).Where(a => VaultOfLock(a) == row.SmartContractUID))
                VBTCBridgeLockState.BlacklistLock(allocation.LockId, allocation.UnlockAmount, SatsOf(allocation.UnlockAmount), $"exit voided: {why}");

            var parts = BtcAttestStore.ExitParts(row.RequestHash, row.RequestVfxHeight, db);
            var anyOpen = parts.Any(p => p.State == VaultWithdrawalState.Pinned);
            var anyPaid = parts.Any(p => p.State == VaultWithdrawalState.Settled);
            if (!anyOpen && !anyPaid && !exit.IsComplete)
            {
                exit.IsComplete = true;
                exit.BtcTxHash = $"{VBTCBridgeBtcExitState.FailedMarkerPrefix}void@{vfxHeight}";
                VBTCBridgeBtcExitState.Update(exit);
            }
            ErrorLogUtility.LogError($"Bridge exit for burn {exit.BaseBurnTxHash}: the part on vault {row.SmartContractUID} was voided ({why}). Its locks were restored and taken out of use."
                + (anyOpen || anyPaid ? " Other parts of this exit are paid or will be: the remainder needs an operator." : " The exit was marked failed and can be made again."),
                "BtcWithdrawals.VoidExitPart()");
        }
    }
}
