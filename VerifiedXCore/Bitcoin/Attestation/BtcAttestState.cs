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
    ///
    /// A vault can have more than one: each is one key (plan 8.8). The key its creation's ceremony made is generation
    /// 0; when too many of a key's holders have left, the vault is moved to a new key made by the validators of the
    /// day (a re-key), which is the next generation, and its coins are sent there. The vault's current key is its
    /// newest generation. Every coin records the script it is at, so each is signed by the holders of its own key.
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
        /// <summary>0 for the key of the vault's creation, one more for each re-key.</summary>
        public int Generation { get; set; }
        /// <summary>The UID the validators file this key's shares under: the vault's own UID for generation 0.</summary>
        public string? KeyUID { get; set; }
        /// <summary>The validators holding shares of this key (its ceremony's participants), ordinal order.</summary>
        public List<string>? Holders { get; set; }
        /// <summary>Shares that sign together with this key.</summary>
        public int MinSigners { get; set; }
        /// <summary>The key's public key, hex, as its ceremony made it.</summary>
        public string? GroupKeyHex { get; set; }
        /// <summary>The block a newer key replaced this one at; 0 while it is the vault's key.</summary>
        public long RetiredAtVfxHeight { get; set; }
        /// <summary>
        /// Holders of this key that were active validators with their stake bonded and no exit, at the last look
        /// (every epoch, <see cref="BtcVaultKeys.CheckHealth"/>); -1 before the first.
        /// </summary>
        public int HealthyHolders { get; set; } = -1;
        public long HealthCheckedAtVfxHeight { get; set; }
        /// <summary>The block from which the key had too few healthy holders to be kept (a re-key may be made); 0 while it has enough.</summary>
        public long RekeyDueSinceVfxHeight { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    public enum VaultCoinStatus
    {
        Unspent = 0,
        /// <summary>Committed to a withdrawal or bridge exit by a mined pin; still in the vault.</summary>
        Pinned = 1,
        Spent = 2,
        /// <summary>
        /// Created by a Bitcoin block that a deep reorganisation removed. The coin does not exist: it counts for
        /// nothing and is never selected. Rows are not deleted; if the same transaction confirms again the coin comes
        /// back under the same outpoint.
        /// </summary>
        Orphaned = 3,
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
        /// Other withdrawals whose fixed transaction spends this coin too: each took it from a withdrawal that had
        /// stopped moving (plan 5.5). Bitcoin lets one of those transactions confirm; the others are then void. Null
        /// when the coin is held for one withdrawal only, which is nearly always.
        /// </summary>
        public List<string>? AlsoPinnedTo { get; set; }
        /// <summary>
        /// The vault's UID while the coin is Unspent, otherwise null. Indexed, so a vault's free coins are found
        /// without reading every coin it ever held. Set by <see cref="BtcAttestStore.SaveCoin"/> from the status.
        /// </summary>
        public string? FreeOf { get; set; }
        /// <summary>The deposit script the coin pays (which of the vault's keys signs for it). Null: the vault's first script.</summary>
        public string? ScriptHex { get; set; }
        public long LastModifiedHeight { get; set; }
    }

    /// <summary>A vault's attested Bitcoin, kept so a balance is one lookup. The id is the contract UID.</summary>
    public sealed class VaultDepositTotalRow
    {
        [BsonId] public string SmartContractUID { get; set; } = "";
        public long UnspentSats { get; set; }
        public long PinnedSats { get; set; }
        /// <summary>
        /// Bitcoin this vault has paid out for bridge exits, in all. Added back to the owner's figure: the vBTC behind
        /// it was debited when it was locked for the bridge, so the Bitcoin leaving must not count against the owner again.
        /// </summary>
        public long SettledExitSats { get; set; }
        /// <summary>
        /// Bitcoin fees this vault has paid to send a withdrawal's coins back to itself at its requester's wish (a
        /// release), in all. Each was taken out of that requester's refund, so it is added back to the owner's figure:
        /// the Bitcoin left the vault once and must be counted against one party only.
        /// </summary>
        public long ReleaseFeeSats { get; set; }
        /// <summary>
        /// Bitcoin fees this vault has paid to move its coins to a new key (sweeps, plan 8.8), in all. Not added back:
        /// nobody else was charged for them, so they fall on the owner's figure.
        /// </summary>
        public long SweepFeeSats { get; set; }
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
        /// <summary>
        /// Its requester had its coins sent back to the vault, and Bitcoin shows that transaction: none of its payments
        /// can confirm any more. Closed and refunded, less the Bitcoin fee of sending the coins back.
        /// </summary>
        Released = 6,
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
        /// <summary>The fee rate this transaction was built at, sat/vB: the request's, or a later fee bump's.</summary>
        public long FeeRate { get; set; }
        public long PinnedAtVfxHeight { get; set; }
        /// <summary>One of its coins was spent by another transaction: it can never confirm.</summary>
        public bool Dead { get; set; }
        /// <summary>
        /// A later transaction of the same withdrawal pays a higher fee. This one spends the same coins and is still
        /// valid: if Bitcoin confirms it instead, it settles the withdrawal all the same.
        /// </summary>
        public bool Superseded { get; set; }
        /// <summary>
        /// Pays nobody: it sends the withdrawal's coins back to the vault's own deposit script. Fixed at the requester's
        /// wish to end a withdrawal that is not confirming. When Bitcoin shows it the withdrawal is closed and refunded.
        /// </summary>
        public bool IsRelease { get; set; }
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
        /// <summary>
        /// One vault's part of a bridge exit rather than a withdrawal request. The id is then "exit:exitTx@height|scUID";
        /// <see cref="RequestHash"/> is the exit transaction and <see cref="RequestVfxHeight"/> its block, which the
        /// parts of one exit share.
        /// </summary>
        public bool IsBridgeExit { get; set; }
        /// <summary>For a bridge exit: the Base burn it pays, as its exit record holds it.</summary>
        public string? ExitBurnHash { get; set; }
        /// <summary>
        /// A sweep: the vault's coins moving from a replaced key to its current one (plan 8.8). No requester, nothing
        /// debited or refunded; the id is "sweep:scUID|gN@height#n" and <see cref="DestinationScriptHex"/> is the
        /// current key's script.
        /// </summary>
        public bool IsSweep { get; set; }
        /// <summary>For a sweep: the script of the key it moves coins from.</summary>
        public string? SweepFromScriptHex { get; set; }
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
        public static readonly string[] AllCollections = { Headers, Scripts, Coins, Totals, Meta, SignerSets, Withdrawals, VerifiedXCore.Services.ValidatorBond.Collection };

        public static LiteDatabase Db => DbContext.DB_BtcAttest;

        public static void EnsureIndexes(LiteDatabase db)
        {
            db.GetCollection<AttestedHeaderRow>(Headers).EnsureIndex(x => x.LastModifiedHeight);
            var scripts = db.GetCollection<VaultDepositScriptRow>(Scripts);
            scripts.EnsureIndex(x => x.SmartContractUID);
            scripts.EnsureIndex(x => x.LastModifiedHeight);
            scripts.EnsureIndex(x => x.KeyUID);
            scripts.EnsureIndex(x => x.RetiredAtVfxHeight);
            var coins = db.GetCollection<VaultCoinRow>(Coins);
            coins.EnsureIndex(x => x.SmartContractUID);
            coins.EnsureIndex(x => x.FreeOf);
            coins.EnsureIndex(x => x.CreatedInHeight);
            coins.EnsureIndex(x => x.SpentInHeight);
            coins.EnsureIndex(x => x.LastModifiedHeight);
            db.GetCollection<VaultDepositTotalRow>(Totals).EnsureIndex(x => x.LastModifiedHeight);
            db.GetCollection<AttestationSignerSetRow>(SignerSets).EnsureIndex(x => x.LastModifiedHeight);
            var withdrawals = db.GetCollection<VaultWithdrawalRow>(Withdrawals);
            withdrawals.EnsureIndex(x => x.SmartContractUID);
            withdrawals.EnsureIndex(x => x.State);
            withdrawals.EnsureIndex(x => x.RequestHash);
            withdrawals.EnsureIndex(x => x.LastModifiedHeight);
            VerifiedXCore.Services.ValidatorBond.EnsureIndexes(db); // validator bonds are kept with the other height-stamped tables
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

        /// <summary>The vault's current key: its newest generation. Null when the chain does not watch the vault.</summary>
        public static VaultDepositScriptRow? ScriptRowForVault(string? scUID, LiteDatabase? db = null) =>
            string.IsNullOrEmpty(scUID) ? null : ScriptRowsForVault(scUID, db).LastOrDefault();

        /// <summary>Every key the vault has had, oldest first.</summary>
        public static List<VaultDepositScriptRow> ScriptRowsForVault(string? scUID, LiteDatabase? db = null) =>
            string.IsNullOrEmpty(scUID) ? new List<VaultDepositScriptRow>()
                : (db ?? Db).GetCollection<VaultDepositScriptRow>(Scripts).Find(x => x.SmartContractUID == scUID)
                    .OrderBy(r => r.Generation).ThenBy(r => r.ScriptHex, StringComparer.Ordinal).ToList();

        /// <summary>The key a coin is held under: the script it pays (the vault's first script for a coin recorded without one).</summary>
        public static VaultDepositScriptRow? ScriptRowOfCoin(VaultCoinRow? coin, LiteDatabase? db = null) =>
            coin == null ? null : (coin.ScriptHex != null ? ScriptRow(coin.ScriptHex, db) : ScriptRowsForVault(coin.SmartContractUID, db).FirstOrDefault());

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

        /// <summary>Every coin created or spent by a Bitcoin block above <paramref name="bitcoinHeight"/> (what a rewind to that height takes back).</summary>
        public static List<VaultCoinRow> CoinsTouchedAbove(long bitcoinHeight, LiteDatabase? db = null)
        {
            var coins = (db ?? Db).GetCollection<VaultCoinRow>(Coins);
            return coins.Find(x => x.CreatedInHeight > bitcoinHeight)
                .Concat(coins.Find(x => x.SpentInHeight > bitcoinHeight))
                .GroupBy(c => c.Outpoint, StringComparer.Ordinal).Select(g => g.First()).ToList();
        }

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

        public static void AddSettledExit(string scUID, long sats, LiteDatabase? db = null)
        {
            var d = db ?? Db;
            var total = Total(scUID, d);
            total.SettledExitSats += sats;
            total.LastModifiedHeight = StateWriteContext.StampHeight;
            d.GetCollection<VaultDepositTotalRow>(Totals).Upsert(total);
        }

        public static void AddReleaseFee(string scUID, long sats, LiteDatabase? db = null)
        {
            var d = db ?? Db;
            var total = Total(scUID, d);
            total.ReleaseFeeSats += sats;
            total.LastModifiedHeight = StateWriteContext.StampHeight;
            d.GetCollection<VaultDepositTotalRow>(Totals).Upsert(total);
        }

        public static void AddSweepFee(string scUID, long sats, LiteDatabase? db = null)
        {
            var d = db ?? Db;
            var total = Total(scUID, d);
            total.SweepFeeSats += sats;
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

        /// <summary>Every vault's part of one bridge exit (the exit transaction, and the block it was applied in).</summary>
        public static List<VaultWithdrawalRow> ExitParts(string exitTxHash, long exitVfxHeight, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).Find(x => x.RequestHash == exitTxHash)
                .Where(x => x.IsBridgeExit && x.RequestVfxHeight == exitVfxHeight).ToList();

        /// <summary>The requests still waiting for a pin that were mined before <paramref name="vfxHeight"/>.</summary>
        /// <summary>The withdrawals requested in one block that are still waiting.</summary>
        public static List<VaultWithdrawalRow> WaitingWithdrawalsRequestedAt(long vfxHeight, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).Find(x => x.State == VaultWithdrawalState.Waiting && x.RequestVfxHeight == vfxHeight).ToList();

        public static List<VaultWithdrawalRow> WaitingWithdrawalsRequestedBefore(long vfxHeight, LiteDatabase? db = null) =>
            (db ?? Db).GetCollection<VaultWithdrawalRow>(Withdrawals).Find(x => x.State == VaultWithdrawalState.Waiting && x.RequestVfxHeight < vfxHeight).ToList();

        /// <summary>
        /// Whether any row of this layer was written by a block above <paramref name="vfxHeight"/>. Every write here
        /// is stamped with its block's height, and some are made by a block that carries no transaction of this layer:
        /// at the start of a block an unpinned withdrawal or bridge exit past its window is closed, and at the start of
        /// an epoch the signer set is stored. A rollback that undoes blocks one transaction at a time would leave those
        /// writes behind; it asks this first.
        /// </summary>
        public static bool ChangedAbove(long vfxHeight, LiteDatabase? db = null)
        {
            var d = db ?? Db;
            if (d == null) return false;
            return d.GetCollection<AttestationMetaRow>(Meta).Exists(x => x.LastModifiedHeight > vfxHeight)
                || d.GetCollection<VaultWithdrawalRow>(Withdrawals).Exists(x => x.LastModifiedHeight > vfxHeight)
                || d.GetCollection<VaultCoinRow>(Coins).Exists(x => x.LastModifiedHeight > vfxHeight)
                || d.GetCollection<VaultDepositTotalRow>(Totals).Exists(x => x.LastModifiedHeight > vfxHeight)
                || d.GetCollection<VaultDepositScriptRow>(Scripts).Exists(x => x.LastModifiedHeight > vfxHeight)
                || d.GetCollection<AttestationSignerSetRow>(SignerSets).Exists(x => x.LastModifiedHeight > vfxHeight)
                || d.GetCollection<AttestedHeaderRow>(Headers).Exists(x => x.LastModifiedHeight > vfxHeight)
                || VerifiedXCore.Services.ValidatorBond.ChangedAbove(vfxHeight, d);
        }

        /// <summary>Empties every table of this layer (state wipe before a replay; chain-reference migration).</summary>
        /// <summary>
        /// At node start, before any block is applied. Everything in this database is written by blocks at or after the
        /// height the validator bonds start and the height Bitcoin recording starts. While the chain's tip is below
        /// both, nothing can rightly be here: rows found are left from a release whose heights were lower and have
        /// passed (the heights were moved on 13 October 2026). With them a node would not start the bonds or the
        /// Bitcoin view at the new heights, and would hold other signer sets and other watched vaults than the nodes
        /// that never ran that release. They are removed. Returns how many rows were.
        /// </summary>
        public static int ClearIfBeforeItsStart(long tipHeight, LiteDatabase? db = null)
        {
            var d = db ?? Db;
            if (d == null) return 0;
            var starts = Math.Min(Globals.ValidatorBondHeight, Globals.BitcoinAttestationRecordHeight);
            if (tipHeight >= starts) return 0;
            var rows = AllCollections.Sum(name => d.GetCollection(name).Count());
            if (rows == 0) return 0;
            WipeAll(d);
            // The snapshot slots copy these tables by row and never record a removal: a restore from one would bring
            // the removed rows back. They are all marked unusable; new ones are taken as blocks arrive.
            try { VerifiedXCore.Services.StateSnapshotService.InvalidateAbove(-1); } catch { }
            VerifiedXCore.Utilities.ErrorLogUtility.LogError($"The Bitcoin attestation and validator bond tables held {rows} row(s) although the chain's tip ({tipHeight}) is below the height they start at ({starts}). They were left by a release with earlier heights and have been removed.", "BtcAttestStore.ClearIfBeforeItsStart()");
            return rows;
        }

        /// <summary>
        /// At node start: why this database cannot be the one the present settings build, or null. Past the start
        /// heights it must have been started at them: the bonds seeded at the bond height, the Bitcoin view at the
        /// configured checkpoint. Anything else was built under other settings and can only be rebuilt from the chain.
        /// </summary>
        public static string? BuiltUnderOtherSettings(long tipHeight, LiteDatabase? db = null)
        {
            var d = db ?? Db;
            if (d == null) return null;
            if (tipHeight >= Globals.ValidatorBondHeight)
            {
                var marker = d.GetCollection<VerifiedXCore.Services.ValidatorBondRow>(VerifiedXCore.Services.ValidatorBond.Collection).FindById("#seeded");
                if (marker == null)
                    return $"the validator bonds start at block {Globals.ValidatorBondHeight} and this node's state (tip {tipHeight}) has none";
                if (marker.BondedAtHeight != Globals.ValidatorBondHeight)
                    return $"this node's validator bonds were started at block {marker.BondedAtHeight}; this release starts them at {Globals.ValidatorBondHeight}";
            }
            if (tipHeight >= Globals.BitcoinAttestationRecordHeight)
            {
                var meta = GetMeta(d);
                var checkpoint = BtcCheckpoint.Configured(out _);
                if (meta != null && checkpoint != null && meta.CheckpointHeight != checkpoint.Height)
                    return $"this node's Bitcoin view was started at Bitcoin block {meta.CheckpointHeight}; this release starts it at {checkpoint.Height}";
            }
            return null;
        }

        public static void WipeAll(LiteDatabase? db = null)
        {
            var d = db ?? Db;
            if (d == null) return;
            foreach (var name in AllCollections)
                d.GetCollection(name).DeleteAll();
        }
    }
}
