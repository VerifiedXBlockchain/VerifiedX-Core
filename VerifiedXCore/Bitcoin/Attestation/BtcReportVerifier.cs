using System.Numerics;
using LiteDB;
using NBitcoin;
using VerifiedXCore.Services;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>What one reported Bitcoin transaction does to a vault's coins. Derived by the verifier, never stated by the report.</summary>
    public sealed class BtcCoinEffect
    {
        public bool IsSpend { get; init; }
        public string Outpoint { get; init; } = "";
        public string SmartContractUID { get; init; } = "";
        public long Sats { get; init; }
        /// <summary>The Bitcoin transaction that creates (for a new coin) or spends (for a spend) the coin.</summary>
        public string Txid { get; init; } = "";
        public long BitcoinHeight { get; init; }
        public bool IsCoinbase { get; init; }
    }

    public sealed class BtcReportOutcome
    {
        /// <summary>Null when the report is valid.</summary>
        public string? Error { get; init; }
        public string ReportHash { get; init; } = "";
        /// <summary>Headers to store, with their Bitcoin heights.</summary>
        public List<(long Height, BtcHeader Header)> NewHeaders { get; init; } = new();
        /// <summary>The first height whose stored header is replaced (0 when the headers only extend the tip).</summary>
        public long ReplacesFromHeight { get; init; }
        public long NewTipHeight { get; init; }
        public string NewTipHash { get; init; } = "";
        public List<BtcCoinEffect> Effects { get; init; } = new();
        /// <summary>True for the last part of a forward report: the events-through height moves to the report's block.</summary>
        public bool AdvancesEventsThrough { get; init; }
        /// <summary>For a quiet report: the height the events-through height moves to (0 otherwise).</summary>
        public long QuietThroughHeight { get; init; }
        /// <summary>The position of the last event (parts continue in block order).</summary>
        public long LastPosition { get; init; }
        /// <summary>True for a rewind: the events of every block from <see cref="ReplacesFromHeight"/> up are taken back.</summary>
        public bool IsRewind { get; init; }

        public bool Ok => Error == null;
        internal static BtcReportOutcome Fail(string reason) => new() { Error = reason };
    }

    /// <summary>
    /// Decides whether a Bitcoin report is valid, from chain state alone (docs/PLAN-vBTC-Bitcoin-Attestation.md, 3.1-3.3).
    /// The same function runs at mempool admission, in block validation and when the block is applied, so every node
    /// reaches the same verdict and the same effects.
    ///
    /// What a valid report establishes, and by what:
    /// <list type="bullet">
    /// <item>its headers are Bitcoin: each extends the last and meets the target the rules require;</item>
    /// <item>the reported block is buried: <see cref="BtcAttestRules.Depth"/> attested headers sit on top of it;</item>
    /// <item>each event transaction is in that block: a Merkle branch to the header, bound to the coinbase's depth;</item>
    /// <item>what each transaction does to vault coins: read from the transaction's own inputs and outputs;</item>
    /// <item>which Bitcoin branch is followed, and that skipped blocks held nothing: two thirds of the epoch's signers.</item>
    /// </list>
    /// </summary>
    public static class BtcReportVerifier
    {
        public static BtcReportOutcome Verify(BtcReport report, long vfxHeight, LiteDatabase? db = null)
        {
            db ??= BtcAttestStore.Db;
            if (report == null) return BtcReportOutcome.Fail("no report");
            if (!BtcAttestRules.Recording(vfxHeight)) return BtcReportOutcome.Fail("Bitcoin reports are not accepted before the recording height");

            var meta = BtcAttestStore.GetMeta(db);
            if (meta == null) return BtcReportOutcome.Fail("the Bitcoin view has not been initialised (no checkpoint)");

            // ── Shape ───────────────────────────────────────────────────────────────────────────────────────
            if (report.V != 1) return BtcReportOutcome.Fail("unknown report version");
            if (report.Rewind != null) return BtcReportOutcome.Fail("a rewind is carried by its own transaction type, not by a report");
            if (string.IsNullOrEmpty(report.Scope)) return BtcReportOutcome.Fail("a report names its scope");
            if (report.Headers.Count > BtcAttestRules.MaxHeaders) return BtcReportOutcome.Fail($"a report carries at most {BtcAttestRules.MaxHeaders} headers");
            if (report.Events.Count > BtcAttestRules.MaxEventsPerReport) return BtcReportOutcome.Fail($"a report carries at most {BtcAttestRules.MaxEventsPerReport} events");
            if (report.Part < 1) return BtcReportOutcome.Fail("the report's part number is out of range");
            if (report.BlockHeight < 0 || report.ReplaceAbove < 0) return BtcReportOutcome.Fail("heights cannot be negative");
            if (report.Part > 1 && (report.Headers.Count > 0 || report.ReplaceAbove != 0))
                return BtcReportOutcome.Fail("only the first part of a report carries headers");
            var forward = report.Scope == BtcReport.ScopeAll;

            // ── Signatures (checked before the heavier work; they bind every field through the hash) ─────────
            var hash = report.Hash();
            var signatureError = CheckSignatures(report, hash, vfxHeight, db);
            if (signatureError != null) return BtcReportOutcome.Fail(signatureError);

            // ── Headers ─────────────────────────────────────────────────────────────────────────────────────
            var rules = BtcChainRules.Current;
            var accepted = new Dictionary<long, BtcHeader>();
            var baseHeight = meta.TipHeight;
            long replacesFrom = 0;
            if (report.ReplaceAbove != 0)
            {
                // A reorganisation of headers whose blocks have had no events reported: nothing but headers changes.
                if (report.BlockHeight != 0 || !forward || report.Events.Count != 0 || report.Part != 1 || !report.Final
                    || report.CarriesCoinbaseProof || report.BlockHash != null)
                    return BtcReportOutcome.Fail("a header replacement carries headers only");
                if (meta.PendingNextPart != 0) return BtcReportOutcome.Fail("a header replacement cannot interrupt a multi-part report");
                if (report.ReplaceAbove < meta.EventsThroughHeight || report.ReplaceAbove < meta.CheckpointHeight)
                    return BtcReportOutcome.Fail("headers at or below the events-through height cannot be replaced by this report (that takes a rewind)");
                if (report.ReplaceAbove >= meta.TipHeight) return BtcReportOutcome.Fail("there is nothing above that height to replace");
                if (report.Headers.Count == 0) return BtcReportOutcome.Fail("a header replacement carries the replacing headers");
                baseHeight = report.ReplaceAbove;
                replacesFrom = report.ReplaceAbove + 1;
            }

            // Below the base, the stored chain; above it, only what this report has brought so far.
            BtcHeader? Lookup(long h) => h > baseHeight ? (accepted.TryGetValue(h, out var a) ? a : null) : BtcAttestStore.HeaderAt(h, db);

            var newHeaders = new List<(long, BtcHeader)>(report.Headers.Count);
            var height = baseHeight;
            foreach (var hex in report.Headers)
            {
                if (!BtcHeader.TryParse(hex, out var header) || header == null || !string.Equals(header.Hex, hex, StringComparison.Ordinal))
                    return BtcReportOutcome.Fail("a header is not 80 bytes of lowercase hex");
                height++;
                var reason = rules.Validate(header, height, Lookup);
                if (reason != null) return BtcReportOutcome.Fail(reason);
                accepted[height] = header;
                newHeaders.Add((height, header));
            }
            var newTip = height;
            var newTipHash = newHeaders.Count > 0 ? newHeaders[^1].Item2.Hash : meta.TipHash;

            if (report.ReplaceAbove != 0)
            {
                // The replacing branch must carry more work than the branch it replaces.
                var oldWork = BigInteger.Zero;
                for (var h = replacesFrom; h <= meta.TipHeight; h++)
                    oldWork += BtcAttestStore.HeaderAt(h, db)?.Work() ?? BigInteger.Zero;
                var newWork = newHeaders.Aggregate(BigInteger.Zero, (sum, x) => sum + x.Item2.Work());
                if (newWork <= oldWork) return BtcReportOutcome.Fail("the replacing headers do not carry more work than the headers they replace");
                return new BtcReportOutcome { ReportHash = hash, NewHeaders = newHeaders, ReplacesFromHeight = replacesFrom, NewTipHeight = newTip, NewTipHash = newTipHash };
            }

            // ── A quiet report: the next QuietBlocks blocks hold no vault event ─────────────────────────────
            if (report.BlockHeight == 0)
            {
                if (!forward || report.Events.Count != 0 || report.Part != 1 || !report.Final || report.CarriesCoinbaseProof)
                    return BtcReportOutcome.Fail("a report without a block carries headers only");
                if (meta.PendingNextPart != 0) return BtcReportOutcome.Fail("a quiet report cannot interrupt a multi-part report");
                // The range is fixed by state: exactly the next QuietBlocks blocks, buried like any reported block. It
                // carries the headers that bury them and no others, so every validator builds the same report.
                var quietThrough = meta.EventsThroughHeight + BtcAttestRules.QuietBlocks;
                // Already buried by the attested headers: it carries none. Otherwise it carries the headers up to the
                // one that completes the burial, and that one is its last.
                var exact = rules.BuriedAt(quietThrough, meta.TipHeight, BtcAttestRules.Depth, Lookup) != null
                    ? report.Headers.Count == 0
                    : rules.BuriedAt(quietThrough, newTip, BtcAttestRules.Depth, Lookup) == newTip;
                // It names the last block of its range, so that it is a statement about those blocks and no others.
                if (report.BlockHash == null || !string.Equals(Lookup(quietThrough)?.Hash, report.BlockHash, StringComparison.Ordinal))
                    return BtcReportOutcome.Fail($"a quiet report names the hash of the last block of its range (block {quietThrough})");
                if (!exact)
                    return BtcReportOutcome.Fail($"a quiet report covers blocks {meta.EventsThroughHeight + 1} to {quietThrough} and carries the headers through the one that buries them ({BtcAttestRules.Depth} of full difficulty), no more and no fewer");
                return new BtcReportOutcome { ReportHash = hash, NewHeaders = newHeaders, NewTipHeight = newTip, NewTipHash = newTipHash, QuietThroughHeight = quietThrough };
            }

            // ── The reported block ──────────────────────────────────────────────────────────────────────────
            var block = report.BlockHeight;
            if (block <= meta.CheckpointHeight) return BtcReportOutcome.Fail("blocks at or below the checkpoint are not reported");
            // The one report about a block that may be empty: a last part that only closes the block (what was left
            // for it when the earlier parts were built has since been reported another way).
            if (report.Events.Count == 0 && !(forward && report.Part > 1 && report.Final))
                return BtcReportOutcome.Fail("a report about a block carries at least one event");

            VaultDepositScriptRow? scopedVault = null;
            long afterPosition = -1;
            if (forward)
            {
                if (meta.PendingNextPart == 0)
                {
                    if (report.Part != 1) return BtcReportOutcome.Fail("the first part of this block's report has not been applied");
                    if (block <= meta.EventsThroughHeight) return BtcReportOutcome.Fail($"block {block} is at or below the events-through height {meta.EventsThroughHeight}");
                }
                else
                {
                    if (block != meta.PendingPartBlockHeight || report.Part != meta.PendingNextPart)
                        return BtcReportOutcome.Fail($"part {meta.PendingNextPart} for block {meta.PendingPartBlockHeight} is expected next");
                    afterPosition = meta.PendingLastPosition;
                }
            }
            else
            {
                if (report.Part != 1 || !report.Final) return BtcReportOutcome.Fail("a backfill report has one part");
                scopedVault = BtcAttestStore.ScriptRowForVault(report.Scope, db);
                if (scopedVault == null) return BtcReportOutcome.Fail($"no vault {report.Scope} is known to the Bitcoin view");
                if (block > meta.EventsThroughHeight) return BtcReportOutcome.Fail("a backfill report is for a block whose events were already reported");
                if (block < scopedVault.WatchFromBitcoinHeight) return BtcReportOutcome.Fail($"block {block} is before vault {report.Scope} is watched from ({scopedVault.WatchFromBitcoinHeight})");
            }

            // Burial: enforced here, not left to the signers.
            if (rules.BuriedAt(block, newTip, BtcAttestRules.Depth, Lookup) == null)
                return BtcReportOutcome.Fail($"block {block} is not under {BtcAttestRules.Depth} attested headers of full difficulty (attested tip {newTip})");

            var blockHeader = Lookup(block);
            if (blockHeader == null) return BtcReportOutcome.Fail($"the header of block {block} is not attested");
            if (!string.Equals(blockHeader.Hash, report.BlockHash, StringComparison.Ordinal))
                return BtcReportOutcome.Fail($"a report about a block names that block's hash (block {block} is {blockHeader.Hash})");

            // The coinbase proof fixes the depth of the block's tree for every event: the coinbase itself, or, when a
            // miner made it too large to carry, its last block and its length. A part with no events (one that only
            // closes the block) has nothing to bind and carries none.
            var depth = 0;
            if (report.Events.Count > 0)
            {
                string? coinbaseError;
                if (report.CoinbaseTxHex != null)
                {
                    if (report.CoinbaseState != null || report.CoinbaseTailHex != null || report.CoinbaseBytes != null)
                        return BtcReportOutcome.Fail("a report carries one coinbase proof");
                    coinbaseError = BtcTxInclusion.VerifyCoinbase(blockHeader, report.CoinbaseTxHex, report.CoinbaseBranch, out depth, out _, out _);
                }
                else
                    coinbaseError = BtcTxInclusion.VerifyCoinbaseTail(blockHeader, report.CoinbaseState, report.CoinbaseTailHex, report.CoinbaseBytes, report.CoinbaseBranch, out depth);
                if (coinbaseError != null) return BtcReportOutcome.Fail($"coinbase proof: {coinbaseError}");
            }
            else if (report.CarriesCoinbaseProof)
                return BtcReportOutcome.Fail("a report with no events carries no coinbase proof");

            // ── Events ──────────────────────────────────────────────────────────────────────────────────────
            var effects = new List<BtcCoinEffect>();
            var overlay = new Dictionary<string, VaultCoinStatus>(StringComparer.Ordinal); // coins touched earlier in this report
            var lastPosition = afterPosition;
            foreach (var ev in report.Events)
            {
                if (ev == null) return BtcReportOutcome.Fail("an event is empty");
                if (ev.Position <= lastPosition) return BtcReportOutcome.Fail("events are in block order, each transaction once");
                lastPosition = ev.Position;

                // Every event is a transaction with its bytes and its proof. What it does is read from those bytes;
                // nothing about it is taken on the signers' word.
                if (ev.TxHex == null) return BtcReportOutcome.Fail("an event carries its transaction and its proof");
                var inclusion = BtcTxInclusion.Verify(blockHeader, ev.TxHex, ev.Position, ev.Branch, depth, out var tx, out var id);
                if (inclusion != null) return BtcReportOutcome.Fail($"event at position {ev.Position}: {inclusion}");
                var txid = id!;
                var isCoinbase = tx!.IsCoinBase;
                if (isCoinbase != (ev.Position == 0)) return BtcReportOutcome.Fail("only the transaction at position 0 is a coinbase");
                var spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                var outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();

                var added = DeriveEffects(txid, isCoinbase, spends, outputs, block, scopedVault?.SmartContractUID, overlay, effects, db);
                if (added == 0)
                    return BtcReportOutcome.Fail($"transaction {txid} touches no vault coin{(scopedVault != null ? " of " + scopedVault.SmartContractUID : "")}; a report carries only transactions that do");
            }

            return new BtcReportOutcome
            {
                ReportHash = hash, NewHeaders = newHeaders, NewTipHeight = newTip, NewTipHash = newTipHash, Effects = effects,
                AdvancesEventsThrough = forward && report.Final, LastPosition = lastPosition,
            };
        }

        /// <summary>
        /// Decides whether a rewind is valid (plan section 7). A rewind is for a Bitcoin reorganisation that reaches
        /// blocks whose vault events this chain has already applied: it names the last height the two branches share,
        /// carries the headers of the branch that replaced the rest, and takes the applied events back. It rests on
        /// the same things as a report: the headers are Bitcoin (each extends the last and meets its target), the
        /// replacing branch carries more work than the one it replaces, and two thirds of the epoch's signers say this
        /// is the branch to follow. It carries no events; the new branch's blocks are reported afterwards like any others.
        /// </summary>
        public static BtcReportOutcome VerifyRewind(BtcReport report, long vfxHeight, LiteDatabase? db = null)
        {
            db ??= BtcAttestStore.Db;
            if (report == null) return BtcReportOutcome.Fail("no rewind");
            if (!BtcAttestRules.Recording(vfxHeight)) return BtcReportOutcome.Fail("Bitcoin rewinds are not accepted before the recording height");
            var meta = BtcAttestStore.GetMeta(db);
            if (meta == null) return BtcReportOutcome.Fail("the Bitcoin view has not been initialised (no checkpoint)");

            if (report.V != 1) return BtcReportOutcome.Fail("unknown rewind version");
            if (report.Rewind != true) return BtcReportOutcome.Fail("a rewind says that it is one");
            if (report.Scope != BtcReport.ScopeAll || report.BlockHeight != 0 || report.Events.Count != 0 || report.Part != 1 || !report.Final
                || report.CarriesCoinbaseProof || report.BlockHash != null)
                return BtcReportOutcome.Fail("a rewind carries a height and headers only");
            if (report.Headers.Count == 0) return BtcReportOutcome.Fail("a rewind carries the replacing headers");
            if (report.Headers.Count > BtcAttestRules.MaxHeaders) return BtcReportOutcome.Fail($"a rewind carries at most {BtcAttestRules.MaxHeaders} headers");

            var to = report.ReplaceAbove;
            if (to < meta.CheckpointHeight) return BtcReportOutcome.Fail("a rewind cannot go below the checkpoint");
            if (to >= meta.TipHeight) return BtcReportOutcome.Fail("there is nothing above that height to rewind");
            // Needed only when something of the replaced blocks was applied: their events, or the mined parts of a
            // block's report that is still under way. Otherwise a report's header replacement does it.
            var appliedAbove = to < meta.EventsThroughHeight || (meta.PendingNextPart != 0 && to < meta.PendingPartBlockHeight);
            if (!appliedAbove) return BtcReportOutcome.Fail("no events were applied above that height; a report's header replacement is enough");

            var hash = report.Hash();
            var signatureError = CheckSignatures(report, hash, vfxHeight, db);
            if (signatureError != null) return BtcReportOutcome.Fail(signatureError);

            if (BtcAttestStore.HeaderAt(to, db) == null) return BtcReportOutcome.Fail($"the header at {to} is not attested");
            var rules = BtcChainRules.Current;
            var accepted = new Dictionary<long, BtcHeader>();
            BtcHeader? Lookup(long h) => h > to ? (accepted.TryGetValue(h, out var a) ? a : null) : BtcAttestStore.HeaderAt(h, db);
            var newHeaders = new List<(long, BtcHeader)>(report.Headers.Count);
            var height = to;
            foreach (var hex in report.Headers)
            {
                if (!BtcHeader.TryParse(hex, out var header) || header == null || !string.Equals(header.Hex, hex, StringComparison.Ordinal))
                    return BtcReportOutcome.Fail("a header is not 80 bytes of lowercase hex");
                height++;
                var reason = rules.Validate(header, height, Lookup);
                if (reason != null) return BtcReportOutcome.Fail(reason);
                accepted[height] = header;
                newHeaders.Add((height, header));
            }

            var oldWork = BigInteger.Zero;
            for (var h = to + 1; h <= meta.TipHeight; h++)
                oldWork += BtcAttestStore.HeaderAt(h, db)?.Work() ?? BigInteger.Zero;
            var newWork = newHeaders.Aggregate(BigInteger.Zero, (sum, x) => sum + x.Item2.Work());
            if (newWork <= oldWork) return BtcReportOutcome.Fail("the replacing headers do not carry more work than the headers they replace");

            return new BtcReportOutcome
            {
                ReportHash = hash, NewHeaders = newHeaders, ReplacesFromHeight = to + 1, NewTipHeight = height, NewTipHash = newHeaders[^1].Item2.Hash, IsRewind = true,
            };
        }

        /// <summary>
        /// What one Bitcoin transaction of block <paramref name="block"/> does to vault coins, appended to
        /// <paramref name="effects"/>; returns how many effects it has. The rule is the whole of "what counts":
        /// an input that is a coin this layer holds (and has not seen spent) spends it; an output that pays the deposit
        /// script of a vault watched at this block creates a coin, once. <paramref name="overlay"/> carries the coins
        /// touched earlier in the same report, so a coin created and spent within one block is followed.
        /// The verifier and the scanner that builds reports both call this, so they cannot disagree.
        /// </summary>
        internal static int DeriveEffects(string txid, bool isCoinbase, IReadOnlyList<string> spends, IReadOnlyList<(long Vout, string ScriptHex, long Sats)> outputs,
            long block, string? scopedVault, Dictionary<string, VaultCoinStatus> overlay, List<BtcCoinEffect> effects, LiteDatabase? db)
        {
            var before = effects.Count;

            foreach (var outpoint in spends)
            {
                var coin = BtcAttestStore.Coin(outpoint, db);
                var status = overlay.TryGetValue(outpoint, out var touched) ? touched : coin?.Status;
                if (status == null || status == VaultCoinStatus.Spent || status == VaultCoinStatus.Orphaned) continue;
                var created = coin == null ? effects.First(e => !e.IsSpend && e.Outpoint == outpoint) : null;
                var owner = coin?.SmartContractUID ?? created!.SmartContractUID;
                if (scopedVault != null && owner != scopedVault) continue;
                effects.Add(new BtcCoinEffect { IsSpend = true, Outpoint = outpoint, SmartContractUID = owner, Sats = coin?.ValueSats ?? created!.Sats, Txid = txid, BitcoinHeight = block });
                overlay[outpoint] = VaultCoinStatus.Spent;
            }

            foreach (var (vout, scriptHex, sats) in outputs)
            {
                if (sats <= 0) continue;
                var row = BtcAttestStore.ScriptRow(scriptHex, db);
                if (row == null || row.WatchFromBitcoinHeight > block) continue;
                if (scopedVault != null && row.SmartContractUID != scopedVault) continue;
                var outpoint = BtcAttestStore.OutpointKey(txid, vout);
                // Already known: one coin, once. (A coin a rewind orphaned is not known: its transaction confirming again creates it again.)
                if (overlay.ContainsKey(outpoint) || BtcAttestStore.Coin(outpoint, db) is { Status: not VaultCoinStatus.Orphaned }) continue;
                effects.Add(new BtcCoinEffect { Outpoint = outpoint, SmartContractUID = row.SmartContractUID, Sats = sats, Txid = txid, BitcoinHeight = block, IsCoinbase = isCoinbase });
                overlay[outpoint] = VaultCoinStatus.Unspent;
            }

            return effects.Count - before;
        }

        /// <summary>
        /// The signatures: distinct members of the stored signer set of the report's epoch (the current epoch or the one
        /// before it), every one valid over the report hash, and at least two thirds of the set.
        /// </summary>
        public static string? CheckSignatures(BtcReport report, string reportHash, long vfxHeight, LiteDatabase? db = null)
        {
            var current = BtcAttestRules.EpochOf(vfxHeight);
            if (report.SignerEpoch != current && report.SignerEpoch != current - 1)
                return $"the report is signed under epoch {report.SignerEpoch}; the current epoch is {current}";
            var set = BtcAttestStore.SignerSet(report.SignerEpoch, db);
            if (set == null || set.Addresses.Count < BtcAttestRules.MinSigners)
                return $"epoch {report.SignerEpoch} has no signer set of at least {BtcAttestRules.MinSigners} validators";

            var signatures = report.Signatures ?? new List<BtcReportSignature>();
            if (signatures.Count > set.Addresses.Count) return "more signatures than signers";
            var members = new HashSet<string>(set.Addresses, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var message = BtcReport.SigningMessage(reportHash);
            foreach (var s in signatures)
            {
                if (s == null || string.IsNullOrEmpty(s.ValidatorAddress) || string.IsNullOrEmpty(s.Signature)) return "a signature is empty";
                if (!members.Contains(s.ValidatorAddress)) return $"{s.ValidatorAddress} is not a signer in epoch {report.SignerEpoch}";
                if (!seen.Add(s.ValidatorAddress)) return $"{s.ValidatorAddress} signed twice";
                if (!SignatureService.VerifySignature(s.ValidatorAddress, message, s.Signature)) return $"the signature of {s.ValidatorAddress} is not valid";
            }
            var required = BtcAttestRules.RequiredSignatures(set.Addresses.Count);
            return seen.Count >= required ? null : $"{seen.Count} signature(s); {required} of {set.Addresses.Count} are required";
        }
    }
}
