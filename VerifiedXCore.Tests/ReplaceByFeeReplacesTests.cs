using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using NBitcoin;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Utilities;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Replace By Fee replaces the pending send: the replacement spends exactly the original's inputs, pays the
    /// recipient once with the same amount, and takes the higher fee out of the original's change. It is refused when
    /// the rate is not higher, the change cannot cover the fee, or the original is already confirmed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ReplaceByFeeReplacesTests : IDisposable
    {
        private const long RecipientSats = 20_000;
        private const long OriginalRate = 2;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly Network _priorNetwork = Globals.BTCNetwork;
        private readonly ScriptPubKeyType _priorScriptType = Globals.ScriptPubKeyType;
        private readonly string[] _priorSignetSeeds = NodeFinder.SignetSeeds;

        public ReplaceByFeeReplacesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rbf_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            TestWalletKeystore.Ensure("correct horse rbf");
            Startup.APIEnabled = true;
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.BTCNetwork = Network.TestNet;
            Globals.ScriptPubKeyType = ScriptPubKeyType.Segwit;
            NodeFinder.SignetSeeds = Array.Empty<string>(); // the broadcast finds no peers
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.BTCNetwork = _priorNetwork;
            Globals.ScriptPubKeyType = _priorScriptType;
            NodeFinder.SignetSeeds = _priorSignetSeeds;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static TestServer NewServer() => new TestServer(new WebHostBuilder().UseStartup<Startup>());

        /// <summary>
        /// A pending send as SendTransaction stores it: two inputs, the recipient and change outputs, 226 vB at 2 sat/vB,
        /// inputs marked spent. The original's change and another unconfirmed coin are in the wallet as unspent UTXOs.
        /// </summary>
        private static (BitcoinTransaction Stored, Transaction Signed) PendingSend(long[] inputSats, bool confirmed = false)
        {
            var sender = BitcoinAccount.CreateAddress(save: false);
            sender.Balance = 1M;
            Assert.True(BitcoinAccount.SaveBitcoinAddress(sender));
            var senderAddress = BitcoinAddress.Create(sender.Address, Globals.BTCNetwork);
            var recipient = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Globals.BTCNetwork);

            var utxos = inputSats.Select(v => new BitcoinUTXO
            {
                Address = sender.Address, TxId = RandomUtils.GetUInt256().ToString(), Vout = 0, Value = v, IsUsed = true
            }).ToList();
            utxos.ForEach(x => BitcoinUTXO.SaveBitcoinUTXO(x));

            long fee = OriginalRate * (74 * inputSats.Length + 2 * 34 + 10);
            var signed = Globals.BTCNetwork.CreateTransactionBuilder()
                .AddCoins(utxos.Select(x => new Coin(new OutPoint(uint256.Parse(x.TxId), x.Vout), new TxOut(Money.Satoshis(x.Value), senderAddress.ScriptPubKey))).ToArray())
                .AddKeys(new Key(sender.PrivateKey.HexToByteArray()))
                .Send(recipient, Money.Satoshis(RecipientSats))
                .SetChange(senderAddress)
                .SendFees(Money.Satoshis(fee))
                .SetOptInRBF(true)
                .BuildTransaction(true);

            var stored = new BitcoinTransaction
            {
                Hash = signed.GetHash().ToString(),
                FromAddress = sender.Address,
                ToAddress = recipient.ToString(),
                Amount = RecipientSats * 0.00000001M,
                Fee = fee * 0.00000001M,
                FeeRate = OriginalRate,
                Signature = signed.ToHex(),
                IsConfirmed = confirmed,
                TransactionType = BTCTransactionType.Send,
                BitcoinUTXOs = utxos,
            };
            Assert.True(BitcoinTransaction.SaveBitcoinTX(stored));

            var change = signed.Outputs.AsIndexedOutputs().Single(x => x.TxOut.ScriptPubKey == senderAddress.ScriptPubKey);
            BitcoinUTXO.SaveBitcoinUTXO(new BitcoinUTXO { Address = sender.Address, TxId = stored.Hash, Vout = (int)change.N, Value = change.TxOut.Value.Satoshi });
            BitcoinUTXO.SaveBitcoinUTXO(new BitcoinUTXO { Address = sender.Address, TxId = RandomUtils.GetUInt256().ToString(), Vout = 0, Value = 1_230 });

            return (stored, signed);
        }

        private static async Task<JObject> ReplaceByFee(string txid, int feeRate)
        {
            using var server = NewServer();
            return JObject.Parse(await server.CreateClient().GetStringAsync($"/btcapi/BTCV2/ReplaceByFee/{txid}/{feeRate}"));
        }

        private static long Fee(Transaction tx, IEnumerable<BitcoinUTXO> coins) =>
            tx.Inputs.Sum(i => coins.Single(c => c.TxId == i.PrevOut.Hash.ToString() && c.Vout == i.PrevOut.N).Value) - tx.Outputs.Sum(o => o.Value.Satoshi);

        [Fact]
        public async Task Replacement_SpendsTheOriginalInputs_PaysTheRecipientOnce_AndRaisesTheFee()
        {
            var (stored, original) = PendingSend(new long[] { 8_870, 169_960 });

            var result = await ReplaceByFee(stored.Hash, 5);
            Assert.True(result.Value<bool>("Success"), result.ToString());

            var saved = await BitcoinTransaction.GetTX(result.Value<string>("Hash")!);
            Assert.NotNull(saved);
            var replacement = Transaction.Parse(saved!.Signature, Globals.BTCNetwork);
            Assert.Equal(saved.Hash, replacement.GetHash().ToString());

            Assert.Equal(original.Inputs.Select(i => i.PrevOut).OrderBy(o => o.ToString()), replacement.Inputs.Select(i => i.PrevOut).OrderBy(o => o.ToString()));
            Assert.DoesNotContain(replacement.Inputs, i => i.PrevOut.Hash == original.GetHash());
            Assert.All(replacement.Inputs, i => Assert.True(i.Sequence.IsRBF));

            var recipientScript = BitcoinAddress.Create(stored.ToAddress, Globals.BTCNetwork).ScriptPubKey;
            Assert.Single(replacement.Outputs, o => o.ScriptPubKey == recipientScript);
            Assert.Equal(RecipientSats, replacement.Outputs.Single(o => o.ScriptPubKey == recipientScript).Value.Satoshi);
            Assert.Equal(original.Outputs.Count, replacement.Outputs.Count);

            long originalFee = Fee(original, stored.BitcoinUTXOs);
            long newFee = Fee(replacement, stored.BitcoinUTXOs);
            Assert.Equal(452, originalFee);
            Assert.Equal(5 * 226, newFee);

            Assert.Equal(BTCTransactionType.Replaced, (await BitcoinTransaction.GetTX(stored.Hash))!.TransactionType);
            Assert.DoesNotContain(BitcoinUTXO.GetBitcoinUTXO()!.Find(x => x.TxId == stored.Hash).ToList(), x => !x.IsUsed);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Replacement_AtARateNoHigherThanTheOriginal_IsRefused(int feeRate)
        {
            var (stored, _) = PendingSend(new long[] { 8_870, 169_960 });

            var result = await ReplaceByFee(stored.Hash, feeRate);
            Assert.False(result.Value<bool>("Success"));
            Assert.Contains("must be higher", result.Value<string>("Message"));
            Assert.Equal(BTCTransactionType.Send, (await BitcoinTransaction.GetTX(stored.Hash))!.TransactionType);
            Assert.Single(BitcoinTransaction.GetAllTXs());
        }

        [Fact]
        public async Task Replacement_TheChangeCannotCover_IsRefused()
        {
            var (stored, _) = PendingSend(new long[] { 8_870, 12_500 }); // change 920 sats

            var result = await ReplaceByFee(stored.Hash, 10);
            Assert.False(result.Value<bool>("Success"));
            Assert.Contains("cannot cover the higher fee", result.Value<string>("Message"));
            Assert.Single(BitcoinTransaction.GetAllTXs());
        }

        [Fact]
        public async Task Replacement_OfAConfirmedTransaction_IsRefused()
        {
            var (stored, _) = PendingSend(new long[] { 8_870, 169_960 }, confirmed: true);

            var result = await ReplaceByFee(stored.Hash, 5);
            Assert.False(result.Value<bool>("Success"));
            Assert.Contains("already confirmed", result.Value<string>("Message"));
            Assert.Single(BitcoinTransaction.GetAllTXs());
        }

        [Fact]
        public async Task Replacement_OfAnAlreadyReplacedTransaction_IsRefused()
        {
            var (stored, _) = PendingSend(new long[] { 8_870, 169_960 });
            Assert.True((await ReplaceByFee(stored.Hash, 5)).Value<bool>("Success"));

            var again = await ReplaceByFee(stored.Hash, 8);
            Assert.False(again.Value<bool>("Success"));
            Assert.Contains("Only a pending send", again.Value<string>("Message"));
            Assert.Equal(2, BitcoinTransaction.GetAllTXs().Count);
        }
    }
}
