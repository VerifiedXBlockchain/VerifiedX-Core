using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// What the wallet API says about a vault the chain watches: its Bitcoin as consensus holds it, and where each
    /// withdrawal or bridge exit stands. Read-only, from chain state, so every node gives the same answer for any
    /// vault. Nothing here is consensus.
    /// </summary>
    public static class BtcVaultView
    {
        /// <summary>
        /// Prefix of the message for a withdrawal that is waiting for its Bitcoin transaction to be fixed. With
        /// <see cref="VBTCService.AwaitingConfirmationMarker"/> (fixed, not yet confirmed) it tells a caller of the
        /// completion routes that the withdrawal is in the chain's hands and needs nothing from it.
        /// </summary>
        public const string WaitingForPinMarker = "[AWAITING-BTC-PIN]";

        /// <summary>Whether a completion message says "the chain is paying this; nothing to do" (rather than a failure).</summary>
        public static bool IsInTheChainsHands(string? message) =>
            message != null && (message.StartsWith(WaitingForPinMarker, StringComparison.Ordinal)
                || message.StartsWith(VBTCService.AwaitingConfirmationMarker, StringComparison.Ordinal));

        private static string Stored(string? scUID) =>
            string.IsNullOrEmpty(scUID) ? "" : SmartContractStateTrei.GetSmartContractState(scUID)?.SmartContractUID ?? scUID;

        private static decimal Btc(long sats) => sats / BtcAttestBalance.SatsPerBtc;

        // ── A vault ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// How the attestation rules see a vBTC V2 vault: watched (its Bitcoin is in consensus), or created before the
        /// recording height (the earlier rules until the enforcement height, frozen from then on).
        /// </summary>
        public sealed class VaultKind
        {
            /// <summary>The chain records this vault's Bitcoin; balances and withdrawals follow the attested coins.</summary>
            public bool Attested { get; init; }
            /// <summary>Created before the recording height: not watched, and frozen from <see cref="FreezeHeight"/>.</summary>
            public bool Legacy { get; init; }
            /// <summary>The block from which a legacy vault is frozen; null while that height is not set.</summary>
            public long? FreezeHeight { get; init; }
            public bool Frozen { get; init; }
            public string? Note { get; init; }
        }

        public static VaultKind KindOf(string? scUID)
        {
            var attested = BtcAttestBalance.IsAttestedVault(Stored(scUID));
            var legacy = !attested && BtcLegacyFreeze.IsLegacyVault(scUID);
            var set = Globals.BitcoinAttestationHeight > 0 && Globals.BitcoinAttestationHeight < long.MaxValue / 2;
            var frozen = legacy && BtcLegacyFreeze.Active((Globals.LastBlock?.Height ?? 0) + 1);
            return new VaultKind
            {
                Attested = attested,
                Legacy = legacy,
                FreezeHeight = legacy && set ? Globals.BitcoinAttestationHeight : null,
                Frozen = frozen,
                Note = !legacy ? null
                    : frozen ? $"This vault was created before Bitcoin attestation and has been frozen since block {Globals.BitcoinAttestationHeight}."
                    : set ? $"This vault was created before Bitcoin attestation. It is frozen from block {Globals.BitcoinAttestationHeight}: move everything to a new vault before then. Anything left stays there."
                    : "This vault was created before Bitcoin attestation. It will be frozen at the enforcement height once that is set: plan to move to a new vault.",
            };
        }

        /// <summary>A watched vault's Bitcoin: its script, its coins, its totals and its open withdrawals. Null when the vault is not watched.</summary>
        public static object? Vault(string? scUID)
        {
            scUID = Stored(scUID);
            var script = BtcAttestStore.ScriptRowForVault(scUID);
            if (script == null) return null;
            var total = BtcAttestStore.Total(scUID);
            var meta = BtcAttestStore.GetMeta();
            var coins = BtcAttestStore.CoinsOf(scUID);
            var open = BtcAttestStore.WithdrawalsOf(scUID, VaultWithdrawalState.Waiting)
                .Concat(BtcAttestStore.WithdrawalsOf(scUID, VaultWithdrawalState.Pinned))
                .OrderBy(w => w.RequestVfxHeight).ThenBy(w => w.Ref, StringComparer.Ordinal).ToList();
            return new
            {
                SmartContractUID = scUID,
                Attested = true,
                script.DepositAddress,
                DepositScriptHex = script.ScriptHex,
                script.CreatedAtVfxHeight,
                script.WatchFromBitcoinHeight,
                BitcoinTipHeight = meta?.TipHeight ?? 0,
                EventsThroughBitcoinHeight = meta?.EventsThroughHeight ?? 0,
                ConfirmationsRequired = BtcAttestRules.Depth,
                HeldBtc = Btc(total.HeldSats),
                total.HeldSats,
                total.UnspentSats,
                total.PinnedSats,
                PaidOutForBridgeExitsSats = total.SettledExitSats,
                Coins = coins.Where(c => c.Status != VaultCoinStatus.Spent)
                    .OrderBy(c => c.CreatedInHeight).ThenBy(c => c.Outpoint, StringComparer.Ordinal)
                    .Select(c => new { c.Outpoint, c.ValueSats, Status = c.Status.ToString(), c.CreatedInHeight, c.IsCoinbase, HeldFor = c.PinnedToRef }).ToList(),
                SpentCoinCount = coins.Count(c => c.Status == VaultCoinStatus.Spent),
                OpenWithdrawals = open.Select(Describe).ToList(),
            };
        }

        // ── A withdrawal ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One withdrawal's row for a watched vault, or null when there is none (the vault is not watched, or the request is unknown there).</summary>
        public static VaultWithdrawalRow? Row(string? scUID, string? requestHash) =>
            string.IsNullOrEmpty(requestHash) ? null : BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(requestHash, Stored(scUID)));

        /// <summary>Where a withdrawal from a watched vault stands, for the API. Null when there is no such withdrawal.</summary>
        public static object? Withdrawal(string? scUID, string? requestHash)
        {
            var row = Row(scUID, requestHash);
            return row == null ? null : Describe(row);
        }

        /// <summary>Every part of the bridge exit the chain holds for a burn, with the exit record. Null when no exit is recorded for it.</summary>
        public static object? Exit(string? baseBurnTxHash)
        {
            var exit = VBTCBridgeBtcExitState.GetByBurnHash(baseBurnTxHash ?? "");
            if (exit == null) return null;
            var watched = BtcWithdrawals.IsWatchedExit(exit);
            var parts = watched ? BtcAttestStore.ExitParts(exit.ExitTxHash, exit.ExitVfxHeight).OrderBy(p => p.Ref, StringComparer.Ordinal).ToList() : new List<VaultWithdrawalRow>();
            return new
            {
                exit.BaseBurnTxHash,
                exit.ExitTxHash,
                exit.ExitVfxHeight,
                exit.Amount,
                exit.AmountSats,
                exit.BtcDestination,
                PaidByPinnedTransactions = watched,
                Status = exit.IsFailedRecord ? "Failed" : exit.IsComplete ? "Complete" : parts.Any(p => p.State == VaultWithdrawalState.Pinned || p.State == VaultWithdrawalState.Settled) ? "Pinned" : "Waiting",
                exit.IsComplete,
                Failed = exit.IsFailedRecord,
                BitcoinTransactions = exit.IsComplete && !exit.IsFailedRecord ? exit.BtcTxHash : null,
                FailureRecord = exit.IsFailedRecord ? exit.BtcTxHash : null,
                Parts = parts.Select(Describe).ToList(),
            };
        }

        private static object Describe(VaultWithdrawalRow row)
        {
            var newest = row.Pins.LastOrDefault(p => !p.Dead);
            var open = row.State == VaultWithdrawalState.Waiting || row.State == VaultWithdrawalState.Pinned;
            return new
            {
                row.SmartContractUID,
                RequestHash = row.RequestHash,
                Kind = row.IsBridgeExit ? "BridgeExit" : "Withdrawal",
                BaseBurnTxHash = row.ExitBurnHash,
                Requester = string.IsNullOrEmpty(row.Requester) ? null : row.Requester,
                State = row.State.ToString(),
                Summary = Summary(row),
                Amount = Btc(row.AmountSats),
                row.AmountSats,
                FeeRate = newest?.FeeRate > 0 ? newest.FeeRate : row.FeeRate,
                row.RequestVfxHeight,
                ExpiresAtVfxHeight = row.State == VaultWithdrawalState.Waiting ? row.RequestVfxHeight + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1 : (long?)null,
                WaitingFor = row.State == VaultWithdrawalState.Waiting ? row.WaitingReason : null,
                ClosedBecause = open || row.State == VaultWithdrawalState.Settled ? null : row.WaitingReason,
                BitcoinTxid = row.SettledTxid ?? newest?.Txid,
                row.SettledTxid,
                ClosedAtVfxHeight = open ? (long?)null : row.ClosedAtVfxHeight,
                CanCancel = row.State == VaultWithdrawalState.Waiting && !row.IsBridgeExit,
                CanRaiseFee = row.State == VaultWithdrawalState.Pinned && !row.IsBridgeExit && row.Pins.Count < BtcWithdrawals.MaxPins,
                Transactions = row.Pins.Select(p => new
                {
                    p.Txid,
                    FeeRate = p.FeeRate > 0 ? p.FeeRate : row.FeeRate,
                    p.FeeSats,
                    p.DestinationSats,
                    p.ChangeSats,
                    p.Inputs,
                    p.PinnedAtVfxHeight,
                    p.Superseded,
                    p.Dead,
                    Confirmed = row.SettledTxid == p.Txid,
                    UnsignedTxHex = p.TxHex,
                }).ToList(),
            };
        }

        private static string Summary(VaultWithdrawalRow row)
        {
            var what = row.IsBridgeExit ? "This part of the bridge exit" : "This withdrawal";
            var newest = row.Pins.LastOrDefault(p => !p.Dead);
            switch (row.State)
            {
                case VaultWithdrawalState.Waiting:
                    return $"{what} is waiting for its Bitcoin transaction to be fixed ({row.WaitingReason ?? "the vault's coins"}). That happens by itself as soon as the vault's coins allow. "
                        + (row.IsBridgeExit
                            ? $"If the exit cannot be fixed by block {row.RequestVfxHeight + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1} it is failed, its locks are restored and it is made again."
                            : $"If it cannot be fixed by block {row.RequestVfxHeight + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1} it is closed and the vBTC returned. Until then its requester can cancel it.");
                case VaultWithdrawalState.Pinned:
                    return $"{what} is paid by Bitcoin transaction {newest?.Txid}, fixed by the chain. The vault's signers sign and broadcast it on their own, and it completes when Bitcoin has confirmed it {BtcAttestRules.Depth} times. Nothing needs to be done."
                        + (row.IsBridgeExit ? "" : " If it is not confirming, its requester can raise the fee.");
                case VaultWithdrawalState.Settled:
                    return $"{what} was paid by Bitcoin transaction {row.SettledTxid}.";
                default:
                    return $"{what} was closed ({row.State}): {row.WaitingReason}. "
                        + (row.IsBridgeExit ? "Its bridge locks were restored." : "The vBTC was returned to its requester.");
            }
        }

        // ── The completion routes ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What the "complete a withdrawal" calls return for a watched vault, in the shape they have always returned.
        /// Nobody completes such a withdrawal by hand: the answer says where it stands. Success is true once Bitcoin has
        /// paid it; while the chain is still paying it the message starts with one of the two markers
        /// (<see cref="IsInTheChainsHands"/>), which callers treat as "wait", not as a failure.
        /// </summary>
        public static (bool Success, string VfxTxHash, string BtcTxHash, string Message) Completion(string? scUID, string? requestHash)
        {
            var row = Row(scUID, requestHash);
            if (row == null)
                return (false, "", "", $"Withdrawal request {requestHash} is not recorded on vault {scUID}. If it was just sent, it has not been mined yet.");
            var newest = row.Pins.LastOrDefault(p => !p.Dead);
            switch (row.State)
            {
                case VaultWithdrawalState.Settled:
                {
                    var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
                    return (true, request?.CompletionTxHash ?? "", row.SettledTxid ?? "", Summary(row));
                }
                case VaultWithdrawalState.Pinned:
                    return (false, "", newest?.Txid ?? "", $"{VBTCService.AwaitingConfirmationMarker} {Summary(row)}");
                case VaultWithdrawalState.Waiting:
                    return (false, "", "", $"{WaitingForPinMarker} {Summary(row)}");
                default:
                    return (false, "", "", Summary(row));
            }
        }

        /// <summary>
        /// The JSON the completion routes answer with for a watched vault; null for any other vault (the route then
        /// does what it has always done).
        /// </summary>
        public static object? CompletionAnswer(string? scUID, string? requestHash)
        {
            if (!BtcAttestBalance.IsAttestedVault(Stored(scUID))) return null;
            var (success, vfxTxHash, btcTxHash, message) = Completion(scUID, requestHash);
            var row = Row(scUID, requestHash);
            var waiting = IsInTheChainsHands(message);
            return new
            {
                // "Success" as these routes have always meant it for a caller that polls them: the withdrawal is paid
                // or is being paid. False only when there is nothing to wait for.
                Success = success || waiting,
                Attested = true,
                Message = message,
                Status = row == null ? "Unknown" : row.State switch
                {
                    VaultWithdrawalState.Settled => "Completed",
                    VaultWithdrawalState.Pinned => "Awaiting_BTC_Confirmation",
                    VaultWithdrawalState.Waiting => "Awaiting_BTC_Transaction",
                    _ => "Closed",
                },
                NothingToDo = true,
                VFXTransactionHash = string.IsNullOrEmpty(vfxTxHash) ? null : vfxTxHash,
                BTCTransactionHash = string.IsNullOrEmpty(btcTxHash) ? null : btcTxHash,
                SmartContractUID = Stored(scUID),
                WithdrawalRequestHash = requestHash,
                Withdrawal = row == null ? null : Describe(row),
            };
        }

        // ── The network ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The attestation layer as this node's chain state holds it (the heights, the Bitcoin tip, the signer set).</summary>
        public static object Network()
        {
            static long? Set(long height) => height > 0 && height < long.MaxValue / 2 ? height : null;
            var tip = Globals.LastBlock?.Height ?? 0;
            var meta = BtcAttestStore.GetMeta();
            var epoch = BtcAttestRules.EpochOf(tip);
            var signers = meta == null ? null : BtcAttestStore.SignerSet(epoch);
            return new
            {
                Network = BtcAttestRules.NetworkTag,
                VfxHeight = tip,
                ObserveHeight = Set(Globals.BitcoinAttestationObserveHeight),
                RecordHeight = Set(Globals.BitcoinAttestationRecordHeight),
                EnforceHeight = Set(Globals.BitcoinAttestationHeight),
                Recording = BtcAttestRules.Recording(tip + 1),
                Enforced = BtcAttestRules.Enforced(tip + 1),
                Started = meta != null,
                BitcoinCheckpointHeight = meta?.CheckpointHeight,
                BitcoinTipHeight = meta?.TipHeight,
                BitcoinTipHash = meta?.TipHash,
                EventsThroughBitcoinHeight = meta?.EventsThroughHeight,
                ConfirmationsRequired = BtcAttestRules.Depth,
                SignerEpoch = epoch,
                Signers = signers?.Addresses.Count ?? 0,
                SignaturesRequired = signers == null ? 0 : BtcAttestRules.RequiredSignatures(signers.Addresses.Count),
                OpenWithdrawals = meta == null ? 0
                    : BtcAttestStore.WithdrawalsInState(VaultWithdrawalState.Waiting).Count + BtcAttestStore.WithdrawalsInState(VaultWithdrawalState.Pinned).Count,
            };
        }
    }
}
