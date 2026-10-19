using LiteDB;
using NBitcoin;
using VerifiedXCore.Bitcoin.FROST;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>The one Bitcoin transaction that pays a withdrawal, as every node works it out.</summary>
    public sealed class BtcCanonicalTx
    {
        /// <summary>The transaction id. A Taproot key-path signature goes in the witness and does not change it, so it is known before anyone signs.</summary>
        public string Txid { get; init; } = "";
        /// <summary>The unsigned transaction without witness data, hex: what the vault's signers sign.</summary>
        public string TxHex { get; init; } = "";
        /// <summary>The vault coins it spends, in input order.</summary>
        public List<string> Inputs { get; init; } = new();
        public long InputSats { get; init; }
        public long DestinationSats { get; init; }
        /// <summary>What returns to the vault's own deposit script as output 1 (0 when there is no change output).</summary>
        public long ChangeSats { get; init; }
        public long FeeSats { get; init; }
    }

    /// <summary>
    /// Builds the Bitcoin transaction for a withdrawal from a vault's attested coins, by fixed rules
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.2). Because nothing in it is chosen, consensus can record which coins a
    /// withdrawal will spend, and under which transaction id, before any signature exists; the signers then build the
    /// same bytes from state and sign them.
    ///
    /// The rules:
    /// <list type="bullet">
    /// <item>Coins: the vault's unspent coins (never a coinbase output younger than 100 blocks, and one pinned to
    /// another withdrawal only under the rule of <see cref="BuildTaking"/>). If a single coin covers the amount, the smallest such coin, so a small withdrawal does not tie
    /// up the vault's largest coin. Otherwise the largest coins first until the amount is covered. Ties go to the older
    /// coin, then the lower outpoint.</item>
    /// <item>Fee: the request's fee rate times the size estimate the wallet has always used (57.5 vB per input, 43 per
    /// output for two outputs, 10.5 overhead, rounded down), taken out of the amount.</item>
    /// <item>Outputs: the destination gets the amount less the fee; the rest of the coins (inputs less the amount)
    /// returns to the vault's deposit script. A remainder under 546 sats is too small to be an output: the change is
    /// made up to 546 out of the payout, so nothing of the vault's goes to the miners.</item>
    /// <item>Version 2, lock time 0, empty input scripts, and every input with sequence fffffffd: the transaction says
    /// it may be replaced, so that Bitcoin nodes relay a replacement that pays a higher fee
    /// (<see cref="BuildReplacement"/>).</item>
    /// </list>
    /// </summary>
    public static class BtcCanonicalWithdrawal
    {
        /// <summary>The smallest payout and the smallest change output created: relayable for every standard output type.</summary>
        public const long DustSats = 546;
        /// <summary>A withdrawal that would need more coins than this is not pinned until the vault's coins allow fewer.</summary>
        public const int MaxInputs = 50;
        /// <summary>The sequence of every input: replaceable (BIP 125), with no effect on the lock time.</summary>
        public const uint InputSequence = 0xFFFFFFFD;

        /// <summary>
        /// The size estimate, in virtual bytes: 57.5 per input + 2 x 43 + 10.5, rounded up (154 for one input, 212 for
        /// two). Rounded down, a transaction with an even number of inputs paid a fraction under its fee rate, and at
        /// 1 sat/vB that is under what Bitcoin nodes relay.
        /// </summary>
        public static long VSize(int inputs) => (115L * inputs + 194) / 2;

        /// <summary>
        /// The highest fee rate a withdrawal's Bitcoin transaction may be fixed at, sat/vB. Above anything Bitcoin has
        /// asked for, and under the 10,000 sat/vB at which Bitcoin nodes refuse to send a transaction on as a likely
        /// mistake: a withdrawal fixed above that could not be broadcast, and every way out of it (a raise, a release,
        /// a takeover) needs a still higher rate, so its coins and its requester's vBTC were held with no way out
        /// (second path audit, 14 October 2026). It also bounds what a mistyped rate can cost.
        /// </summary>
        public const long MaxFeeRate = 5_000;

        /// <summary>The script a Bitcoin address pays to, on the network this chain follows; false when it is not an address there.</summary>
        public static bool TryDestinationScript(string? address, out string scriptHex)
        {
            scriptHex = "";
            if (string.IsNullOrWhiteSpace(address)) return false;
            try
            {
                scriptHex = BitcoinAddress.Create(address.Trim(), FrostDkgAttestation.ConsensusNetwork).ScriptPubKey.ToHex().ToLowerInvariant();
                return scriptHex.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// The transaction for a withdrawal of <paramref name="amountSats"/> from this vault as its coins stand now, or
        /// null with the reason when none can be built (not enough free coins, or the fee leaves too little to pay).
        /// </summary>
        public static BtcCanonicalTx? Build(string scUID, string destinationScriptHex, long amountSats, long feeRate, out string? reason, LiteDatabase? db = null)
        {
            reason = null;
            if (amountSats <= 0 || feeRate <= 0) { reason = "the amount and the fee rate must be positive"; return null; }
            var vault = BtcAttestStore.ScriptRowForVault(scUID, db);
            if (vault == null) { reason = "the vault is not watched"; return null; }
            var meta = BtcAttestStore.GetMeta(db);
            if (meta == null) { reason = "the Bitcoin view has not started"; return null; }

            var chosen = Choose(FreeCoins(vault, meta, db), amountSats, out reason);
            return chosen == null ? null : Assemble(vault, destinationScriptHex, amountSats, feeRate, chosen, out reason);
        }

        /// <summary>
        /// The vault's coins no withdrawal holds, under its current key, less coinbase outputs too young to spend. A
        /// transaction spends the coins of one key only: each input is signed by its own key's holders, and a key on
        /// its way out (its coins are being moved to the new one, plan 8.8) is not mixed into a payment.
        /// </summary>
        private static List<VaultCoinRow> FreeCoins(VaultDepositScriptRow vault, AttestationMetaRow meta, LiteDatabase? db) =>
            BtcAttestStore.FreeCoinsOf(vault.SmartContractUID, db)
                .Where(c => c.ValueSats > 0)
                .Where(c => !c.IsCoinbase || meta.TipHeight >= c.CreatedInHeight + BtcAttestRules.CoinbaseMaturity)
                .Where(c => IsUnder(c, vault, db))
                .ToList();

        /// <summary>Whether a coin is held under this key (script) of its vault.</summary>
        internal static bool IsUnder(VaultCoinRow coin, VaultDepositScriptRow key, LiteDatabase? db) =>
            string.Equals(BtcAttestStore.ScriptRowOfCoin(coin, db)?.ScriptHex, key.ScriptHex, StringComparison.Ordinal);

        /// <summary>The coin rule: the smallest single coin that covers the amount, otherwise the largest coins first.</summary>
        private static List<VaultCoinRow>? Choose(List<VaultCoinRow> coins, long amountSats, out string? reason)
        {
            reason = null;
            var covering = coins.Where(c => c.ValueSats >= amountSats)
                .OrderBy(c => c.ValueSats).ThenBy(c => c.CreatedInHeight).ThenBy(c => c.Outpoint, StringComparer.Ordinal)
                .FirstOrDefault();
            if (covering != null) return new List<VaultCoinRow> { covering };

            var chosen = new List<VaultCoinRow>();
            long sum = 0;
            foreach (var coin in coins.OrderByDescending(c => c.ValueSats).ThenBy(c => c.CreatedInHeight).ThenBy(c => c.Outpoint, StringComparer.Ordinal))
            {
                chosen.Add(coin);
                sum += coin.ValueSats;
                if (sum >= amountSats) break;
                if (chosen.Count == MaxInputs) break;
            }
            if (sum >= amountSats) return chosen;
            reason = chosen.Count == MaxInputs
                ? $"the vault's {MaxInputs} largest free coins do not cover the amount"
                : "the vault's free coins do not cover the amount (others are held by withdrawals not yet confirmed)";
            return null;
        }

        /// <summary>
        /// The transaction for a withdrawal that the vault's free coins cannot pay, built on those and on coins held by
        /// withdrawals that have stopped moving (<see cref="BtcWithdrawals.TakeableCoins"/>): the same coin rule over
        /// both. It must be able to replace, on Bitcoin, every transaction it conflicts with: its fee rate is above
        /// theirs (the caller offers only such coins) and its fee is at least the fees of the newest transaction of
        /// each withdrawal it takes from plus one sat per virtual byte of its own size. Null with the reason when no
        /// coin is takeable, the coins still do not cover the amount, or the fee is too small to replace them.
        /// <paramref name="takenFrom"/> names the withdrawals whose coins it spends.
        /// </summary>
        public static BtcCanonicalTx? BuildTaking(string scUID, string destinationScriptHex, long amountSats, long feeRate, long vfxHeight,
            out List<string> takenFrom, out string? reason, LiteDatabase? db = null)
        {
            reason = null;
            takenFrom = new List<string>();
            if (amountSats <= 0 || feeRate <= 0) { reason = "the amount and the fee rate must be positive"; return null; }
            var vault = BtcAttestStore.ScriptRowForVault(scUID, db);
            if (vault == null) { reason = "the vault is not watched"; return null; }
            var meta = BtcAttestStore.GetMeta(db);
            if (meta == null) { reason = "the Bitcoin view has not started"; return null; }

            var takeable = BtcWithdrawals.TakeableCoins(scUID, feeRate, vfxHeight, db).Where(t => IsUnder(t.Coin, vault, db)).ToList();
            if (takeable.Count == 0) { reason = "no coin of the vault is held by a withdrawal that has stopped moving and pays a lower fee rate"; return null; }

            var chosen = Choose(FreeCoins(vault, meta, db).Concat(takeable.Select(t => t.Coin)).ToList(), amountSats, out reason);
            if (chosen == null) return null;
            var tx = Assemble(vault, destinationScriptHex, amountSats, feeRate, chosen, out reason);
            if (tx == null) return null;

            var replaced = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var (coin, holders) in takeable.Where(t => chosen.Any(c => c.Outpoint == t.Coin.Outpoint)))
                foreach (var holder in holders)
                    replaced[holder.Ref] = holder.Pins.Last(p => !p.Dead).FeeSats;
            if (replaced.Count == 0) { reason = "the vault's free coins cover it"; return null; } // the caller builds from free coins first
            var needed = replaced.Values.Sum() + VSize(chosen.Count);
            if (tx.FeeSats < needed)
            {
                reason = $"its Bitcoin fee of {tx.FeeSats} sats could not replace the transaction(s) holding the coins ({needed} sats needed)";
                return null;
            }
            takenFrom = replaced.Keys.ToList();
            return tx;
        }

        /// <summary>
        /// The transaction that moves a replaced key's coins to the vault's current key (plan 8.8): the old key's free
        /// coins, largest first (up to <see cref="MaxInputs"/>), and the coins of withdrawals on that key that have
        /// stopped moving (<see cref="BtcWithdrawals.TakeableCoins"/>), as for a withdrawal that takes them; one output,
        /// everything less the fee, to the current key. Null with the reason when there is nothing to move or the fee
        /// leaves less than an output. Without the coins it would take when its fee could not replace their transactions.
        /// </summary>
        public static BtcCanonicalTx? BuildSweep(VaultDepositScriptRow from, VaultDepositScriptRow to, long feeRate, long vfxHeight,
            out List<string> takenFrom, out string? reason, LiteDatabase? db = null)
        {
            reason = null;
            takenFrom = new List<string>();
            if (feeRate <= 0) { reason = "the fee rate must be positive"; return null; }
            if (from.ScriptHex == to.ScriptHex || from.SmartContractUID != to.SmartContractUID) { reason = "a sweep moves coins from one key of a vault to another"; return null; }
            var meta = BtcAttestStore.GetMeta(db);
            if (meta == null) { reason = "the Bitcoin view has not started"; return null; }

            // A coin worth no more than the fee its input adds is left where it is: anyone could send a vault dust and have
            // every sweep spend more on it than it holds (fourth review, round 3). It goes with a later sweep when fees fall.
            var free = FreeCoins(from, meta, db).Where(c => c.ValueSats * 2 > 115L * feeRate).ToList();
            var takeable = BtcWithdrawals.TakeableCoins(from.SmartContractUID, feeRate, vfxHeight, db)
                .Where(t => IsUnder(t.Coin, from, db) && t.Holders.All(h => !h.IsSweep && !h.IsBridgeExit)).ToList();
            BtcCanonicalTx? Try(List<VaultCoinRow> coins, out string? why)
            {
                var chosen = coins.OrderByDescending(c => c.ValueSats).ThenBy(c => c.CreatedInHeight).ThenBy(c => c.Outpoint, StringComparer.Ordinal).Take(MaxInputs).ToList();
                if (chosen.Count == 0) { why = "the key holds no free coin"; return null; }
                return Assemble(to, to.ScriptHex, chosen.Sum(c => c.ValueSats), feeRate, chosen, out why);
            }

            if (takeable.Count > 0)
            {
                var withTaken = Try(free.Concat(takeable.Select(t => t.Coin)).ToList(), out reason);
                if (withTaken != null)
                {
                    var replaced = new SortedDictionary<string, long>(StringComparer.Ordinal);
                    foreach (var (coin, holders) in takeable.Where(t => withTaken.Inputs.Contains(t.Coin.Outpoint)))
                        foreach (var holder in holders)
                            replaced[holder.Ref] = holder.Pins.Last(p => !p.Dead).FeeSats;
                    if (replaced.Count == 0 || withTaken.FeeSats >= replaced.Values.Sum() + VSize(withTaken.Inputs.Count))
                    {
                        takenFrom = replaced.Keys.ToList();
                        return withTaken;
                    }
                }
            }
            return Try(free, out reason);
        }

        /// <summary>
        /// The replacement for a withdrawal's pinned transaction at a higher fee rate: the same coins in the same order,
        /// the same outputs, with the larger fee taken out of the amount paid to the destination. Null with the reason
        /// when the coins are no longer held for this withdrawal or the fee leaves too little to pay.
        /// </summary>
        public static BtcCanonicalTx? BuildReplacement(string scUID, string destinationScriptHex, long amountSats, long feeRate, IReadOnlyList<string> inputs,
            string reference, out string? reason, LiteDatabase? db = null)
        {
            reason = null;
            if (amountSats <= 0 || feeRate <= 0) { reason = "the amount and the fee rate must be positive"; return null; }
            var vault = BtcAttestStore.ScriptRowForVault(scUID, db);
            if (vault == null) { reason = "the vault is not watched"; return null; }
            var chosen = HeldCoins(vault, inputs, reference, out reason, db);
            return chosen == null ? null : Assemble(vault, destinationScriptHex, amountSats, feeRate, chosen, out reason);
        }

        /// <summary>The coins of a withdrawal's newest transaction, as state holds them; null when one is no longer held for it.</summary>
        private static List<VaultCoinRow>? HeldCoins(VaultDepositScriptRow vault, IReadOnlyList<string> inputs, string reference, out string? reason, LiteDatabase? db)
        {
            reason = null;
            if (inputs == null || inputs.Count == 0 || inputs.Count > MaxInputs) { reason = "the transaction it replaces has no coins"; return null; }

            var chosen = new List<VaultCoinRow>();
            foreach (var outpoint in inputs)
            {
                var coin = BtcAttestStore.Coin(outpoint, db);
                // Frozen too (#293): a frozen coin keeps its holders, and a false freeze must not strand a fee raise or release.
                if (coin == null || coin.Status is not (VaultCoinStatus.Pinned or VaultCoinStatus.Frozen) || !BtcWithdrawals.RefsOn(coin).Contains(reference, StringComparer.Ordinal) || coin.SmartContractUID != vault.SmartContractUID)
                {
                    reason = $"coin {outpoint} is not held for this withdrawal";
                    return null;
                }
                chosen.Add(coin);
            }
            return chosen;
        }

        /// <summary>
        /// The transaction that ends a withdrawal without paying it: the same coins in the same order, and one output,
        /// everything less the fee, back to the vault's own deposit script. It conflicts with every payment fixed for
        /// the withdrawal, so once Bitcoin shows it none of them can confirm. Null with the reason when the coins are
        /// no longer held for this withdrawal or the fee leaves less than a dust output.
        /// </summary>
        public static BtcCanonicalTx? BuildRelease(string scUID, long feeRate, IReadOnlyList<string> inputs, string reference, out string? reason, LiteDatabase? db = null)
        {
            reason = null;
            if (feeRate <= 0) { reason = "the fee rate must be positive"; return null; }
            var vault = BtcAttestStore.ScriptRowForVault(scUID, db);
            if (vault == null) { reason = "the vault is not watched"; return null; }
            var chosen = HeldCoins(vault, inputs, reference, out reason, db);
            if (chosen == null) return null;

            var inputSats = chosen.Sum(c => c.ValueSats);
            long feeSats;
            try { feeSats = checked(VSize(chosen.Count) * feeRate); }
            catch (OverflowException) { reason = "the fee rate is out of range"; return null; }
            if (feeSats > inputSats - DustSats)
            {
                reason = $"a fee of {feeSats} sats ({chosen.Count} input(s) at {feeRate} sat/vB) leaves less than {DustSats} sats of the coins";
                return null;
            }
            return Serialize(chosen, new[] { (vault.ScriptHex, inputSats - feeSats) }, inputSats, 0, inputSats - feeSats, feeSats, out reason);
        }

        private static BtcCanonicalTx? Assemble(VaultDepositScriptRow vault, string destinationScriptHex, long amountSats, long feeRate, List<VaultCoinRow> chosen, out string? reason)
        {
            reason = null;
            var inputSats = chosen.Sum(c => c.ValueSats);
            long feeSats;
            try { feeSats = checked(VSize(chosen.Count) * feeRate); }
            catch (OverflowException) { reason = "the fee rate is out of range"; return null; }
            if (feeSats > amountSats - DustSats)
            {
                reason = $"a fee of {feeSats} sats ({chosen.Count} input(s) at {feeRate} sat/vB) leaves less than {DustSats} sats to pay out of {amountSats}";
                return null;
            }

            var destinationSats = amountSats - feeSats;
            var changeSats = inputSats - amountSats;
            if (changeSats > 0 && changeSats < DustSats)
            {
                // What the coins hold beyond the amount is the vault's, and too little to be an output. It is not
                // given to the miners (nobody was debited for it; any holder could have burned the owner's remainder
                // a few hundred sats at a time): the change is made up to the smallest output out of the payout, so
                // the cost of an amount that leaves an awkward remainder is the requester's.
                var topUp = DustSats - changeSats;
                if (destinationSats - topUp < DustSats)
                {
                    reason = $"the coins hold {changeSats} sats more than the amount, too little for a change output; making it up from the payout leaves less than {DustSats} sats to pay";
                    return null;
                }
                destinationSats -= topUp;
                changeSats = DustSats;
            }

            var outputs = new List<(string ScriptHex, long Sats)> { (destinationScriptHex, destinationSats) };
            if (changeSats > 0) outputs.Add((vault.ScriptHex, changeSats));
            return Serialize(chosen, outputs, inputSats, destinationSats, changeSats, feeSats, out reason);
        }

        private static BtcCanonicalTx? Serialize(List<VaultCoinRow> chosen, IReadOnlyList<(string ScriptHex, long Sats)> outputs, long inputSats,
            long destinationSats, long changeSats, long feeSats, out string? reason)
        {
            reason = null;
            try
            {
                var network = FrostDkgAttestation.ConsensusNetwork;
                var tx = network.CreateTransaction();
                tx.Version = 2;
                tx.LockTime = LockTime.Zero;
                foreach (var coin in chosen)
                {
                    var split = coin.Outpoint.IndexOf(':');
                    tx.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(coin.Outpoint.Substring(0, split)), uint.Parse(coin.Outpoint.Substring(split + 1))))
                    {
                        Sequence = new Sequence(InputSequence),
                    });
                }
                foreach (var (scriptHex, sats) in outputs)
                    tx.Outputs.Add(Money.Satoshis(sats), Script.FromHex(scriptHex));

                var bytes = tx.WithOptions(TransactionOptions.None).ToBytes();
                return new BtcCanonicalTx
                {
                    Txid = BtcMerkle.TxidOfStripped(bytes),
                    TxHex = Convert.ToHexString(bytes).ToLowerInvariant(),
                    Inputs = chosen.Select(c => c.Outpoint).ToList(),
                    InputSats = inputSats, DestinationSats = destinationSats, ChangeSats = changeSats, FeeSats = feeSats,
                };
            }
            catch (Exception ex)
            {
                reason = $"the transaction could not be built: {ex.Message}";
                return null;
            }
        }
    }
}
