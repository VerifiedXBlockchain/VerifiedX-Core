using LiteDB;
using VerifiedXCore.Data;

namespace VerifiedXCore.Bitcoin.Attestation
{
    // Consensus state for Bitcoin facts (docs/PLAN-vBTC-Bitcoin-Attestation.md, section 4). Every row here is derived
    // from mined transactions only, so it is wiped and rebuilt by a state replay, copied by snapshots and restored with
    // them. Rows are never deleted (a status changes instead): the snapshot layer copies these tables by the
    // LastModifiedHeight stamp, and a stamp-based diff cannot see a deletion.

    /// <summary>One attested Bitcoin header. The id is the Bitcoin height.</summary>
    public sealed class AttestedHeaderRow
    {
        [BsonId(false)] public long Height { get; set; }
        public string Hash { get; set; } = "";
        /// <summary>The 80 bytes, hex.</summary>
        public string Hex { get; set; } = "";
        /// <summary>False once a deeper Bitcoin reorganisation replaced this height's block (plan section 7).</summary>
        public bool Active { get; set; } = true;
        /// <summary>The VFX height whose block attested it (the activation height for the checkpoint headers).</summary>
        public long AttestedAtVfxHeight { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    /// <summary>
    /// A vault's Bitcoin deposit script. The id is the scriptPubKey, hex. A row is written when a vault is created at or
    /// after the recording height; a vault with no row predates this layer (a legacy vault, plan 8.2): its coins are
    /// not known to consensus and it is frozen at enforcement.
    /// </summary>
    public sealed class VaultDepositScriptRow
    {
        [BsonId] public string ScriptHex { get; set; } = "";
        public string SmartContractUID { get; set; } = "";
        public string DepositAddress { get; set; } = "";
        public long CreatedAtVfxHeight { get; set; }
        /// <summary>
        /// The first Bitcoin height whose blocks count for this vault: a little before the events-through height at its
        /// creation (<see cref="BtcAttestRules.WatchBackBlocks"/>), so a deposit sent before the creation transaction was
        /// mined can still be reported.
        /// </summary>
        public long WatchFromBitcoinHeight { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    public enum VaultCoinStatus
    {
        Unspent = 0,
        /// <summary>Committed to a withdrawal or bridge exit by a mined pin; still in the vault.</summary>
        Pinned = 1,
        Spent = 2,
    }

    /// <summary>One Bitcoin output held by a vault. The id is the outpoint, "txid:vout", lowercase.</summary>
    public sealed class VaultCoinRow
    {
        [BsonId] public string Outpoint { get; set; } = "";
        public string SmartContractUID { get; set; } = "";
        public long ValueSats { get; set; }
        public VaultCoinStatus Status { get; set; }
        /// <summary>Bitcoin height of the block that created it.</summary>
        public long CreatedInHeight { get; set; }
        /// <summary>A coinbase output cannot be spent, so cannot be pinned, for 100 blocks.</summary>
        public bool IsCoinbase { get; set; }
        public string? SpentByTxid { get; set; }
        public long SpentInHeight { get; set; }
        /// <summary>The withdrawal request or bridge-exit reference the coin is pinned to.</summary>
        public string? PinnedToRef { get; set; }
        /// <summary>
        /// The vault's UID while the coin is Unspent, otherwise null. Indexed, so a vault's free coins are found
        /// without reading every coin it ever held. Set by <see cref="BtcAttestStore.SaveCoin"/> from the status.
        /// </summary>
        public string? FreeOf { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    /// <summary>A vault's attested Bitcoin, kept so a balance is one lookup. The id is the contract UID.</summary>
    public sealed class VaultDepositTotalRow
    {
        [BsonId] public string SmartContractUID { get; set; } = "";
        public long UnspentSats { get; set; }
        public long PinnedSats { get; set; }
        public long LastModifiedHeight { get; set; }

        /// <summary>What the vault holds on Bitcoin: unspent coins and coins pinned to a withdrawal not yet confirmed.</summary>
        public long HeldSats => UnspentSats + PinnedSats;
    }

    /// <summary>The single row describing where the attested view of Bitcoin stands.</summary>
    public sealed class AttestationMetaRow
    {
        public const int TheId = 1;
        [BsonId(false)] public int Id { get; set; } = TheId;
        public long CheckpointHeight { get; set; }
        public long TipHeight { get; set; }
        public string TipHash { get; set; } = "";
        /// <summary>Every Bitcoin block up to this height has had its vault events reported.</summary>
        public long EventsThroughHeight { get; set; }
        /// <summary>A multi-part forward report in progress: its Bitcoin block (0 when none).</summary>
        public long PendingPartBlockHeight { get; set; }
        /// <summary>The next part expected (0 when none).</summary>
        public int PendingNextPart { get; set; }
        /// <summary>The block position of the last event applied from the parts so far (parts continue in block order).</summary>
        public long PendingLastPosition { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    /// <summary>The validators whose signatures count in one epoch (plan 3.5). The id is the epoch number.</summary>
    public sealed class AttestationSignerSetRow
    {
        [BsonId(false)] public long Epoch { get; set; } // never auto-numbered: epoch 0 is a real id
        /// <summary>The VFX height at whose first moment the set was taken.</summary>
        public long AtVfxHeight { get; set; }
        /// <summary>Validator addresses, ordinal order.</summary>
        public List<string> Addresses { get; set; } = new();
        public long LastModifiedHeight { get; set; }
    }

    public enum VaultWithdrawalState
    {
        /// <summary>Requested; the vault's free coins cannot pay it yet. No transaction is fixed and nothing is signed.</summary>
        Waiting = 0,
        /// <summary>Its Bitcoin transaction is fixed and its coins are held for it.</summary>
        Pinned = 1,
        /// <summary>Bitcoin shows its transaction: completed.</summary>
        Settled = 2,
        /// <summary>A pinned coin was spent by another transaction; closed and refunded.</summary>
        Void = 3,
        /// <summary>Never pinned within the window; closed and refunded.</summary>
        Expired = 4,
        /// <summary>Cancelled by its requester before it was pinned; refunded.</summary>
        Cancelled = 5,
    }

    /// <summary>One Bitcoin transaction fixed for a withdrawal.</summary>
    public sealed class VaultPin
    {
        public string Txid { get; set; } = "";
        /// <summary>The unsigned transaction without witness data: what the vault's signers sign.</summary>
        public string TxHex { get; set; } = "";
        public List<string> Inputs { get; set; } = new();
        public long DestinationSats { get; set; }
        public long ChangeSats { get; set; }
        public long FeeSats { get; set; }
        public long PinnedAtVfxHeight { get; set; }
        /// <summary>One of its coins was spent by another transaction: it can never confirm.</summary>
        public bool Dead { get; set; }
    }

    /// <summary>
    /// A withdrawal from a watched vault as consensus follows it (plan section 5). The id is the request's transaction
    /// hash and the contract ("hash|scUID"): a multi-vault request has one row per vault.
    /// </summary>
    public sealed class VaultWithdrawalRow
    {
        [BsonId] public string Ref { get; set; } = "";
        public string SmartContractUID { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string Requester { get; set; } = "";
        public long AmountSats { get; set; }
        public string DestinationScriptHex { get; set; } = "";
        public long FeeRate { get; set; }
        public long RequestVfxHeight { get; set; }
        public VaultWithdrawalState State { get; set; }
        public List<VaultPin> Pins { get; set; } = new();
        public string? SettledTxid { get; set; }
        public long ClosedAtVfxHeight { get; set; }
        /// <summary>Why it is not pinned, or why it was closed. For people; nothing reads it.</summary>
        public string? WaitingReason { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    /// <summary>
    /// Reads and writes of the attestation tables. Every write goes through here and is stamped with the height of the
    /// block being applied (<see cref="StateWriteContext.StampHeight"/>), which is what the snapshot diff copies by.
    /// Methods take the database so a replay into a scratch database uses the same code as the live node.
    /// </summary>
    public static class BtcAttestStore
    {
        public const string Headers = "btc_attested_headers";
        public const string Scripts = "btc_vault_scripts";
        public const string Coins = "btc_vault_coins";
        public const string Totals = "btc_vault_totals";
        public const string Meta = "btc_attest_meta";
        public const string SignerSets = "btc_attest_signers";
        public const string Withdrawals = "btc_vault_withdrawals";

        /// <summary>Every collection of this layer (state wipe, chain-reference migration, snapshot list).</summary>
        public static readonly string[] AllCollections = { Headers, Scripts, Coins, Totals, Meta, SignerSets, Withdrawals };

        public static LiteDatabase Db => DbContext.DB_BtcAttest;

        public static void EnsureIndexes(LiteDatabase db)
        {
            db.GetCollection<AttestedHeaderRow>(Headers).EnsureIndex(x => x.LastModifiedHeight);
            var scripts = db.GetCollection<VaultDepositScriptRow>(Scripts);
            scripts.EnsureIndex(x => x.SmartContractUID);
            scripts.EnsureIndex(x => x.LastModifiedHeight);
            var coins = db.GetCollection<VaultCoinRow>(Coins);
            coins.EnsureIndex(x => x.SmartContractUID);
            coins.EnsureIndex(x => x.FreeOf);
            coins.EnsureIndex(x => x.LastModifiedHeight);
            db.GetCollection<VaultDepositTotalRow>(Totals).EnsureIndex(x => x.LastModifiedHeight);
            db.GetCollection<AttestationSignerSetRow>(SignerSets).EnsureIndex(x => x.LastModifiedHeight);
            var withdrawals = db.GetCollection<VaultWithdrawalRow>(Withdrawals);
            withdrawals.EnsureIndex(x => x.SmartContractUID);
            withdrawals.EnsureIndex(x => x.State);
            withdrawals.EnsureIndex(x => x.LastModifiedHeight);
        }

        // ── Meta ────────────────────────────────────────────────────────────────────────────────────────────

        public static AttestationMetaRow? GetMeta(LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<AttestationMetaRow>(Meta).FindById(AttestationMetaRow.TheId);

        public static void SaveMeta(AttestationMetaRow meta, LiteDatabase? db = null)
        {
            meta.Id = AttestationMetaRow.TheId;
            meta.LastModifiedHeight = StateWriteContext.StampHeight;
            (db ?? Db).GetCollection<AttestationMetaRow>(Meta).Upsert(meta);
        }

        // ── Headers ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The attested, still active header at a Bitcoin height, parsed; null when there is none.</summary>
        public static BtcHeader? HeaderAt(long height, LiteDatabase? db = null)
        {
            var row = (db ?? Db).GetCollection<AttestedHeaderRow>(Headers).FindById(height);
            if (row == null || !row.Active) return null;
            return BtcHeader.TryParse(row.Hex, out var header) ? header : null;
        }

        public static void SaveHeader(long height, BtcHeader header, long attestedAtVfxHeight, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<AttestedHeaderRow>(Headers).Upsert(new AttestedHeaderRow
            {
                Height = height, Hash = header.Hash, Hex = header.Hex, Active = true,
                AttestedAtVfxHeight = attestedAtVfxHeight, LastModifiedHeight = StateWriteContext.StampHeight,
            });

        // ── Vault scripts ───────────────────────────────────────────────────────────────────────────────────

        public static VaultDepositScriptRow? ScriptRow(string? scriptHex, LiteDatabase? db = null) =>
            string.IsNullOrEmpty(scriptHex) ? null : (db ?? Db).GetCollection<VaultDepositScriptRow>(Scripts).FindById(scriptHex.ToLowerInvariant());

        public static VaultDepositScriptRow? ScriptRowForVault(string? scUID, LiteDatabase? db = null) =>
            string.IsNullOrEmpty(scUID) ? null : (db ?? Db).GetCollection<VaultDepositScriptRow>(Scripts).FindOne(x => x.SmartContractUID == scUID);

        public static void SaveScript(VaultDepositScriptRow row, LiteDatabase? db = null)
        {
            row.ScriptHex = row.ScriptHex.ToLowerInvariant();
            row.LastModifiedHeight = StateWriteContext.StampHeight;
            (db ?? Db).GetCollection<VaultDepositScriptRow>(Scripts).Upsert(row);
        }

        // ── Coins and totals ────────────────────────────────────────────────────────────────────────────────

        public static string OutpointKey(string txid, long vout) => $"{txid}:{vout}".ToLowerInvariant();

        public static VaultCoinRow? Coin(string? outpoint, LiteDatabase? db = null) =>
            string.IsNullOrEmpty(outpoint) ? null : (db ?? Db).GetCollection<VaultCoinRow>(Coins).FindById(outpoint.ToLowerInvariant());

        public static List<VaultCoinRow> CoinsOf(string scUID, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultCoinRow>(Coins).Find(x => x.SmartContractUID == scUID).ToList();

        /// <summary>The vault's Unspent coins (not pinned, not spent).</summary>
        public static List<VaultCoinRow> FreeCoinsOf(string scUID, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultCoinRow>(Coins).Find(x => x.FreeOf == scUID).Where(c => c.Status == VaultCoinStatus.Unspent && c.SmartContractUID == scUID).ToList();

        public static VaultDepositTotalRow Total(string scUID, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultDepositTotalRow>(Totals).FindById(scUID) ?? new VaultDepositTotalRow { SmartContractUID = scUID };

        /// <summary>
        /// Writes a coin and moves its vault's totals by the difference between the coin's previous and new status, so the
        /// totals can never drift from the coins. <paramref name="previous"/> is the row as read before the change (null
        /// for a new coin).
        /// </summary>
        public static void SaveCoin(VaultCoinRow coin, VaultCoinRow? previous, LiteDatabase? db = null)
        {
            var d = db ?? Db;
            coin.Outpoint = coin.Outpoint.ToLowerInvariant();
            coin.FreeOf = coin.Status == VaultCoinStatus.Unspent ? coin.SmartContractUID : null;
            coin.LastModifiedHeight = StateWriteContext.StampHeight;
            d.GetCollection<VaultCoinRow>(Coins).Upsert(coin);

            long unspent = 0, pinned = 0;
            if (previous != null)
            {
                if (previous.Status == VaultCoinStatus.Unspent) unspent -= previous.ValueSats;
                else if (previous.Status == VaultCoinStatus.Pinned) pinned -= previous.ValueSats;
            }
            if (coin.Status == VaultCoinStatus.Unspent) unspent += coin.ValueSats;
            else if (coin.Status == VaultCoinStatus.Pinned) pinned += coin.ValueSats;
            if (unspent == 0 && pinned == 0) return;

            var total = Total(coin.SmartContractUID, d);
            total.UnspentSats += unspent;
            total.PinnedSats += pinned;
            total.LastModifiedHeight = StateWriteContext.StampHeight;
            d.GetCollection<VaultDepositTotalRow>(Totals).Upsert(total);
        }

        // ── Signer sets ─────────────────────────────────────────────────────────────────────────────────────

        public static AttestationSignerSetRow? SignerSet(long epoch, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<AttestationSignerSetRow>(SignerSets).FindById(epoch);

        public static void SaveSignerSet(AttestationSignerSetRow row, LiteDatabase? db = null)
        {
            row.Addresses = row.Addresses.Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
            row.LastModifiedHeight = StateWriteContext.StampHeight;
            (db ?? Db).GetCollection<AttestationSignerSetRow>(SignerSets).Upsert(row);
        }

        // ── Withdrawals ─────────────────────────────────────────────────────────────────────────────────────

        public static VaultWithdrawalRow? Withdrawal(string? reference, LiteDatabase? db = null) =>
            string.IsNullOrEmpty(reference) ? null : (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).FindById(reference);

        public static void SaveWithdrawal(VaultWithdrawalRow row, LiteDatabase? db = null)
        {
            row.LastModifiedHeight = StateWriteContext.StampHeight;
            (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).Upsert(row);
        }

        public static List<VaultWithdrawalRow> WithdrawalsOf(string scUID, VaultWithdrawalState state, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).Find(x => x.SmartContractUID == scUID && x.State == state).ToList();

        public static List<VaultWithdrawalRow> WithdrawalsInState(VaultWithdrawalState state, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).Find(x => x.State == state).ToList();

        /// <summary>The requests still waiting for a pin that were mined before <paramref name="vfxHeight"/>.</summary>
        public static List<VaultWithdrawalRow> WaitingWithdrawalsRequestedBefore(long vfxHeight, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).Find(x => x.State == VaultWithdrawalState.Waiting && x.RequestVfxHeight < vfxHeight).ToList();

        /// <summary>Empties every table of this layer (state wipe before a replay; chain-reference migration).</summary>
        public static void WipeAll(LiteDatabase? db = null)
        {
            var d = db ?? Db;
            if (d == null) return;
            foreach (var name in AllCollections)
                d.GetCollection(name).DeleteAll();
        }
    }
}
