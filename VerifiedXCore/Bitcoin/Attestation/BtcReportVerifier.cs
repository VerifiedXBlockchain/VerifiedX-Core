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
        /// <summary>The position of the last event (parts continue in block order).</summary>
        public long LastPosition { get; init; }

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
            if (string.IsNullOrEmpty(report.Scope)) return BtcReportOutcome.Fail("a report names its scope");
            if (report.Headers.Count > BtcAttestRules.HeadersOnlyBatch) return BtcReportOutcome.Fail($"a report carries at most {BtcAttestRules.HeadersOnlyBatch} headers");
            if (report.Events.Count > BtcAttestRules.MaxEventsPerReport) return BtcReportOutcome.Fail($"a report carries at most {BtcAttestRules.MaxEventsPerReport} events");
            if (report.Parts < 1 || report.Parts > BtcAttestRules.MaxParts || report.Part < 1 || report.Part > report.Parts)
                return BtcReportOutcome.Fail("the report's part numbers are out of range");
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
                if (report.BlockHeight != 0 || !forward || report.Events.Count != 0 || report.Parts != 1)
                    return BtcReportOutcome.Fail("a header replacement carries headers only");
                if (meta.PendingNextPart != 0) return BtcReportOutcome.Fail("a header replacement cannot interrupt a multi-part report");
                if (report.ReplaceAbove < meta.EventsThroughHeight || report.ReplaceAbove < meta.CheckpointHeight)
                    return BtcReportOutcome.Fail("headers at or below the events-through height cannot be replaced by this report");
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

            // ── Headers only ────────────────────────────────────────────────────────────────────────────────
            if (report.BlockHeight == 0)
            {
                if (!forward || report.Events.Count != 0 || report.Parts != 1 || report.CoinbaseTxHex != null || report.CoinbaseBranch != null)
                    return BtcReportOutcome.Fail("a report without a block carries headers only");
                if (report.Headers.Count != BtcAttestRules.HeadersOnlyBatch)
                    return BtcReportOutcome.Fail($"a headers-only report carries exactly {BtcAttestRules.HeadersOnlyBatch} headers");
                return new BtcReportOutcome { ReportHash = hash, NewHeaders = newHeaders, NewTipHeight = newTip, NewTipHash = newTipHash };
            }

            // ── The reported block ──────────────────────────────────────────────────────────────────────────
            var block = report.BlockHeight;
            if (block <= meta.CheckpointHeight) return BtcReportOutcome.Fail("blocks at or below the checkpoint are not reported");
            if (report.Events.Count == 0) return BtcReportOutcome.Fail("a report about a block carries at least one event");

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
                    if (block != meta.PendingPartBlockHeight || report.Part != meta.PendingNextPart || report.Parts != meta.PendingParts)
                        return BtcReportOutcome.Fail($"part {meta.PendingNextPart} of {meta.PendingParts} for block {meta.PendingPartBlockHeight} is expected next");
                    afterPosition = meta.PendingLastPosition;
                }
            }
            else
            {
                if (report.Parts != 1) return BtcReportOutcome.Fail("a backfill report has one part");
                scopedVault = BtcAttestStore.ScriptRowForVault(report.Scope, db);
                if (scopedVault == null) return BtcReportOutcome.Fail($"no vault {report.Scope} is known to the Bitcoin view");
                if (block > meta.EventsThroughHeight) return BtcReportOutcome.Fail("a backfill report is for a block whose events were already reported");
                if (block < scopedVault.WatchFromBitcoinHeight) return BtcReportOutcome.Fail($"block {block} is before vault {report.Scope} is watched from ({scopedVault.WatchFromBitcoinHeight})");
            }

            // Burial: enforced here, not left to the signers.
            if (block + BtcAttestRules.Depth > newTip)
                return BtcReportOutcome.Fail($"block {block} is not under {BtcAttestRules.Depth} attested headers (attested tip {newTip})");

            var blockHeader = Lookup(block);
            if (blockHeader == null) return BtcReportOutcome.Fail($"the header of block {block} is not attested");

            // The coinbase proof fixes the depth of the block's tree for every event that carries its bytes. A part
            // whose events are all listings has nothing to bind and carries none (so a block whose coinbase is too
            // large to carry can still be reported, on the signatures alone).
            var depth = 0;
            if (report.Events.Any(e => e?.TxHex != null))
            {
                var coinbaseError = BtcTxInclusion.VerifyCoinbase(blockHeader, report.CoinbaseTxHex, report.CoinbaseBranch, out depth, out _, out _);
                if (coinbaseError != null) return BtcReportOutcome.Fail($"coinbase proof: {coinbaseError}");
            }
            else if (report.CoinbaseTxHex != null || report.CoinbaseBranch != null)
                return BtcReportOutcome.Fail("a report whose events are all listings carries no coinbase proof");

            // ── Events ──────────────────────────────────────────────────────────────────────────────────────
            var effects = new List<BtcCoinEffect>();
            var overlay = new Dictionary<string, VaultCoinStatus>(StringComparer.Ordinal); // coins touched earlier in this report
            var lastPosition = afterPosition;
            foreach (var ev in report.Events)
            {
                if (ev == null) return BtcReportOutcome.Fail("an event is empty");
                if (ev.Position <= lastPosition) return BtcReportOutcome.Fail("events are in block order, each transaction once");
                lastPosition = ev.Position;

                string txid;
                bool isCoinbase;
                List<string> spends;
                List<(long Vout, string ScriptHex, long Sats)> outputs;

                if (ev.TxHex != null)
                {
                    if (ev.Txid != null || ev.Spends != null || ev.Creates != null) return BtcReportOutcome.Fail("an event is either a transaction with its proof or an oversize listing");
                    var inclusion = BtcTxInclusion.Verify(blockHeader, ev.TxHex, ev.Position, ev.Branch, depth, out var tx, out var id);
                    if (inclusion != null) return BtcReportOutcome.Fail($"event at position {ev.Position}: {inclusion}");
                    txid = id!;
                    isCoinbase = tx!.IsCoinBase;
                    if (isCoinbase != (ev.Position == 0)) return BtcReportOutcome.Fail("only the transaction at position 0 is a coinbase");
                    spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                    outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();
                }
                else
                {
                    // A listing: carried without bytes or branch, on the report's signatures alone (plan 3.2).
                    if (ev.Branch != null) return BtcReportOutcome.Fail("an oversize event carries no branch");
                    if (!IsTxid(ev.Txid)) return BtcReportOutcome.Fail("an oversize event names its transaction id");
                    txid = ev.Txid!;
                    isCoinbase = ev.Position == 0;
                    spends = (ev.Spends ?? new List<string>()).ToList();
                    if (isCoinbase && spends.Count > 0) return BtcReportOutcome.Fail("a coinbase transaction spends nothing");
                    if (spends.Any(s => !IsOutpoint(s)) || spends.Distinct(StringComparer.Ordinal).Count() != spends.Count)
                        return BtcReportOutcome.Fail("an oversize event lists distinct outpoints in lowercase");
                    outputs = new List<(long, string, long)>();
                    foreach (var c in ev.Creates ?? new List<BtcReportOutput>())
                    {
                        if (c == null || c.Vout < 0 || c.Sats <= 0 || string.IsNullOrEmpty(c.ScriptHex) || c.ScriptHex != c.ScriptHex.ToLowerInvariant())
                            return BtcReportOutcome.Fail("an oversize event lists outputs with a position, a script in lowercase hex and a positive value");
                        outputs.Add((c.Vout, c.ScriptHex, c.Sats));
                    }
                    if (outputs.Select(o => o.Item1).Distinct().Count() != outputs.Count) return BtcReportOutcome.Fail("an oversize event lists each output once");
                }

                var added = DeriveEffects(txid, isCoinbase, spends, outputs, block, scopedVault?.SmartContractUID, overlay, effects, db);
                if (added == 0)
                    return BtcReportOutcome.Fail($"transaction {txid} touches no vault coin{(scopedVault != null ? " of " + scopedVault.SmartContractUID : "")}; a report carries only transactions that do");
            }

            return new BtcReportOutcome
            {
                ReportHash = hash, NewHeaders = newHeaders, NewTipHeight = newTip, NewTipHash = newTipHash, Effects = effects,
                AdvancesEventsThrough = forward && report.Part == report.Parts, LastPosition = lastPosition,
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
                if (status == null || status == VaultCoinStatus.Spent) continue;
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
                if (overlay.ContainsKey(outpoint) || BtcAttestStore.Coin(outpoint, db) != null) continue; // already known: one coin, once
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

        private static bool IsTxid(string? s) => s != null && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        private static bool IsOutpoint(string? s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var colon = s.IndexOf(':');
            return colon == 64 && IsTxid(s.Substring(0, 64)) && long.TryParse(s.Substring(65), out var n) && n >= 0 && s == BtcAttestStore.OutpointKey(s.Substring(0, 64), n);
        }
    }
}
