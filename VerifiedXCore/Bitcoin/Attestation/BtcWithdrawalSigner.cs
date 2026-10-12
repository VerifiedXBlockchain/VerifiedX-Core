using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
        private const int LoopSeconds = 20;

        /// <summary>When this node last led a ceremony for a pinned transaction, and whether it got it out.</summary>
        private static readonly ConcurrentDictionary<string, (DateTime At, bool Broadcast)> Attempts = new(StringComparer.Ordinal);

        /// <summary>Signs the pinned transaction with the vault's signers and broadcasts it. Replaceable for tests.</summary>
        internal static Func<VaultWithdrawalRow, VaultPin, Task<(bool Ok, string Detail)>> SignAndBroadcast = SignAndBroadcastWithFrostAsync;

        /// <summary>Whether Bitcoin already knows a transaction (in a block or waiting for one); null when nobody could say. Replaceable for tests.</summary>
        internal static Func<string, Task<bool?>> KnownOnBitcoin = async txid =>
        {
            var lookup = await BitcoinTransactionService.GetTransactionConfirmationsResilient(txid);
            return lookup.Confirmations.HasValue ? true : null;
        };

        public static async Task RunLoop(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                return;
            try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if ((Globals.LastBlock?.Height ?? 0) + 1 >= Globals.BitcoinAttestationRecordHeight
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
                var pin = row.Pins.LastOrDefault(p => !p.Dead);
                if (pin == null) continue;
                // After a deep Bitcoin reorganisation a pinned transaction can spend a coin that is not there (yet):
                // it cannot be broadcast until that coin's own transaction has confirmed again.
                if (pin.Inputs.Any(o => BtcAttestStore.Coin(o)?.Status != VaultCoinStatus.Pinned)) continue;
                var signers = SignersOf(row.SmartContractUID);
                if (!signers.Contains(me, StringComparer.Ordinal)) continue;
                if (tipHeight < pin.PinnedAtVfxHeight + (long)Turn(pin.Txid, me, signers) * TurnBlocks) continue;

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
            foreach (var old in Attempts.Where(a => DateTime.UtcNow - a.Value.At > TimeSpan.FromHours(6)).ToList())
                Attempts.TryRemove(old.Key, out _);
            return led;
        }

        /// <summary>The validators that hold this vault's key shares (the participants of its key ceremony).</summary>
        public static List<string> SignersOf(string scUID) =>
            VBTCChainView.GetVault(scUID)?.Feature.ValidatorAddressesSnapshot?.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).ToList()
            ?? new List<string>();

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
            var script = BtcAttestStore.ScriptRowForVault(row.SmartContractUID);
            if (vault == null || script == null) return (false, "the vault is not in state");

            // What the ceremony is told it is signing for. The transaction itself is the pinned one either way.
            string reference, destination;
            decimal amount;
            long feeRate;
            if (row.IsBridgeExit)
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

            var snapshot = SignersOf(row.SmartContractUID);
            var registered = VBTCValidatorRegistry.GetActiveValidators().Where(v => snapshot.Contains(v.ValidatorAddress, StringComparer.Ordinal)).ToList();
            var reachable = await FrostMPCService.ProbeValidatorReachability(registered);
            if (reachable.Count == 0) return (false, $"none of the vault's {snapshot.Count} signers is reachable");
            var threshold = VBTCThresholdCalculator.CalculateAdjustedThreshold(snapshot.Count, reachable.Count, vault.Feature.ProofBlockHeight, Globals.LastBlock?.Height ?? 0);

            var result = await BitcoinTransactionService.ExecuteFROSTWithdrawal(
                script.DepositAddress, destination, amount, feeRate, row.SmartContractUID, reachable, threshold,
                broadcast: true, coordinatorAddress: Globals.ValidatorAddress, withdrawalRequestHash: reference,
                pinnedUnsignedTxHex: pin.TxHex, pinnedCoins: coins);
            if (!result.Success) return (false, result.ErrorMessage);
            return string.Equals(result.TxHash, pin.Txid, StringComparison.OrdinalIgnoreCase)
                ? (true, result.TxHash)
                : (false, $"the signed transaction is {result.TxHash}, not the pinned {pin.Txid}");
        }
    }
}
