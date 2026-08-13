using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.ElectrumX.Results;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// FIND-028 retry determinism (Aug 2026 testnet incident): a single-input withdrawal against a
    /// deposit holding two equal-value UTXOs picked a different coin on retry (the UTXO sort had no
    /// tiebreaker over Electrum-server order), changing the sighash — the validators' per-withdrawal
    /// sighash pin then correctly refused every rebuild and wedged the contract. Fixes under test:
    /// (1) fully deterministic UTXO ordering; (2) pinned-build round-trip — the exact unsigned tx +
    /// coins persisted on the request row must reproduce identical BIP341 sighashes when reloaded.
    /// </summary>
    public class VBTCWithdrawalPinnedBuildTests
    {
        private static BlockchainScripthashListunspentResult Utxo(string txHash, uint txPos, ulong value) =>
            new BlockchainScripthashListunspentResult { TxHash = txHash, TxPos = txPos, Value = value, Height = 100 };

        // ── Deterministic sort ───────────────────────────────────────────

        [Fact]
        public void SortUtxosDeterministic_EqualValues_OrderIndependentOfInput()
        {
            var a = Utxo("bb".PadRight(64, '0'), 0, 500_000);
            var b = Utxo("aa".PadRight(64, '0'), 1, 500_000);
            var c = Utxo("aa".PadRight(64, '0'), 0, 500_000);
            var big = Utxo("cc".PadRight(64, '0'), 5, 900_000);

            var order1 = BitcoinTransactionService.SortUtxosDeterministic(new[] { a, b, c, big });
            var order2 = BitcoinTransactionService.SortUtxosDeterministic(new[] { big, c, b, a });
            var order3 = BitcoinTransactionService.SortUtxosDeterministic(new[] { c, a, big, b });

            string Key(BlockchainScripthashListunspentResult u) => $"{u.TxHash}:{u.TxPos}";
            Assert.Equal(order1.Select(Key), order2.Select(Key));
            Assert.Equal(order1.Select(Key), order3.Select(Key));

            // value DESC first, then txid, then vout
            Assert.Equal("cc".PadRight(64, '0') + ":5", Key(order1[0]));
            Assert.Equal("aa".PadRight(64, '0') + ":0", Key(order1[1]));
            Assert.Equal("aa".PadRight(64, '0') + ":1", Key(order1[2]));
            Assert.Equal("bb".PadRight(64, '0') + ":0", Key(order1[3]));
        }

        // ── Pinned coin round-trip ───────────────────────────────────────

        [Fact]
        public void PinnedWithdrawalCoin_JsonAndCoinRoundTrip_Identical()
        {
            var key = new Key();
            var scriptPubKey = key.PubKey.GetTaprootFullPubKey().ScriptPubKey;
            var original = new Coin(
                new OutPoint(uint256.Parse("11".PadRight(64, '2')), 3),
                new TxOut(Money.Satoshis(500_000), scriptPubKey));

            var dto = PinnedWithdrawalCoin.FromCoin(original);
            var json = JsonConvert.SerializeObject(new List<PinnedWithdrawalCoin> { dto });
            var restoredDto = JsonConvert.DeserializeObject<List<PinnedWithdrawalCoin>>(json)!.Single();
            var restored = restoredDto.ToCoin();

            Assert.Equal(original.Outpoint, restored.Outpoint);
            Assert.Equal(original.TxOut.Value, restored.TxOut.Value);
            Assert.Equal(original.TxOut.ScriptPubKey, restored.TxOut.ScriptPubKey);
            Assert.Equal($"{original.Outpoint.Hash}:{original.Outpoint.N}".ToLowerInvariant(), restoredDto.ToOutpointKey());
        }

        // ── Sighash reproduction through the pinned round-trip ──────────

        [Fact]
        public void PinnedBuild_RoundTrip_ReproducesIdenticalTaprootSighashes()
        {
            var network = Network.TestNet;
            var depositKey = new Key();
            var destKey = new Key();
            var depositAddress = depositKey.PubKey.GetTaprootFullPubKey().GetAddress(network);
            var destAddress = destKey.PubKey.GetTaprootFullPubKey().GetAddress(network);

            // Two equal-value prevouts — the incident's exact shape.
            var coins = new List<Coin>
            {
                new Coin(new OutPoint(uint256.Parse("aa".PadRight(64, 'a')), 0), new TxOut(Money.Satoshis(500_000), depositAddress.ScriptPubKey)),
                new Coin(new OutPoint(uint256.Parse("bb".PadRight(64, 'b')), 1), new TxOut(Money.Satoshis(500_000), depositAddress.ScriptPubKey)),
            };

            var builder = network.CreateTransactionBuilder();
            builder.AddCoins(coins.ToArray());
            builder.Send(destAddress, Money.Satoshis(700_000));
            builder.SetChange(depositAddress);
            builder.SendFees(Money.Satoshis(2_000));
            var unsignedTx = builder.BuildTransaction(false);

            // Persist exactly like the coordinator does…
            var pinnedHex = unsignedTx.ToHex();
            var pinnedJson = JsonConvert.SerializeObject(coins.Select(PinnedWithdrawalCoin.FromCoin).ToList());

            // …and reload exactly like a retry does.
            var reloadedTx = Transaction.Parse(pinnedHex, network);
            var reloadedCoins = JsonConvert.DeserializeObject<List<PinnedWithdrawalCoin>>(pinnedJson)!
                .Select(c => c.ToCoin()).ToList();

            var origPrecomputed = unsignedTx.PrecomputeTransactionData(coins.Select(c => c.TxOut).ToArray());
            var reloadPrecomputed = reloadedTx.PrecomputeTransactionData(reloadedCoins.Select(c => c.TxOut).ToArray());

            Assert.Equal(unsignedTx.GetHash(), reloadedTx.GetHash());
            for (int i = 0; i < unsignedTx.Inputs.Count; i++)
            {
                var execData = new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default };
                var origSighash = unsignedTx.GetSignatureHashTaproot(origPrecomputed, execData);
                var reloadSighash = reloadedTx.GetSignatureHashTaproot(reloadPrecomputed, execData);
                Assert.Equal(origSighash, reloadSighash);
            }
        }
    }
}
