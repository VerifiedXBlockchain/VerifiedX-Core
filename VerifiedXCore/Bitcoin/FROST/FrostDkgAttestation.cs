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

        /// <summary>
        /// A vault's new key (a re-key, plan 8.8) is made at this share of its holders, whoever leads the ceremony and
        /// whatever the old key used: chosen by the leader, a key every holder had to sign with could be withheld by
        /// any one of them, and would fall due again at once.
        /// </summary>
        public const int RekeyThresholdPercent = 67;

        /// <summary>
        /// The share of eligible validators a re-key's ceremony must include. Lower than a creation's: a re-key is
        /// needed exactly when validators are leaving, and one that cannot be made in time loses the vault's Bitcoin.
        /// Still two thirds of every eligible stake, each attesting.
        /// </summary>
        public const double RekeyParticipationShare = 0.67;

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
            /// <summary>
            /// SHA-256 of the public key package every participant finished with (the group key and every holder's
            /// verifying share, derived from all the commitments it was given), lowercase hex. Every attestation signs it,
            /// so a proof stands only if all participants were given the same commitments (see <see cref="KeyPackageHash"/>).
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? PublicKeyPackageHash { get; set; }

            /// <summary>
            /// Each participant's Round 1 constant term and its proof of knowledge, by address (a creation's proof). The
            /// terms sum to the group key, and each proof shows its dealer knows its term's secret: together, that the
            /// participants hold the key (<see cref="KeyPossessionRefusal"/>).
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, Round1Proof>? Round1Proofs { get; set; }
        }

        /// <summary>A dealer's Round 1 constant term (33-byte compressed point, hex) and its proof of knowledge (R x-only || z, hex).</summary>
        public class Round1Proof
        {
            public string ConstantTerm { get; set; } = "";
            public string ProofOfKnowledge { get; set; } = "";
        }

        // ── Possession of the key ─────────────────────────────────────────────────────────────────────────────

        private static readonly System.Numerics.BigInteger CurveOrder = System.Numerics.BigInteger.Parse(
            "0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);

        private static byte[] Be32(System.Numerics.BigInteger value)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            var result = new byte[32];
            Buffer.BlockCopy(bytes, 0, result, 32 - bytes.Length, bytes.Length);
            return result;
        }

        /// <summary>expand_message_xmd with SHA-256 (RFC 9380 section 5.3.1).</summary>
        private static byte[] ExpandMessageXmd(byte[] message, byte[] dst, int length)
        {
            var dstPrime = dst.Concat(new[] { (byte)dst.Length }).ToArray();
            var b0 = System.Security.Cryptography.SHA256.HashData(new byte[64].Concat(message).Concat(new[] { (byte)(length >> 8), (byte)length, (byte)0 }).Concat(dstPrime).ToArray());
            var b = System.Security.Cryptography.SHA256.HashData(b0.Concat(new byte[] { 1 }).Concat(dstPrime).ToArray());
            var output = new List<byte>(b);
            for (var i = 2; output.Count < length; i++)
            {
                b = System.Security.Cryptography.SHA256.HashData(b0.Zip(b, (x, y) => (byte)(x ^ y)).Concat(new[] { (byte)i }).Concat(dstPrime).ToArray());
                output.AddRange(b);
            }
            return output.Take(length).ToArray();
        }

        /// <summary>The challenge of a Round 1 proof of knowledge: H_dkg(identifier ‖ φ ‖ R), R the even-y point of x <paramref name="rX"/>.</summary>
        internal static System.Numerics.BigInteger DkgChallenge(byte[] identifier32, byte[] phiCompressed33, byte[] rX32)
        {
            var preimage = identifier32.Concat(phiCompressed33).Concat(new byte[] { 0x02 }).Concat(rX32).ToArray();
            var uniform = ExpandMessageXmd(preimage, Encoding.ASCII.GetBytes("FROST-secp256k1-SHA256-TR-v1dkg"), 48);
            return System.Numerics.BigInteger.Parse("0" + Convert.ToHexString(uniform), System.Globalization.NumberStyles.HexNumber) % CurveOrder;
        }

        /// <summary>
        /// Whether a dealer's Round 1 proof of knowledge holds, as the FROST library (ciphersuite
        /// FROST-secp256k1-SHA256-TR-v1) makes it: R = z·G − c·φ with c = H_dkg(identifier ‖ φ ‖ R), R of even y. Checked in
        /// managed code against the library's own packages (FrostDkgRetryTests).
        /// </summary>
        public static bool ProofOfKnowledgeHolds(string identifierHex, string constantTermHex, string proofHex)
        {
            try
            {
                var identifier = Convert.FromHexString(identifierHex);
                var phiBytes = Convert.FromHexString(constantTermHex);
                var proof = Convert.FromHexString(proofHex);
                if (identifier.Length != 32 || phiBytes.Length != 33 || proof.Length != 64) return false;
                var rX = proof.AsSpan(0, 32).ToArray();
                var z = proof.AsSpan(32, 32).ToArray();
                if (!NBitcoin.Secp256k1.ECPubKey.TryCreate(phiBytes, NBitcoin.Secp256k1.Context.Instance, out _, out var phi) || phi == null) return false;
                var c = DkgChallenge(identifier, phi.ToBytes(true), rX);
                if (c.IsZero) return false;
                if (!NBitcoin.Secp256k1.ECPrivKey.TryCreate(z, out var zKey) || zKey == null) return false;
                if (!NBitcoin.Secp256k1.ECPrivKey.TryCreate(Be32(CurveOrder - c), out var negC) || negC == null) return false;
                if (!NBitcoin.Secp256k1.ECPubKey.TryCombine(NBitcoin.Secp256k1.Context.Instance, new[] { zKey.CreatePubKey(), phi.GetSharedPubkey(negC) }, out var r) || r == null)
                    return false;
                var rBytes = r.ToBytes(true);
                return rBytes[0] == 0x02 && rBytes.AsSpan(1).SequenceEqual(rX);
            }
            catch { return false; }
        }

        /// <summary>
        /// Why a creation's proof does not show that its participants hold the key it names; null when it does. Every
        /// participant gives its Round 1 constant term and proof of knowledge (under its identifier, from the sorted
        /// participant list), each proof holds, and the terms sum to the group key. An S3C vault needs only a few of its
        /// own validators' attestations, and the deposit address is the group key itself: three self-declared S3C
        /// validators could name any Taproot address (a pending vault's, a re-key's, anyone's) and have its Bitcoin
        /// credited to their vault (fourth review, round 11). Nobody can prove knowledge of a key they do not hold.
        /// </summary>
        public static string? KeyPossessionRefusal(Proof proof, IReadOnlyList<string> canonicalParticipants, string groupKeyHex)
        {
            if (proof.Round1Proofs == null || proof.Round1Proofs.Count != canonicalParticipants.Count)
                return "The key ceremony proof carries no proof that its participants hold the key (each participant's Round 1 constant term and proof of knowledge).";
            var identifiers = FrostStartup.BuildAddressToIdentifierMap(canonicalParticipants.ToList());
            var terms = new List<NBitcoin.Secp256k1.ECPubKey>();
            foreach (var participant in canonicalParticipants)
            {
                if (!proof.Round1Proofs.TryGetValue(participant, out var round1) || round1 == null
                    || !ProofOfKnowledgeHolds(identifiers[participant], round1.ConstantTerm, round1.ProofOfKnowledge))
                    return $"The key ceremony proof has no valid proof of knowledge from participant {participant}.";
                if (!NBitcoin.Secp256k1.ECPubKey.TryCreate(Convert.FromHexString(round1.ConstantTerm), NBitcoin.Secp256k1.Context.Instance, out _, out var term) || term == null)
                    return $"The key ceremony proof has an unreadable constant term from participant {participant}.";
                terms.Add(term);
            }
            if (!NBitcoin.Secp256k1.ECPubKey.TryCombine(NBitcoin.Secp256k1.Context.Instance, terms, out var sum) || sum == null)
                return "The participants' constant terms do not sum to a key.";
            var groupKey = Convert.FromHexString(groupKeyHex);
            var groupX = groupKey.Length == 33 ? groupKey.AsSpan(1).ToArray() : groupKey;
            // The library's group key is the sum taken as a Taproot internal key and tweaked (BIP341, no script tree);
            // knowing the sum's secret is knowing the tweaked key's, whose tweak is public. Either form is accepted.
            var internalX = sum.ToBytes(true).AsSpan(1).ToArray();
            var tweakedX = new TaprootInternalPubKey(internalX).GetTaprootFullPubKey().ToBytes();
            return internalX.AsSpan().SequenceEqual(groupX) || tweakedX.AsSpan().SequenceEqual(groupX)
                ? null : "The participants' constant terms do not sum to the vault's group key: they do not hold it.";
        }

        /// <summary>Each participant's constant term and proof of knowledge, read from the Round 1 packages a coordinator collected.</summary>
        public static Dictionary<string, Round1Proof>? Round1ProofsOf(IReadOnlyDictionary<string, string>? round1Packages, IEnumerable<string> participants)
        {
            if (round1Packages == null) return null;
            var proofs = new Dictionary<string, Round1Proof>(StringComparer.Ordinal);
            foreach (var participant in participants)
            {
                if (!round1Packages.TryGetValue(participant, out var package)) return null;
                try
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(package);
                    var term = (json["commitment"] as Newtonsoft.Json.Linq.JArray)?.FirstOrDefault()?.ToString();
                    var pok = json["proof_of_knowledge"]?.ToString();
                    if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(pok)) return null;
                    proofs[participant] = new Round1Proof { ConstantTerm = term, ProofOfKnowledge = pok };
                }
                catch { return null; }
            }
            return proofs;
        }

        /// <summary>
        /// The hash a participant signs of the public key package it finished a ceremony with. The ceremony's leader relays
        /// every Round 1 commitment and nobody compared what they were given: a leader could give some participants other
        /// commitments (and matching shares) than the rest, every check passed, every participant derived and attested the
        /// same group key, and no group of holders could ever sign with it; through a re-key, one validator froze a
        /// vault's Bitcoin for good (fourth review, round 3). Each holder's verifying share is derived from every
        /// commitment it saw, so participants given different commitments finish with different packages.
        /// </summary>
        public static string? KeyPackageHash(string? publicKeyPackage) =>
            string.IsNullOrEmpty(publicKeyPackage) ? null
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyPackage))).ToLowerInvariant();

        private static bool IsKeyPackageHash(string? hash) =>
            hash != null && hash.Length == 64 && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        /// <summary>Participants in the one canonical order every signer and verifier uses.</summary>
        public static List<string> Canonical(IEnumerable<string>? participants) =>
            (participants ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

        /// <summary>The message every participant signs.</summary>
        /// <param name="rekeyOf">
        /// For a re-key's ceremony, the vault it makes a new key for. Part of what each participant signs, so a
        /// creation's proof is never a re-key's and a re-key's names its vault.
        /// </param>
        /// <param name="keyPackageHash">The <see cref="KeyPackageHash"/> of the package this participant finished with.</param>
        public static string Message(string contractUid, string groupPublicKey, string taprootAddress, string owner, int threshold, IEnumerable<string> participants, string? rekeyOf = null, string? keyPackageHash = null) =>
            $"VFX_DKG_ATTEST_V2|{contractUid}|{groupPublicKey}|{taprootAddress}|{owner}|{threshold}|{string.Join(",", Canonical(participants))}"
            + (string.IsNullOrEmpty(rekeyOf) ? "" : $"|REKEY|{rekeyOf}")
            + (string.IsNullOrEmpty(keyPackageHash) ? "" : $"|PKP|{keyPackageHash}");

        /// <summary>The FROST signing threshold the validators use for n participants at a threshold percentage.</summary>
        public static int ThresholdFor(int participants, int thresholdPercent) => (int)Math.Ceiling(participants * (thresholdPercent / 100.0));

        /// <summary>
        /// How many of a new key's <paramref name="participants"/> sign together after a re-key: 67%, but never every one of
        /// them (and never fewer than a majority). At three participants 67% is all three: such a key has no holder to
        /// spare, is due for replacement at once, and was re-keyed and swept again and again (fourth review).
        /// </summary>
        public static int RekeyThreshold(int participants) =>
            Math.Max(Required(participants), Math.Min(ThresholdFor(participants, RekeyThresholdPercent), participants - 1));

        /// <summary>A ceremony's signing threshold: a re-key's by <see cref="RekeyThreshold"/>, a creation's by its percentage.</summary>
        public static int CeremonyThreshold(int participants, int thresholdPercent, string? rekeyOf) =>
            string.IsNullOrEmpty(rekeyOf) ? ThresholdFor(participants, thresholdPercent) : RekeyThreshold(participants);

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
            IEnumerable<string> participants, IEnumerable<Attestation> attestations, string? keyPackageHash = null, Dictionary<string, Round1Proof>? round1Proofs = null) =>
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
                PublicKeyPackageHash = string.IsNullOrEmpty(keyPackageHash) ? null : keyPackageHash,
                Round1Proofs = round1Proofs,
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

        public static bool Verify(Attestation? a, string contractUid, string groupPublicKey, string taprootAddress, string owner, int threshold, IEnumerable<string> participants, string? rekeyOf = null, string? keyPackageHash = null) =>
            a != null && !string.IsNullOrEmpty(a.ValidatorAddress) && !string.IsNullOrEmpty(a.Signature)
            && SignatureService.VerifySignature(a.ValidatorAddress, Message(contractUid, groupPublicKey, taprootAddress, owner, threshold, participants, rekeyOf, keyPackageHash), a.Signature);

        /// <summary>A majority (51%) of n, and never fewer than two.</summary>
        public static int Required(int n) => Math.Max(2, (int)Math.Ceiling(n * 0.51));

        /// <summary>Participants a public ceremony needs out of <paramref name="eligible"/> validators: at least 90%, and at least three.</summary>
        public static int RequiredParticipants(int eligible, double share = PublicParticipationShare) => Math.Max(MinParticipants, (int)Math.Ceiling(eligible * share));

        /// <summary>
        /// This validator's attestation for a ceremony it completed, or null. It signs only when it is a participant, its key
        /// store holds the key package for this contract UID with this group key (and, when recorded, the same participant
        /// list), and the address is the group key's Taproot address. Owner, threshold and participants come from its own
        /// session: the leader is authenticated at the ceremony start.
        /// </summary>
        public static Attestation? SignLocal(string? contractUid, string? groupPublicKey, string? taprootAddress, string? owner, int threshold, List<string>? participants, string? rekeyOf = null, string? keyPackageHash = null)
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
                var signature = SignatureService.ValidatorSignature(Message(contractUid, groupPublicKey, taprootAddress, owner, threshold, list, rekeyOf, keyPackageHash));
                return string.IsNullOrEmpty(signature) || signature == "ERROR" || signature == "F" ? null
                    : new Attestation { ValidatorAddress = me, Signature = signature };
            }
            catch { return null; }
        }

        // ── Wallet side (NEW-26 follow-up): never run or finish a ceremony whose contract consensus would refuse ──

        /// <summary>Whether a contract created now (at the next block) must carry validator attestations.</summary>
        public static bool RequiredForNextBlock => (Globals.LastBlock?.Height ?? -1) + 1 >= Globals.VbtcV2DkgAttestationHeight;

        /// <summary>
        /// The validators consensus counts for a key ceremony judged in the next block: active at the tip, holding the
        /// validator balance and, from the bond height, with a stake locked for two days (ValidatorBond.CeremonyEligible).
        /// </summary>
        public static List<VBTCValidator> EligibleAtTip() =>
            VerifiedXCore.Services.ValidatorBond.CeremonyEligible(Services.VBTCValidatorRegistry.GetActiveValidatorsAt(Globals.LastBlock?.Height ?? -1), (Globals.LastBlock?.Height ?? -1) + 1);

        /// <summary>
        /// Before a ceremony: null when enough validators are reachable for the contract to be accepted, else the reason. A
        /// public ceremony needs at least 90% of the funded active public validators; an S3C ceremony its whole pool. A
        /// ceremony with fewer could complete and produce a real key whose contract is refused.
        /// </summary>
        public static string? PreCeremonyShortfall(int reachable, bool isS3C, int s3cPoolSize, double share = PublicParticipationShare)
        {
            if (!RequiredForNextBlock) return null;
            var basis = isS3C ? s3cPoolSize : EligibleAtTip().Count(v => v.IsActive && !v.IsS3C);
            var need = isS3C ? Math.Max(MinParticipants, s3cPoolSize) : RequiredParticipants(basis, share);
            return reachable >= need ? null
                : $"Only {reachable} validator(s) are reachable; a vBTC V2 contract needs a key ceremony with at least {need} of the {basis} eligible validators, so its creation would be refused. Try again when more validators are online.";
        }

        /// <summary>
        /// After a ceremony, before its deposit address is recorded or shown: null when the contract it produced would pass
        /// the consensus check at the tip (same rule, same validator set), else the reason.
        /// </summary>
        public static string? CeremonyResultError(string contractUid, string? groupPublicKey, string? taprootAddress, string? dkgProof, List<string>? participants, bool isS3C, string owner, string? rekeyOf = null)
        {
            if (!RequiredForNextBlock && rekeyOf == null) return null;
            var feature = new TokenizationV2Feature
            {
                DepositAddress = taprootAddress ?? "", FrostGroupPublicKey = groupPublicKey ?? "", DKGProof = dkgProof ?? "",
                ValidatorAddressesSnapshot = participants ?? new List<string>(), IsS3C = isS3C,
            };
            return Validate(feature, contractUid, owner, EligibleAtTip, rekeyOf,
                requireKeyPackageHash: rekeyOf != null || (Globals.LastBlock?.Height ?? -1) + 1 >= Globals.PathAuditRulesHeight);
        }

        /// <summary>
        /// Consensus check for a TokenizationV2 contract created at some height by <paramref name="owner"/> (the transaction
        /// sender); null when it passes. <paramref name="activeAtPreviousBlock"/> returns the funded vBTC validator set
        /// derived from committed state up to height - 1 (deterministic on every node); it is read only after the cheap
        /// checks pass.
        /// </summary>
        /// <param name="rekeyOf">For a re-key (plan 8.8): the vault the new key is for. The attestations must name it, the
        /// threshold is <see cref="RekeyThresholdPercent"/> of the participants exactly, and the ceremony includes at
        /// least <see cref="RekeyParticipationShare"/> of the eligible validators.</param>
        /// <param name="requireKeyPackageHash">
        /// The proof must carry the hash of the public key package every participant attested (<see cref="KeyPackageHash"/>):
        /// always for a re-key, from Globals.PathAuditRulesHeight for a creation.
        /// </param>
        public static string? Validate(TokenizationV2Feature? feature, string contractUid, string owner, Func<List<VBTCValidator>> activeAtPreviousBlock, string? rekeyOf = null, bool requireKeyPackageHash = false,
            bool requireKeyPossession = false)
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
            if (requireKeyPackageHash && !IsKeyPackageHash(proof.PublicKeyPackageHash))
                return "vBTC V2 contract DKG proof does not name the public key package its participants finished with.";
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
            if (rekeyOf != null && proof.Threshold != RekeyThreshold(n))
                return $"A vault's new key signs with {RekeyThreshold(n)} of its {n} holders ({RekeyThresholdPercent}%, never all); this one names {proof.Threshold}.";

            // Every participant: an active validator of the contract's class that holds the validator balance.
            var eligible = new HashSet<string>(activeAtPreviousBlock().Where(v => v != null && v.IsActive && v.IsS3C == feature.IsS3C && !string.IsNullOrEmpty(v.ValidatorAddress))
                .Select(v => v.ValidatorAddress), StringComparer.Ordinal);
            var notEligible = canonical.FirstOrDefault(p => !eligible.Contains(p));
            if (notEligible != null)
                return $"vBTC V2 contract key ceremony participant {notEligible} is not an eligible validator.";

            // Participation: a public ceremony includes nearly every eligible validator; an S3C ceremony is its own pool.
            var share = rekeyOf != null ? RekeyParticipationShare : PublicParticipationShare;
            if (!feature.IsS3C && n < RequiredParticipants(eligible.Count, share))
                return $"vBTC V2 contract key ceremony had {n} participants; at least {RequiredParticipants(eligible.Count, share)} of the {eligible.Count} eligible validators are required.";

            // Exactly one valid attestation from every participant.
            if (proof.Attestations == null || proof.Attestations.Count != n)
                return $"vBTC V2 contract DKG proof must carry one attestation from each of its {n} participants.";
            var byAddress = new Dictionary<string, Attestation>(StringComparer.Ordinal);
            foreach (var a in proof.Attestations)
                if (a?.ValidatorAddress == null || !byAddress.TryAdd(a.ValidatorAddress, a))
                    return "vBTC V2 contract DKG proof carries a duplicate or unnamed attestation.";
            foreach (var p in canonical)
            {
                if (!byAddress.TryGetValue(p, out var a) || !Verify(a, contractUid, groupKey, address, owner, proof.Threshold, canonical, rekeyOf, proof.PublicKeyPackageHash))
                    return $"vBTC V2 contract DKG proof has no valid attestation from participant {p}.";
            }
            return requireKeyPossession ? KeyPossessionRefusal(proof, canonical, groupKey) : null;
        }
    }
}
