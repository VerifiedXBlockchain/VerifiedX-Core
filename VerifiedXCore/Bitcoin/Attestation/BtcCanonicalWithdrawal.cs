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
    /// <item>Coins: the vault's unspent coins (never one pinned to another withdrawal, never a coinbase output younger
    /// than 100 blocks). If a single coin covers the amount, the smallest such coin, so a small withdrawal does not tie
    /// up the vault's largest coin. Otherwise the largest coins first until the amount is covered. Ties go to the older
    /// coin, then the lower outpoint.</item>
    /// <item>Fee: the request's fee rate times the size estimate the wallet has always used (57.5 vB per input, 43 per
    /// output for two outputs, 10.5 overhead, rounded down), taken out of the amount.</item>
    /// <item>Outputs: the destination gets the amount less the fee; the rest of the coins (inputs less the amount)
    /// returns to the vault's deposit script. Change under 546 sats is not created and goes to the fee.</item>
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

        /// <summary>The size estimate, in virtual bytes: 57.5 per input + 2 x 43 + 10.5, rounded down (154 for one input).</summary>
        public static long VSize(int inputs) => (115L * inputs + 193) / 2;

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

            var free = BtcAttestStore.FreeCoinsOf(scUID, db)
                .Where(c => c.ValueSats > 0)
                .Where(c => !c.IsCoinbase || meta.TipHeight >= c.CreatedInHeight + BtcAttestRules.CoinbaseMaturity)
                .ToList();

            List<VaultCoinRow> chosen;
            var covering = free.Where(c => c.ValueSats >= amountSats)
                .OrderBy(c => c.ValueSats).ThenBy(c => c.CreatedInHeight).ThenBy(c => c.Outpoint, StringComparer.Ordinal)
                .FirstOrDefault();
            if (covering != null)
            {
                chosen = new List<VaultCoinRow> { covering };
            }
            else
            {
                chosen = new List<VaultCoinRow>();
                long sum = 0;
                foreach (var coin in free.OrderByDescending(c => c.ValueSats).ThenBy(c => c.CreatedInHeight).ThenBy(c => c.Outpoint, StringComparer.Ordinal))
                {
                    chosen.Add(coin);
                    sum += coin.ValueSats;
                    if (sum >= amountSats) break;
                    if (chosen.Count == MaxInputs) break;
                }
                if (sum < amountSats)
                {
                    reason = chosen.Count == MaxInputs
                        ? $"the vault's {MaxInputs} largest free coins do not cover the amount"
                        : "the vault's free coins do not cover the amount (others are held by withdrawals not yet confirmed)";
                    return null;
                }
            }

            return Assemble(vault, destinationScriptHex, amountSats, feeRate, chosen, out reason);
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
            if (inputs == null || inputs.Count == 0 || inputs.Count > MaxInputs) { reason = "the transaction it replaces has no coins"; return null; }

            var chosen = new List<VaultCoinRow>();
            foreach (var outpoint in inputs)
            {
                var coin = BtcAttestStore.Coin(outpoint, db);
                if (coin == null || coin.Status != VaultCoinStatus.Pinned || coin.PinnedToRef != reference || coin.SmartContractUID != vault.SmartContractUID)
                {
                    reason = $"coin {outpoint} is not held for this withdrawal";
                    return null;
                }
                chosen.Add(coin);
            }
            return Assemble(vault, destinationScriptHex, amountSats, feeRate, chosen, out reason);
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
            if (changeSats < DustSats)
            {
                feeSats += changeSats;
                changeSats = 0;
            }

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
                tx.Outputs.Add(Money.Satoshis(destinationSats), Script.FromHex(destinationScriptHex));
                if (changeSats > 0)
                    tx.Outputs.Add(Money.Satoshis(changeSats), Script.FromHex(vault.ScriptHex));

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
