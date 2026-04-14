using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Services
{
    /// <summary>
    /// Canonical validator snapshot for caster consensus (deterministic across nodes with same chain + VBTC validator DB).
    /// </summary>
    public static class ValidatorSnapshotService
    {
        public const long SnapshotPeriodBlocks = 500;
        public const long StaleThresholdBlocks = 1500;

        private static readonly object SnapshotLock = new object();
        private static List<ValidatorSnapshotEntry> _snapshotEntries = new List<ValidatorSnapshotEntry>();
        private static long _snapshotHeight = -1;

        public static List<ValidatorSnapshotEntry> CurrentSnapshot
        {
            get { lock (SnapshotLock) { return _snapshotEntries; } }
        }

        public static long SnapshotHeight
        {
            get { lock (SnapshotLock) { return _snapshotHeight; } }
        }

        /// <summary>
        /// Anchor height S for the snapshot used when producing block at height <paramref name="nextBlockHeight"/>.
        /// </summary>
        public static long SnapshotAnchor(long nextBlockHeight)
        {
            var parent = nextBlockHeight - 1;
            if (parent < 0)
                return 0;
            return parent - parent % SnapshotPeriodBlocks;
        }

        /// <summary>
        /// Rebuild snapshot from VBTC validator registry + account state. Call when anchor boundary crossed or snapshot missing.
        /// </summary>
        public static Task BuildSnapshot(long nextBlockHeight)
        {
            var tip = Globals.LastBlock.Height;
            if (tip < 0)
                tip = 0;

            var anchor = SnapshotAnchor(nextBlockHeight);
            var candidates = Bitcoin.Services.VBTCValidatorRegistry.GetActiveValidators();

            var list = new List<ValidatorSnapshotEntry>();
            foreach (var v in candidates.OrderBy(x => x.ValidatorAddress, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(v.ValidatorAddress))
                    continue;

                var pk = v.FrostPublicKey;
                if (string.IsNullOrWhiteSpace(pk) || pk == "PLACEHOLDER_FROST_PUBLIC_KEY")
                    continue;

                var state = StateData.GetSpecificAccountStateTrei(v.ValidatorAddress);
                if (state == null || state.Balance < ValidatorService.ValidatorRequiredAmount())
                    continue;

                list.Add(new ValidatorSnapshotEntry
                {
                    Address = v.ValidatorAddress,
                    PublicKey = pk,
                    IPAddress = (v.IPAddress ?? "").Replace("::ffff:", ""),
                    Balance = state.Balance,
                    LastHeartbeatHeight = v.LastHeartbeatBlock
                });
            }

            lock (SnapshotLock)
            {
                _snapshotEntries = list;
                _snapshotHeight = anchor;
            }

            ProofUtility.ClearProofGenerationCache();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns the snapshot entries for the anchor of <paramref name="nextBlockHeight"/>.
        /// </summary>
        public static List<ValidatorSnapshotEntry> GetSnapshotForHeight(long nextBlockHeight)
        {
            var anchor = SnapshotAnchor(nextBlockHeight);
            lock (SnapshotLock)
            {
                if (_snapshotEntries != null && _snapshotHeight == anchor)
                    return _snapshotEntries;
                return _snapshotEntries ?? new List<ValidatorSnapshotEntry>();
            }
        }

        public static Task RefreshSnapshotIfNeededAsync(long nextBlockHeight)
        {
            var parentH = nextBlockHeight - 1;
            if (SnapshotHeight < 0 || (parentH >= 0 && parentH % SnapshotPeriodBlocks == 0))
                return BuildSnapshot(nextBlockHeight);
            return Task.CompletedTask;
        }
    }
}
