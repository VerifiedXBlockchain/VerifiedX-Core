using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;
using static VerifiedXCore.Bitcoin.Services.CompletedWithdrawalConfirmation;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 8 (validator-local): a Completed withdrawal is added back to the owner's spendable balance at
    /// admission and proposal only when the Bitcoin transaction its COMPLETE names is confirmed, took at least the
    /// withdrawal's amount out of the vault, pays the withdrawal's destination, is named by no earlier completed row, is
    /// not a bridge exit's transaction and is the one this node signed (when it signed).
    ///
    /// The tests go through the entry point the balance check calls (NotYetCountedAmountAsync(vault, height)) over real
    /// rows, signing records and exit records in the stores; only the Electrum answer is substituted, and that answer is
    /// built from real Bitcoin transactions by the same function production uses (BuildFacts). The fourth review's
    /// finding was a floor computed from the request's own fee rate: nothing the requester sets is in the rule now.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss08_CompletedUnconfirmedWithdrawalTests : IDisposable
    {
        private static readonly Network Net = Network.TestNet;
        private static readonly BitcoinAddress VaultAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        private static readonly BitcoinAddress OtherVaultAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        private static readonly BitcoinAddress DestAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
        private static readonly BitcoinAddress StrangerAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
        private static readonly string Deposit = VaultAddr.ToString();
        private static readonly string Dest = DestAddr.ToString();
        private static long Height => Globals.V2WithdrawalOwnerAddBackFixHeight + 1;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Func<string, Task<BtcTxFacts?>> _priorLookup;
        private readonly Func<string, string?> _priorResolve;
        private readonly string _vault = "vault:" + Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, BtcTxFacts?> _chain = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _asked = new();
        private long _clock = 1_700_000_000;

        public FundLoss08_CompletedUnconfirmedWithdrawalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl08_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLookup = LookupTransaction;
            _priorResolve = ResolveDepositAddress;
            ResolveDepositAddress = _ => Deposit;
            LookupTransaction = txid =>
            {
                _asked.Add(txid);
                return Task.FromResult(_chain.TryGetValue(txid, out var f) ? f : null); // unknown to every server: no answer
            };
            ResetForTests();
            DbContext.Initialize();
        }

        public void Dispose()
        {
            LookupTransaction = _priorLookup;
            ResolveDepositAddress = _priorResolve;
            ResetForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        // ── Bitcoin ─────────────────────────────────────────────────────────────────────────────────────────

        private static long Sats(decimal btc) => ToSats(btc);

        /// <summary>A transaction paying <paramref name="outputs"/>, with one funding transaction per input.</summary>
        private static (NBitcoin.Transaction Tx, Dictionary<uint256, NBitcoin.Transaction> Prev) Build((BitcoinAddress From, decimal Btc)[] inputs, params (BitcoinAddress To, decimal Btc)[] outputs)
        {
            var prev = new Dictionary<uint256, NBitcoin.Transaction>();
            var tx = Net.CreateTransaction();
            foreach (var (from, btc) in inputs)
            {
                var funding = Net.CreateTransaction();
                funding.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0));
                funding.Outputs.Add(Money.Satoshis(777), StrangerAddr);          // the spent output is not the first one
                funding.Outputs.Add(Money.Satoshis(Sats(btc)), from);
                prev[funding.GetHash()] = funding;
                tx.Inputs.Add(new OutPoint(funding.GetHash(), 1));
            }
            foreach (var (to, btc) in outputs)
                tx.Outputs.Add(Money.Satoshis(Sats(btc)), to);
            return (tx, prev);
        }

        /// <summary>Puts a transaction on the test chain with <paramref name="confirmations"/> and returns its txid.</summary>
        private string Mine(int confirmations, (BitcoinAddress From, decimal Btc)[] inputs, params (BitcoinAddress To, decimal Btc)[] outputs)
        {
            var (tx, prev) = Build(inputs, outputs);
            var txid = tx.GetHash().ToString();
            _chain[txid] = confirmations < 1
                ? new BtcTxFacts { Confirmations = confirmations }
                : BuildFacts(tx, h => prev.TryGetValue(h, out var p) ? p : null, confirmations, Net);
            return txid;
        }

        /// <summary>
        /// The transaction the validators sign for a withdrawal of <paramref name="amount"/> from a vault coin of
        /// <paramref name="coin"/>: the destination gets amount less the fee, the change (coin less amount) returns to the vault.
        /// </summary>
        private string MineWithdrawal(int confirmations, decimal amount, decimal coin = 2.0M, decimal fee = 0.0001M, BitcoinAddress? dest = null, BitcoinAddress? vault = null)
            => Mine(confirmations, new[] { (vault ?? VaultAddr, coin) }, (dest ?? DestAddr, amount - fee), (vault ?? VaultAddr, coin - amount));

        // ── Stores ──────────────────────────────────────────────────────────────────────────────────────────

        private string Completed(decimal amount, string? btcTxId, string? dest = null, int feeRate = 10, string? vault = null, string? requestHash = null)
        {
            var hash = requestHash ?? "req-" + Guid.NewGuid().ToString("N");
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "xOwner", OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = vault ?? _vault,
                TransactionHash = hash, Amount = amount, BTCDestination = dest ?? Dest, FeeRate = feeRate, Timestamp = _clock,
                RequestBlockHeight = 100, Status = VBTCWithdrawalStatus.Completed, IsCompleted = true, BTCTxHash = btcTxId, CompletionTimestamp = _clock++,
            }));
            return hash;
        }

        private Task<decimal?> NotCounted() => NotYetCountedAmountAsync(_vault, Height);

        // ── The rule, through the production entry point ───────────────────────────────────────────────────

        [Fact]
        public async Task AConfirmedWithdrawal_IsAddedBack_AnUnconfirmedOrWithheldOneIsNot()
        {
            Assert.Equal(0M, await NotCounted());                                  // nothing completed: nothing asked
            Assert.Empty(_asked);

            Completed(0.5M, MineWithdrawal(6, 0.5M));
            Assert.Equal(0M, await NotCounted());

            Completed(0.3M, MineWithdrawal(0, 0.3M));                              // in the mempool
            Assert.Equal(0.3M, await NotCounted());

            Completed(0.2M, new string('a', 64));                                  // withheld: no server knows it
            Assert.Null(await NotCounted());                                       // no answer: the caller fails closed
        }

        /// <summary>
        /// The fourth review's attack. The requester sets the fee rate on the request; the earlier floor was amount less
        /// (rate x 1,000 vB), so a large rate brought it to nothing and a small real payout completed a large withdrawal.
        /// </summary>
        [Fact]
        public async Task ASmallPayout_DoesNotCompleteALargeWithdrawal_WhateverFeeRateTheRequestCarries()
        {
            var small = MineWithdrawal(10, 0.001M);                                // a real, confirmed 0.001 BTC withdrawal to Dest
            foreach (var feeRate in new[] { 1, 10, 100_000, int.MaxValue })
            {
                ResetForTests();
                VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.DeleteAll();
                Completed(1.0M, small, feeRate: feeRate);                          // a 1 BTC withdrawal's COMPLETE names it
                Assert.Equal(1.0M, await NotCounted());
            }

            // The same transaction is the 0.001 withdrawal's.
            VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.DeleteAll();
            Completed(0.001M, small, feeRate: int.MaxValue);
            Assert.Equal(0M, await NotCounted());
        }

        /// <summary>What counts is what left the vault: coins spent from it less what came back to it.</summary>
        [Fact]
        public void TheRule_IsWhatLeftTheVault()
        {
            BtcTxFacts F((BitcoinAddress, decimal)[] ins, params (BitcoinAddress, decimal)[] outs)
            {
                var (tx, prev) = Build(ins, outs);
                return BuildFacts(tx, h => prev.TryGetValue(h, out var p) ? p : null, 3, Net)!;
            }
            bool Ok(BtcTxFacts f, decimal amount, out string why, string? dest = null, string? deposit = null) => IsTheWithdrawalsTransaction(f, dest ?? Dest, deposit ?? Deposit, amount, out why);

            // The signed shape: fee out of the amount, change back to the vault.
            Assert.True(Ok(F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.4999M), (VaultAddr, 1.5M)), 0.5M, out _));
            // The first builds paid the fee on top: the vault lost amount plus fee.
            Assert.True(Ok(F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.5M), (VaultAddr, 1.4999M)), 0.5M, out _));
            // Dust change dropped into the fee: the vault lost a little more than the amount.
            Assert.True(Ok(F(new[] { (VaultAddr, 0.500003M) }, (DestAddr, 0.4999M)), 0.5M, out _));
            // Several vault coins.
            Assert.True(Ok(F(new[] { (VaultAddr, 0.3M), (VaultAddr, 0.3M) }, (DestAddr, 0.4999M), (VaultAddr, 0.1M)), 0.5M, out _));
            // A huge real fee still took the amount out of the vault.
            Assert.True(Ok(F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.01M), (VaultAddr, 1.0M)), 1.0M, out _));

            // Almost everything goes back to the vault: 0.001 left it, the withdrawal says 1.
            Assert.False(Ok(F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.0009M), (VaultAddr, 1.999M)), 1.0M, out var w1));
            Assert.Contains("out of the vault", w1);
            // Someone else's coins pay the destination; the vault only lends a small coin.
            Assert.False(Ok(F(new[] { (StrangerAddr, 1.0M), (VaultAddr, 0.001M) }, (DestAddr, 0.9999M)), 1.0M, out var w2));
            Assert.Contains("out of the vault", w2);
            // A deposit INTO the vault, a stranger's transaction, the wrong destination, an unpaid destination.
            Assert.False(Ok(F(new[] { (StrangerAddr, 1.0M) }, (VaultAddr, 0.9999M)), 0.5M, out var w3));
            Assert.Contains("does not spend the vault", w3);
            Assert.False(Ok(F(new[] { (StrangerAddr, 1.0M) }, (DestAddr, 0.9999M)), 0.5M, out _));
            Assert.False(Ok(F(new[] { (VaultAddr, 2.0M) }, (StrangerAddr, 0.4999M), (VaultAddr, 1.5M)), 0.5M, out var w4));
            Assert.Contains("destination", w4);
            // The vault's money went mostly to a third address; the destination got a token payment.
            Assert.False(Ok(F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.0001M), (StrangerAddr, 0.4998M), (VaultAddr, 1.5M)), 0.5M, out var w5));
            Assert.Contains("pays the destination", w5);
            // A withdrawal "to" the vault itself moves nothing out.
            Assert.False(Ok(F(new[] { (VaultAddr, 2.0M) }, (VaultAddr, 0.4999M), (VaultAddr, 1.5M)), 0.5M, out _, dest: Deposit));
            // Another vault's withdrawal does not count for this one.
            Assert.False(Ok(F(new[] { (OtherVaultAddr, 2.0M) }, (DestAddr, 0.4999M), (OtherVaultAddr, 1.5M)), 0.5M, out _));

            // Unknown deposit address, destination or amount: nothing can be established.
            var real = F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.4999M), (VaultAddr, 1.5M));
            Assert.False(IsTheWithdrawalsTransaction(real, Dest, null, 0.5M, out _));
            Assert.False(IsTheWithdrawalsTransaction(real, null, Deposit, 0.5M, out _));
            Assert.False(IsTheWithdrawalsTransaction(real, Dest, Deposit, 0M, out _));
            // Bech32 compares case-insensitively.
            Assert.True(IsTheWithdrawalsTransaction(real, Dest.ToUpperInvariant(), Deposit.ToUpperInvariant(), 0.5M, out _));
            // One satoshi of rounding between the ledger's decimal and Bitcoin, no more.
            Assert.True(Ok(F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.4999M), (VaultAddr, 1.50000001M)), 0.5M, out _));
            Assert.False(Ok(F(new[] { (VaultAddr, 2.0M) }, (DestAddr, 0.4999M), (VaultAddr, 1.50000002M)), 0.5M, out _));
        }

        /// <summary>The facts come from the transaction and the transactions it spends; a body that is not the one asked for is no answer.</summary>
        [Fact]
        public void Facts_AreReadFromTheTransactionAndItsInputs()
        {
            var (tx, prev) = Build(new[] { (VaultAddr, 2.0M), (StrangerAddr, 0.25M) }, (DestAddr, 0.4999M), (VaultAddr, 1.5M));
            tx.Outputs.Add(Money.Zero, TxNullDataTemplate.Instance.GenerateScriptPubKey(new byte[] { 1, 2, 3 }));       // an output with no address is still an output
            var facts = BuildFacts(tx, h => prev.TryGetValue(h, out var p) ? p : null, 4, Net)!;
            Assert.Equal(4, facts.Confirmations);
            Assert.Equal(Sats(2.0M), facts.SpentFrom(Deposit));
            Assert.Equal(Sats(0.25M), facts.SpentFrom(StrangerAddr.ToString()));
            Assert.Equal(Sats(1.5M), facts.PaidTo(Deposit));
            Assert.Equal(Sats(0.4999M), facts.PaidTo(Dest));
            Assert.Equal(3, facts.Outputs.Count);
            Assert.Equal(Sats(2.25M) - Sats(1.9999M), facts.FeeSats);

            Assert.Null(BuildFacts(tx, _ => null, 4, Net));                                                        // a previous transaction no server has
            var wrong = prev.Values.First();
            Assert.Null(BuildFacts(tx, _ => wrong, 4, Net));                                                       // a previous transaction that is not the one asked for

            var many = Net.CreateTransaction();
            for (var i = 0; i <= MaxInputs; i++) many.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0));
            many.Outputs.Add(Money.Coins(1), DestAddr);
            var notExamined = BuildFacts(many, _ => throw new InvalidOperationException("no lookups for an oversized transaction"), 9, Net)!;
            Assert.NotNull(notExamined.NotExamined);
            Assert.False(IsTheWithdrawalsTransaction(notExamined, Dest, Deposit, 0.5M, out _));
        }

        /// <summary>One Bitcoin transaction pays one withdrawal: the earliest completed row that names it.</summary>
        [Fact]
        public async Task ATxidThatPaidOneWithdrawal_DoesNotCountForAnother()
        {
            var real = MineWithdrawal(10, 0.7M);
            var first = Completed(0.5M, real);                                    // 0.7 left the vault: covers this row
            Assert.Equal(0M, await NotCounted());
            Completed(0.7M, real.ToUpperInvariant());                             // the same txid again, any case
            Assert.Equal(0.7M, await NotCounted());
            Completed(0.1M, real);
            Assert.Equal(0.8M, await NotCounted());

            Assert.True(IsBtcTxidAlreadyUsed(real, "another-request", out var by));
            Assert.Equal(first, by);
            Assert.False(IsBtcTxidAlreadyUsed(new string('b', 64), "another-request", out _));
            Assert.False(IsBtcTxidAlreadyUsed(null, "another-request", out _));
        }

        /// <summary>A completed row that names no transaction is not added back (it used to skip the check altogether).</summary>
        [Fact]
        public async Task ACompletedRowWithNoTxid_IsNotAddedBack()
        {
            Completed(0.4M, null);
            Completed(0.1M, "  ");
            Assert.Equal(0.5M, await NotCounted());
            Assert.Empty(_asked);
        }

        /// <summary>
        /// Fourth review: the signed-txid check read a field only the node that ran the ceremony fills. Every validator that
        /// contributed a share holds a durable signing record; that is what binds the COMPLETE now.
        /// </summary>
        [Fact]
        public async Task ASigner_CountsOnlyTheTransactionItSigned()
        {
            var signedTx = MineWithdrawal(10, 0.5M);
            var otherTx = MineWithdrawal(10, 0.5M);                                // another confirmed spend of the vault to the same destination
            var req = Completed(0.5M, otherTx);
            Assert.Equal(0M, await NotCounted());                                  // not a signer: judged on the chain facts

            Assert.True(FrostSignedWithdrawalEvidence.Record(_vault, req, signedTx.ToUpperInvariant(), new[] { "aa:0" }));
            Assert.Equal(signedTx, SignedTxIdFor(_vault, req, null));
            Assert.Equal(0.5M, await NotCounted());                                // this node signed a different transaction for it

            Assert.True(FrostSignedWithdrawalEvidence.Record(_vault, req, otherTx, new[] { "aa:0" }));
            Assert.Equal(0M, await NotCounted());

            // The node that ran the ceremony has the txid on its row; a signing record, when there is one, is preferred.
            Assert.Equal("cc", SignedTxIdFor(_vault, "no-record", " CC "));
            Assert.Null(SignedTxIdFor(_vault, "no-record", null));
            Assert.Equal(otherTx, SignedTxIdFor(_vault, req, "cc"));
        }

        /// <summary>A bridge exit spends the same vault; its outflow is carried by the exit's lock debit, never by a withdrawal.</summary>
        [Fact]
        public async Task ABridgeExitsTransaction_DoesNotCompleteAWithdrawal()
        {
            var exitTx = MineWithdrawal(10, 0.5M);                                 // same shape, same destination
            var secondExitTx = MineWithdrawal(10, 0.25M);
            Assert.True(VBTCBridgeBtcExitState.TryInsert(new VBTCBridgeBtcExitState { BaseBurnTxHash = "0xburn1", SmartContractUID = _vault, Amount = 0.75M, BtcDestination = Dest }));
            Completed(0.5M, exitTx);
            Completed(0.25M, secondExitTx);
            Assert.Equal(0M, await NotCounted());                                  // the exit has not completed: nothing records its transactions yet

            VBTCBridgeBtcExitState.MarkComplete("0xburn1", exitTx.ToUpperInvariant() + "," + secondExitTx);   // one exit, one transaction per vault it drew on
            Assert.Equal(0.75M, await NotCounted());
            Assert.Contains(exitTx, BridgeExitTxIds());
            Assert.Contains(secondExitTx, BridgeExitTxIds());

            // A failed exit paid nothing and names no Bitcoin transaction.
            Assert.True(VBTCBridgeBtcExitState.TryInsert(new VBTCBridgeBtcExitState { BaseBurnTxHash = "0xburn2", SmartContractUID = _vault, BtcTxHash = VBTCBridgeBtcExitState.FailedMarkerPrefix + "vfxhash" }));
            Assert.Equal(2, BridgeExitTxIds().Count);
        }

        /// <summary>What a validator refuses to admit (never block acceptance): reuse, an exit's transaction, a transaction it did not sign.</summary>
        [Fact]
        public void Admission_RefusesReuse_AnExitsTransaction_AndATransactionThisNodeDidNotSign()
        {
            var real = new string('1', 64);
            var exit = new string('2', 64);
            var signed = new string('3', 64);
            var prior = Completed(0.5M, real);
            VBTCBridgeBtcExitState.TryInsert(new VBTCBridgeBtcExitState { BaseBurnTxHash = "0xburn9", SmartContractUID = _vault });
            VBTCBridgeBtcExitState.MarkComplete("0xburn9", exit);
            FrostSignedWithdrawalEvidence.Record(_vault, "req-new", signed, new[] { "aa:0" });

            Assert.Null(AdmissionRefusal(_vault, "req-new", signed, null));
            Assert.Null(AdmissionRefusal(_vault, "req-unsigned", new string('4', 64), null));     // this node signed nothing for it and knows nothing against it
            Assert.Null(AdmissionRefusal(_vault, prior, real, null));                              // the row's own transaction
            Assert.Contains("already completed", AdmissionRefusal(_vault, "req-new", real.ToUpperInvariant(), null));
            Assert.Contains("bridge exit", AdmissionRefusal(_vault, "req-unsigned", exit, null));
            Assert.Contains("This node signed", AdmissionRefusal(_vault, "req-new", new string('4', 64), null));
            Assert.Contains("This node signed", AdmissionRefusal(_vault, "req-coordinator", new string('4', 64), signed)); // the row of the node that ran the ceremony
        }

        /// <summary>Facts are cached only once a transaction is deep enough that a reorg is not planned for; verdicts are never cached.</summary>
        [Fact]
        public async Task FactsAreCached_OnlyAfterSixConfirmations()
        {
            var shallow = MineWithdrawal(2, 0.5M);
            Completed(0.5M, shallow);
            await NotCounted();
            await NotCounted();
            Assert.Equal(2, _asked.Count);                                         // asked again: 2 confirmations is not cached

            _asked.Clear();
            VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.DeleteAll();
            var deep = MineWithdrawal(6, 0.5M);
            Completed(0.5M, deep);
            Assert.Equal(0M, await NotCounted());
            Assert.Equal(0M, await NotCounted());
            Assert.Single(_asked);

            // The cached facts still judge each row on its own amount.
            VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.DeleteAll();
            Completed(0.9M, deep);
            Assert.Equal(0.9M, await NotCounted());
            Assert.Single(_asked);
        }

        /// <summary>Without the vault's deposit address nothing can be verified: the add-back waits.</summary>
        [Fact]
        public async Task WithoutTheDepositAddress_NothingIsAddedBack()
        {
            Completed(0.5M, MineWithdrawal(10, 0.5M));
            ResolveDepositAddress = _ => null;
            Assert.Equal(0.5M, await NotCounted());
            ResolveDepositAddress = _ => throw new InvalidOperationException("state unavailable");
            Assert.Null(await NotCounted());
        }

        [Fact]
        public void Candidates_AreTheCompletedRowsTheAddBackCounts()
        {
            var h1 = Completed(0.5M, "btc-1");
            var h2 = Completed(0.3M, "btc-2");
            VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest { RequestorAddress = "xOwner", OriginalUniqueId = "u3", SmartContractUID = _vault, TransactionHash = "H3", Amount = 0.9M, BTCDestination = Dest, Status = VBTCWithdrawalStatus.Cancelled, IsCompleted = true });   // no burn: never added back, never a candidate
            VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest { RequestorAddress = "xOwner", OriginalUniqueId = "u4", SmartContractUID = _vault, TransactionHash = "H4", Amount = 0.7M, BTCDestination = Dest, Status = VBTCWithdrawalStatus.Requested });
            FrostSignedWithdrawalEvidence.Record(_vault, h2, "BTC-2", null);
            var candidates = Candidates(_vault, Height);
            Assert.Equal(2, candidates.Count);
            Assert.Contains(candidates, c => c.BtcTxId == "btc-1" && c.Amount == 0.5M && c.Destination == Dest && c.SmartContractUID == _vault && c.RequestHash == h1 && c.CompletedAt > 0 && c.SignedBtcTxId == null);
            Assert.Contains(candidates, c => c.BtcTxId == "btc-2" && c.Amount == 0.3M && c.RequestHash == h2 && c.SignedBtcTxId == "btc-2");
            Assert.Equal(0.8M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount("xOwner", _vault, Height));
        }

        /// <summary>The balance check itself: the deposit must cover what is asked of it plus every withdrawal not yet counted.</summary>
        [Fact]
        public async Task OwnerDepositCheck_RequiresTheDepositToCoverTheUncountedWithdrawal()
        {
            var (tx, prev) = Build(new[] { (VaultAddr, 1.0M) }, (DestAddr, 0.3999M), (VaultAddr, 0.6M));
            var txid = tx.GetHash().ToString();
            Completed(0.4M, txid, feeRate: int.MaxValue);
            _chain[txid] = new BtcTxFacts { Confirmations = 0 };                   // broadcast, or withheld in a mempool

            using var electrum = new FakeElectrum(("one.test", true, 1.0M));
            // The owner ledger (with the 0.4 add-back) leaves 0.8 for the deposit to cover; the deposit shows 1.0 but
            // 0.4 of it is the withheld withdrawal, so only 0.6 is really spendable.
            var withRule = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, Height);
            Assert.Equal(OwnerDepositStatus.Checked, withRule.Status);
            Assert.Equal(0.6M, withRule.DepositBalance);

            var before = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false);
            Assert.Equal(1.0M, before.DepositBalance); // the hole: the full deposit counted

            // Confirmed, but only 0.001 left the vault (the rest went back to it): still not counted.
            var (tiny, tinyPrev) = Build(new[] { (VaultAddr, 1.0M) }, (DestAddr, 0.0009M), (VaultAddr, 0.999M));
            _chain[txid] = BuildFacts(tiny, h => tinyPrev.TryGetValue(h, out var q) ? q : null, 5, Net);
            var small = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, Height);
            Assert.Equal(0.6M, small.DepositBalance);

            // The real withdrawal, confirmed: counted, the full deposit stands.
            _chain[txid] = BuildFacts(tx, h => prev.TryGetValue(h, out var q) ? q : null, 6, Net);
            var real = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, Height);
            Assert.Equal(1.0M, real.DepositBalance);

            ResetForTests();
            _chain.Remove(txid);
            var noAnswer = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, false, _vault, Height);
            Assert.Equal(OwnerDepositStatus.Unverifiable, noAnswer.Status); // fail closed, like an unreadable deposit

            var blockValidation = await VbtcOwnerDeposit.CheckAsync(() => Deposit, 0.8M, false, true, _vault, Height);
            Assert.Equal(OwnerDepositStatus.NotQueried, blockValidation.Status); // block validation untouched
        }
    }
}
