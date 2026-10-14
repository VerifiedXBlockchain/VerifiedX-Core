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
        public static List<BtcReport> BuildForward(Block block, long blockHeight, IReadOnlyList<string> headers, long signerEpoch, LiteDatabase? db = null) =>
            Build(block, blockHeight, BtcReport.ScopeAll, headers, signerEpoch, db, afterPosition: -1, firstPart: 1);

        /// <summary>
        /// The rest of a block's report once some of its parts are mined: built from the state those parts left, the
        /// events after <paramref name="afterPosition"/> only, numbered from <paramref name="nextPart"/>. Never empty:
        /// when nothing is left, one part with no events closes the block.
        /// </summary>
        public static List<BtcReport> BuildForwardRest(Block block, long blockHeight, long afterPosition, int nextPart, long signerEpoch, LiteDatabase? db = null) =>
            Build(block, blockHeight, BtcReport.ScopeAll, Array.Empty<string>(), signerEpoch, db, afterPosition, nextPart);

        /// <summary>The backfill report for one vault in one already-reported block; empty when the block holds nothing new for it.</summary>
        /// <remarks>
        /// A backfill is one part (the verifier takes no other). When a vault's unrecorded events in the block do not fit
        /// in one, the report carries the first part's worth and is final; the rest are still unrecorded afterwards, and
        /// the next backfill of the block carries them. A built second part used to be marked as such and was refused by
        /// every node, and the sweep stopped at that block (fourth review).
        /// </remarks>
        public static List<BtcReport> BuildBackfill(Block block, long blockHeight, string scUID, long signerEpoch, LiteDatabase? db = null)
        {
            var parts = Build(block, blockHeight, scUID, Array.Empty<string>(), signerEpoch, db, afterPosition: -1, firstPart: 1);
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
                if (CannotBeProven(tx, position)) continue; // never an event (see Build)
                var isCoinbase = position == 0 && tx.IsCoinBase;
                var spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                var outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();
                BtcReportVerifier.DeriveEffects(tx.GetHash().ToString(), isCoinbase, spends, outputs, blockHeight, null, overlay, effects, db);
            }
            return effects.Select(e => e.SmartContractUID).Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// A transaction larger than a report can carry (<see cref="BtcTxInclusion.MaxTxBytes"/> without witness data:
        /// larger than any Bitcoin node relays). It is not an event and nothing it does is recorded: an event is a
        /// transaction with its proof, and nothing else. What such a transaction pays to a vault is not credited.
        /// </summary>
        private static bool TooLargeToProve(Transaction tx) =>
            tx.WithOptions(TransactionOptions.None).GetSerializedSize() > BtcTxInclusion.MaxTxBytes;

        /// <summary>
        /// A transaction the verifier would refuse as an event whatever its proof: one too large to carry. It is left
        /// out here exactly as the verifier leaves it out.
        /// </summary>
        private static bool CannotBeProven(Transaction tx, int position) => TooLargeToProve(tx);

        private static List<BtcReport> Build(Block block, long blockHeight, string scope, IReadOnlyList<string> headers, long signerEpoch, LiteDatabase? db,
            long afterPosition, int firstPart)
        {
            var result = new List<BtcReport>();
            if (block == null || block.Transactions.Count == 0) return result;
            var scopedVault = scope == BtcReport.ScopeAll ? null : scope;

            var txids = block.Transactions.Select(t => t.GetHash().ToString()).ToList();
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
                if (CannotBeProven(tx, position)) continue; // before anything of it is counted: the verifier never sees it
                var isCoinbase = position == 0 && tx.IsCoinBase;
                var spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                var outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();

                if (BtcReportVerifier.DeriveEffects(txids[position], isCoinbase, spends, outputs, blockHeight, scopedVault, overlay, effects, db) == 0)
                    continue;
                events.Add(new BtcReportEvent { Position = position, TxHex = BtcTxInclusion.ToStrippedHex(tx), Branch = BtcMerkle.BranchFor(txids, position) });
            }
            if (events.Count == 0)
            {
                if (firstPart > 1)
                    result.Add(new BtcReport { Scope = scope, BlockHeight = blockHeight, BlockHash = blockHash, SignerEpoch = signerEpoch, Part = firstPart, Final = true });
                return result;
            }

            var coinbaseBranch = BtcMerkle.BranchFor(txids, 0)!;

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

            // Greedy split in block order: a part takes events until the next one would pass the budget.
            var current = NewPart(first: true);
            foreach (var ev in events)
            {
                current.Events.Add(ev);
                if (current.Events.Count > 1 && (current.ToCanonicalJson().Length > BtcAttestRules.PartTextBudget || current.Events.Count > BtcAttestRules.MaxEventsPerReport))
                {
                    current.Events.RemoveAt(current.Events.Count - 1);
                    result.Add(current);
                    current = NewPart(first: false);
                    current.Events.Add(ev);
                }
            }
            result.Add(current);

            for (var i = 0; i < result.Count; i++)
            {
                result[i].Part = firstPart + i;
                result[i].Final = i == result.Count - 1;
            }
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
