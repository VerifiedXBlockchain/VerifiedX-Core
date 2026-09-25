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
    ///  - a DKGProof (DKG_ATTESTED_V2) in which EVERY participant of the key ceremony signed the contract UID, group key,
    ///    address, owner (the ceremony leader), signing threshold and the sorted participant list. A validator signs only
    ///    for a ceremony it took part in and whose key package it stored.
    /// Consensus then requires every participant to be an active validator of the contract's class holding the validator
    /// balance in committed state; a public ceremony to include at least 90% of those eligible validators (near-full
    /// participation, owner decision Sep 25 2026 - so neither a creator's unfunded registrations nor a funded minority can
    /// form the group and hold the key alone); an S3C ceremony its whole creator-chosen pool; a threshold of at least a
    /// majority of the participants; and the owner to be the transaction sender (attestations cannot be reused by another
    /// creator).
    /// </summary>
    public static class FrostDkgAttestation
    {
        public const string ProofType = "DKG_ATTESTED_V2";
        public const int MaxParticipants = 512;
        /// <summary>A public ceremony must include at least this share of the eligible validators.</summary>
        public const double PublicParticipationShare = 0.90;
        /// <summary>FROST minimum: a threshold key needs at least three participants.</summary>
        public const int MinParticipants = 3;

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
            public string Owner { get; set; } = "";
            public int Threshold { get; set; }
            public List<string> Participants { get; set; } = new();
            public List<Attestation> Attestations { get; set; } = new();
        }

        /// <summary>Participants in the one canonical order every signer and verifier uses.</summary>
        public static List<string> Canonical(IEnumerable<string>? participants) =>
            (participants ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

        /// <summary>The message every participant signs.</summary>
        public static string Message(string contractUid, string groupPublicKey, string taprootAddress, string owner, int threshold, IEnumerable<string> participants) =>
            $"VFX_DKG_ATTEST_V2|{contractUid}|{groupPublicKey}|{taprootAddress}|{owner}|{threshold}|{string.Join(",", Canonical(participants))}";

        /// <summary>The FROST signing threshold the validators use for n participants at a threshold percentage.</summary>
        public static int ThresholdFor(int participants, int thresholdPercent) => (int)Math.Ceiling(participants * (thresholdPercent / 100.0));

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

        public static string BuildProof(string contractUid, string groupPublicKey, string taprootAddress, string owner, int threshold,
            IEnumerable<string> participants, IEnumerable<Attestation> attestations) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Proof
            {
                ProofType = ProofType,
                ContractUID = contractUid,
                GroupPublicKey = groupPublicKey,
                TaprootAddress = taprootAddress,
                Owner = owner,
                Threshold = threshold,
                Participants = Canonical(participants),
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

        public static bool Verify(Attestation? a, string contractUid, string groupPublicKey, string taprootAddress, string owner, int threshold, IEnumerable<string> participants) =>
            a != null && !string.IsNullOrEmpty(a.ValidatorAddress) && !string.IsNullOrEmpty(a.Signature)
            && SignatureService.VerifySignature(a.ValidatorAddress, Message(contractUid, groupPublicKey, taprootAddress, owner, threshold, participants), a.Signature);

        /// <summary>A majority (51%) of n, and never fewer than two.</summary>
        public static int Required(int n) => Math.Max(2, (int)Math.Ceiling(n * 0.51));

        /// <summary>Participants a public ceremony needs out of <paramref name="eligible"/> validators: at least 90%, and at least three.</summary>
        public static int RequiredParticipants(int eligible) => Math.Max(MinParticipants, (int)Math.Ceiling(eligible * PublicParticipationShare));

        /// <summary>
        /// This validator's attestation for a ceremony it completed, or null. It signs only when it is a participant, its key
        /// store holds the key package for this contract UID with this group key (and, when recorded, the same participant
        /// list), and the address is the group key's Taproot address. Owner, threshold and participants come from its own
        /// session: the leader is authenticated at the ceremony start.
        /// </summary>
        public static Attestation? SignLocal(string? contractUid, string? groupPublicKey, string? taprootAddress, string? owner, int threshold, List<string>? participants)
        {
            try
            {
                var me = Globals.ValidatorAddress;
                var list = Canonical(participants);
                if (string.IsNullOrEmpty(me) || string.IsNullOrEmpty(contractUid) || string.IsNullOrEmpty(groupPublicKey) || string.IsNullOrEmpty(taprootAddress)
                    || string.IsNullOrEmpty(owner) || threshold < 1 || threshold > list.Count || !list.Contains(me))
                    return null;
                if (DeriveTaprootAddress(groupPublicKey, ConsensusNetwork) != taprootAddress) return null;
                var stored = FrostValidatorKeyStore.GetKeyPackage(contractUid, me);
                if (stored == null || string.IsNullOrEmpty(stored.KeyPackage)
                    || !string.Equals(stored.GroupPublicKey, groupPublicKey, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!string.IsNullOrWhiteSpace(stored.ParticipantOrderJson))
                {
                    var recorded = Canonical(JsonConvert.DeserializeObject<List<string>>(stored.ParticipantOrderJson));
                    if (!recorded.SequenceEqual(list, StringComparer.Ordinal)) return null;
                }
                var signature = SignatureService.ValidatorSignature(Message(contractUid, groupPublicKey, taprootAddress, owner, threshold, list));
                return string.IsNullOrEmpty(signature) || signature == "ERROR" || signature == "F" ? null
                    : new Attestation { ValidatorAddress = me, Signature = signature };
            }
            catch { return null; }
        }

        // ── Wallet side (NEW-26 follow-up): never run or finish a ceremony whose contract consensus would refuse ──

        /// <summary>Whether a contract created now (at the next block) must carry validator attestations.</summary>
        public static bool RequiredForNextBlock => (Globals.LastBlock?.Height ?? -1) + 1 >= Globals.VbtcV2DkgAttestationHeight;

        /// <summary>The validators consensus counts for the next block: active at the tip and holding the validator balance.</summary>
        public static List<VBTCValidator> EligibleAtTip() =>
            Services.VBTCValidatorRegistry.FundedOnly(Services.VBTCValidatorRegistry.GetActiveValidatorsAt(Globals.LastBlock?.Height ?? -1));

        /// <summary>
        /// Before a ceremony: null when enough validators are reachable for the contract to be accepted, else the reason. A
        /// public ceremony needs at least 90% of the funded active public validators; an S3C ceremony its whole pool. A
        /// ceremony with fewer could complete and produce a real key whose contract is refused.
        /// </summary>
        public static string? PreCeremonyShortfall(int reachable, bool isS3C, int s3cPoolSize)
        {
            if (!RequiredForNextBlock) return null;
            var basis = isS3C ? s3cPoolSize : EligibleAtTip().Count(v => v.IsActive && !v.IsS3C);
            var need = isS3C ? Math.Max(MinParticipants, s3cPoolSize) : RequiredParticipants(basis);
            return reachable >= need ? null
                : $"Only {reachable} validator(s) are reachable; a vBTC V2 contract needs a key ceremony with at least {need} of the {basis} eligible validators, so its creation would be refused. Try again when more validators are online.";
        }

        /// <summary>
        /// After a ceremony, before its deposit address is recorded or shown: null when the contract it produced would pass
        /// the consensus check at the tip (same rule, same validator set), else the reason.
        /// </summary>
        public static string? CeremonyResultError(string contractUid, string? groupPublicKey, string? taprootAddress, string? dkgProof, List<string>? participants, bool isS3C, string owner)
        {
            if (!RequiredForNextBlock) return null;
            var feature = new TokenizationV2Feature
            {
                DepositAddress = taprootAddress ?? "", FrostGroupPublicKey = groupPublicKey ?? "", DKGProof = dkgProof ?? "",
                ValidatorAddressesSnapshot = participants ?? new List<string>(), IsS3C = isS3C,
            };
            return Validate(feature, contractUid, owner, EligibleAtTip);
        }

        /// <summary>
        /// Consensus check for a TokenizationV2 contract created at some height by <paramref name="owner"/> (the transaction
        /// sender); null when it passes. <paramref name="activeAtPreviousBlock"/> returns the funded vBTC validator set
        /// derived from committed state up to height - 1 (deterministic on every node); it is read only after the cheap
        /// checks pass.
        /// </summary>
        public static string? Validate(TokenizationV2Feature? feature, string contractUid, string owner, Func<List<VBTCValidator>> activeAtPreviousBlock)
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
            if (string.IsNullOrEmpty(owner) || !string.Equals(proof.Owner, owner, StringComparison.Ordinal))
                return "vBTC V2 contract DKG proof was not made for this contract's creator.";

            // Participants: canonical, within bounds, and exactly the contract's validator list.
            var participants = proof.Participants ?? new List<string>();
            var canonical = Canonical(participants);
            if (participants.Count < MinParticipants || participants.Count > MaxParticipants || !canonical.SequenceEqual(participants, StringComparer.Ordinal))
                return $"vBTC V2 contract DKG proof must list {MinParticipants} to {MaxParticipants} distinct participants in canonical order.";
            var snapshot = Canonical(feature.ValidatorAddressesSnapshot);
            if (!snapshot.SequenceEqual(canonical, StringComparer.Ordinal))
                return "vBTC V2 contract validator list does not match the key ceremony's participants.";

            var n = canonical.Count;
            if (proof.Threshold < Required(n) || proof.Threshold > n)
                return $"vBTC V2 contract signing threshold {proof.Threshold} is not a majority of its {n} participants.";

            // Every participant: an active validator of the contract's class that holds the validator balance.
            var eligible = new HashSet<string>(activeAtPreviousBlock().Where(v => v != null && v.IsActive && v.IsS3C == feature.IsS3C && !string.IsNullOrEmpty(v.ValidatorAddress))
                .Select(v => v.ValidatorAddress), StringComparer.Ordinal);
            var notEligible = canonical.FirstOrDefault(p => !eligible.Contains(p));
            if (notEligible != null)
                return $"vBTC V2 contract key ceremony participant {notEligible} is not an eligible validator.";

            // Participation: a public ceremony includes nearly every eligible validator; an S3C ceremony is its own pool.
            if (!feature.IsS3C && n < RequiredParticipants(eligible.Count))
                return $"vBTC V2 contract key ceremony had {n} participants; at least {RequiredParticipants(eligible.Count)} of the {eligible.Count} eligible validators are required.";

            // Exactly one valid attestation from every participant.
            if (proof.Attestations == null || proof.Attestations.Count != n)
                return $"vBTC V2 contract DKG proof must carry one attestation from each of its {n} participants.";
            var byAddress = new Dictionary<string, Attestation>(StringComparer.Ordinal);
            foreach (var a in proof.Attestations)
                if (a?.ValidatorAddress == null || !byAddress.TryAdd(a.ValidatorAddress, a))
                    return "vBTC V2 contract DKG proof carries a duplicate or unnamed attestation.";
            foreach (var p in canonical)
            {
                if (!byAddress.TryGetValue(p, out var a) || !Verify(a, contractUid, groupKey, address, owner, proof.Threshold, canonical))
                    return $"vBTC V2 contract DKG proof has no valid attestation from participant {p}.";
            }
            return null;
        }
    }
}
