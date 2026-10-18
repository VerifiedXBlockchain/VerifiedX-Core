using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// What ends at the enforcement height, <see cref="Globals.BitcoinAttestationHeight"/>
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 8.2 and 8.4).
    ///
    /// A vault created before the recording height is not watched: the chain has no record of its Bitcoin, so an
    /// owner debit on it can only be judged the old way, by the block producer's word. From the enforcement height
    /// such a vault is frozen: nothing debits a balance on it, for the owner or for a holder. People move to a vault
    /// created after the recording height before then; what is left on an old vault stays there. Two settlements still
    /// move vBTC between holders inside such a vault, leaving its total unchanged: a reserve's delayed send that falls due
    /// after the height, and a reserve's Recover() sweep to its recovery address; the new holder can take nothing off it.
    ///
    /// The bridge follows: no exit to Bitcoin and no unlock back to VFX may draw on a lock that belongs to such a
    /// vault (an unlock would hand somebody vBTC on a frozen vault).
    ///
    /// The transactions that only the old way of working needs are refused for every vault from the same height:
    /// the owner-sent withdrawal completion, the cancellation vote, and the caster-sent bridge exit completion.
    /// (Watched vaults never accept the first two; see <see cref="BtcWithdrawals"/>.)
    /// </summary>
    public static class BtcLegacyFreeze
    {
        public static bool Active(long height) => BtcAttestRules.Enforced(height);

        /// <summary>A vBTC V2 vault the chain does not watch.</summary>
        public static bool IsLegacyVault(string? scUID)
        {
            if (string.IsNullOrEmpty(scUID)) return false;
            var state = SmartContractStateTrei.GetSmartContractState(scUID);
            return state != null && VBTCService.IsVbtcV2Contract(state) && !BtcAttestBalance.IsAttestedVault(state.SmartContractUID);
        }

        /// <summary>
        /// Why a transaction is refused at <paramref name="height"/> (the block's height in block validation, the tip
        /// + 1 at admission); null when it is not refused here.
        /// </summary>
        public static string? Refusal(Transaction tx, long height)
        {
            if (tx == null || !Active(height)) return null;

            switch (tx.TransactionType)
            {
                case TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE:
                    return $"Withdrawal completion transactions are no longer accepted (from block {Globals.BitcoinAttestationHeight}): a withdrawal is completed when Bitcoin shows its transaction.";
                case TransactionType.VBTC_V2_WITHDRAWAL_VOTE:
                    return $"Cancellation votes are no longer accepted (from block {Globals.BitcoinAttestationHeight}).";
                case TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE:
                    return $"Bridge exit completion transactions are no longer accepted (from block {Globals.BitcoinAttestationHeight}): an exit is completed when Bitcoin shows its transaction.";
                // Nor failures: an older vault's exit still open at the freeze may have been paid by its signers, and a
                // failure freed its burn to be exited again from watched vaults (fifth review, round 20).
                case TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL:
                    return $"Bridge exit failure transactions are no longer accepted (from block {Globals.BitcoinAttestationHeight}).";
            }

            foreach (var (key, _) in SameBlockDebitGuard.GetDebits(tx, height))
                if (key.Kind == SameBlockDebitGuard.LedgerKind.VbtcV2 && IsLegacyVault(key.ContractUid))
                    return Frozen(key.ContractUid);

            // Neither an exit to Bitcoin nor an unlock back to VFX may draw on the locks of a frozen vault. The vault
            // is the one the lock's own record names, whatever the transaction says.
            if (tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC || tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK
                || tx.TransactionType == TransactionType.VBTC_V2_BRIDGE_UNLOCK)
            {
                try
                {
                    var data = JObject.Parse(tx.Data);
                    var allocations = data["Allocations"] is JArray listed
                        ? listed.ToObject<List<VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation>>() ?? new()
                        : new List<VerifiedXCore.Bitcoin.Models.PoolUnlockAllocation>();
                    var vaults = allocations
                        .Select(a => VerifiedXCore.Bitcoin.Models.VBTCBridgeLockState.GetByLockId(a.LockId)?.SmartContractUID ?? a.SmartContractUID)
                        .Append(VerifiedXCore.Bitcoin.Models.VBTCBridgeLockState.GetByLockId(data["LockId"]?.ToObject<string?>() ?? "")?.SmartContractUID)
                        .Append(data["ContractUID"]?.ToObject<string?>());
                    foreach (var vault in vaults)
                        if (IsLegacyVault(vault)) return Frozen(vault!);
                }
                catch { }
            }

            if (tx.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_CANCEL)
            {
                string? scUID = null;
                try { scUID = JObject.Parse(tx.Data)["ContractUID"]?.ToObject<string?>(); } catch { }
                if (IsLegacyVault(scUID)) return Frozen(scUID!);
            }
            return null;
        }

        private static string Frozen(string scUID) =>
            $"Vault {scUID} was created before Bitcoin attestation and has been frozen since block {Globals.BitcoinAttestationHeight}. Its vBTC can no longer be transferred, withdrawn or bridged.";
    }
}
