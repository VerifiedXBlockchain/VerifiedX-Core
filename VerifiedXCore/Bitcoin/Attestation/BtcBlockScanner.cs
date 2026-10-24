using LiteDB;
using NBitcoin;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// Turns one Bitcoin block into the report every validator will sign (docs/PLAN-vBTC-Bitcoin-Attestation.md, 2.2 and
    /// 3.4). One pass over the block's transactions: an output is looked up among the vault deposit scripts, an input
    /// among the coins. Nothing is asked about any address.
    ///
    /// The result depends only on the block and on chain state, so validators at the same VFX height holding the same
    /// Bitcoin block build byte-identical reports. Which transactions are events is decided by the verifier's own rule
    /// (<see cref="BtcReportVerifier.DeriveEffects"/>), so a report built here is one the verifier accepts.
    /// </summary>
    public static class BtcBlockScanner
    {
        /// <summary>
        /// The forward report for <paramref name="block"/>, split into parts when it is large; empty when the block holds
        /// no vault event. <paramref name="headers"/> are the headers from the attested tip + 1 that the first part
        /// carries (enough to bury the block). The parts are unsigned.
        /// </summary>
        public static List<BtcReport> BuildForward(Block block, long blockHeight, IReadOnlyList<string> headers, long signerEpoch, LiteDatabase? db = null, int maxParts = int.MaxValue) =>
            Build(block, blockHeight, BtcReport.ScopeAll, headers, signerEpoch, db, afterPosition: -1, firstPart: 1, maxParts);

        /// <summary>
        /// The rest of a block's report once some of its parts are mined: built from the state those parts left, the
        /// events after <paramref name="afterPosition"/> only, numbered from <paramref name="nextPart"/>. Never empty:
        /// when nothing is left, one part with no events closes the block.
        /// </summary>
        public static List<BtcReport> BuildForwardRest(Block block, long blockHeight, long afterPosition, int nextPart, long signerEpoch, LiteDatabase? db = null, int maxParts = int.MaxValue) =>
            Build(block, blockHeight, BtcReport.ScopeAll, Array.Empty<string>(), signerEpoch, db, afterPosition, nextPart, maxParts);

        /// <summary>The backfill report for one vault in one already-reported block; empty when the block holds nothing new for it.</summary>
        /// <remarks>
        /// A backfill is one part (the verifier takes no other). When a vault's unrecorded events in the block do not fit
        /// in one, the report carries the first part's worth and is final; the rest are still unrecorded afterwards, and
        /// the next backfill of the block carries them. A built second part used to be marked as such and was refused by
        /// every node, and the sweep stopped at that block (fourth review).
        /// </remarks>
        public static List<BtcReport> BuildBackfill(Block block, long blockHeight, string scUID, long signerEpoch, LiteDatabase? db = null)
        {
            var parts = Build(block, blockHeight, scUID, Array.Empty<string>(), signerEpoch, db, afterPosition: -1, firstPart: 1, maxParts: 1);
            if (parts.Count == 0) return parts;
            var first = parts[0];
            first.Part = 1;
            first.Final = true;
            return new List<BtcReport> { first };
        }

        /// <summary>
        /// The vaults for which an already-reported block still holds something unrecorded (a deposit mined before the
        /// vault was known, a spend of a coin recorded since). One pass, whatever the number of vaults; each vault found
        /// then gets its own backfill report.
        /// </summary>
        public static List<string> VaultsWithUnrecordedEvents(Block block, long blockHeight, LiteDatabase? db = null)
        {
            var effects = new List<BtcCoinEffect>();
            if (block == null || block.Transactions.Count == 0) return new List<string>();
            var overlay = new Dictionary<string, VaultCoinStatus>(StringComparer.Ordinal);
            for (var position = 0; position < block.Transactions.Count; position++)
            {
                var tx = block.Transactions[position];
                if (TooLargeToProve(tx))
                {
                    // Its spends of recorded coins are frozen (see Build); every one is found here, past the cap of an event.
                    if (position > 0) BtcReportVerifier.DeriveFreezes(tx.GetHash().ToString(), SortedInputs(tx), blockHeight, null, overlay, effects, db, int.MaxValue);
                    continue;
                }
                var isCoinbase = position == 0 && tx.IsCoinBase;
                var spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                var outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();
                BtcReportVerifier.DeriveEffects(tx.GetHash().ToString(), isCoinbase, spends, outputs, blockHeight, null, overlay, effects, db);
            }
            return effects.Select(e => e.SmartContractUID).Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// A transaction larger than a report can carry (<see cref="BtcTxInclusion.MaxTxBytes"/> without witness data:
        /// larger than any Bitcoin node relays). Its bytes are never an event. It is reported spend-only: its id with its
        /// proof, and the recorded coins it spends, which are frozen (#171): left out, a vault's 51% key holders and one
        /// miner could move a coin in a padded transaction while the chain kept counting it as backing. What such a
        /// transaction pays to a vault is not credited.
        /// </summary>
        private static bool TooLargeToProve(Transaction tx) =>
            tx.WithOptions(TransactionOptions.None).GetSerializedSize() > BtcTxInclusion.MaxTxBytes;

        /// <summary>A transaction's inputs as outpoint keys, each once, in ordinal order: how a spend-only event lists them.</summary>
        private static List<string> SortedInputs(Transaction tx) =>
            tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N))
                .Distinct(StringComparer.Ordinal).OrderBy(o => o, StringComparer.Ordinal).ToList();

        private static List<BtcReport> Build(Block block, long blockHeight, string scope, IReadOnlyList<string> headers, long signerEpoch, LiteDatabase? db,
            long afterPosition, int firstPart, int maxParts = int.MaxValue)
        {
            var result = new List<BtcReport>();
            if (block == null || block.Transactions.Count == 0) return result;
            var scopedVault = scope == BtcReport.ScopeAll ? null : scope;

            var txids = block.Transactions.Select(t => t.GetHash().ToString()).ToList();
            var levels = BtcMerkle.Levels(txids); // the tree once; every branch is read from it (round 9)
            var blockHash = block.GetHash().ToString();
            // Every proof in a block rests on the block's coinbase transaction, which fixes the depth of its tree. Each
            // part carries it, or, when it is too large for that (a miner paying out to thousands of addresses), its
            // last hash block and its length, which show the same thing in a few hundred characters.
            var coinbaseBytes = block.Transactions[0].WithOptions(TransactionOptions.None).ToBytes();
            var coinbaseWhole = coinbaseBytes.Length <= BtcAttestRules.MaxCoinbaseProofBytes;
            var overlay = new Dictionary<string, VaultCoinStatus>(StringComparer.Ordinal);
            var effects = new List<BtcCoinEffect>();
            var events = new List<BtcReportEvent>();

            // Transactions at or before afterPosition belong to parts already mined. They are skipped whole, as the
            // verifier does: anything of theirs that is still unrecorded (a vault created since) is a backfill later.
            for (var position = (int)Math.Max(0, afterPosition + 1); position < block.Transactions.Count; position++)
            {
                var tx = block.Transactions[position];
                if (TooLargeToProve(tx))
                {
                    // Spend-only (#171): its id and proof, and the recorded coins it spends, at most a cap of them; the rest
                    // are found by VaultsWithUnrecordedEvents and frozen by backfills. A coinbase spends nothing.
                    if (position == 0) continue;
                    var first = effects.Count;
                    if (BtcReportVerifier.DeriveFreezes(txids[position], SortedInputs(tx), blockHeight, scopedVault, overlay, effects, db, BtcAttestRules.MaxSpendsPerEvent) == 0)
                        continue;
                    events.Add(new BtcReportEvent
                    {
                        Position = position, Txid = txids[position], Spends = effects.Skip(first).Select(e => e.Outpoint).ToList(),
                        Branch = levels == null ? null : BtcMerkle.BranchFrom(levels, position),
                    });
                    continue;
                }
                var isCoinbase = position == 0 && tx.IsCoinBase;
                var spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                var outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();

                if (BtcReportVerifier.DeriveEffects(txids[position], isCoinbase, spends, outputs, blockHeight, scopedVault, overlay, effects, db) == 0)
                    continue;
                events.Add(new BtcReportEvent { Position = position, TxHex = BtcTxInclusion.ToStrippedHex(tx), Branch = levels == null ? null : BtcMerkle.BranchFrom(levels, position) });
            }
            if (events.Count == 0)
            {
                if (firstPart > 1)
                    result.Add(new BtcReport { Scope = scope, BlockHeight = blockHeight, BlockHash = blockHash, SignerEpoch = signerEpoch, Part = firstPart, Final = true });
                return result;
            }

            var coinbaseBranch = (levels == null ? null : BtcMerkle.BranchFrom(levels, 0))!;

            BtcReport NewPart(bool first)
            {
                var part = new BtcReport
                {
                    Scope = scope, BlockHeight = blockHeight, BlockHash = blockHash, Headers = first ? headers.ToList() : new List<string>(),
                    CoinbaseBranch = coinbaseBranch, SignerEpoch = signerEpoch,
                };
                if (coinbaseWhole)
                {
                    part.CoinbaseTxHex = Convert.ToHexString(coinbaseBytes).ToLowerInvariant();
                    return part;
                }
                var tail = coinbaseBytes.Length % 64 == 0 ? 64 : coinbaseBytes.Length % 64;
                part.CoinbaseState = BtcSha256.StateAfter(coinbaseBytes, (coinbaseBytes.Length - tail) / 64);
                part.CoinbaseTailHex = Convert.ToHexString(coinbaseBytes.AsSpan(coinbaseBytes.Length - tail)).ToLowerInvariant();
                part.CoinbaseBytes = coinbaseBytes.Length;
                return part;
            }

            // Greedy split in block order: a part takes events until the next one would pass the budget. The part's length
            // is kept as a running sum (its text without events, each event's text, a comma between two): the whole part
            // was serialised again after every event, about 40 MB of text for one full part (fourth review, round 9).
            // Only the first maxParts parts are built: the planner signs the first, and the rest wait for it to be mined.
            var current = NewPart(first: true);
            var length = current.ToCanonicalJson().Length;
            var lengths = new List<int>();
            var stoppedEarly = false;
            foreach (var ev in events)
            {
                var eventLength = BtcReport.CanonicalLength(ev);
                var withIt = length + eventLength + (current.Events.Count > 0 ? 1 : 0);
                if (current.Events.Count > 0 && (withIt > BtcAttestRules.PartTextBudget || current.Events.Count + 1 > BtcAttestRules.MaxEventsPerReport))
                {
                    result.Add(current);
                    lengths.Add(length);
                    if (result.Count >= maxParts) { stoppedEarly = true; break; }
                    current = NewPart(first: false);
                    length = current.ToCanonicalJson().Length;
                    withIt = length + eventLength;
                }
                current.Events.Add(ev);
                length = withIt;
            }
            if (!stoppedEarly) { result.Add(current); lengths.Add(length); }
            // The running sum is the text's length exactly; checked once a part. Should it ever not be, the parts are made
            // the slow way, which every validator also gets (never a refusal to plan: that would stop reporting).
            if (result.Where((part, i) => part.ToCanonicalJson().Length != lengths[i]).Any())
            {
                VerifiedXCore.Utilities.ErrorLogUtility.LogError($"Report parts of block {blockHeight}: the running length was not exact; split the slow way.", "BtcBlockScanner.Build");
                result = SplitSlowly(events, NewPart);
                stoppedEarly = result.Count > maxParts;
                if (stoppedEarly) result = result.Take(maxParts).ToList();
            }

            for (var i = 0; i < result.Count; i++)
            {
                result[i].Part = firstPart + i;
                result[i].Final = i == result.Count - 1 && !stoppedEarly;
            }
            return result;
        }

        /// <summary>The greedy split as it was first written: the part serialised again after every event.</summary>
        internal static List<BtcReport> SplitSlowly(List<BtcReportEvent> events, Func<bool, BtcReport> newPart)
        {
            var result = new List<BtcReport>();
            var current = newPart(true);
            foreach (var ev in events)
            {
                current.Events.Add(ev);
                if (current.Events.Count > 1 && (current.ToCanonicalJson().Length > BtcAttestRules.PartTextBudget || current.Events.Count > BtcAttestRules.MaxEventsPerReport))
                {
                    current.Events.RemoveAt(current.Events.Count - 1);
                    result.Add(current);
                    current = newPart(false);
                    current.Events.Add(ev);
                }
            }
            result.Add(current);
            return result;
        }

        /// <summary>
        /// A quiet report: the next <see cref="BtcAttestRules.QuietBlocks"/> blocks above the events-through height hold
        /// no vault event. <paramref name="headers"/> are the headers from the attested tip + 1 through the last of
        /// those blocks through the header that buries them (none when they are attested already).
        /// <paramref name="lastBlockHash"/> is the hash of the last block of the range.
        /// </summary>
        public static BtcReport BuildQuiet(IReadOnlyList<string> headers, long signerEpoch, string lastBlockHash) =>
            new() { Scope = BtcReport.ScopeAll, BlockHeight = 0, BlockHash = lastBlockHash, Headers = headers.ToList(), SignerEpoch = signerEpoch };

        /// <summary>
        /// A rewind to <paramref name="rewindTo"/>: the attested headers above it are replaced by a heavier branch, and
        /// the events this chain applied from the replaced blocks are taken back. Carried by VBTC_V2_BTC_REORG.
        /// </summary>
        public static BtcReport BuildRewind(long rewindTo, IReadOnlyList<string> headers, long signerEpoch) =>
            new() { Scope = BtcReport.ScopeAll, BlockHeight = 0, ReplaceAbove = rewindTo, Headers = headers.ToList(), SignerEpoch = signerEpoch, Rewind = true };

        /// <summary>A report that replaces the attested headers above <paramref name="replaceAbove"/> with a heavier branch.</summary>
        public static BtcReport BuildHeaderReplacement(long replaceAbove, IReadOnlyList<string> headers, long signerEpoch) =>
            new() { Scope = BtcReport.ScopeAll, BlockHeight = 0, ReplaceAbove = replaceAbove, Headers = headers.ToList(), SignerEpoch = signerEpoch };
    }
}
