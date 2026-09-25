using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// NEW-26: validator attestations binding a vBTC V2 contract's deposit address to a real FROST DKG.
    ///
    /// Consensus accepted any DepositAddress, FrostGroupPublicKey and DKGProof a contract creator wrote into the contract
    /// body (the proof was unsigned JSON). A creator could therefore name a Bitcoin address it controls alone: deposits
    /// to it mint vBTC that others hold, while the creator can move the BTC without any validator. From the activation
    /// height (Globals.VbtcV2DkgAttestationHeight) a TokenizationV2 contract must carry:
    ///  - a DepositAddress that is the Taproot address of its FrostGroupPublicKey (raw x-only key, as the FROST signer
    ///    uses), and
    ///  - a DKGProof listing signatures by active vBTC validators over (contract UID, group key, address). A validator
    ///    signs only for a DKG it took part in and whose key package it stored, so the key is one the validators hold.
    /// Public contracts need a majority of the active public validator set at the previous block; S3C contracts (a
    /// creator-chosen private pool, disclosed as such) need a majority of their listed S3C validators.
    /// </summary>
    public static class FrostDkgAttestation
    {
        public const string ProofType = "DKG_ATTESTED_V1";
        public const int MaxAttestations = 512;

        public class Attestation
        {
            public string ValidatorAddress { get; set; } = "";
            public string Signature { get; set; } = "";
        }

        public class Proof
        {
            public string ProofType { get; set; } = "";
            public string ContractUID { get; set; } = "";
            public string GroupPublicKey { get; set; } = "";
            public string TaprootAddress { get; set; } = "";
            public List<Attestation> Attestations { get; set; } = new();
        }

        /// <summary>The message a validator signs. The contract UID binds the key to one contract.</summary>
        public static string Message(string contractUid, string groupPublicKey, string taprootAddress) =>
            $"VFX_DKG_ATTEST_V1|{contractUid}|{groupPublicKey}|{taprootAddress}";

        /// <summary>A new vBTC V2 contract UID. The DKG runs under this id so the attestations name the contract.</summary>
        public static string NewContractUid() => Guid.NewGuid().ToString("N") + ":" + VerifiedXCore.Utilities.TimeUtil.GetTime().ToString();

        /// <summary>The Bitcoin network consensus uses: fixed by the chain, not by configuration.</summary>
        public static Network ConsensusNetwork => Globals.IsTestNet ? Network.TestNet4 : Network.Main;

        /// <summary>
        /// Taproot address of a FROST group key (32-byte x-only or 33-byte compressed hex). The FROST signer signs with the
        /// raw group key (no BIP341 tweak), so the address encodes the raw key. Null when the key is malformed.
        /// </summary>
        public static string? DeriveTaprootAddress(string? groupPublicKeyHex, Network network)
        {
            try
            {
                if (string.IsNullOrEmpty(groupPublicKeyHex)) return null;
                var bytes = Convert.FromHexString(groupPublicKeyHex);
                byte[] xOnly;
                if (bytes.Length == 32) xOnly = bytes;
                else if (bytes.Length == 33 && (bytes[0] == 0x02 || bytes[0] == 0x03)) xOnly = bytes[1..];
                else return null;
                return new TaprootPubKey(xOnly).GetAddress(network).ToString();
            }
            catch { return null; }
        }

        public static string BuildProof(string contractUid, string groupPublicKey, string taprootAddress, IEnumerable<Attestation> attestations) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Proof
            {
                ProofType = ProofType,
                ContractUID = contractUid,
                GroupPublicKey = groupPublicKey,
                TaprootAddress = taprootAddress,
                Attestations = attestations.ToList(),
            })));

        public static Proof? ParseProof(string? base64)
        {
            try
            {
                if (string.IsNullOrEmpty(base64)) return null;
                return JsonConvert.DeserializeObject<Proof>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            }
            catch { return null; }
        }

        public static bool Verify(Attestation? a, string contractUid, string groupPublicKey, string taprootAddress) =>
            a != null && !string.IsNullOrEmpty(a.ValidatorAddress) && !string.IsNullOrEmpty(a.Signature)
            && SignatureService.VerifySignature(a.ValidatorAddress, Message(contractUid, groupPublicKey, taprootAddress), a.Signature);

        /// <summary>Attestations needed from a set of n validators: a majority (51%), and never fewer than two.</summary>
        public static int Required(int n) => Math.Max(2, (int)Math.Ceiling(n * 0.51));

        /// <summary>
        /// This validator's attestation for a DKG it completed, or null. It signs only when its key store holds the
        /// key package for this contract UID with this group key, and the address is the group key's Taproot address.
        /// </summary>
        public static Attestation? SignLocal(string? contractUid, string? groupPublicKey, string? taprootAddress)
        {
            try
            {
                var me = Globals.ValidatorAddress;
                if (string.IsNullOrEmpty(me) || string.IsNullOrEmpty(contractUid) || string.IsNullOrEmpty(groupPublicKey) || string.IsNullOrEmpty(taprootAddress))
                    return null;
                if (DeriveTaprootAddress(groupPublicKey, ConsensusNetwork) != taprootAddress) return null;
                var stored = FrostValidatorKeyStore.GetKeyPackage(contractUid, me);
                if (stored == null || string.IsNullOrEmpty(stored.KeyPackage)
                    || !string.Equals(stored.GroupPublicKey, groupPublicKey, StringComparison.OrdinalIgnoreCase))
                    return null;
                var signature = SignatureService.ValidatorSignature(Message(contractUid, groupPublicKey, taprootAddress));
                return string.IsNullOrEmpty(signature) || signature == "ERROR" || signature == "F" ? null
                    : new Attestation { ValidatorAddress = me, Signature = signature };
            }
            catch { return null; }
        }

        /// <summary>
        /// Consensus check for a TokenizationV2 contract created at some height; null when it passes.
        /// <paramref name="activeAtPreviousBlock"/> returns the vBTC validator set derived from committed blocks up to
        /// height - 1 (deterministic on every node); it is read only after the cheap checks pass.
        /// </summary>
        public static string? Validate(TokenizationV2Feature? feature, string contractUid, Func<List<VBTCValidator>> activeAtPreviousBlock)
        {
            if (feature == null) return "vBTC V2 contract has no TokenizationV2 data.";
            var groupKey = feature.FrostGroupPublicKey ?? "";
            var address = feature.DepositAddress ?? "";

            var derived = DeriveTaprootAddress(groupKey, ConsensusNetwork);
            if (derived == null)
                return "vBTC V2 contract FROST group public key is malformed.";
            if (!string.Equals(derived, address, StringComparison.Ordinal))
                return "vBTC V2 contract deposit address is not the Taproot address of its FROST group public key.";

            var proof = ParseProof(feature.DKGProof);
            if (proof == null || proof.ProofType != ProofType)
                return "vBTC V2 contract DKG proof is not a validator attestation proof.";
            if (!string.Equals(proof.ContractUID, contractUid, StringComparison.Ordinal)
                || !string.Equals(proof.GroupPublicKey, groupKey, StringComparison.Ordinal)
                || !string.Equals(proof.TaprootAddress, address, StringComparison.Ordinal))
                return "vBTC V2 contract DKG proof does not match the contract.";
            if (proof.Attestations == null || proof.Attestations.Count == 0 || proof.Attestations.Count > MaxAttestations)
                return "vBTC V2 contract DKG proof has no usable attestation list.";

            // Eligible signers: active validators of the contract's class, listed in the contract's validator snapshot.
            var snapshot = new HashSet<string>((feature.ValidatorAddressesSnapshot ?? new List<string>()).Where(a => !string.IsNullOrEmpty(a)), StringComparer.Ordinal);
            var classSet = new HashSet<string>(activeAtPreviousBlock().Where(v => v != null && v.IsActive && v.IsS3C == feature.IsS3C && !string.IsNullOrEmpty(v.ValidatorAddress))
                .Select(v => v.ValidatorAddress), StringComparer.Ordinal);

            int basis;
            if (feature.IsS3C)
            {
                // A private pool the creator chose: every listed validator must be an active S3C validator.
                if (snapshot.Count < 3 || !snapshot.All(classSet.Contains))
                    return "vBTC V2 S3C contract validator list must name at least three active S3C validators.";
                basis = snapshot.Count;
            }
            else
            {
                basis = classSet.Count;
            }

            var signers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in proof.Attestations)
            {
                if (a?.ValidatorAddress == null || signers.Contains(a.ValidatorAddress)) continue;
                if (!classSet.Contains(a.ValidatorAddress) || !snapshot.Contains(a.ValidatorAddress)) continue;
                if (Verify(a, contractUid, groupKey, address)) signers.Add(a.ValidatorAddress);
            }

            var required = Required(basis);
            return signers.Count >= required ? null
                : $"vBTC V2 contract DKG is attested by {signers.Count} eligible validator(s); {required} required.";
        }
    }
}
