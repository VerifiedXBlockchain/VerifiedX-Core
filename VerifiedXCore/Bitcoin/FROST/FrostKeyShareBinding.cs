using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// Fund-loss audit item 4 (fourth review). Before NEW-26 a DKG ran under a session id, and each validator's key share
    /// stayed filed under that id until the vault's first signing found it by group key and relabelled it. Finding by
    /// group key alone is the hole: a contract body that copies a vault's group key asks first and is handed the share.
    /// The earlier fixes closed it for shares already filed under a contract; a share still under its session id was
    /// adopted by whichever body asked.
    ///
    /// One rule now decides who a session-id share belongs to: the ONE vault on chain that carries its group key. If two
    /// contracts carry the key, the share is used for neither (a copied key cannot be told from the original by anything
    /// in chain state, so this fails closed and an operator decides). The rule is applied in two places:
    /// <list type="bullet">
    /// <item>once the node is synced, every session-id share whose key has exactly one vault is bound to it
    /// (<see cref="BindSessionShares"/>), so it is filed under the contract before anyone asks for it;</item>
    /// <item>at signing, for a share that arrives later (a peer-backup restore): the same rule
    /// (<see cref="FrostDkgGuard.MayAdoptKeyRecord"/>).</item>
    /// </list>
    /// A share filed under a contract is never relabelled, by either path.
    /// </summary>
    public static class FrostKeyShareBinding
    {
        /// <summary>Every vBTC V2 contract on chain with the FROST group key its body carries. Replaceable for tests.</summary>
        internal static Func<List<(string ScUid, string GroupKey)>> OnChainVaultKeys = () =>
            VBTCChainView.AllVaults().Select(v => (v.SmartContractUID, v.Feature.FrostGroupPublicKey ?? string.Empty)).ToList();

        /// <summary>
        /// One spelling per key: trimmed, lower case, no 0x, and the 32-byte x-only form of a 33-byte compressed key. Two
        /// bodies that spell the same key differently still collide.
        /// </summary>
        public static string NormalizeKey(string? key)
        {
            var k = (key ?? string.Empty).Trim().ToLowerInvariant();
            if (k.StartsWith("0x", StringComparison.Ordinal)) k = k.Substring(2);
            if (k.Length == 66 && (k.StartsWith("02", StringComparison.Ordinal) || k.StartsWith("03", StringComparison.Ordinal))) k = k.Substring(2);
            return k;
        }

        /// <summary>The contracts on chain whose body carries <paramref name="groupKey"/>.</summary>
        public static List<string> VaultsCarrying(string? groupKey)
        {
            var wanted = NormalizeKey(groupKey);
            if (wanted.Length == 0) return new List<string>();
            return OnChainVaultKeys()
                .Where(v => NormalizeKey(v.GroupKey) == wanted)
                .Select(v => v.ScUid)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Whether <paramref name="scUid"/> is the only contract on chain carrying <paramref name="groupKey"/>. Fails closed
        /// when chain state cannot be read.
        /// </summary>
        public static bool IsSoleVault(string? groupKey, string scUid, out string reason)
        {
            reason = "";
            List<string> vaults;
            try { vaults = VaultsCarrying(groupKey); }
            catch (Exception ex)
            {
                reason = $"the vaults carrying group key {groupKey} could not be read from chain state ({ex.Message})";
                return false;
            }
            if (vaults.Count == 1 && string.Equals(vaults[0], scUid, StringComparison.Ordinal))
                return true;
            reason = vaults.Count == 0
                ? $"no contract on chain carries group key {groupKey}"
                : vaults.Count == 1
                    ? $"group key {groupKey} belongs to contract {vaults[0]}, not {scUid}"
                    : $"{vaults.Count} contracts on chain carry group key {groupKey} ({string.Join(", ", vaults)}); a key share filed under a session id is used for none of them until an operator binds it (POST /frost/keystore/bind)";
            return false;
        }

        public sealed class BindResult
        {
            public int Records { get; set; }
            /// <summary>Session-id shares bound to their vault by this run.</summary>
            public int Bound { get; set; }
            /// <summary>Already filed under a contract (on chain, or a NEW-26 ceremony's contract UID).</summary>
            public int FiledUnderContract { get; set; }
            /// <summary>Session-id shares whose key no contract carries (a ceremony whose contract was never created).</summary>
            public int NoVault { get; set; }
            /// <summary>Session-id shares whose key more than one contract carries: left alone, used for none.</summary>
            public int Ambiguous { get; set; }
            public int Failed { get; set; }
            public List<string> Notes { get; } = new();
        }

        /// <summary>
        /// Binds every key share still filed under a session id to the one vault that carries its group key. Idempotent.
        /// Call only when chain state is current (see <see cref="RunOnceWhenSyncedAsync"/>): the answer to "which
        /// contracts carry this key" is read from it.
        /// </summary>
        public static BindResult BindSessionShares()
        {
            var result = new BindResult();
            List<(string ScUid, string GroupKey)> vaults;
            try { vaults = OnChainVaultKeys(); }
            catch (Exception ex)
            {
                result.Failed++;
                result.Notes.Add($"chain state could not be read: {ex.Message}");
                return result;
            }
            var onChain = new HashSet<string>(vaults.Select(v => v.ScUid), StringComparer.Ordinal);
            var byKey = vaults
                .Where(v => NormalizeKey(v.GroupKey).Length > 0)
                .GroupBy(v => NormalizeKey(v.GroupKey), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(v => v.ScUid).Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

            foreach (var record in FrostValidatorKeyStore.GetAllKeyPackages())
            {
                result.Records++;
                var filedUnder = record.SmartContractUID ?? string.Empty;
                if (onChain.Contains(filedUnder) || FrostDkgGuard.IsContractUid(filedUnder))
                {
                    result.FiledUnderContract++;
                    continue;
                }
                var key = NormalizeKey(record.GroupPublicKey);
                if (key.Length == 0 || !byKey.TryGetValue(key, out var carrying) || carrying.Count == 0)
                {
                    result.NoVault++;
                    continue;
                }
                if (carrying.Count > 1)
                {
                    result.Ambiguous++;
                    var note = $"key share {record.Id} (filed under session id {filedUnder}, group key {record.GroupPublicKey}) is NOT bound: {carrying.Count} contracts carry that key ({string.Join(", ", carrying)}). One of them copied it. The share is used for none until an operator binds it (POST /frost/keystore/bind).";
                    result.Notes.Add(note);
                    ErrorLogUtility.LogError("[FROST KeyStore] " + note, "FrostKeyShareBinding.BindSessionShares()");
                    continue;
                }
                var vault = carrying[0];
                if (FrostValidatorKeyStore.UpdateSmartContractUID(record.Id, vault))
                {
                    result.Bound++;
                    result.Notes.Add($"key share {record.Id} bound to {vault} (was filed under session id {filedUnder})");
                }
                else
                {
                    result.Failed++;
                    result.Notes.Add($"key share {record.Id} could not be bound to {vault} (see the error log)");
                }
            }

            LogUtility.Log($"[FROST KeyStore] Session-id key shares: {result.Records} record(s), {result.Bound} bound now, {result.FiledUnderContract} already under a contract, {result.NoVault} with no vault, {result.Ambiguous} ambiguous, {result.Failed} failed.",
                "FrostKeyShareBinding.BindSessionShares()");
            return result;
        }

        /// <summary>
        /// An operator's decision for a share <see cref="BindSessionShares"/> left alone: file record
        /// <paramref name="recordId"/> under <paramref name="scUid"/>. Only a share still under a session id, and only onto
        /// a contract on chain that carries the share's group key.
        /// </summary>
        public static (bool Ok, string Message) BindByOperator(long recordId, string? scUid)
        {
            if (string.IsNullOrWhiteSpace(scUid)) return (false, "scUID required");
            var record = FrostValidatorKeyStore.GetAllKeyPackages().FirstOrDefault(r => r.Id == recordId);
            if (record == null) return (false, $"no key record with id {recordId}");
            if (string.Equals(record.SmartContractUID, scUid, StringComparison.Ordinal)) return (true, "already filed under that contract");
            List<(string ScUid, string GroupKey)> vaults;
            try { vaults = OnChainVaultKeys(); }
            catch (Exception ex) { return (false, $"chain state could not be read: {ex.Message}"); }
            if (vaults.Any(v => v.ScUid == record.SmartContractUID) || FrostDkgGuard.IsContractUid(record.SmartContractUID))
                return (false, $"record {recordId} is filed under contract {record.SmartContractUID}; a share filed under a contract is never relabelled");
            var target = vaults.FirstOrDefault(v => v.ScUid == scUid);
            if (target.ScUid == null) return (false, $"contract {scUid} is not a vBTC V2 contract on chain");
            if (NormalizeKey(target.GroupKey).Length == 0 || NormalizeKey(target.GroupKey) != NormalizeKey(record.GroupPublicKey))
                return (false, $"contract {scUid} carries group key {target.GroupKey}; the record's is {record.GroupPublicKey}");
            if (!FrostValidatorKeyStore.UpdateSmartContractUID(record.Id, scUid))
                return (false, "the record could not be relabelled (see the error log)");
            LogUtility.Log($"[FROST KeyStore] Operator bound key share {recordId} to {scUid} (was filed under {record.SmartContractUID}).", "FrostKeyShareBinding.BindByOperator()");
            return (true, $"key share {recordId} bound to {scUid}");
        }

        private static int _ran;

        /// <summary>
        /// Waits until the node is at the network's height, then runs <see cref="BindSessionShares"/> once for the process.
        /// Not at raw startup: a node still catching up has not seen every contract, and "the one vault that carries this
        /// key" must be answered from current state.
        /// </summary>
        public static async Task<BindResult?> RunOnceWhenSyncedAsync(CancellationToken cancellationToken = default, int pollMilliseconds = 30_000)
        {
            try
            {
                while (!Globals.IsChainSynced)
                    await Task.Delay(pollMilliseconds, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _ran, 1) == 1)
                    return null;
                return BindSessionShares();
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Session-id key share binding failed: {ex}", "FrostKeyShareBinding.RunOnceWhenSyncedAsync()");
                return null;
            }
        }

        internal static void ResetForTests() => Interlocked.Exchange(ref _ran, 0);
    }
}
