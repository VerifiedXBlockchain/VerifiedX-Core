using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 4: the report transaction against real Bitcoin (plan sections 3 and 8.1). Nothing
    /// here is made for the test except the vault contract and the validators: the checkpoint is twelve real mainnet
    /// headers ending at block 967,680, the rules are Bitcoin's mainnet rules with the mainnet depth of six, and the
    /// events are a real deposit to a vBTC vault address (block 967,702) and the real withdrawal that spent it and
    /// returned change to the same address (block 967,729).
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest04_MainnetFlowTests : BtcAttestTestBase
    {
        public sealed class Proven
        {
            public string Txid { get; set; } = "";
            public long BlockHeight { get; set; }
            public string BlockHash { get; set; } = "";
            public long Position { get; set; }
            public List<string> Branch { get; set; } = new();
            public string TxHex { get; set; } = "";
            public string CoinbaseTxid { get; set; } = "";
            public List<string> CoinbaseBranch { get; set; } = new();
            public string CoinbaseHex { get; set; } = "";
        }

        public sealed class Output
        {
            public long Vout { get; set; }
            public long ValueSats { get; set; }
        }

        public sealed class Flow
        {
            public string Script { get; set; } = "";
            public string Address { get; set; } = "";
            public long Vout { get; set; }
            public long ValueSats { get; set; }
            public Proven Deposit { get; set; } = new();
            public Proven Spend { get; set; } = new();
            public List<Output> SpendOutputsToScript { get; set; } = new();
            public List<string> SpendInputsFromScript { get; set; } = new();
        }

        private const long Checkpoint = 967_680; // 480 x 2016
        private readonly Flow _flow;
        private readonly Dictionary<long, string> _headers = new();

        public BtcAttest04_MainnetFlowTests() : base(realMainnetRules: true)
        {
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            _flow = JsonConvert.DeserializeObject<Flow>(File.ReadAllText(Path.Combine(fixtures, "btc-flow-mainnet.json")))!;
            foreach (var line in File.ReadAllLines(Path.Combine(fixtures, "btc-headers-mainnet.txt")))
            {
                var parts = line.Split(',');
                if (parts.Length == 2) _headers[long.Parse(parts[0])] = parts[1].Trim();
            }
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Join("\n", Headers(Checkpoint - 11, Checkpoint));
        }

        private List<string> Headers(long from, long to) => Enumerable.Range(0, (int)(to - from + 1)).Select(i => _headers[from + i]).ToList();

        private static string Stripped(string fullHex) => BtcTxInclusion.ToStrippedHex(Transaction.Parse(fullHex, Network.Main));

        private string HashAt(long height) => BtcHeader.TryParse(_headers[height], out var header) ? header!.Hash : throw new InvalidOperationException($"no header {height}");

        private BtcReport ReportOf(Proven p, long headersFrom, long headersTo) => new()
        {
            BlockHeight = p.BlockHeight, BlockHash = HashAt(p.BlockHeight), Headers = Headers(headersFrom, headersTo),
            CoinbaseTxHex = Stripped(p.CoinbaseHex), CoinbaseBranch = p.CoinbaseBranch,
            Events = { new BtcReportEvent { Position = p.Position, TxHex = Stripped(p.TxHex), Branch = p.Branch } },
        };

        [Fact]
        public async Task ARealDeposit_AndTheRealWithdrawalThatSpentIt_AreFollowedFromBitcoinAlone()
        {
            Assert.Equal(6, BtcAttestRules.Depth);
            Assert.Same(BtcChainRules.Mainnet, BtcChainRules.Current);

            await Start();
            var meta = BtcAttestStore.GetMeta()!;
            Assert.Equal(Checkpoint, meta.TipHeight);
            Assert.True(BtcHeader.TryParse(_headers[Checkpoint], out var checkpointTip));
            Assert.Equal(checkpointTip!.Hash, meta.TipHash);

            var vault = await CreateVault(H1 + 1, _flow.Address);
            Assert.Equal(_flow.Script, BtcAttestStore.ScriptRowForVault(vault.Uid)!.ScriptHex);
            var depositAt = _flow.Deposit.BlockHeight;
            var depositCoin = BtcAttestStore.OutpointKey(_flow.Deposit.Txid, _flow.Vout);

            // Five blocks on top of the deposit: one short of the mainnet depth.
            var (ok, why) = await Verify(Signed(ReportOf(_flow.Deposit, Checkpoint + 1, depositAt + 5)), H1 + 2);
            Assert.False(ok);
            Assert.Contains("is not under 6 attested headers", why);

            var deposit = Signed(ReportOf(_flow.Deposit, Checkpoint + 1, depositAt + 6));
            (ok, why) = await Verify(deposit, H1 + 2);
            Assert.True(ok, why);
            await Apply(H1 + 2, deposit);

            var coin = BtcAttestStore.Coin(depositCoin)!;
            Assert.Equal(vault.Uid, coin.SmartContractUID);
            Assert.Equal(_flow.ValueSats, coin.ValueSats);
            Assert.Equal(VaultCoinStatus.Unspent, coin.Status);
            Assert.Equal(depositAt, coin.CreatedInHeight);
            Assert.Equal(_flow.ValueSats, BtcAttestStore.Total(vault.Uid).HeldSats);
            meta = BtcAttestStore.GetMeta()!;
            Assert.Equal(depositAt + 6, meta.TipHeight);
            Assert.Equal(depositAt, meta.EventsThroughHeight);
            Assert.Equal(_flow.Deposit.BlockHash, BtcAttestStore.HeaderAt(depositAt)!.Hash);

            // The withdrawal: a different block, a tree one level shallower, the coin spent and change returned.
            var spendAt = _flow.Spend.BlockHeight;
            Assert.Equal(depositCoin, Assert.Single(_flow.SpendInputsFromScript));
            var spend = Signed(ReportOf(_flow.Spend, depositAt + 7, spendAt + 6));
            (ok, why) = await Verify(spend, H1 + 3);
            Assert.True(ok, why);
            await Apply(H1 + 3, spend);

            coin = BtcAttestStore.Coin(depositCoin)!;
            Assert.Equal(VaultCoinStatus.Spent, coin.Status);
            Assert.Equal(_flow.Spend.Txid, coin.SpentByTxid);
            Assert.Equal(spendAt, coin.SpentInHeight);
            var back = Assert.Single(_flow.SpendOutputsToScript);
            var change = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(_flow.Spend.Txid, back.Vout))!;
            Assert.Equal(VaultCoinStatus.Unspent, change.Status);
            Assert.Equal(back.ValueSats, change.ValueSats);
            Assert.Equal(back.ValueSats, BtcAttestStore.Total(vault.Uid).HeldSats);
            Assert.Equal(spendAt, BtcAttestStore.GetMeta()!.EventsThroughHeight);
            Assert.Equal(_flow.Spend.BlockHash, BtcAttestStore.HeaderAt(spendAt)!.Hash);
        }

        [Fact]
        public async Task RealProofOfWork_CannotBeFaked_AndAProofForOneBlockIsNoProofForAnother()
        {
            await Start();
            await CreateVault(H1 + 1, _flow.Address);
            var depositAt = _flow.Deposit.BlockHeight;

            async Task Bad(Action<BtcReport> change, string because)
            {
                var report = ReportOf(_flow.Deposit, Checkpoint + 1, depositAt + 6);
                change(report);
                var (ok, why) = await Verify(Signed(report), H1 + 2);
                Assert.False(ok, $"accepted; expected: {because}");
                Assert.Contains(because, why);
            }

            // One changed nonce: the header no longer meets the target mainnet requires.
            await Bad(r => r.Headers[3] = r.Headers[3].Substring(0, 152) + "00000000", "does not meet its own proof-of-work target");
            // An easier target, claimed by the header itself.
            await Bad(r => r.Headers[0] = r.Headers[0].Substring(0, 144) + "ffff7f20" + r.Headers[0].Substring(152), "the rules require");
            // The deposit's proof under another block's height: the report no longer names its block; and named as that
            // other block, the proof is no proof.
            await Bad(r => r.BlockHeight = depositAt - 1, "names that block's hash");
            await Bad(r => { r.BlockHeight = depositAt - 1; r.BlockHash = HashAt(depositAt - 1); }, "coinbase proof");
            // The transaction with its witness data left in is not the form a proof is made of.
            await Bad(r => r.Events[0].TxHex = _flow.Deposit.TxHex, "event at position");
            // The withdrawal presented before the deposit it spends is known: it only shows its change.
            var early = ReportOf(_flow.Spend, Checkpoint + 1, _flow.Spend.BlockHeight + 6);
            var (accepted, _) = await Verify(Signed(early), H1 + 2);
            Assert.True(accepted);
        }
    }
}
