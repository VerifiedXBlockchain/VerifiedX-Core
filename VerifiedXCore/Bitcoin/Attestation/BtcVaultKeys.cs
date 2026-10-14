using LiteDB;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// The content of a re-key transaction (<see cref="TransactionType.VBTC_V2_VAULT_REKEY"/>): the vault, and the proof
    /// of the key ceremony that made its new key. Sender-less, like a report: the authority is the attestations of the
    /// ceremony's participants inside the proof.
    /// </summary>
    public sealed class BtcRekey
    {
        [JsonProperty(Order = 1)] public int V { get; set; } = 1;
        /// <summary>The vault being moved to a new key.</summary>
        [JsonProperty(Order = 2)] public string ContractUID { get; set; } = "";
        /// <summary>The key ceremony's proof (<see cref="FrostDkgAttestation.Proof"/>, as text): its ContractUID is the new key's UID.</summary>
        [JsonProperty(Order = 3)] public string DKGProof { get; set; } = "";

        private static readonly JsonSerializerSettings Canonical = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            MissingMemberHandling = MissingMemberHandling.Error,
        };

        public string ToCanonicalJson() => JsonConvert.SerializeObject(this, Canonical);

        /// <summary>Parses a re-key transaction's Data. False unless the text is exactly the canonical form of what it parses to.</summary>
        public static bool TryParse(string? data, out BtcRekey? rekey, out string? reason)
        {
            rekey = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(data)) { reason = "a re-key carries no data"; return false; }
            try { rekey = JsonConvert.DeserializeObject<BtcRekey>(data, Canonical); }
            catch (Exception ex) { reason = $"the re-key does not parse: {ex.Message}"; return false; }
            if (rekey == null) { reason = "the re-key does not parse"; return false; }
            if (!string.Equals(rekey.ToCanonicalJson(), data, StringComparison.Ordinal))
            {
                rekey = null;
                reason = "the re-key is not in its canonical form";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// A vault's keys (docs/PLAN-vBTC-Bitcoin-Attestation.md, 8.8).
    ///
    /// A vault's Bitcoin is held by a threshold key whose shares the validators of its creation hold. Validators leave:
    /// below the key's threshold of holders who still run, nothing of the vault can ever be signed again, a withdrawal
    /// fixed on its coins can be neither paid nor given up, and the Bitcoin is gone for good. So a vault is moved to a
    /// new key before that, while its old key still signs:
    /// <list type="number">
    /// <item><b>Health.</b> Every <see cref="HealthCheckBlocks"/> blocks (and at each epoch start) each vault key is
    /// looked at: of its holders, how many are active validators whose stake is bonded with no exit. When half of the
    /// holders it can spare above its threshold are gone, the key is due for replacement, and stays due until it is
    /// replaced (<see cref="CheckHealth"/>). The rule reads the chain only.</item>
    /// <item><b>Re-key.</b> While a key is due, a key ceremony among the validators of the day makes a new one, under
    /// a new UID of its own: the participants are eligible validators as for a creation, at least 67% of them take
    /// part, the key signs with 67% of its holders, and every attestation names the vault. Anyone may lead it and
    /// submit the result; validators do so on their own, in turns (<see cref="BtcVaultKeyService"/>). The new key
    /// becomes the vault's current key.</item>
    /// <item><b>Sweep.</b> The old key's coins are sent to the new key by transactions fixed by rule, signed by the
    /// old key's holders like any withdrawal (BtcWithdrawals.Sweeps.cs).</item>
    /// </list>
    /// Nothing here is decided by an appointed node: who holds a key, when it is due and what the new key must satisfy
    /// are read from the chain, and the leader of a ceremony chooses nothing.
    /// </summary>
    public static class BtcVaultKeys
    {
        // ── Which key ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The key a UID names: a key's own UID (the vault's UID for its first key), or a vault's UID for its first key.
        /// Null when the chain watches no such key.
        /// </summary>
        public static VaultDepositScriptRow? KeyRow(string? uid, LiteDatabase? db = null)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            var byKey = (db ?? BtcAttestStore.Db).GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts).FindOne(x => x.KeyUID == uid);
            return byKey ?? BtcAttestStore.ScriptRowsForVault(uid, db).FirstOrDefault();
        }

        /// <summary>The vault a key UID belongs to (a vault's own UID answers itself); null when none.</summary>
        public static string? VaultOf(string? uid, LiteDatabase? db = null) => KeyRow(uid, db)?.SmartContractUID;

        /// <summary>The UID the holders file this key's shares under.</summary>
        public static string KeyUidOf(VaultDepositScriptRow key) => string.IsNullOrEmpty(key.KeyUID) ? key.SmartContractUID : key.KeyUID;

        /// <summary>The holders of a key (from the contract for a first key recorded without them).</summary>
        public static List<string> Holders(VaultDepositScriptRow key)
        {
            if (key.Holders is { Count: > 0 }) return key.Holders;
            return VBTCChainView.GetVault(key.SmartContractUID)?.Feature.ValidatorAddressesSnapshot?
                       .Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList()
                   ?? new List<string>();
        }

        /// <summary>How many of a key's holders sign together.</summary>
        public static int MinSigners(VaultDepositScriptRow key)
        {
            if (key.MinSigners > 0) return key.MinSigners;
            var feature = VBTCChainView.GetVault(key.SmartContractUID)?.Feature;
            var attested = FrostDkgAttestation.ParseProof(feature?.DKGProof)?.Threshold ?? 0;
            return attested > 0 ? attested : FrostDkgAttestation.ThresholdFor(Holders(key).Count, feature?.RequiredThreshold ?? 0);
        }

        /// <summary>The key's public key, hex.</summary>
        public static string? GroupKey(VaultDepositScriptRow key) =>
            !string.IsNullOrEmpty(key.GroupKeyHex) ? key.GroupKeyHex : VBTCChainView.GetVault(key.SmartContractUID)?.Feature.FrostGroupPublicKey;

        /// <summary>The one key the given coins are all held under; null when they are not (or there are none).</summary>
        public static VaultDepositScriptRow? KeyOfCoins(IEnumerable<string> outpoints, LiteDatabase? db = null)
        {
            VaultDepositScriptRow? key = null;
            foreach (var outpoint in outpoints)
            {
                var row = BtcAttestStore.ScriptRowOfCoin(BtcAttestStore.Coin(outpoint, db), db);
                if (row == null) return null;
                if (key == null) key = row;
                else if (!string.Equals(key.ScriptHex, row.ScriptHex, StringComparison.Ordinal)) return null;
            }
            return key;
        }

        // ── Health ─────────────────────────────────────────────────────────────────

        /// <summary>How often the keys' holders are counted, in VFX blocks (about twenty minutes).</summary>
        public const int HealthCheckBlocks = 100;

        /// <summary>
        /// Holders a key may lose before it is replaced: half of those it can spare above its threshold, at least one.
        /// The other half is the time the replacement takes (a ceremony, then the old key signs the sweep).
        /// </summary>
        public static int SpareBeforeRekey(int holders, int minSigners) => Math.Max(1, (holders - minSigners + 1) / 2);

        /// <summary>A holder that counts: an active validator now, its stake bonded and no exit sent.</summary>
        private static bool IsHealthy(string address, HashSet<string> active, long height, LiteDatabase? db)
        {
            if (!active.Contains(address)) return false;
            if (!VerifiedXCore.Services.ValidatorBond.Active(height)) return true;
            var bond = VerifiedXCore.Services.ValidatorBond.Row(address, db);
            return bond != null && bond.ExitHeight == 0 && VerifiedXCore.Services.ValidatorBond.Holds(bond, height);
        }

        /// <summary>
        /// Every <see cref="HealthCheckBlocks"/> blocks: how many holders of each vault's current key still count, and whether the key is
        /// due for replacement. The registry and the bonds read committed blocks and state only, so every node writes the
        /// same. A row is written only when what it says changes.
        /// </summary>
        public static void CheckHealth(long vfxHeight, LiteDatabase? db = null)
        {
            var active = new HashSet<string>(VBTCValidatorRegistry.GetActiveValidatorsAt(vfxHeight - 1)
                .Where(v => v != null && v.IsActive && !string.IsNullOrEmpty(v.ValidatorAddress)).Select(v => v.ValidatorAddress), StringComparer.Ordinal);
            // Each validator is looked at once per count, however many keys it holds a share of: the same validators
            // hold most keys, and a lookup per key and holder grew with vaults times validators (fourth review, round 2).
            // Nothing changes the bonds while the count runs.
            var healthyByAddress = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Counts(string address)
            {
                if (!healthyByAddress.TryGetValue(address, out var counts))
                    healthyByAddress[address] = counts = IsHealthy(address, active, vfxHeight, db);
                return counts;
            }
            foreach (var key in (db ?? BtcAttestStore.Db).GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts)
                         .Find(x => x.RetiredAtVfxHeight == 0).OrderBy(x => x.ScriptHex, StringComparer.Ordinal).ToList())
            {
                var holders = Holders(key);
                var threshold = MinSigners(key);
                if (holders.Count == 0 || threshold <= 0) continue;
                var healthy = holders.Count(Counts);
                var due = healthy < threshold + SpareBeforeRekey(holders.Count, threshold);
                if (!due && key.RekeyDueSinceVfxHeight > 0 && healthy < holders.Count) due = true; // once due, due until replaced
                var dueSince = due ? (key.RekeyDueSinceVfxHeight > 0 ? key.RekeyDueSinceVfxHeight : vfxHeight) : 0;
                if (key.HealthyHolders == healthy && key.RekeyDueSinceVfxHeight == dueSince) continue;
                key.HealthyHolders = healthy;
                key.HealthCheckedAtVfxHeight = vfxHeight;
                key.RekeyDueSinceVfxHeight = dueSince;
                BtcAttestStore.SaveScript(key, db);
                if (due)
                    SCLogUtility.Log($"Vault {key.SmartContractUID}: {healthy} of the {holders.Count} holders of its key (generation {key.Generation}) still count; {threshold} sign. A new key is due.", "BtcVaultKeys.CheckHealth()");
            }
        }

        /// <summary>
        /// Why nothing new may be debited for the vault's Bitcoin now: its current key had fewer holders that count than
        /// it needs to sign at the last look. A withdrawal requested then would hold its requester's vBTC for a
        /// transaction nobody can sign. Null when the key can sign (or has not been looked at yet).
        /// </summary>
        public static string? CannotSignRefusal(string? scUID, LiteDatabase? db = null)
        {
            var key = BtcAttestStore.ScriptRowForVault(scUID, db);
            if (key == null || key.HealthyHolders < 0) return null;
            var threshold = MinSigners(key);
            return key.HealthyHolders >= threshold ? null
                : $"Vault {scUID}'s Bitcoin key has {key.HealthyHolders} holders left of the {threshold} it needs to sign; nothing can be paid from it until it is moved to a new key.";
        }

        // ── Re-key ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Why a re-key is refused against the state it lands on (before its block at validation, the same state at
        /// apply); null when it is valid, with the new key's row in <paramref name="newKey"/>.
        /// </summary>
        public static string? Refusal(BtcRekey rekey, long vfxHeight, out VaultDepositScriptRow? newKey, LiteDatabase? db = null)
        {
            newKey = null;
            if (rekey.V != 1) return "unknown re-key version";
            var current = BtcAttestStore.ScriptRowForVault(rekey.ContractUID, db);
            if (current == null || !string.Equals(current.SmartContractUID, rekey.ContractUID, StringComparison.Ordinal))
                return $"no vault {rekey.ContractUID} is watched (a re-key names the vault's UID exactly)";
            if (current.RekeyDueSinceVfxHeight <= 0 || current.RekeyDueSinceVfxHeight > vfxHeight)
                return $"vault {rekey.ContractUID}'s key is not due for replacement";
            var vault = VBTCChainView.GetVault(rekey.ContractUID);
            if (vault == null) return $"vault {rekey.ContractUID} is not in state";
            // A pool's own validators hold an S3C vault's key: whom to move it to is its owner's matter, not a rule's.
            if (vault.Feature.IsS3C) return "an S3C vault's key is not replaced by the chain";

            var proof = FrostDkgAttestation.ParseProof(rekey.DKGProof);
            if (proof == null) return "the re-key carries no key ceremony proof";
            var keyUid = proof.ContractUID ?? "";
            // The new key's UID is one of its own: the ceremony ran under it and its holders file their shares under it.
            if (!LooksLikeContractUid(keyUid)) return "the new key's UID is not a UID";
            if (string.Equals(keyUid, rekey.ContractUID, StringComparison.Ordinal)) return "the new key's UID is the vault's own";
            // The new key's UID is the one the chain fixed for it when the key fell due (its first 32 characters; the
            // rest is when the ceremony ran, so a retry has its own). Nothing else can be created under it from that
            // block on, so no contract (an NFT, an S3C vault made by a few validators in seconds) can take it first.
            if (!keyUid.StartsWith(NextKeyHex(current) + ":", StringComparison.Ordinal))
                return $"the new key's UID is not the one fixed for this vault's next key ({NextKeyHex(current)}:<time>)";
            // Another vault's UID or another key's is refused. Any other contract under the same UID is not: its holders
            // learn the UID when the ceremony starts, and minting an NFT under it first would otherwise block every
            // re-key (a key UID is looked up as a key before it is looked up as a contract, and no vault can ever be
            // created under it, LedgerIntegrityRules.CreatedUidWasNeverWatched).
            var scripts = (db ?? BtcAttestStore.Db).GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts);
            if (scripts.Exists(x => x.SmartContractUID == keyUid)) return "the new key's UID is a vault's";
            if (scripts.Exists(x => x.KeyUID == keyUid)) return "the new key's UID is already a key's";
            // The ceremony ran after the key fell due (a UID ends in the time it was made): no proof is kept in hand for
            // a key before it is due.
            var dueBlock = VerifiedXCore.Data.BlockchainData.GetBlockByHeight(current.RekeyDueSinceVfxHeight);
            if (dueBlock != null && long.TryParse(keyUid.Substring(keyUid.IndexOf(':') + 1), out var made)
                && made < dueBlock.Timestamp - Globals.MaxFutureSkewSeconds - 3600)
                return "the new key's ceremony was run before the key fell due";

            // The rule a vault's creation follows, word for word: the address is the key's, every participant is an
            // eligible validator, a public ceremony includes nearly all of them, and each attested.
            var feature = new VerifiedXCore.Models.SmartContracts.TokenizationV2Feature
            {
                DepositAddress = proof.TaprootAddress ?? "", FrostGroupPublicKey = proof.GroupPublicKey ?? "", DKGProof = rekey.DKGProof,
                ValidatorAddressesSnapshot = proof.Participants ?? new List<string>(), IsS3C = false,
            };
            // ... except that the attestations name this vault (a creation's proof, or another vault's, is not this re-key's),
            // the threshold is the one every re-key uses, and two thirds of the eligible validators suffice.
            var ceremonyError = FrostDkgAttestation.Validate(feature, keyUid, proof.Owner ?? "",
                () => VerifiedXCore.Services.ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(vfxHeight - 1), vfxHeight),
                rekeyOf: rekey.ContractUID);
            if (ceremonyError != null) return ceremonyError;

            string scriptHex;
            try { scriptHex = BitcoinAddress.Create(feature.DepositAddress, FrostDkgAttestation.ConsensusNetwork).ScriptPubKey.ToHex().ToLowerInvariant(); }
            catch { return "the new key's address is not an address on this network"; }
            if (BtcAttestStore.ScriptRow(scriptHex, db) != null) return "the new key's address is already a vault's";

            var meta = BtcAttestStore.GetMeta(db);
            var participants = FrostDkgAttestation.Canonical(proof.Participants);
            newKey = new VaultDepositScriptRow
            {
                ScriptHex = scriptHex, SmartContractUID = current.SmartContractUID, DepositAddress = feature.DepositAddress,
                CreatedAtVfxHeight = vfxHeight,
                WatchFromBitcoinHeight = meta == null ? 0 : Math.Max(meta.CheckpointHeight + 1, meta.EventsThroughHeight - BtcAttestRules.WatchBackBlocks + 1),
                Generation = current.Generation + 1, KeyUID = keyUid, Holders = participants, MinSigners = proof.Threshold,
                GroupKeyHex = proof.GroupPublicKey, HealthyHolders = participants.Count, HealthCheckedAtVfxHeight = vfxHeight,
            };
            return null;
        }

        /// <summary>
        /// Why a validator does not join a re-key's ceremony for this vault now; null when it may. The vault's current key
        /// must be due at the tip and the vault not an S3C vault, and the threshold the one every re-key uses.
        /// </summary>
        public static string? RekeyCeremonyRefusal(string? vaultUid, int requiredThresholdPercent)
        {
            var key = BtcAttestStore.ScriptRowForVault(vaultUid);
            if (key == null || !string.Equals(key.SmartContractUID, vaultUid, StringComparison.Ordinal)) return $"no vault {vaultUid} is watched";
            if (key.RekeyDueSinceVfxHeight <= 0) return $"vault {vaultUid}'s key is not due for replacement";
            if (VBTCChainView.GetVault(vaultUid)?.Feature.IsS3C != false) return "an S3C vault's key is not replaced by the chain";
            if (requiredThresholdPercent != FrostDkgAttestation.RekeyThresholdPercent)
                return $"a re-key's ceremony uses a threshold of {FrostDkgAttestation.RekeyThresholdPercent}%";
            return null;
        }

        /// <summary>
        /// The first part of the UID of a due key's successor, fixed by the chain when the key fell due: a hash of the
        /// vault, the generation and the block it fell due. No contract can be created under it while the key is due
        /// (<see cref="DueKeyClaiming"/>), and no proof can be made for it before it falls due.
        /// </summary>
        public static string NextKeyHex(VaultDepositScriptRow key) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"VFX-VAULT-REKEY|{key.SmartContractUID}|{key.Generation + 1}|{key.RekeyDueSinceVfxHeight}")))[..32].ToLowerInvariant();

        private static long _lastKeyUidTime;

        /// <summary>A UID for a re-key ceremony of this due key: its fixed first part and a time, a new one for every attempt.</summary>
        public static string NewKeyUid(VaultDepositScriptRow key)
        {
            long now = TimeUtil.GetTime(), last, next;
            do { last = Interlocked.Read(ref _lastKeyUidTime); next = Math.Max(now, last + 1); }
            while (Interlocked.CompareExchange(ref _lastKeyUidTime, next, last) != last);
            return $"{NextKeyHex(key)}:{next}";
        }

        /// <summary>The vault whose due key's successor a UID would be (its first part is that key's <see cref="NextKeyHex"/>); null when none.</summary>
        public static string? DueKeyClaiming(string? uid, LiteDatabase? db = null)
        {
            if (string.IsNullOrEmpty(uid) || uid.IndexOf(':') != 32) return null;
            var hex = uid.Substring(0, 32).ToLowerInvariant();
            foreach (var key in (db ?? BtcAttestStore.Db).GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts)
                         .Find(x => x.RetiredAtVfxHeight == 0 && x.RekeyDueSinceVfxHeight > 0))
                if (NextKeyHex(key) == hex) return key.SmartContractUID;
            return null;
        }

        private static bool LooksLikeContractUid(string uid)
        {
            var colon = uid.IndexOf(':');
            return colon == 32 && uid.Length > 33 && uid.Length <= 64
                && uid.Take(32).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                && uid.Skip(33).All(c => c >= '0' && c <= '9');
        }

        /// <summary>
        /// Applies a mined re-key, verified again against the state it lands on: the new key becomes the vault's
        /// current key and the old one is retired, then the old key's coins are fixed on transactions to the new one.
        /// </summary>
        public static void ApplyRekey(VerifiedXCore.Models.Transaction tx, long vfxHeight, LiteDatabase? db = null)
        {
            if (!BtcRekey.TryParse(tx?.Data, out var rekey, out var parseReason) || rekey == null)
            {
                ErrorLogUtility.LogError($"Re-key {tx?.Hash} at block {vfxHeight} was not applied: {parseReason}", "BtcVaultKeys.ApplyRekey()");
                return;
            }
            var refusal = Refusal(rekey, vfxHeight, out var newKey, db);
            if (refusal != null || newKey == null)
            {
                ErrorLogUtility.LogError($"Re-key {tx!.Hash} of vault {rekey.ContractUID} at block {vfxHeight} was not applied: {refusal}", "BtcVaultKeys.ApplyRekey()");
                return;
            }
            var old = BtcAttestStore.ScriptRowForVault(rekey.ContractUID, db)!;
            old.RetiredAtVfxHeight = vfxHeight;
            BtcAttestStore.SaveScript(old, db);
            BtcAttestStore.SaveScript(newKey, db);
            SCLogUtility.Log($"Vault {rekey.ContractUID} moved to a new key at block {vfxHeight}: generation {newKey.Generation}, {newKey.Holders!.Count} holders, {newKey.MinSigners} sign, address {newKey.DepositAddress} (key {newKey.KeyUID}). The coins of generation {old.Generation} are sent to it.", "BtcVaultKeys.ApplyRekey()");
            BtcWithdrawals.PlanSweeps(rekey.ContractUID, vfxHeight, db);
        }
    }
}
