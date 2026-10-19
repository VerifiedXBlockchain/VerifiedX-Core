using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// The vault's signers pay a pinned withdrawal on their own (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.1): the
    /// requester sends its request and nothing else. Any signer of the vault may start the signing ceremony for a
    /// transaction consensus has pinned; the others join, because the only transaction any of them will sign for that
    /// withdrawal is the pinned one (<see cref="FROST.FrostSigningAuthorization"/>).
    ///
    /// Nobody is appointed. Each signer works out its place in an order that depends on the transaction and on its
    /// own address, and starts only after that many turns have passed without the transaction appearing on Bitcoin.
    /// Two signers starting at once is harmless: they sign the same bytes.
    ///
    /// What this does is not consensus. If it does nothing, the withdrawal stays pinned and is paid when a signer does
    /// act; it can never be paid twice or to anyone else, because of the pin.
    /// </summary>
    public static class BtcWithdrawalSigner
    {
        /// <summary>VFX blocks one signer is given before the next in line also starts (about a minute).</summary>
        public const int TurnBlocks = 5;
        /// <summary>
        /// VFX blocks a pin is left to settle before any signer leads or joins a ceremony for it (about two minutes).
        /// "Nothing is pinned, so nothing is signed" holds only while the block that made the pin stays in the chain. A
        /// signature given in that very block would outlive the block if it were replaced: the Bitcoin would be paid
        /// for a request the surviving chain never mined, or mined on other coins. Bitcoin itself takes ten minutes a
        /// block, so the wait costs a withdrawal nothing. A validator's own rule, not a consensus rule.
        /// </summary>
        public static int SettleBlocks => SettleBlocksOverride ?? 10;
        /// <summary>Tests only.</summary>
        internal static int? SettleBlocksOverride;
        private const int LoopSeconds = 20;

        /// <summary>When this node last led a ceremony for a pinned transaction, and whether it got it out.</summary>
        private static readonly ConcurrentDictionary<string, (DateTime At, bool Broadcast)> Attempts = new(StringComparer.Ordinal);

        /// <summary>Signs the pinned transaction with the vault's signers and broadcasts it. Replaceable for tests.</summary>
        internal static Func<VaultWithdrawalRow, VaultPin, Task<(bool Ok, string Detail)>> SignAndBroadcast = SignAndBroadcastWithFrostAsync;

        /// <summary>Whether Bitcoin already knows a transaction (in a block or waiting for one); null when nobody could say. Replaceable for tests.</summary>
        internal static Func<string, Task<bool?>> KnownOnBitcoin = async txid =>
        {
            // Two hosts must know it: every signer of every vault asks the same servers, so one server's word stopped
            // them all from ever sending it. Signing and sending a pinned transaction Bitcoin already has changes nothing
            // (only the pinned transaction is signed, so it is the same transaction).
            var seen = await BitcoinTransactionService.GetTransactionConfirmationsCorroborated(txid, stopWhenKnown: true);
            return seen.Known >= BitcoinTransactionService.ConfirmationAgreementRequired ? true : null;
        };

        /// <summary>
        /// Holders that, in a ceremony this node led for a pinned transaction, committed in Round 1 and gave no share:
        /// pinned txid to holder to when. Asked again they would stall every attempt, so the next ones leave them out
        /// for <see cref="WithheldFor"/>, never below the key's own threshold and never leaving out this node.
        /// </summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> Withheld = new(StringComparer.Ordinal);
        internal static TimeSpan WithheldFor => WithheldForOverride ?? TimeSpan.FromMinutes(60);
        /// <summary>Tests only.</summary>
        internal static TimeSpan? WithheldForOverride;
        /// <summary>Which of the key's holders answer now. Replaceable for tests.</summary>
        internal static Func<List<VBTCValidator>, Task<List<VBTCValidator>>> Reachable = FrostMPCService.ProbeValidatorReachability;
        /// <summary>Tests only: runs the ceremony instead of ExecuteFROSTWithdrawal, given the holders asked and the threshold.</summary>
        internal static Func<List<VBTCValidator>, int, Task<(bool Success, string TxHash, string SignedTxHex, string ErrorMessage, FrostCeremonyOutcome? Ceremony)>>? CeremonyForTests;

        /// <summary>The holders to ask for a pinned transaction: the reachable ones less those that withheld their share lately.</summary>
        internal static List<VBTCValidator> SignersToAsk(string txid, List<VBTCValidator> reachable, int minSigners, string me)
        {
            if (!Withheld.TryGetValue(txid, out var held)) return reachable;
            var now = DateTime.UtcNow;
            var left = reachable.Where(v => v.ValidatorAddress == me
                || !held.TryGetValue(v.ValidatorAddress, out var at) || now - at >= WithheldFor).ToList();
            return left.Count >= Math.Max(1, minSigners) ? left : reachable;
        }

        private static void NoteWithheld(string txid, IEnumerable<string> holders, List<VBTCValidator> asked)
        {
            var held = Withheld.GetOrAdd(txid, _ => new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal));
            foreach (var holder in holders.Where(h => asked.Any(a => a.ValidatorAddress == h)))
                held[holder] = DateTime.UtcNow;
        }

        internal static void ForgetWithheld() => Withheld.Clear();

        public static async Task RunLoop(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Asked every pass, not once at start: a node may start validating later, and a wallet that is
                    // unlocked after start has no validator address until then. (The loop used to end here for good.)
                    if (!string.IsNullOrEmpty(Globals.ValidatorAddress)
                        && (Globals.LastBlock?.Height ?? 0) + 1 >= Globals.BitcoinAttestationRecordHeight
                        && VBTCCancellationVoteService.NodeIsAtNetworkHeight().Ready)
                        await TickAsync(Globals.ValidatorAddress, Globals.LastBlock!.Height);
                }
                catch (Exception ex) { ErrorLogUtility.LogError($"Withdrawal signer loop: {ex}", "BtcWithdrawalSigner.RunLoop()"); }
                try { await Task.Delay(TimeSpan.FromSeconds(LoopSeconds), ct); } catch { return; }
            }
        }

        /// <summary>One pass over the pinned withdrawals: this signer leads the ones whose turn has come to it. Returns the transactions it led.</summary>
        internal static async Task<List<string>> TickAsync(string me, long tipHeight)
        {
            var led = new List<string>();
            foreach (var row in BtcAttestStore.WithdrawalsInState(VaultWithdrawalState.Pinned).OrderBy(r => r.RequestVfxHeight).ThenBy(r => r.Ref, StringComparer.Ordinal))
            {
              // One withdrawal that cannot be led (a fault in its vault's data, a signer that throws) must not stop
              // the ones after it: the rows are taken oldest first, so it would stop them on every pass.
              try
              {
                var pin = row.Pins.LastOrDefault(p => !p.Dead);
                if (pin == null) continue;
                // After a deep Bitcoin reorganisation a pinned transaction can spend a coin that is not there (yet):
                // it cannot be broadcast until that coin's own transaction has confirmed again.
                // A coin frozen on the signers' word (#171) is still signed for: broadcast, the transaction confirms if the
                // claim was false and fails if it was true (#293).
                if (pin.Inputs.Any(o => BtcAttestStore.Coin(o)?.Status is not (VaultCoinStatus.Pinned or VaultCoinStatus.Frozen))) continue;
                // The holders of the key its coins are under (a vault can have had more than one, plan 8.8).
                var key = BtcVaultKeys.KeyOfCoins(pin.Inputs);
                if (key == null) continue;
                var signers = BtcVaultKeys.Holders(key);
                if (!signers.Contains(me, StringComparer.Ordinal)) continue;
                if (tipHeight < pin.PinnedAtVfxHeight + SettleBlocks + (long)Turn(pin.Txid, me, signers) * TurnBlocks) continue;

                // Led by this node a moment ago: give Bitcoin's relay and the other signers time before trying again.
                if (Attempts.TryGetValue(pin.Txid, out var last) && DateTime.UtcNow - last.At < TimeSpan.FromMinutes(last.Broadcast ? 30 : 3))
                    continue;
                if (await KnownOnBitcoin(pin.Txid) == true)
                {
                    Attempts[pin.Txid] = (DateTime.UtcNow, true);
                    continue;
                }

                var (ok, detail) = await SignAndBroadcast(row, pin);
                Attempts[pin.Txid] = (DateTime.UtcNow, ok);
                led.Add(pin.Txid);
                if (ok)
                    LogUtility.Log($"Withdrawal {row.RequestHash} on {row.SmartContractUID}: pinned Bitcoin transaction {pin.Txid} signed and broadcast by this validator.", "BtcWithdrawalSigner.TickAsync()");
                else
                    ErrorLogUtility.LogError($"Withdrawal {row.RequestHash} on {row.SmartContractUID}: signing the pinned transaction {pin.Txid} did not complete: {detail}", "BtcWithdrawalSigner.TickAsync()");
              }
              catch (Exception ex)
              {
                ErrorLogUtility.LogError($"Withdrawal {row.RequestHash} on {row.SmartContractUID} could not be led by this validator: {ex.Message}", "BtcWithdrawalSigner.TickAsync()");
              }
            }
            foreach (var old in Attempts.Where(a => DateTime.UtcNow - a.Value.At > TimeSpan.FromHours(6)).ToList())
                Attempts.TryRemove(old.Key, out _);
            foreach (var old in Withheld.Where(w => w.Value.Values.All(at => DateTime.UtcNow - at >= WithheldFor)).ToList())
                Withheld.TryRemove(old.Key, out _);
            return led;
        }

        /// <summary>
        /// The validators that hold the shares of the key a UID names (the participants of its key ceremony): a vault's
        /// UID names its first key, a later key is named by its own UID (plan 8.8).
        /// </summary>
        public static List<string> SignersOf(string uid)
        {
            var key = BtcVaultKeys.KeyRow(uid);
            if (key != null) return BtcVaultKeys.Holders(key);
            return VBTCChainView.GetVault(uid)?.Feature.ValidatorAddressesSnapshot?.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).ToList()
                ?? new List<string>();
        }

        /// <summary>
        /// A signer's place in line for one transaction: 0 starts at once, 1 after one turn, and so on. The order is the
        /// signers sorted by a hash of the transaction id and their address, so it differs for every withdrawal and
        /// nobody is first every time.
        /// </summary>
        public static int Turn(string txid, string signer, IReadOnlyCollection<string> signers)
        {
            string Key(string address) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(txid + "|" + address)));
            return signers.OrderBy(Key, StringComparer.Ordinal).ToList().IndexOf(signer);
        }

        internal static void ForgetAttempts() => Attempts.Clear();

        private static async Task<(bool Ok, string Detail)> SignAndBroadcastWithFrostAsync(VaultWithdrawalRow row, VaultPin pin)
        {
            var vault = VBTCChainView.GetVault(row.SmartContractUID);
            // The key the pinned coins are under: its address, its holders, the UID its shares are filed under.
            var script = BtcVaultKeys.KeyOfCoins(pin.Inputs);
            if (vault == null || script == null) return (false, "the vault, or the one key its coins are under, is not in state");

            // What the ceremony is told it is signing for. The transaction itself is the pinned one either way.
            string reference, destination;
            decimal amount;
            long feeRate;
            if (row.IsSweep)
            {
                reference = row.Ref;
                destination = BtcAttestStore.ScriptRow(row.DestinationScriptHex)?.DepositAddress ?? "";
                amount = pin.DestinationSats / BtcAttestBalance.SatsPerBtc;
                feeRate = pin.FeeRate > 0 ? pin.FeeRate : row.FeeRate;
            }
            else if (row.IsBridgeExit)
            {
                var exit = VBTCBridgeBtcExitState.GetByBurnHash(row.ExitBurnHash ?? "");
                if (exit == null) return (false, "the bridge exit is not in state");
                reference = BtcWithdrawals.ExitSigningReference(exit.BaseBurnTxHash, row.SmartContractUID);
                destination = exit.BtcDestination;
                amount = row.AmountSats / BtcAttestBalance.SatsPerBtc;
                feeRate = row.FeeRate;
            }
            else
            {
                var request = VBTCWithdrawalRequest.GetByTransactionHash(row.RequestHash, row.SmartContractUID);
                if (request == null) return (false, "the withdrawal request is not in state");
                reference = row.RequestHash;
                destination = request.BTCDestination;
                amount = request.Amount;
                feeRate = request.FeeRate;
            }

            // The coins exactly as state holds them: the signers check every sighash against these.
            var coins = new List<PinnedWithdrawalCoin>();
            foreach (var outpoint in pin.Inputs)
            {
                var coin = BtcAttestStore.Coin(outpoint);
                if (coin == null) return (false, $"coin {outpoint} is not in state");
                var split = outpoint.IndexOf(':');
                coins.Add(new PinnedWithdrawalCoin
                {
                    TxId = outpoint.Substring(0, split), Vout = uint.Parse(outpoint.Substring(split + 1)),
                    ValueSats = (ulong)coin.ValueSats, ScriptPubKeyHex = script.ScriptHex,
                });
            }

            var snapshot = BtcVaultKeys.Holders(script);
            var registered = VBTCValidatorRegistry.GetActiveValidators().Where(v => snapshot.Contains(v.ValidatorAddress, StringComparer.Ordinal)).ToList();
            // A holder that has exited (the reason a key is replaced) may still run and still hold its share; it is asked
            // at the address this node last knew for it. Sweeps of a replaced key depend on exactly these.
            foreach (var holder in snapshot.Where(a => registered.All(r => r.ValidatorAddress != a)))
                if (Globals.NetworkValidators.TryGetValue(holder, out var known) && !string.IsNullOrEmpty(known.IPAddress))
                    registered.Add(new VBTCValidator { ValidatorAddress = holder, IPAddress = known.IPAddress, IsActive = true });
            var reachable = await Reachable(registered);
            if (reachable.Count == 0) return (false, $"none of the key's {snapshot.Count} signers is reachable");
            // Holders that withheld their share in this node's last ceremonies for this transaction are left out while
            // the rest still meet the key's threshold: asked again, one such holder failed every attempt.
            var signers = SignersToAsk(pin.Txid, reachable, BtcVaultKeys.MinSigners(script), Globals.ValidatorAddress);
            var madeAt = script.Generation > 0 ? script.CreatedAtVfxHeight : vault.Feature.ProofBlockHeight; // the height a vault names is its creator's word: never after the tip
            var threshold = VBTCThresholdCalculator.CalculateAdjustedThreshold(snapshot.Count, signers.Count,
                Math.Max(0, Math.Min(madeAt, Globals.LastBlock?.Height ?? 0)), Globals.LastBlock?.Height ?? 0);

            var result = CeremonyForTests != null
                ? await CeremonyForTests(signers, threshold)
                : await BitcoinTransactionService.ExecuteFROSTWithdrawal(
                    script.DepositAddress, destination, amount, feeRate, BtcVaultKeys.KeyUidOf(script), signers, threshold,
                    broadcast: true, coordinatorAddress: Globals.ValidatorAddress, withdrawalRequestHash: reference,
                    pinnedUnsignedTxHex: pin.TxHex, pinnedCoins: coins);
            if (result.Ceremony?.FailureCode == FrostCeremonyFailureCode.Round2SharesWithheld)
                NoteWithheld(pin.Txid, result.Ceremony.Withholders, signers);
            if (result.Success) Withheld.TryRemove(pin.Txid, out _);
            if (!result.Success) return (false, result.ErrorMessage);
            return string.Equals(result.TxHash, pin.Txid, StringComparison.OrdinalIgnoreCase)
                ? (true, result.TxHash)
                : (false, $"the signed transaction is {result.TxHash}, not the pinned {pin.Txid}");
        }
    }
}
