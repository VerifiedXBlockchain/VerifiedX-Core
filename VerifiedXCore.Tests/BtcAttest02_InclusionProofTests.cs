using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 2: proof that a transaction is in a block (docs/PLAN-vBTC-Bitcoin-Attestation.md,
    /// section 3.2). The fixture is real: the Bitcoin transactions of the twelve completed vBTC withdrawals on mainnet,
    /// each with its block header, its Merkle branch, and the block's coinbase transaction with its branch.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest02_InclusionProofTests : IDisposable
    {
        public sealed class Proof
        {
            public string Txid { get; set; } = "";
            public string Vault { get; set; } = "";
            public string Destination { get; set; } = "";
            public decimal AmountBtc { get; set; }
            public long BlockHeight { get; set; }
            public string BlockHash { get; set; } = "";
            public int TxCount { get; set; }
            public string HeaderHex { get; set; } = "";
            public long Position { get; set; }
            public List<string> Branch { get; set; } = new();
            public string TxHex { get; set; } = "";
            public string CoinbaseTxid { get; set; } = "";
            public List<string> CoinbaseBranch { get; set; } = new();
            public string CoinbaseHex { get; set; } = "";
        }

        private readonly Network? _priorNetwork;
        private readonly bool _priorTestNet;
        private readonly List<Proof> _proofs;

        public BtcAttest02_InclusionProofTests()
        {
            _priorNetwork = Globals.BTCNetwork;
            _priorTestNet = Globals.IsTestNet;
            Globals.BTCNetwork = Network.Main;
            Globals.IsTestNet = false;
            _proofs = JsonConvert.DeserializeObject<List<Proof>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "btc-proofs-mainnet.json")))!;
        }

        public void Dispose()
        {
            Globals.BTCNetwork = _priorNetwork;
            Globals.IsTestNet = _priorTestNet;
        }

        private static string Stripped(string fullHex) => BtcTxInclusion.ToStrippedHex(Transaction.Parse(fullHex, Network.Main));

        private static BtcHeader Header(Proof p)
        {
            Assert.True(BtcHeader.TryParse(p.HeaderHex, out var header));
            Assert.Equal(p.BlockHash, header!.Hash);
            Assert.True(header.MeetsOwnTarget());
            return header;
        }

        [Fact]
        public void EveryRealWithdrawal_IsProvenInItsBlock_WithTheDepthItsCoinbaseFixes()
        {
            Assert.Equal(12, _proofs.Count);
            foreach (var p in _proofs)
            {
                var header = Header(p);
                Assert.Null(BtcTxInclusion.VerifyCoinbase(header, Stripped(p.CoinbaseHex), p.CoinbaseBranch, out var depth, out var coinbase, out var coinbaseId));
                Assert.Equal(p.CoinbaseTxid, coinbaseId);
                Assert.True(coinbase!.IsCoinBase);
                // The depth is the one a tree over the block's transaction count has.
                Assert.Equal((int)Math.Ceiling(Math.Log2(p.TxCount)), depth);

                var reason = BtcTxInclusion.Verify(header, Stripped(p.TxHex), p.Position, p.Branch, depth, out var tx, out var txid);
                Assert.True(reason == null, reason);
                Assert.Equal(p.Txid, txid);
                Assert.Equal(p.Txid, tx!.GetHash().ToString());

                // What the proof establishes is read from the transaction's own bytes: it pays the withdrawal's destination.
                var destination = BitcoinAddress.Create(p.Destination, Network.Main).ScriptPubKey;
                Assert.Contains(tx.Outputs, o => o.ScriptPubKey == destination);
                Assert.True(tx.Outputs.Where(o => o.ScriptPubKey == destination).Sum(o => o.Value.Satoshi) <= (long)(p.AmountBtc * 100_000_000M));
            }
        }

        [Fact]
        public void AProofIsRefused_WhenAnythingInItIsNotWhatTheBlockHolds()
        {
            var p = _proofs[2];
            var header = Header(p);
            var stripped = Stripped(p.TxHex);
            Assert.Null(BtcTxInclusion.VerifyCoinbase(header, Stripped(p.CoinbaseHex), p.CoinbaseBranch, out var depth, out _, out _));
            string? Check(string? hex, long position, IReadOnlyList<string>? branch, BtcHeader? h = null, int? d = null) =>
                BtcTxInclusion.Verify(h ?? header, hex, position, branch, d ?? depth, out _, out _);
            Assert.Null(Check(stripped, p.Position, p.Branch));

            // Another position, a sibling changed, the branch in another order.
            Assert.Contains("is not in block", Check(stripped, p.Position ^ 1, p.Branch));
            Assert.Contains("is not in block", Check(stripped, p.Position + 2, p.Branch));
            var changed = p.Branch.ToList(); changed[3] = new string('a', 64);
            Assert.Contains("is not in block", Check(stripped, p.Position, changed));
            Assert.Contains("is not in block", Check(stripped, p.Position, p.Branch.AsEnumerable().Reverse().ToList()));
            // Another block's header.
            Assert.Contains("is not in block", Check(stripped, p.Position, p.Branch, Header(_proofs[0]), p.Branch.Count));
            // Another real transaction with this one's branch.
            Assert.Contains("is not in block", Check(Stripped(_proofs[5].TxHex), p.Position, p.Branch));

            // One byte of the transaction changed (an output amount): a different transaction, not in the block.
            var bytes = Convert.FromHexString(stripped); bytes[^10] ^= 0x01;
            var altered = Check(Convert.ToHexString(bytes).ToLowerInvariant(), p.Position, p.Branch);
            Assert.NotNull(altered);

            // The serialization WITH witness data is not what a report carries: its bytes do not hash to the txid.
            Assert.NotEqual(stripped, p.TxHex);
            Assert.Contains("canonical serialization without witness", Check(p.TxHex, p.Position, p.Branch));

            // Malformed inputs.
            Assert.Contains("not valid hex", Check("zz" + stripped.Substring(2), p.Position, p.Branch));
            Assert.Contains("not valid hex", Check(stripped + "0", p.Position, p.Branch));
            Assert.Contains("not valid hex", Check(null, p.Position, p.Branch));
            Assert.Contains("no Merkle branch", Check(stripped, p.Position, null));
            var shortHash = p.Branch.ToList(); shortHash[0] = "abcd";
            Assert.Contains("malformed", Check(stripped, p.Position, shortHash));
            Assert.Contains("malformed", Check(stripped, 1L << depth, p.Branch));
            Assert.Contains("malformed", Check(stripped, -1, p.Branch));
        }

        /// <summary>
        /// The 64-byte weakness. An inner node of the tree is 64 bytes (two hashes); a checker that takes proofs of any
        /// length can be shown an inner node as if it were a transaction, or a made-up transaction one level below a
        /// real 64-byte one. Fixing the depth from the coinbase, and refusing 64-byte transactions, closes both.
        /// </summary>
        [Fact]
        public void AProofOfAnotherLength_IsRefused_SoAnInnerNodeCannotPassAsATransaction()
        {
            var p = _proofs[0];
            var header = Header(p);
            var stripped = Stripped(p.TxHex);
            Assert.Null(BtcTxInclusion.VerifyCoinbase(header, Stripped(p.CoinbaseHex), p.CoinbaseBranch, out var depth, out _, out _));

            // The honest hashes one level up: the parent of the leaf, with the rest of the branch. The arithmetic reaches
            // the block's root (this is the trick), and it is refused because the proof is one level short.
            var parent = BtcMerkle.RootFromBranch(p.Txid, p.Position & 1, new[] { p.Branch[0] })!;
            Assert.Equal(header.MerkleRoot, BtcMerkle.RootFromBranch(parent, p.Position >> 1, p.Branch.Skip(1).ToList()));
            var oneShort = BtcTxInclusion.Verify(header, stripped, p.Position >> 1, p.Branch.Skip(1).ToList(), depth, out _, out _);
            Assert.Contains("every transaction of a block sits at the same depth", oneShort);
            // One level too many.
            var oneLong = BtcTxInclusion.Verify(header, stripped, p.Position << 1, new[] { new string('b', 64) }.Concat(p.Branch).ToList(), depth, out _, out _);
            Assert.Contains("every transaction of a block sits at the same depth", oneLong);

            // Exactly 64 bytes, the size of an inner node, is never an event here.
            Assert.Contains("exactly 64 bytes", BtcTxInclusion.Verify(header, new string('1', 128), p.Position, p.Branch, depth, out _, out _));

            // The depth comes from a real coinbase: a non-coinbase transaction cannot stand in for it, nor a coinbase at
            // a made-up depth, nor another block's coinbase.
            Assert.NotNull(BtcTxInclusion.VerifyCoinbase(header, stripped, p.Branch, out _, out _, out _));
            // (In a block built here, where an ordinary transaction sits first, the refusal names the reason.)
            var made = Network.Main.Consensus.ConsensusFactory.CreateBlock();
            for (var i = 0; i < 4; i++)
            {
                var t = Network.Main.CreateTransaction();
                t.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), i));
                t.Outputs.Add(Money.Satoshis(5000 + i), new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main));
                made.Transactions.Add(t);
            }
            made.UpdateMerkleRoot();
            Assert.True(BtcHeader.TryParse(Convert.ToHexString(made.Header.ToBytes()), out var madeHeader));
            var madeIds = made.Transactions.Select(t => t.GetHash().ToString()).ToList();
            Assert.Contains("not a coinbase", BtcTxInclusion.VerifyCoinbase(madeHeader!, BtcTxInclusion.ToStrippedHex(made.Transactions[0]), BtcMerkle.BranchFor(madeIds, 0), out _, out _, out _));
            Assert.Null(BtcTxInclusion.Verify(madeHeader!, BtcTxInclusion.ToStrippedHex(made.Transactions[2]), 2, BtcMerkle.BranchFor(madeIds, 2), 2, out _, out var madeId));
            Assert.Equal(madeIds[2], madeId);
            Assert.NotNull(BtcTxInclusion.VerifyCoinbase(header, Stripped(p.CoinbaseHex), p.CoinbaseBranch.Skip(1).ToList(), out _, out _, out _));
            Assert.NotNull(BtcTxInclusion.VerifyCoinbase(header, Stripped(_proofs[4].CoinbaseHex), _proofs[4].CoinbaseBranch, out _, out _, out _));
            Assert.Contains("out of range", BtcTxInclusion.Verify(header, stripped, p.Position, p.Branch, BtcMerkle.MaxDepth + 1, out _, out _));
        }

        /// <summary>
        /// Bitcoin has no smallest transaction: 60 bytes is one input and one output with an empty script. Refusing
        /// everything of 64 bytes or fewer meant a block whose coinbase was that small had no provable event at all, and
        /// a report carrying a small spend of a vault coin was refused by every verifier and offered again for ever
        /// (second path audit, 14 October 2026). A coinbase is taken at any size, a transaction shorter than an inner
        /// node is taken, and one of exactly 64 bytes is left out by the scanner as it is refused here.
        /// </summary>
        [Fact]
        public void ASmallCoinbase_FixesTheDepth_AndASmallTransaction_IsProven_UnlessItIsExactly64Bytes()
        {
            var made = Network.Main.Consensus.ConsensusFactory.CreateBlock();
            // A coinbase of exactly 64 bytes: a four-byte height push and one output with no script.
            var coinbase = Network.Main.CreateTransaction();
            coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue), new Script(Op.GetPushOp(800_000))));
            coinbase.Outputs.Add(Money.Coins(3.125m), Script.Empty);
            made.Transactions.Add(coinbase);
            // One input, one output: 60 bytes plus the output's script.
            NBitcoin.Transaction Small(int scriptBytes)
            {
                var t = Network.Main.CreateTransaction();
                t.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0));
                t.Outputs.Add(Money.Satoshis(1000), new Script(Enumerable.Repeat((byte)0x51, scriptBytes).ToArray()));
                return t;
            }
            made.Transactions.Add(Small(0));
            made.Transactions.Add(Small(3));
            made.Transactions.Add(Small(4));
            made.UpdateMerkleRoot();
            Assert.Equal(64, coinbase.WithOptions(TransactionOptions.None).GetSerializedSize());
            Assert.Equal(new[] { 60, 63, 64 }, made.Transactions.Skip(1).Select(t => t.WithOptions(TransactionOptions.None).GetSerializedSize()));
            Assert.True(BtcHeader.TryParse(Convert.ToHexString(made.Header.ToBytes()), out var madeHeader));
            var ids = made.Transactions.Select(t => t.GetHash().ToString()).ToList();

            Assert.Null(BtcTxInclusion.VerifyCoinbase(madeHeader!, BtcTxInclusion.ToStrippedHex(coinbase), BtcMerkle.BranchFor(ids, 0), out var depth, out _, out _));
            Assert.Equal(2, depth);
            Assert.Null(BtcTxInclusion.Verify(madeHeader!, BtcTxInclusion.ToStrippedHex(made.Transactions[1]), 1, BtcMerkle.BranchFor(ids, 1), depth, out _, out _));
            Assert.Null(BtcTxInclusion.Verify(madeHeader!, BtcTxInclusion.ToStrippedHex(made.Transactions[2]), 2, BtcMerkle.BranchFor(ids, 2), depth, out _, out _));
            Assert.Contains("exactly 64 bytes", BtcTxInclusion.Verify(madeHeader!, BtcTxInclusion.ToStrippedHex(made.Transactions[3]), 3, BtcMerkle.BranchFor(ids, 3), depth, out _, out _));
            // As an event (not as the anchor) a 64-byte coinbase is a 64-byte transaction like any other.
            Assert.Contains("exactly 64 bytes", BtcTxInclusion.Verify(madeHeader!, BtcTxInclusion.ToStrippedHex(coinbase), 0, BtcMerkle.BranchFor(ids, 0), depth, out _, out _));
            // An inner node offered as the coinbase one level up does not parse as one.
            var inner = BtcMerkle.RootFromBranch(ids[0], 0, new[] { ids[1] })!;
            Assert.NotNull(BtcTxInclusion.VerifyCoinbase(madeHeader!, string.Concat(Convert.FromHexString(ids[0]).Reverse().Concat(Convert.FromHexString(ids[1]).Reverse()).Select(b => b.ToString("x2"))),
                BtcMerkle.BranchFor(ids, 0)!.Skip(1).ToList(), out _, out _, out _));
            Assert.NotNull(inner);
        }

        /// <summary>Validators build branches from a block's transaction list; the builder and the checker agree, odd levels included.</summary>
        [Fact]
        public void BranchesBuiltFromABlocksTransactions_VerifyAgainstItsRoot()
        {
            foreach (var count in new[] { 1, 2, 3, 5, 8, 13, 100, 1023, 1025 })
            {
                var txids = Enumerable.Range(0, count).Select(i => Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant()).ToList();
                var root = BtcMerkle.RootOf(txids)!;
                var depth = count == 1 ? 0 : (int)Math.Ceiling(Math.Log2(count));
                foreach (var position in new[] { 0, count / 2, count - 1 }.Distinct())
                {
                    var branch = BtcMerkle.BranchFor(txids, position)!;
                    Assert.Equal(depth, branch.Count);                                   // every leaf at the same depth
                    Assert.Equal(root, BtcMerkle.RootFromBranch(txids[position], position, branch));
                    // The last transaction of an odd level is paired with itself, so it proves at its own position and
                    // at the one beside it. That is a property of Bitcoin's tree, and harmless here: a coin is known by
                    // its outpoint, so the same transaction shown twice is still one coin.
                    var pairedWithItself = count > 1 && branch[0] == txids[position];
                    if (count > 1 && !pairedWithItself)
                        Assert.NotEqual(root, BtcMerkle.RootFromBranch(txids[position], position ^ 1, branch));
                    if (pairedWithItself)
                        Assert.Equal(root, BtcMerkle.RootFromBranch(txids[position], position ^ 1, branch));
                }
            }
            Assert.Null(BtcMerkle.RootOf(new List<string>()));
            Assert.Null(BtcMerkle.BranchFor(new List<string> { new string('a', 64) }, 1));
            Assert.Null(BtcMerkle.RootFromBranch("abc", 0, new List<string>()));

            // NBitcoin's own tree over a block built in memory gives the same root.
            var block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
            for (var i = 0; i < 7; i++)
            {
                var tx = Network.Main.CreateTransaction();
                tx.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), i));
                tx.Outputs.Add(Money.Satoshis(1000 + i), new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main));
                block.Transactions.Add(tx);
            }
            block.UpdateMerkleRoot();
            Assert.Equal(block.Header.HashMerkleRoot.ToString(), BtcMerkle.RootOf(block.Transactions.Select(t => t.GetHash().ToString()).ToList()));
        }
    }
}
