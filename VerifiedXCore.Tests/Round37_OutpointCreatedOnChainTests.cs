using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: a signed withdrawal transaction is dead only once one of its inputs is spent by a confirmed transaction,
    /// and that is shown, not inferred: an input counts as spent when <see cref="BitcoinTransactionService.SpentProofsRequired"/>
    /// Electrum servers each show the confirmed transaction that spends it, validly signed over the real funding output
    /// (which a confirmed transaction created at the vault's address) and other than the transaction asked about. A server
    /// that only leaves a coin out of its unspent set, shows an unsigned spender, or stands alone cannot make a signed
    /// transaction look dead. Driven through BitcoinTransactionService.GetOutpointSpendVerdict, which every dead-transaction
    /// decision reads (the sign/start guard, the pin release, the cancellation vote's evidence, the deferred completion),
    /// against Electrum servers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_OutpointCreatedOnChainTests : IDisposable
    {
        private static readonly Network Net = Network.TestNet;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly List<ClientSettings>? _priorServers = Globals.ClientSettings;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly VerifiedXCore.Models.Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRecordHeight = Globals.BitcoinAttestationRecordHeight;
        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"r37outpoint_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
        private const long SpenderBtcHeight = 101;
        private readonly Key _vaultKey = new();
        private readonly BitcoinAddress _vault;
        private readonly BitcoinAddress _elsewhere = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        private readonly NBitcoin.Transaction _funding;   // confirmed: vout 0 and 1 pay the vault, vout 2 pays elsewhere
        private readonly NBitcoin.Transaction _spender;   // confirmed, signed by the vault key: spends funding:0
        private readonly NBitcoin.Transaction _forged;    // spends funding:0 with no valid signature
        private readonly List<LoopbackElectrum> _servers = new();
        private readonly Dictionary<long, string> _headerHex = new();   // headers the servers answer blockchain.block.headers with

        public Round37_OutpointCreatedOnChainTests()
        {
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            VerifiedXCore.Data.DbContext.Initialize();
            Globals.LastBlock = new VerifiedXCore.Models.Block { Height = 10 }; // before the attestation's recording height
            Globals.BTCNetwork = Net;
            _vault = _vaultKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
            _funding = Net.CreateTransaction();
            _funding.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0));
            _funding.Outputs.Add(Money.Satoshis(100_000), _vault);
            _funding.Outputs.Add(Money.Satoshis(200_000), _vault);
            _funding.Outputs.Add(Money.Satoshis(300_000), _elsewhere);

            _spender = Net.CreateTransactionBuilder()
                .AddCoins(new Coin(_funding, 0u)).AddKeys(_vaultKey)
                .Send(_elsewhere, Money.Satoshis(90_000)).SetChange(_vault).SendFees(Money.Satoshis(10_000))
                .BuildTransaction(true);
            _forged = Net.CreateTransaction();
            _forged.Inputs.Add(new OutPoint(_funding.GetHash(), 0));
            _forged.Outputs.Add(Money.Satoshis(90_000), _elsewhere);
        }

        public void Dispose()
        {
            foreach (var s in _servers) s.Dispose();
            Globals.ClientSettings = _priorServers;
            Globals.BTCNetwork = _priorNetwork;
            Globals.BitcoinAttestationRecordHeight = _priorRecordHeight;
            Globals.LastBlock = _priorLastBlock;
            try { VerifiedXCore.Data.DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string Id(NBitcoin.Transaction tx) => tx.GetHash().ToString();

        /// <summary>One Electrum server: its unspent set and its confirmed history (every transaction it can return).</summary>
        private void Server(IEnumerable<string> unspent, params NBitcoin.Transaction[] confirmed)
        {
            var raw = confirmed.Append(_funding).ToDictionary(Id, t => t.ToHex());
            var unspentRows = unspent.Select(o => new JObject { ["tx_hash"] = o.Split(':')[0], ["tx_pos"] = int.Parse(o.Split(':')[1]), ["height"] = 100, ["value"] = 1 }).ToList();
            var history = confirmed.Prepend(_funding).Select((t, i) => new JObject { ["tx_hash"] = Id(t), ["height"] = 100 + i }).ToList();
            var server = new LoopbackElectrum(req =>
            {
                var p = req["params"] as JArray;
                switch ((string?)req["method"])
                {
                    case "server.version": return new JArray("Loopback", "1.4");
                    case "blockchain.scripthash.listunspent": return new JArray(unspentRows);
                    case "blockchain.scripthash.get_history": return new JArray(history);
                    case "blockchain.transaction.get": return raw.TryGetValue((string)p![0]!, out var hex) ? hex : null;
                    // Each confirmed transaction is the only one in its block: an empty branch, position 0.
                    case "blockchain.transaction.get_merkle": return new JObject { ["merkle"] = new JArray(), ["block_height"] = (int)p![1]!, ["pos"] = 0 };
                    case "blockchain.block.headers":
                    {
                        var from = (long)p![0]!;
                        var count = (int)p[1]!;
                        var hexes = Enumerable.Range(0, count).Select(i => _headerHex.TryGetValue(from + i, out var h) ? h : null).ToList();
                        if (hexes.Any(h => h == null)) return null;
                        return new JObject { ["hex"] = string.Concat(hexes), ["count"] = count, ["max"] = 2016 };
                    }
                    default: return null;
                }
            });
            _servers.Add(server);
            Globals.ClientSettings = _servers.Select(s => new ClientSettings { Host = "127.0.0.1", Port = s.Port, UseSsl = true }).ToList();
        }

        private void ServerWithHeights(IEnumerable<string> unspent, List<(NBitcoin.Transaction Tx, int Height)> history)
        {
            var raw = history.GroupBy(h => Id(h.Tx)).ToDictionary(g => g.Key, g => g.First().Tx.ToHex());
            var unspentRows = unspent.Select(o => new JObject { ["tx_hash"] = o.Split(':')[0], ["tx_pos"] = int.Parse(o.Split(':')[1]), ["height"] = 100, ["value"] = 1 }).ToList();
            var rows = history.Select(h => new JObject { ["tx_hash"] = Id(h.Tx), ["height"] = h.Height }).ToList();
            var server = new LoopbackElectrum(req =>
            {
                var p = req["params"] as JArray;
                switch ((string?)req["method"])
                {
                    case "server.version": return new JArray("Loopback", "1.4");
                    case "blockchain.scripthash.listunspent": return new JArray(unspentRows);
                    case "blockchain.scripthash.get_history": return new JArray(rows);
                    case "blockchain.transaction.get": return raw.TryGetValue((string)p![0]!, out var hex) ? hex : null;
                    case "blockchain.transaction.get_merkle": return new JObject { ["merkle"] = new JArray(), ["block_height"] = (int)p![1]!, ["pos"] = 0 };
                    default: return null;
                }
            });
            _servers.Add(server);
            Globals.ClientSettings = _servers.Select(x => new ClientSettings { Host = "127.0.0.1", Port = x.Port, UseSsl = true }).ToList();
        }

        private string Coin0 => $"{Id(_funding)}:0";
        private string Coin1 => $"{Id(_funding)}:1";

        private Task<(BitcoinTransactionService.OutpointSpendVerdict Verdict, string Detail)> Verdict(string? signedTxId = null, params string[] outpoints) =>
            BitcoinTransactionService.GetOutpointSpendVerdict(_vault.ToString(), outpoints.Length > 0 ? outpoints : new[] { Coin0 }, signedTxId);

        /// <summary>Before the recording height no header is attested, so even two servers showing a signed spend prove nothing.</summary>
        [Fact]
        public async Task BeforeTheRecordingHeight_NoSpendIsProven()
        {
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.Inconclusive, detail);
        }

        [Fact]
        public async Task OneServerShowingTheSpend_IsNotEnough()
        {
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin0, Coin1 });
            var (verdict, detail) = await Verdict();
            Assert.True(verdict != BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed, detail);
        }

        [Fact]
        public async Task ServersThatOnlyLeaveTheCoinOut_DoNotMakeItSpent()
        {
            // Both servers omit the coin from their unspent sets and show no transaction that spends it.
            Server(new[] { Coin1 });
            Server(new[] { Coin1 });
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, detail);
        }

        [Fact]
        public async Task ASpenderWithoutAValidSignature_DoesNotMakeItSpent()
        {
            Server(new[] { Coin1 }, _forged);
            Server(new[] { Coin1 }, _forged);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, detail);
        }

        [Fact]
        public async Task TheTransactionAskedAbout_IsNotItsOwnDeath()
        {
            // The only confirmed spender is the signed transaction itself: it confirmed, it is not dead.
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var (verdict, detail) = await Verdict(Id(_spender));
            Assert.True(verdict != BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed, detail);
        }

        /// <summary>From the recording height: the validators' attested header of the spend's block, whose Merkle root it is.</summary>
        private static int Depth => VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.Depth;

        private void Recording(string? attestedRoot, long headerHeight = SpenderBtcHeight, int? above = null, bool aboveAtEasiestTarget = false)
        {
            var count = above ?? Depth;
            Globals.BitcoinAttestationRecordHeight = 1;
            Globals.LastBlock = new VerifiedXCore.Models.Block { Height = 10 };
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveMeta(new VerifiedXCore.Bitcoin.Attestation.AttestationMetaRow { TipHeight = headerHeight + count });
            for (var h = headerHeight + 1; h <= headerHeight + count; h++)
            {
                var over = Net.Consensus.ConsensusFactory.CreateBlockHeader();
                over.HashMerkleRoot = new uint256(RandomUtils.GetBytes(32));
                over.Bits = new Target(aboveAtEasiestTarget ? VerifiedXCore.Bitcoin.Attestation.BtcChainRules.PowLimitBits : 0x17034219u);
                Assert.True(VerifiedXCore.Bitcoin.Attestation.BtcHeader.TryParse(Convert.ToHexString(over.ToBytes()), out var overParsed));
                VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveHeader(h, overParsed!, 5);
            }
            if (attestedRoot == null) return;
            var header = Net.Consensus.ConsensusFactory.CreateBlockHeader();
            header.HashMerkleRoot = uint256.Parse(attestedRoot);
            Assert.True(VerifiedXCore.Bitcoin.Attestation.BtcHeader.TryParse(Convert.ToHexString(header.ToBytes()), out var parsed));
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveHeader(headerHeight, parsed!, 5);
        }

        /// <summary>A confirmed transaction on the address that spends nothing of the vault's.</summary>
        private NBitcoin.Transaction Unrelated()
        {
            var tx = Net.CreateTransaction();
            tx.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0));
            tx.Outputs.Add(Money.Satoshis(1_000), _vault);
            return tx;
        }

        /// <summary>
        /// A legacy vault whose validator holds two signing records for one withdrawal (an earlier version kept one per
        /// spelling of the reference; the store trims what it saves, so both read the same): A, whose coin a confirmed
        /// transaction spent, and B, whose coin is unspent. A being dead neither removes B's record nor lets a new
        /// transaction be signed or the withdrawal be refunded.
        /// </summary>
        private (string Uid, string Wrh, string Dead, string Live) TwoRecordsForOneWithdrawal(bool deadFirst = true)
        {
            var uid = Guid.NewGuid().ToString("N") + ":1760000000";
            const string wrh = "4f1e3a9c7b2d5e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f50617";
            VerifiedXCore.Models.SmartContractStateTrei.SaveSmartContract(new VerifiedXCore.Models.SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.VaultContractData(uid, "xOwner", _vault.ToString()), MinterAddress = "xOwner", OwnerAddress = "xOwner", Nonce = 0,
            });
            var dead = Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();
            var live = Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();
            var (first, firstCoin, second, secondCoin) = deadFirst ? (dead, Coin0, live, Coin1) : (live, Coin1, dead, Coin0);
            Assert.True(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.Record(uid, wrh, first, new[] { firstCoin }));
            Assert.True(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.Record(uid, " " + wrh, second, new[] { secondCoin }));
            Assert.Equal(2, VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh).Count);
            Recording(Id(_spender));
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            return (uid, wrh, dead, live);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task OneDeadSigningRecord_DoesNotRetireAnotherForTheSameWithdrawal(bool deadFirst)
        {
            var (uid, wrh, _, live) = TwoRecordsForOneWithdrawal(deadFirst);
            var newTx = Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();

            Assert.False((await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, newTx)).Ok);
            Assert.Contains(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh), r => r.BtcTxId == live);
            Assert.False((await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, newTx)).Ok);
        }

        [Fact]
        public async Task WhenEverySigningRecordIsDead_ANewTransactionIsSigned_AndTheRecordsAreRetired()
        {
            var (uid, wrh, dead, _) = TwoRecordsForOneWithdrawal();
            // The second record's transaction spent the same coin as the first: both can never confirm.
            var other = Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();
            foreach (var r in VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh).Where(r => r.BtcTxId != dead))
                VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.DeleteRecord(r.Id);
            Assert.True(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.Record(uid, " " + wrh, other, new[] { Coin0 }));
            Assert.Equal(2, VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh).Count);

            var (ok, reason) = await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant());
            Assert.True(ok, reason);
            Assert.Empty(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh));
        }

        /// <summary>
        /// Two sign/starts for one withdrawal: while the second waits to learn that an old transaction is dead, the first
        /// begins a ceremony for its own transaction. Retiring the dead one leaves that ceremony's record in place, so the
        /// second, for yet another transaction, is still refused.
        /// </summary>
        [Fact]
        public async Task RetiringADeadTransaction_LeavesACeremonyBegunMeanwhileForAnother()
        {
            var (uid, wrh, dead, _) = TwoRecordsForOneWithdrawal();
            foreach (var r in VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh).Where(r => r.BtcTxId != dead))
                VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.DeleteRecord(r.Id);
            string Hex() => Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();
            var (c, sighashC, d, sighashD) = (Hex(), Hex(), Hex(), Hex());

            Assert.False(FrostWithdrawalSigningTracker.TryBeginSigning(uid, wrh, "session-c", 0, sighashC, new List<string> { sighashC }, new List<string> { Coin1 }, btcTxId: c).Blocked);
            var (ok, reason) = await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, d);
            Assert.True(ok, reason);
            Assert.True(FrostWithdrawalSigningTracker.TryBeginSigning(uid, wrh, "session-d", 0, sighashD, new List<string> { sighashD }, new List<string> { Coin1 }, btcTxId: d).Blocked);
            Assert.True(FrostWithdrawalSigningTracker.ConfirmSessionHoldsInput(uid, wrh, 0, "session-c", sighashC).Ok);
        }

        /// <summary>
        /// A wallet's deferred completion holds its withdrawal row across Bitcoin lookups. The row is cancelled (refunded)
        /// meanwhile; when the lookups show its reclaim transaction dead, clearing the reclaim leaves the row cancelled
        /// (saving the held object whole wrote its old "requested" back over it).
        /// </summary>
        [Fact]
        public async Task ClearingADeadReclaim_DoesNotReopenAWithdrawalCancelledMeanwhile()
        {
            var uid = Guid.NewGuid().ToString("N") + ":1760000000";
            const string wrh = "6a1e3a9c7b2d5e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f50617";
            var reclaim = Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();
            var coins = Newtonsoft.Json.JsonConvert.SerializeObject(new[] { new VerifiedXCore.Bitcoin.Models.PinnedWithdrawalCoin { TxId = Id(_funding), Vout = 0, ValueSats = 100_000, ScriptPubKeyHex = _vault.ScriptPubKey.ToHex() } });
            Assert.True(VerifiedXCore.Bitcoin.Models.VBTCWithdrawalRequest.Save(new VerifiedXCore.Bitcoin.Models.VBTCWithdrawalRequest
            {
                RequestorAddress = "xOwner", OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = uid, TransactionHash = wrh, Amount = 0.0005M,
                BTCDestination = _elsewhere.ToString(), FeeRate = 10, Timestamp = 1, RequestBlockHeight = 400, Status = VerifiedXCore.Bitcoin.Models.VBTCWithdrawalStatus.Requested,
                CompleteAfterConfirmation = true, CompleteAfterBtcTxId = reclaim, PinnedCoinsJson = coins, PinnedUnsignedTxHex = "0200",
            }));
            var held = VerifiedXCore.Bitcoin.Models.VBTCWithdrawalRequest.GetByTransactionHash(wrh, uid)!;
            var stored = VerifiedXCore.Bitcoin.Models.VBTCWithdrawalRequest.GetByTransactionHash(wrh, uid)!;
            stored.Status = VerifiedXCore.Bitcoin.Models.VBTCWithdrawalStatus.Cancelled;
            stored.IsCompleted = true;
            Assert.True(VerifiedXCore.Bitcoin.Models.VBTCWithdrawalRequest.Save(stored, true));
            Recording(Id(_spender));
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);

            var (action, message) = await VBTCService.ResolveDeferredCompletion(held, _vault.ToString());
            Assert.True(action == VBTCService.DeferredCompletionAction.Rebuild, message);
            var after = VerifiedXCore.Bitcoin.Models.VBTCWithdrawalRequest.GetByTransactionHash(wrh, uid)!;
            Assert.Equal(VerifiedXCore.Bitcoin.Models.VBTCWithdrawalStatus.Cancelled, after.Status);
            Assert.True(after.IsCompleted);
            Assert.Null(after.CompleteAfterBtcTxId);
            Assert.Null(after.PinnedCoinsJson);
        }

        /// <summary>
        /// A signing record from before this validator signed under exact spellings only may be the one trace of two
        /// transactions (a signing under another letter case was saved over the first). Its transaction proven dead does
        /// not by itself let another be signed or the cancellation be approved; an operator's release does. A record
        /// written since is judged as before.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]    // a record an older binary wrote (no first-signed time), with no stamp at all
        [InlineData(false, false)]
        public async Task ADeadRecordFromBeforeExactSpellings_NeedsAnOperatorsRelease(bool recordIsOld, bool writtenByAnOlderBinary)
        {
            var (uid, wrh, dead, _) = TwoRecordsForOneWithdrawal();
            foreach (var r in VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh).Where(r => r.BtcTxId != dead))
                VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.DeleteRecord(r.Id);
            var now = VerifiedXCore.Utilities.TimeUtil.GetTime();
            if (writtenByAnOlderBinary)
            {
                var records = VerifiedXCore.Data.DbContext.DB_vBTC.GetCollection<VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence>(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.CollectionName);
                var old = Assert.Single(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh));
                old.FirstSignedAt = 0;
                Assert.True(records.Update(old));
            }
            else
                VerifiedXCore.Data.DbContext.DB_vBTC.GetCollection<VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ExactSpelling>(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ExactSpellingCollectionName)
                    .Upsert(new VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ExactSpelling { Since = recordIsOld ? now + 3600 : now - 3600 });
            var newTx = Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();

            var vote = await VBTCCancellationVoteService.GetSigningEvidenceAsync(uid, wrh, null);
            Assert.Equal(!recordIsOld, vote.ProvenDead);
            Assert.Equal(recordIsOld, vote.Inconclusive);
            Assert.Equal(recordIsOld, vote.AwaitsOperatorRelease);
            var (ok, reason) = await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, newTx);
            Assert.True(ok == !recordIsOld, reason);
            if (!recordIsOld) return;
            Assert.Contains("operator", reason);
            Assert.Single(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh));

            Assert.Equal(1, VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ReleaseByOperator(uid, wrh));
            Assert.True((await VBTCCancellationVoteService.GetSigningEvidenceAsync(uid, wrh, null)).ProvenDead);
            (ok, reason) = await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, newTx);
            Assert.True(ok, reason);
            Assert.Empty(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh));
        }

        /// <summary>
        /// An unreleased record from before exact spellings holds every signing for the payment, the transaction it
        /// names included: a retry of that transaction would get its missing input shares while one signed then under
        /// another spelling (recorded over) can still pay. Released, the retry is signed.
        /// </summary>
        [Fact]
        public async Task ARecordFromBeforeExactSpellings_WithNoTransactionId_StillHoldsTheSigning()
        {
            var uid = Guid.NewGuid().ToString("N") + ":1760000000";
            const string wrh = "8c1e3a9c7b2d5e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f50617";
            Assert.True(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.Record(uid, wrh, "", new[] { Coin1 }));
            var records = VerifiedXCore.Data.DbContext.DB_vBTC.GetCollection<VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence>(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.CollectionName);
            var old = Assert.Single(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh));
            old.FirstSignedAt = 0;
            Assert.True(records.Update(old));
            var (ok, reason) = await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant());
            Assert.False(ok);
            Assert.Contains("operator", reason);
        }

        [Fact]
        public async Task ARecordFromBeforeExactSpellings_HoldsEvenARetryOfItsOwnTransaction()
        {
            var uid = Guid.NewGuid().ToString("N") + ":1760000000";
            const string wrh = "7b1e3a9c7b2d5e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f50617";
            var tx = Convert.ToHexString(RandomUtils.GetBytes(32)).ToLowerInvariant();
            Assert.True(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.Record(uid, wrh, tx, new[] { Coin1 }));
            var records = VerifiedXCore.Data.DbContext.DB_vBTC.GetCollection<VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence>(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.CollectionName);
            var old = Assert.Single(VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ForPayment(uid, wrh));
            old.FirstSignedAt = 0;   // written by an older binary
            Assert.True(records.Update(old));

            var (ok, reason) = await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, tx);
            Assert.False(ok);
            Assert.Contains("operator", reason);

            Assert.Equal(1, VerifiedXCore.Bitcoin.Models.FrostSignedWithdrawalEvidence.ReleaseByOperator(uid, wrh));
            (ok, reason) = await VerifiedXCore.Bitcoin.FROST.FrostStartup.CheckPriorSignedTransactionAsync(uid, wrh, tx);
            Assert.True(ok, reason);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task OneDeadSigningRecord_DoesNotApproveTheCancellationWhileAnotherCanPay(bool deadFirst)
        {
            var (uid, wrh, _, live) = TwoRecordsForOneWithdrawal(deadFirst);
            var evidence = await VBTCCancellationVoteService.GetSigningEvidenceAsync(uid, wrh, null);
            Assert.True(evidence.HasEvidence);
            Assert.False(evidence.ProvenDead);
            Assert.False(evidence.Inconclusive);
            Assert.Equal(live, evidence.BtcTxId);
        }

        [Fact]
        public async Task FromTheRecordingHeight_ASpendProvenToTheAttestedHeader_IsSpentByConfirmed()
        {
            Recording(Id(_spender));
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed, detail);
        }

        [Fact]
        public async Task FromTheRecordingHeight_ASpendWhoseBlockIsNotAttestedYet_IsInconclusive()
        {
            Recording(null);
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.Inconclusive, detail);
        }

        [Fact]
        public async Task FromTheRecordingHeight_ASignedSpendShownAsConfirmedButNotInTheAttestedBlock_IsNotSpent()
        {
            // The servers (or one party answering for all of them) present a validly signed spend as confirmed; the attested
            // header of that height commits to other transactions.
            Recording(new uint256(RandomUtils.GetBytes(32)).ToString());
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict != BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed, detail);
        }

        /// <summary>
        /// The attestation begins at a checkpoint (an attested header); a spend mined below it is proven by its block's header
        /// linked by hashes, header to header, up to the attested checkpoint header. A chain that does not reach it proves nothing.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ASpendBelowTheAttestedCheckpoint_IsProvenThroughTheLinkedHeaders(bool chainReachesTheCheckpoint)
        {
            const long checkpointHeight = SpenderBtcHeight + 9;
            Globals.BitcoinAttestationRecordHeight = 1;
            Globals.LastBlock = new VerifiedXCore.Models.Block { Height = 10 };
            uint256 prev = new(RandomUtils.GetBytes(32));
            NBitcoin.BlockHeader? last = null;
            for (var h = SpenderBtcHeight; h <= checkpointHeight; h++)
            {
                var header = Net.Consensus.ConsensusFactory.CreateBlockHeader();
                header.HashPrevBlock = prev;
                header.HashMerkleRoot = h == SpenderBtcHeight ? _spender.GetHash() : new uint256(RandomUtils.GetBytes(32));
                _headerHex[h] = Convert.ToHexString(header.ToBytes()).ToLowerInvariant();
                prev = header.GetHash();
                last = header;
            }
            // The checkpoint the validators attested: this chain's last header, or another one.
            var attested = last!;
            if (!chainReachesTheCheckpoint)
            {
                attested = Net.Consensus.ConsensusFactory.CreateBlockHeader();
                attested.HashPrevBlock = new uint256(RandomUtils.GetBytes(32));
            }
            Assert.True(VerifiedXCore.Bitcoin.Attestation.BtcHeader.TryParse(Convert.ToHexString(attested.ToBytes()), out var parsed));
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveHeader(checkpointHeight, parsed!, 5);
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveMeta(new VerifiedXCore.Bitcoin.Attestation.AttestationMetaRow { CheckpointHeight = checkpointHeight, TipHeight = checkpointHeight });

            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == (chainReachesTheCheckpoint ? BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed : BitcoinTransactionService.OutpointSpendVerdict.Inconclusive), detail);
        }

        [Fact]
        public async Task FromTheRecordingHeight_ASpendNotYetBuriedDeepEnough_IsInconclusive()
        {
            Recording(Id(_spender), above: Depth - 1);
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.Inconclusive, detail);
        }

        /// <summary>
        /// On testnet4 a minimum-difficulty header does not count toward burial (as for the attestation itself): a spend
        /// under only such headers is not yet buried.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OnTestnet4_HeadersAtTheEasiestTargetDoNotBuryASpend(bool easiest)
        {
            VerifiedXCore.Bitcoin.Attestation.BtcChainRules.OverrideForTests = VerifiedXCore.Bitcoin.Attestation.BtcChainRules.Testnet4;
            try
            {
                Recording(Id(_spender), aboveAtEasiestTarget: easiest);
                Server(new[] { Coin1 }, _spender);
                Server(new[] { Coin1 }, _spender);
                var (verdict, detail) = await Verdict();
                Assert.True(verdict == (easiest ? BitcoinTransactionService.OutpointSpendVerdict.Inconclusive : BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed), detail);
            }
            finally { VerifiedXCore.Bitcoin.Attestation.BtcChainRules.OverrideForTests = null; }
        }

        /// <summary>
        /// The search reads only transactions in or after the coin's own block, so history from before the coin was made
        /// does not count toward the limit: a missing coin with no spend since it was made is "not spent" (the signed
        /// transaction stays alive), not Inconclusive because the address is old and busy.
        /// </summary>
        [Fact]
        public async Task HistoryBeforeTheCoinWasMade_DoesNotCountTowardTheScanLimit()
        {
            // 250 older transactions (before the funding transaction), then the funding transaction; nothing spends coin 0.
            var older = Enumerable.Range(0, BitcoinTransactionService.MaxConfirmedHistoryScan + 50).Select(_ => Unrelated()).ToArray();
            var history = older.Select((t, i) => (Tx: t, Height: 10 + i)).Append((_funding, 10 + older.Length + 1)).ToList();
            Recording(Id(_spender));
            ServerWithHeights(new[] { Coin1 }, history);
            ServerWithHeights(new[] { Coin1 }, history);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, detail);
        }

        /// <summary>
        /// An address with more confirmed transactions than are read: the newest are read, so a recent spend is still found;
        /// one older than that is Inconclusive, never "not spent".
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AnAddressWithMoreHistoryThanIsRead_FindsARecentSpend_AndCallsAnOlderOneInconclusive(bool spendIsRecent)
        {
            var others = Enumerable.Range(0, BitcoinTransactionService.MaxConfirmedHistoryScan).Select(_ => Unrelated()).ToArray();
            var confirmed = spendIsRecent ? others.Append(_spender).ToArray() : others.Prepend(_spender).ToArray();
            var spenderHeight = 100 + 1 + Array.IndexOf(confirmed, _spender);
            Recording(Id(_spender), spenderHeight);
            Server(new[] { Coin1 }, confirmed);
            Server(new[] { Coin1 }, confirmed);
            var (verdict, detail) = await Verdict();
            Assert.True(verdict == (spendIsRecent ? BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed : BitcoinTransactionService.OutpointSpendVerdict.Inconclusive), detail);
        }

        [Theory]
        [InlineData(2)]  // the funding transaction's output exists but pays another address
        [InlineData(7)]  // the funding transaction has no such output
        public async Task AnInputNoConfirmedTransactionCreatedAtTheVault_LeavesTheTransactionAlive(int vout)
        {
            Server(new[] { Coin1 }, _spender);
            Server(new[] { Coin1 }, _spender);
            var phantom = $"{new uint256(RandomUtils.GetBytes(32))}:0";
            var (verdict, detail) = await Verdict(null, $"{Id(_funding)}:{vout}", phantom, Coin1);
            Assert.True(verdict == BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, detail);
        }

        /// <summary>Electrum JSON-RPC over TLS on loopback: one request per connection, as the client makes them.</summary>
        private sealed class LoopbackElectrum : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly X509Certificate2 _cert;
            private readonly Func<JObject, JToken?> _handler;
            private readonly CancellationTokenSource _stop = new();
            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public LoopbackElectrum(Func<JObject, JToken?> handler)
            {
                _handler = handler;
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                _cert = new X509Certificate2(selfSigned.Export(X509ContentType.Pfx));
                _listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            }

            private async Task Serve(TcpClient client)
            {
                using (client)
                using (var ssl = new SslStream(client.GetStream(), false))
                {
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(_cert);
                        using var reader = new StreamReader(ssl, Encoding.UTF8, false, 1024, leaveOpen: true);
                        var line = await reader.ReadLineAsync();
                        if (line == null) return;
                        var req = JObject.Parse(line);
                        var result = _handler(req);
                        var reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = req["id"] };
                        if (result == null) reply["error"] = new JObject { ["code"] = 2, ["message"] = "not found" };
                        else reply["result"] = result;
                        var bytes = Encoding.UTF8.GetBytes(reply.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                        await ssl.WriteAsync(bytes);
                        await ssl.FlushAsync();
                    }
                    catch { }
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
                _cert.Dispose();
            }
        }
    }
}
