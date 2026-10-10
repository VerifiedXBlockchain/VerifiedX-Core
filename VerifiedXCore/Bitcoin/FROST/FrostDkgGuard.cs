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

        /// <summary>Whether the contract carries a NEW-26 attested DKG proof (its ceremony ran under its UID). Replaceable for tests.</summary>
        internal static Func<string, bool> RequestHasDkgProof = ContractHasAttestedDkgProof;

        /// <summary>
        /// Fund-loss audit item 4 (validator-local): whether a key-store record filed under <paramref name="recordScUid"/>,
        /// carrying <paramref name="recordGroupKey"/>, may be used - and relabelled - for a signing request on
        /// <paramref name="requestScUid"/>. The group-key fallbacks matched on the key alone, so a contract body copying a
        /// victim vault's group key had the victim's share signed to its own withdrawals and relabelled to itself.
        /// <list type="bullet">
        /// <item>A record filed under the requested contract is that contract's.</item>
        /// <item>A record filed under any other contract - on chain, or a NEW-26 ceremony's contract UID - is that
        /// contract's share and is never used for another.</item>
        /// <item>A vault created with an attested proof (DKG_ATTESTED_V2) had its ceremony under its own UID and never
        /// takes a record filed under anything else. (The proof TYPE is the test: every legacy vault carries a
        /// DKG_COMPLETION_FROST_NATIVE proof.)</item>
        /// <item>A record still under a pre-NEW-26 session id belongs to the ONE vault on chain that carries its group
        /// key (fourth review: it used to go to whichever body asked first, a copied key included). With two carriers
        /// it is used for neither. See <see cref="FrostKeyShareBinding"/>, which also binds such records up front.</item>
        /// </list>
        /// </summary>
        public static bool MayAdoptKeyRecord(string? recordScUid, string requestScUid, string? recordGroupKey, out string reason)
        {
            reason = "";
            if (!string.IsNullOrEmpty(recordScUid) && string.Equals(recordScUid, requestScUid, StringComparison.Ordinal))
                return true;
            bool hasProof;
            try { hasProof = RequestHasDkgProof(requestScUid); } catch { hasProof = false; }
            if (hasProof)
            {
                reason = $"contract {requestScUid} was created with an attested DKG proof, so its key share is filed under its own UID; the record under {recordScUid} is not used for it.";
                return false;
            }
            // Since NEW-26 a DKG runs under the contract UID it will create (FrostDkgAttestation.NewContractUid), so a record
            // filed under a contract-shaped UID is that contract's share whether or not the contract exists on chain yet.
            if (IsContractUid(recordScUid))
            {
                reason = $"the key share found by group key is filed under contract {recordScUid} (a NEW-26 ceremony names its contract); it is that vault's share and is not used for {requestScUid}.";
                return false;
            }
            SmartContractStateTrei? other = null;
            try { other = string.IsNullOrEmpty(recordScUid) ? null : SmartContractStateTrei.GetSmartContractState(recordScUid); } catch { }
            if (other != null)
            {
                reason = $"the key share found by group key is filed under contract {other.SmartContractUID}, which exists on chain; it is that vault's share and is not used for {requestScUid}.";
                return false;
            }
            // A pre-NEW-26 session id: the share belongs to the one vault that carries its key.
            if (FrostKeyShareBinding.IsSoleVault(recordGroupKey, requestScUid, out var why))
                return true;
            reason = $"the key share filed under session id {recordScUid} is not used for {requestScUid}: {why}.";
            return false;
        }

        /// <summary>
        /// True when the contract's TokenizationV2 feature carries a NEW-26 attested DKG proof (ProofType DKG_ATTESTED_V2) in
        /// chain state. A legacy vault's DKG_COMPLETION_FROST_NATIVE proof, an unparsable proof or no proof is false.
        /// </summary>
        public static bool ContractHasAttestedDkgProof(string smartContractUID)
        {
            try
            {
                var st = SmartContractStateTrei.GetSmartContractState(smartContractUID);
                if (st == null || string.IsNullOrEmpty(st.ContractData)) return false;
                var sc = SmartContractMain.GenerateSmartContractInMemory(st.ContractData);
                var feature = sc?.Features?
                    .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                    .Select(x => x.FeatureFeatures)
                    .FirstOrDefault();
                var t = feature as TokenizationV2Feature
                    ?? (feature == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<TokenizationV2Feature>(feature.ToString() ?? ""));
                return IsAttestedDkgProof(t?.DKGProof);
            }
            catch { return false; }
        }

        /// <summary>Whether a DKGProof string is a NEW-26 attested proof (type DKG_ATTESTED_V2), as opposed to a legacy completion proof.</summary>
        public static bool IsAttestedDkgProof(string? dkgProof)
        {
            var proof = FrostDkgAttestation.ParseProof(dkgProof);
            return proof != null && string.Equals(proof.ProofType, FrostDkgAttestation.ProofType, StringComparison.Ordinal);
        }

        /// <summary>A smart contract UID as every creation path mints it: 32 hex characters, a colon, a unix timestamp.</summary>
        public static bool IsContractUid(string? uid)
        {
            if (string.IsNullOrEmpty(uid) || uid.Length < 34) return false;
            var colon = uid.IndexOf(':');
            if (colon != 32 || colon == uid.Length - 1) return false;
            for (var i = 0; i < 32; i++)
                if (!Uri.IsHexDigit(uid[i])) return false;
            for (var i = 33; i < uid.Length; i++)
                if (uid[i] < '0' || uid[i] > '9') return false;
            return true;
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
