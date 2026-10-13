using LiteDB;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>A validator's bond as chain state holds it. The id is the validator's address.</summary>
    public sealed class ValidatorBondRow
    {
        [BsonId] public string Address { get; set; } = "";
        /// <summary>The block whose registration or heartbeat started this bond.</summary>
        public long BondedAtHeight { get; set; }
        /// <summary>The block that mined its exit; 0 while it has not exited.</summary>
        public long ExitHeight { get; set; }
        /// <summary>
        /// The first block at which the address was seen bonded and holding the validator balance (0 until then).
        /// From that block on the balance cannot fall below it while the bond lasts, so this is how long the stake
        /// has really been locked: what the minimum age for a key ceremony counts from.
        /// </summary>
        public long FundedSinceHeight { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    /// <summary>
    /// The validator stake is bonded (owner decision, 13 October 2026). A registered validator's 5,000 VFX cannot be
    /// sent while it is registered, and stays locked for <see cref="ExitWaitBlocks"/> blocks after its exit is mined.
    ///
    /// Before this the balance was only looked at: when a validator registered or sent a heartbeat, when it produced
    /// a block, when a vault's key ceremony was judged and when an epoch's Bitcoin report signers were listed. For
    /// block production that is enough, since it is looked at every round. The vBTC roles are decided at one block
    /// and then last: a vault's key shares for good, a place among the report signers for up to two epochs. The stake
    /// was needed for that one block and could leave in the next.
    ///
    /// <list type="bullet">
    /// <item><b>Bond.</b> A mined registration or heartbeat bonds the sender. From then on no transaction of that
    /// address may take its balance below the validator amount: refused at admission, in a block, and for the
    /// address's transactions of one block taken together.</item>
    /// <item><b>Exit.</b> A mined exit starts the wait. The stake unlocks <see cref="ExitWaitBlocks"/> blocks later:
    /// two signer epochs, the longest a report signed under a set the validator was in is still accepted. Going quiet
    /// unlocks nothing; only the exit does. A registration or heartbeat during the wait resumes the bond.</item>
    /// <item><b>Age.</b> A validator counts for a key ceremony once its stake has been locked for
    /// <see cref="CeremonyAgeBlocks"/> blocks (two days), and only while it is still active
    /// (<see cref="CeremonyEligible"/>).</item>
    /// <item><b>Validators already active when the rule starts</b> are bonded at that block. Those holding the
    /// balance in the block after count as aged: they were validators before there was a bond to age.</item>
    /// </list>
    ///
    /// The rows live with the other chain-derived tables that are stamped by height, so a snapshot, a rollback and
    /// a replay treat them like any other state.
    /// </summary>
    public static class ValidatorBond
    {
        /// <summary>VFX blocks the stake stays locked after the exit is mined: two report-signer epochs (about four and a half hours).</summary>
        public const int ExitWaitBlocks = 1_200;
        /// <summary>VFX blocks a stake must have been locked before its validator counts for a key ceremony: two days at twelve seconds a block.</summary>
        public const int CeremonyAgeBlocks = 14_400;
        /// <summary>How often bonds not yet seen funded are looked at again, in blocks.</summary>
        public const int FundedCheckBlocks = 100;

        public const string Collection = "validator_bonds";
        private const string SeededMarker = "#seeded";

        private static LiteDatabase Db => DbContext.DB_BtcAttest;
        private static ILiteCollection<ValidatorBondRow> Rows(LiteDatabase? db) => (db ?? Db).GetCollection<ValidatorBondRow>(Collection);

        public static void EnsureIndexes(LiteDatabase db)
        {
            var rows = db.GetCollection<ValidatorBondRow>(Collection);
            rows.EnsureIndex(x => x.FundedSinceHeight);
            rows.EnsureIndex(x => x.LastModifiedHeight);
        }

        /// <summary>Whether a row of this table was written by a block above <paramref name="vfxHeight"/> (the rollback guard).</summary>
        public static bool ChangedAbove(long vfxHeight, LiteDatabase? db = null) =>
            Rows(db).Exists(x => x.LastModifiedHeight > vfxHeight);

        public static bool Active(long height) => height >= Globals.ValidatorBondHeight;

        private static decimal Stake => ValidatorService.ValidatorRequiredAmount();

        /// <summary>The bond of an address, or null when it has none. (The store's lookups ignore case; an address is matched exactly.)</summary>
        public static ValidatorBondRow? Row(string? address, LiteDatabase? db = null)
        {
            if (string.IsNullOrEmpty(address) || address == SeededMarker || (db == null && Db == null)) return null;
            var row = Rows(db).FindById(address);
            return row != null && string.Equals(row.Address, address, StringComparison.Ordinal) ? row : null;
        }

        private static void Save(ValidatorBondRow row, LiteDatabase? db)
        {
            row.LastModifiedHeight = StateWriteContext.StampHeight;
            Rows(db).Upsert(row);
        }

        /// <summary>The first block at which an exited validator's stake is free again; 0 while it has not exited.</summary>
        public static long UnlockHeight(ValidatorBondRow row) => row.ExitHeight == 0 ? 0 : row.ExitHeight + ExitWaitBlocks;

        /// <summary>Whether the bond holds the stake for a transaction mined at <paramref name="height"/>.</summary>
        public static bool Holds(ValidatorBondRow? row, long height) =>
            row != null && (row.ExitHeight == 0 || height < row.ExitHeight + ExitWaitBlocks);

        /// <summary>The VFX of an address that a transaction mined at <paramref name="height"/> may not touch.</summary>
        public static decimal LockedAmount(string? address, long height, LiteDatabase? db = null) =>
            Active(height) && Holds(Row(address, db), height) ? Stake : 0M;

        // ── Block application ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Before a block's transactions. At the first block under the rule, every validator active at the block
        /// before is bonded. In the next block, and then every <see cref="FundedCheckBlocks"/> blocks, bonds not yet
        /// seen funded are looked at against the balances the previous block left.
        /// </summary>
        public static void OnBlockStart(long height, LiteDatabase? db = null)
        {
            if (!Active(height)) return;
            try
            {
                var marker = Rows(db).FindById(SeededMarker);
                if (marker == null)
                {
                    // The first block under the rule. The validators active before it are bonded here; whether each
                    // holds its stake is looked at in the next block, once this block's own transactions (which were
                    // judged before any bond existed) have been applied.
                    foreach (var validator in VBTCValidatorRegistry.GetActiveValidatorsAt(height - 1)
                                 .Where(v => !string.IsNullOrEmpty(v.ValidatorAddress)).OrderBy(v => v.ValidatorAddress, StringComparer.Ordinal))
                    {
                        if (Row(validator.ValidatorAddress, db) == null)
                            Save(new ValidatorBondRow { Address = validator.ValidatorAddress, BondedAtHeight = height }, db);
                    }
                    Save(new ValidatorBondRow { Address = SeededMarker, BondedAtHeight = height }, db);
                    LogUtility.Log($"Validator bonds start at block {height}: every validator active at block {height - 1} is bonded.", "ValidatorBond.OnBlockStart()");
                    return;
                }

                // A bond is seen funded at the start of a block, against the balances the block before left. The bond
                // was already in state when that block was judged, so from here the balance cannot fall below the
                // stake. Looked at in the first block after the rule starts, then every FundedCheckBlocks blocks.
                var firstLook = marker.FundedSinceHeight == 0;
                if (!firstLook && height % FundedCheckBlocks != 0) return;
                foreach (var row in Rows(db).Find(x => x.FundedSinceHeight == 0).Where(r => r.Address != SeededMarker)
                             .OrderBy(r => r.Address, StringComparer.Ordinal).ToList())
                {
                    if (!Holds(row, height) || row.BondedAtHeight >= height || !VBTCValidatorRegistry.HoldsValidatorBalance(row.Address)) continue;
                    // A validator from before the rule that holds its stake at this first look counts as aged: it was
                    // a validator before there was a bond to age. Everyone after that waits the full time.
                    row.FundedSinceHeight = firstLook && row.BondedAtHeight == marker.BondedAtHeight ? height - CeremonyAgeBlocks : height;
                    Save(row, db);
                }
                if (firstLook)
                {
                    marker.FundedSinceHeight = height;
                    Save(marker, db);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Validator bond block-start step failed at {height}: {ex}", "ValidatorBond.OnBlockStart()");
            }
        }

        /// <summary>A mined registration or heartbeat bonds its sender; a mined exit starts the wait.</summary>
        public static void OnTransactionApplied(Transaction tx, long height, LiteDatabase? db = null)
        {
            if (tx == null || !Active(height) || string.IsNullOrEmpty(tx.FromAddress)) return;
            try
            {
                switch (tx.TransactionType)
                {
                    case TransactionType.VBTC_V2_VALIDATOR_REGISTER:
                    case TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT:
                    {
                        var row = Row(tx.FromAddress, db);
                        if (row != null && row.ExitHeight == 0) return;          // bonded already: nothing changes
                        if (row != null && Holds(row, height))
                        {
                            row.ExitHeight = 0;                                    // in its exit wait: the bond resumes, the stake never left
                            Save(row, db);
                            return;
                        }
                        Save(new ValidatorBondRow { Address = tx.FromAddress, BondedAtHeight = height }, db); // a new bond (or one after a full exit)
                        return;
                    }
                    case TransactionType.VBTC_V2_VALIDATOR_EXIT:
                    {
                        var row = Row(tx.FromAddress, db);
                        if (row == null || row.ExitHeight != 0) return;
                        row.ExitHeight = height;
                        Save(row, db);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Validator bond could not follow {tx.TransactionType} {tx.Hash}: {ex}", "ValidatorBond.OnTransactionApplied()");
            }
        }

        // ── Rules ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Why a transaction mined at <paramref name="height"/> is refused because it would take a bonded validator's
        /// balance below its stake; null when it is not. The VFX a transaction takes from its sender is read the way
        /// its apply takes it (<see cref="SameBlockDebitGuard.GetDebits"/>), so a sale's inner payments and a shield
        /// count like a plain send.
        /// </summary>
        public static string? SpendRefusal(Transaction tx, long height)
        {
            if (tx == null || !Active(height) || string.IsNullOrEmpty(tx.FromAddress)) return null;
            var row = Row(tx.FromAddress);
            if (!Holds(row, height)) return null;
            var spend = SameBlockDebitGuard.GetDebits(tx).Where(d => d.Key.Kind == SameBlockDebitGuard.LedgerKind.Native).Sum(d => d.Amount);
            if (spend <= 0M) return null;
            var account = StateData.GetSpecificAccountStateTrei(tx.FromAddress);
            if (account == null || account.Balance - Stake >= spend) return null;

            var free = Math.Max(0M, account.Balance - Stake);
            return row!.ExitHeight == 0
                ? $"{tx.FromAddress} is a registered validator: {Stake} VFX of its balance is bonded and cannot be sent. It can send {free} VFX now. To free the stake, stop validating (the validator exit transaction); it unlocks {ExitWaitBlocks} blocks after the exit is mined."
                : $"{tx.FromAddress} has exited as a validator at block {row.ExitHeight}: its {Stake} VFX stake stays bonded until block {UnlockHeight(row)}. It can send {free} VFX now.";
        }

        /// <summary>Whether the stake of an address has been locked for the minimum age, as of a transaction mined at <paramref name="height"/>.</summary>
        public static bool IsAged(string? address, long height, LiteDatabase? db = null)
        {
            var row = Row(address, db);
            return row != null && row.ExitHeight == 0 && row.FundedSinceHeight != 0 && height - row.FundedSinceHeight >= CeremonyAgeBlocks;
        }

        /// <summary>
        /// The validators that count for a vault's key ceremony judged at <paramref name="height"/>, out of those given
        /// (the active set): holding the validator balance now and, from the bond height, with a stake locked for the
        /// minimum age. The list a ceremony is checked against, and the list a coordinator picks from.
        /// </summary>
        public static List<VBTCValidator> CeremonyEligible(IEnumerable<VBTCValidator> active, long height)
        {
            var funded = VBTCValidatorRegistry.FundedOnly(active);
            return Active(height) ? funded.Where(v => IsAged(v.ValidatorAddress, height)).ToList() : funded;
        }

        /// <summary>What the API shows for an address.</summary>
        public static object Describe(string address)
        {
            var next = (Globals.LastBlock?.Height ?? 0) + 1;
            var row = Row(address);
            var account = StateData.GetSpecificAccountStateTrei(address);
            var held = Holds(row, next) && Active(next);
            return new
            {
                Address = address,
                RuleActive = Active(next),
                RuleStartsAtHeight = Globals.ValidatorBondHeight < long.MaxValue / 2 ? Globals.ValidatorBondHeight : (long?)null,
                Bonded = held,
                BondedAmount = held ? Stake : 0M,
                Balance = account?.Balance ?? 0M,
                Spendable = account == null ? 0M : Math.Max(0M, account.Balance - (held ? Stake : 0M)),
                BondedAtHeight = row?.BondedAtHeight,
                ExitHeight = row == null || row.ExitHeight == 0 ? (long?)null : row.ExitHeight,
                UnlocksAtHeight = row == null || row.ExitHeight == 0 ? (long?)null : UnlockHeight(row),
                ExitWaitBlocks,
                StakeLockedSinceHeight = row == null || row.FundedSinceHeight == 0 ? (long?)null : row.FundedSinceHeight,
                CountsForKeyCeremoniesFromHeight = row == null || row.ExitHeight != 0 || row.FundedSinceHeight == 0 ? (long?)null : row.FundedSinceHeight + CeremonyAgeBlocks,
                CountsForKeyCeremoniesNow = IsAged(address, next),
                Note = !Active(next) ? "The validator bond is not in force yet."
                    : row == null ? "This address has no validator bond."
                    : row.ExitHeight != 0 && held ? $"Exited: the stake unlocks at block {UnlockHeight(row)}."
                    : row.ExitHeight != 0 ? "Exited and unlocked."
                    : row.FundedSinceHeight == 0 ? $"Bonded, but not yet seen holding {Stake} VFX: it counts for nothing until it does."
                    : "Bonded.",
            };
        }
    }
}
