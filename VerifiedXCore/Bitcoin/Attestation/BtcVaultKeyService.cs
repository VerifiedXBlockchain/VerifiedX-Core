using System.Collections.Concurrent;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Validators replace a vault's key on their own when the chain says it is due (docs/PLAN-vBTC-Bitcoin-Attestation.md,
    /// 8.8): one of them leads a key ceremony among the validators of the day, exactly as a vault's creation does, and
    /// submits the result. Nobody is appointed: each eligible validator works out its place in an order that depends on
    /// the key and on its own address, and leads only after that many turns have passed without a re-key appearing.
    /// Two leading at once is harmless: the first re-key mined is the vault's new key, and the other is refused.
    ///
    /// What this does is not consensus. A leader chooses nothing the chain does not check (the participants, their
    /// attestations, the new key's address), and if no validator leads, the key stays due and anyone may lead it, the
    /// vault's owner included.
    /// </summary>
    public static class BtcVaultKeyService
    {
        /// <summary>VFX blocks one validator is given before the next in line also leads (about six minutes).</summary>
        public const int TurnBlocks = 30;
        private const int LoopSeconds = 60;

        /// <summary>When this node last led a re-key for a key (its script), so a failure is not retried at once.</summary>
        private static readonly ConcurrentDictionary<string, DateTime> Attempts = new(StringComparer.Ordinal);

        /// <summary>Runs one key ceremony for a vault and submits the re-key. Replaceable for tests.</summary>
        internal static Func<string, VaultDepositScriptRow, string, Task<(bool Ok, string Detail)>> LeadRekey = LeadRekeyWithFrostAsync;

        /// <summary>The validators counted for a ceremony judged in the next block, as a vault's creation counts them. Replaceable for tests.</summary>
        internal static Func<long, List<string>> Eligible = tip =>
            VerifiedXCore.Services.ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(tip), tip + 1)
                .Where(v => v.IsActive && !v.IsS3C && !string.IsNullOrEmpty(v.ValidatorAddress))
                .Select(v => v.ValidatorAddress).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

        public static async Task RunLoop(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(4), ct); } catch { return; }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!string.IsNullOrEmpty(Globals.ValidatorAddress)
                        && (Globals.LastBlock?.Height ?? 0) + 1 >= Globals.BitcoinAttestationRecordHeight
                        && VBTCCancellationVoteService.NodeIsAtNetworkHeight().Ready)
                        await TickAsync(Globals.ValidatorAddress, Globals.LastBlock!.Height);
                }
                catch (Exception ex) { ErrorLogUtility.LogError($"Vault key loop: {ex}", "BtcVaultKeyService.RunLoop()"); }
                try { await Task.Delay(TimeSpan.FromSeconds(LoopSeconds), ct); } catch { return; }
            }
        }

        /// <summary>
        /// One pass: the keys that are due, the vaults holding the most Bitcoin first; this validator leads at most one
        /// ceremony, for the first whose turn has come to it. Returns the vaults it led a re-key for.
        /// </summary>
        internal static async Task<List<string>> TickAsync(string me, long tipHeight)
        {
            var led = new List<string>();
            var eligible = Eligible(tipHeight);
            if (!eligible.Contains(me, StringComparer.Ordinal)) return led;

            var due = BtcAttestStore.Db.GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts)
                .Find(x => x.RetiredAtVfxHeight == 0 && x.RekeyDueSinceVfxHeight > 0).ToList()
                .Where(k => VBTCChainView.GetVault(k.SmartContractUID) is { } v && !v.Feature.IsS3C)
                .OrderByDescending(k => BtcAttestStore.Total(k.SmartContractUID).HeldSats).ThenBy(k => k.ScriptHex, StringComparer.Ordinal)
                .ToList();
            foreach (var key in due)
            {
                try
                {
                    if (tipHeight < key.RekeyDueSinceVfxHeight + (long)Turn(key, me, eligible) * TurnBlocks) continue;
                    if (BtcAttestTxValidator.RekeyInMempool(key.SmartContractUID)) continue;
                    if (Attempts.TryGetValue(key.ScriptHex, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30)) continue;
                    Attempts[key.ScriptHex] = DateTime.UtcNow;

                    var (ok, detail) = await LeadRekey(key.SmartContractUID, key, me);
                    led.Add(key.SmartContractUID);
                    if (ok)
                        LogUtility.Log($"Vault {key.SmartContractUID}: this validator led a key ceremony for its new key and submitted re-key {detail}.", "BtcVaultKeyService.TickAsync()");
                    else
                        ErrorLogUtility.LogError($"Vault {key.SmartContractUID}: the key ceremony for its new key did not complete: {detail}", "BtcVaultKeyService.TickAsync()");
                    break; // one ceremony at a time
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"Vault {key.SmartContractUID}: leading its re-key failed: {ex.Message}", "BtcVaultKeyService.TickAsync()");
                }
            }
            foreach (var old in Attempts.Where(a => DateTime.UtcNow - a.Value > TimeSpan.FromHours(6)).ToList())
                Attempts.TryRemove(old.Key, out _);
            return led;
        }

        /// <summary>A validator's place in line for one key: the eligible validators sorted by a hash of the key, the block it fell due and their address.</summary>
        public static int Turn(VaultDepositScriptRow key, string validator, IReadOnlyCollection<string> eligible) =>
            BtcWithdrawalSigner.Turn($"{key.ScriptHex}|{key.RekeyDueSinceVfxHeight}", validator, eligible);

        internal static void ForgetAttempts() => Attempts.Clear();

        private static async Task<(bool Ok, string Detail)> LeadRekeyWithFrostAsync(string vaultUid, VaultDepositScriptRow key, string me)
        {
            var tip = Globals.LastBlock?.Height ?? 0;
            var vault = VBTCChainView.GetVault(vaultUid);
            if (vault == null) return (false, "the vault is not in state");
            var all = VerifiedXCore.Services.ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetPublicValidators(), tip + 1);
            var reachable = await FrostMPCService.ProbeValidatorReachability(all);
            var shortfall = FrostDkgAttestation.PreCeremonyShortfall(reachable.Count, false, all.Count);
            if (shortfall != null) return (false, shortfall);

            // The new key keeps the vault's own signing threshold, as a share of its holders.
            var threshold = vault.Feature.RequiredThreshold > 0 ? vault.Feature.RequiredThreshold : 67;
            var run = await FrostMPCService.CoordinateDKGCeremony(FrostDkgAttestation.NewContractUid(), me, reachable, threshold,
                participantShortfall: n => FrostDkgAttestation.PreCeremonyShortfall(n, false, all.Count));
            var result = run.Result;
            if (result == null || string.IsNullOrEmpty(run.ContractUID)) return (false, run.Error ?? "the key ceremony did not complete");
            var resultError = FrostDkgAttestation.CeremonyResultError(run.ContractUID!, result.GroupPublicKey, result.TaprootAddress, result.DKGProof, result.ParticipantAddresses, false, me);
            if (resultError != null) return (false, resultError);

            var rekey = new BtcRekey { ContractUID = vaultUid, DKGProof = result.DKGProof ?? "" };
            var tx = BtcAttestTxValidator.Create(TransactionType.VBTC_V2_VAULT_REKEY, rekey.ToCanonicalJson());
            var (ok, why) = await BtcReportService.SubmitAsync(tx);
            return ok ? (true, tx.Hash) : (false, why);
        }
    }
}
