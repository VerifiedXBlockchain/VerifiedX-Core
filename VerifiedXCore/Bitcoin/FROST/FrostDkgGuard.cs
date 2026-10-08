using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// Guards the DKG start path against re-keying an existing vault. A DKG for a contract ID that
    /// already has a key (locally, or on-chain via the contract's FROST group public key) would
    /// finalize into a NEW group key and, before this guard, overwrite the real vault key in the
    /// key store and then in every peer backup — permanently locking the BTC collateral.
    /// </summary>
    public static class FrostDkgGuard
    {
        public static (bool Ok, string Reason) CanStartDkg(string? smartContractUID, string? myValidatorAddress)
        {
            if (string.IsNullOrWhiteSpace(smartContractUID)) return (false, "SmartContractUID required");

            // 0. Chain state must be trustworthy before we can assert "no key exists on chain".
            if (!Globals.IsChainSynced) return (false, "Chain not synced; cannot verify the contract has no vault key yet");

            // 1. This validator already holds a key package for the contract ID.
            if (!string.IsNullOrEmpty(myValidatorAddress))
            {
                var existing = FrostValidatorKeyStore.GetKeyPackage(smartContractUID, myValidatorAddress);
                if (existing != null && !string.IsNullOrEmpty(existing.KeyPackage))
                    return (false, $"Key package already exists for contract {smartContractUID}; refusing to re-key an existing vault");
            }

            // 2. The contract is already deployed with a FROST group key (local record or chain state).
            var onChainGroupKey = ResolveOnChainGroupPublicKey(smartContractUID);
            if (!string.IsNullOrEmpty(onChainGroupKey))
                return (false, $"Contract {smartContractUID} already has an on-chain FROST group key; refusing to re-key an existing vault");

            return (true, "");
        }

        /// <summary>
        /// A key package may be used (or relabelled) for a contract only if its group public key IS
        /// the contract's on-chain vault key. A record carrying a different group key is some other
        /// DKG's output (e.g. an attacker-led junk ceremony) and must never be attached to the
        /// contract. Fail closed: if either side is unknown the key cannot be proven to be the vault
        /// key, so it is refused (every DKG finalize records the group key; every deployed vBTC V2
        /// contract carries its group key on chain).
        /// </summary>
        public static bool KeyPackageMatchesContract(string? keyPackageGroupKey, string? contractGroupKey)
        {
            if (string.IsNullOrWhiteSpace(contractGroupKey) || string.IsNullOrWhiteSpace(keyPackageGroupKey)) return false;
            return string.Equals(keyPackageGroupKey.Trim(), contractGroupKey.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Fund-loss audit item 4 (validator-local): whether a key-store record filed under <paramref name="recordScUid"/>
        /// may be used - and relabelled - for a signing request on <paramref name="requestScUid"/>. A record is adopted only
        /// while it is still filed under its ceremony id (or under the requested contract itself). One filed under a
        /// DIFFERENT contract that exists on chain is that contract's share: the group-key fallbacks matched on the key
        /// alone, so a contract body copying a victim vault's group key had the victim's share signed to its own
        /// withdrawals and the record relabelled to the forged contract.
        /// </summary>
        public static bool MayAdoptKeyRecord(string? recordScUid, string requestScUid, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(recordScUid) || string.Equals(recordScUid, requestScUid, StringComparison.Ordinal))
                return true;
            SmartContractStateTrei? other = null;
            try { other = SmartContractStateTrei.GetSmartContractState(recordScUid); } catch { }
            if (other == null)
                return true; // a ceremony id, never a contract
            reason = $"the key share found by group key is filed under contract {other.SmartContractUID}, which exists on chain; it is that vault's share and is not used for {requestScUid}.";
            return false;
        }

        /// <summary>
        /// Group public key for a contract from the local contract record or the state trei
        /// (TokenizationV2 feature). Empty when the contract does not exist yet.
        /// </summary>
        public static string ResolveOnChainGroupPublicKey(string smartContractUID)
        {
            try
            {
                var local = VBTCContractV2.GetContract(smartContractUID);
                if (local != null && !string.IsNullOrWhiteSpace(local.FrostGroupPublicKey))
                    return local.FrostGroupPublicKey;
            }
            catch { }

            try
            {
                var st = SmartContractStateTrei.GetSmartContractState(smartContractUID);
                if (st == null || string.IsNullOrEmpty(st.ContractData)) return string.Empty;
                var sc = SmartContractMain.GenerateSmartContractInMemory(st.ContractData);
                var feature = sc?.Features?
                    .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                    .Select(x => x.FeatureFeatures)
                    .FirstOrDefault();
                return feature is TokenizationV2Feature t ? (t.FrostGroupPublicKey ?? string.Empty) : string.Empty;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"ResolveOnChainGroupPublicKey({smartContractUID}) failed: {ex.Message}", "FrostDkgGuard");
                return string.Empty;
            }
        }
    }
}
