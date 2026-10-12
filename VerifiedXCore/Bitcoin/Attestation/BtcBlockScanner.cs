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
        public static List<BtcReport> BuildBackfill(Block block, long blockHeight, string scUID, long signerEpoch, LiteDatabase? db = null) =>
            Build(block, blockHeight, scUID, Array.Empty<string>(), signerEpoch, db, afterPosition: -1, firstPart: 1);

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
                var isCoinbase = position == 0 && tx.IsCoinBase;
                var spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                var outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();
                BtcReportVerifier.DeriveEffects(tx.GetHash().ToString(), isCoinbase, spends, outputs, blockHeight, null, overlay, effects, db);
            }
            return effects.Select(e => e.SmartContractUID).Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToList();
        }

        private static List<BtcReport> Build(Block block, long blockHeight, string scope, IReadOnlyList<string> headers, long signerEpoch, LiteDatabase? db,
            long afterPosition, int firstPart)
        {
            var result = new List<BtcReport>();
            if (block == null || block.Transactions.Count == 0) return result;
            var scopedVault = scope == BtcReport.ScopeAll ? null : scope;

            var txids = block.Transactions.Select(t => t.GetHash().ToString()).ToList();
            // Every proof in a block rests on the block's coinbase transaction, which each part carries. A coinbase too
            // large for that (a miner paying out to thousands of addresses) leaves the listing form for the whole block.
            var coinbaseHex = BtcTxInclusion.ToStrippedHex(block.Transactions[0]);
            var proofs = coinbaseHex.Length / 2 <= BtcAttestRules.MaxCoinbaseProofBytes;
            var overlay = new Dictionary<string, VaultCoinStatus>(StringComparer.Ordinal);
            var effects = new List<BtcCoinEffect>();
            var events = new List<BtcReportEvent>();

            // Transactions at or before afterPosition belong to parts already mined. They are skipped whole, as the
            // verifier does: anything of theirs that is still unrecorded (a vault created since) is a backfill later.
            for (var position = (int)Math.Max(0, afterPosition + 1); position < block.Transactions.Count; position++)
            {
                var tx = block.Transactions[position];
                var isCoinbase = position == 0 && tx.IsCoinBase;
                var spends = isCoinbase ? new List<string>() : tx.Inputs.Select(i => BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();
                var outputs = tx.Outputs.Select((o, i) => ((long)i, o.ScriptPubKey.ToHex().ToLowerInvariant(), o.Value.Satoshi)).ToList();

                var mark = effects.Count;
                if (BtcReportVerifier.DeriveEffects(txids[position], isCoinbase, spends, outputs, blockHeight, scopedVault, overlay, effects, db) == 0)
                    continue;

                var stripped = BtcTxInclusion.ToStrippedHex(tx);
                if (proofs && stripped.Length / 2 <= BtcTxInclusion.MaxTxBytes)
                {
                    events.Add(new BtcReportEvent { Position = position, TxHex = stripped, Branch = BtcMerkle.BranchFor(txids, position) });
                }
                else
                {
                    // Too large to carry (or the block's coinbase is): list what it does, on the signatures alone.
                    // Only what this layer acts on is listed.
                    var mine = effects.Skip(mark).ToList();
                    events.Add(new BtcReportEvent
                    {
                        Position = position, Txid = txids[position],
                        Spends = mine.Where(e => e.IsSpend).Select(e => e.Outpoint).ToList(),
                        Creates = mine.Where(e => !e.IsSpend).Select(e =>
                        {
                            var vout = long.Parse(e.Outpoint.Substring(e.Outpoint.IndexOf(':') + 1));
                            return new BtcReportOutput { Vout = vout, ScriptHex = outputs[(int)vout].Item2, Sats = e.Sats };
                        }).ToList(),
                    });
                }
            }
            if (events.Count == 0)
            {
                if (firstPart > 1)
                    result.Add(new BtcReport { Scope = scope, BlockHeight = blockHeight, SignerEpoch = signerEpoch, Part = firstPart, Final = true });
                return result;
            }

            var coinbaseBranch = BtcMerkle.BranchFor(txids, 0)!;

            BtcReport NewPart(bool first) => new()
            {
                Scope = scope, BlockHeight = blockHeight, Headers = first ? headers.ToList() : new List<string>(),
                CoinbaseTxHex = proofs ? coinbaseHex : null, CoinbaseBranch = proofs ? coinbaseBranch : null, SignerEpoch = signerEpoch,
            };

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
                if (result[i].Events.All(e => e.TxHex == null))
                {
                    // Nothing in this part rests on the coinbase.
                    result[i].CoinbaseTxHex = null;
                    result[i].CoinbaseBranch = null;
                }
            }
            return result;
        }

        /// <summary>
        /// A quiet report: the next <see cref="BtcAttestRules.QuietBlocks"/> blocks above the events-through height hold
        /// no vault event. <paramref name="headers"/> are the headers from the attested tip + 1 through the last of
        /// those blocks + the depth (none when they are attested already).
        /// </summary>
        public static BtcReport BuildQuiet(IReadOnlyList<string> headers, long signerEpoch) =>
            new() { Scope = BtcReport.ScopeAll, BlockHeight = 0, Headers = headers.ToList(), SignerEpoch = signerEpoch };

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
