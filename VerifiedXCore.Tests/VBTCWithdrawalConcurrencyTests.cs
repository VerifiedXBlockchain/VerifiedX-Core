using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.ElectrumX.Results;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// S3C §0 revision — withdrawal concurrency (Globals.VbtcWithdrawalConcurrencyHeight):
    ///  - consensus: a request no longer locks its contract, and every request must clear the fee floor
    ///    (the live incident: 0.000001 BTC at 30 sat/vB was mined, then could never be paid, and froze the
    ///    contract for every other holder for 360 blocks);
    ///  - coin selection: smallest covering coin first, pinned coins never selected;
    ///  - validators: pins per withdrawal, disjoint transactions sign in parallel, a live withdrawal's coins
    ///    are refused, a withheld (reclaimable) one's are not;
    ///  - "can this signed tx still confirm": only a CONFIRMED spend of one of its inputs kills it.
    /// Mutates Globals and DbContext — serialized via the DbContextSequential collection.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCWithdrawalConcurrencyTests : IDisposable
    {
        private const long TestHeight = 1000;
        private const string Requestor = "RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7";
        private const string OtherHolder = "RSomeoneElse000000000000000000000001";
        private const string BtcDestination = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorConcurrencyHeight;
        private readonly long _priorExpiryFixHeight;
        private readonly Block _priorLastBlock;

        public VBTCWithdrawalConcurrencyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcconc_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorConcurrencyHeight = Globals.VbtcWithdrawalConcurrencyHeight;
            _priorExpiryFixHeight = Globals.V2WithdrawalExpiryFixHeight;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            FrostWithdrawalSigningTracker.ResetForTests();

            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Requestor, Balance = 1000M, Nonce = 0 });
            Globals.LastBlock = new Block { Height = TestHeight + 1 };
        }

        public void Dispose()
        {
            FrostWithdrawalSigningTracker.ResetForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.VbtcWithdrawalConcurrencyHeight = _priorConcurrencyHeight;
            Globals.V2WithdrawalExpiryFixHeight = _priorExpiryFixHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void ConcurrencyOn() => Globals.VbtcWithdrawalConcurrencyHeight = 1;
        private static void ConcurrencyOff() => Globals.VbtcWithdrawalConcurrencyHeight = 999_999_999_999L;

        private static void SeedContract(string scUid, params (string From, string To, decimal Amount)[] rows)
        {
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = scUid,
                OwnerAddress = "xSomeOwner",
                ContractData = VbtcTestContracts.VbtcV2ContractData,
                SCStateTreiTokenizationTXes = rows
                    .Select(r => new SmartContractStateTreiTokenizationTX { FromAddress = r.From, ToAddress = r.To, Amount = r.Amount })
                    .ToList(),
            });
        }

        private static void SeedOpenRequest(string scUid, string requester, string hash)
        {
            VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = requester,
                SmartContractUID = scUid,
                Amount = 0.00000100M,
                BTCDestination = BtcDestination,
                FeeRate = 30,
                OriginalUniqueId = hash,
                TransactionHash = hash,
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = TestHeight,
            });
        }

        private static Transaction SingleRequest(string scUid, decimal amount, int feeRate)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = Requestor,
                ToAddress = Requestor,
                Amount = 0.0M,
                Fee = 0.00001M,
                Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalRequest()",
                    ContractUID = scUid,
                    RequestorAddress = Requestor,
                    BTCAddress = BtcDestination,
                    Amount = amount,
                    FeeRate = feeRate,
                }),
            };
            tx.Build();
            return tx;
        }

        private static Transaction MultiRequest((string ScUid, decimal Amount)[] inputs, int feeRate)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = Requestor,
                ToAddress = Requestor,
                Amount = 0.0M,
                Fee = 0.00001M,
                Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new
                {
                    Function = VBTCService.MultiWithdrawalFunction,
                    RequestorAddress = Requestor,
                    BTCAddress = BtcDestination,
                    TotalAmount = inputs.Sum(i => i.Amount),
                    FeeRate = feeRate,
                    Inputs = inputs.Select(i => new { SCUID = i.ScUid, Amount = i.Amount }).ToArray(),
                }),
            };
            tx.Build();
            return tx;
        }

        // ── Fee floor (pure) ────────────────────────────────────────────────────────────────────

        [Fact]
        public void FeeFloor_ReportedIncident_Rejected()
        {
            // 100 sats at 30 sat/vB: the smallest withdrawal tx costs 4,620 sats.
            var error = VBTCService.GetWithdrawalFeeFloorError(0.00000100M, 30, "vBTC V2 withdrawal request");
            Assert.NotNull(error);
            Assert.Contains("4620 sats", error);
            Assert.Contains("0.0000495 BTC", error); // 4,620 + 330 dust
        }

        [Fact]
        public void FeeFloor_ExactBoundary()
        {
            Assert.Null(VBTCService.GetWithdrawalFeeFloorError(0.00004950M, 30, "x"));    // payout exactly 330
            Assert.NotNull(VBTCService.GetWithdrawalFeeFloorError(0.00004949M, 30, "x")); // payout 329
            Assert.Null(VBTCService.GetWithdrawalFeeFloorError(0.00000484M, 1, "x"));     // 154 + 330 at 1 sat/vB
            Assert.NotNull(VBTCService.GetWithdrawalFeeFloorError(0.00000483M, 1, "x"));
        }

        [Fact]
        public void FeeFloor_LeavesOtherRulesToTheirOwnChecks()
        {
            Assert.Null(VBTCService.GetWithdrawalFeeFloorError(0.00000100M, 0, "x"));  // fee-rate rule reports
            Assert.Null(VBTCService.GetWithdrawalFeeFloorError(-1M, 30, "x"));         // amount rule reports
        }

        [Fact]
        public void FeeFloor_MatchesTheBuildersSmallestTransaction()
        {
            // BitcoinTransactionService: (int)(57.5 × inputs + 43 × 2 + 10.5) vB.
            Assert.Equal((int)(57.5 * 1 + 43 * 2 + 10.5), VBTCService.MinWithdrawalTxVBytes);
        }

        // ── Consensus: single request ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Single_Active_DustRequestRejectedAtConsensus()
        {
            ConcurrencyOn();
            SeedContract("conc:dust", ("+", Requestor, 0.0001M));

            var (ok, message) = await TransactionValidatorService.VerifyTX(SingleRequest("conc:dust", 0.00000100M, 30));

            Assert.False(ok);
            Assert.Contains("cannot pay a Bitcoin withdrawal at 30 sat/vB", message);
        }

        [Fact]
        public async Task Single_BeforeHeight_DustRequestNotJudgedByTheFloor()
        {
            // Historical replay: block 1,017,408 on testnet holds exactly such a request.
            ConcurrencyOff();
            SeedContract("conc:legacydust", ("+", Requestor, 0.0001M));

            var (ok, message) = await TransactionValidatorService.VerifyTX(SingleRequest("conc:legacydust", 0.00000100M, 30));

            Assert.False(ok);
            Assert.Equal("Signature cannot be null.", message); // cleared every vBTC rule
        }

        [Fact]
        public async Task Single_Active_OtherHoldersOpenRequestDoesNotBlock()
        {
            ConcurrencyOn();
            Globals.V2WithdrawalExpiryFixHeight = 1;
            SeedContract("conc:shared", ("+", Requestor, 0.001M), ("+", OtherHolder, 0.001M));
            SeedOpenRequest("conc:shared", OtherHolder, "otherholderhash");

            var (ok, message) = await TransactionValidatorService.VerifyTX(SingleRequest("conc:shared", 0.0008M, 30));

            Assert.False(ok);
            Assert.Equal("Signature cannot be null.", message);
        }

        [Fact]
        public async Task Single_BeforeHeight_OtherHoldersOpenRequestStillBlocks()
        {
            ConcurrencyOff();
            Globals.V2WithdrawalExpiryFixHeight = 1;
            SeedContract("conc:legacyshared", ("+", Requestor, 0.001M), ("+", OtherHolder, 0.001M));
            SeedOpenRequest("conc:legacyshared", OtherHolder, "legacyotherhash");

            var (ok, message) = await TransactionValidatorService.VerifyTX(SingleRequest("conc:legacyshared", 0.0008M, 30));

            Assert.False(ok);
            Assert.Contains("A withdrawal is already in progress", message);
        }

        [Fact]
        public async Task Single_Active_OwnPriorOpenRequestDoesNotBlock()
        {
            // An exchange pays many customers from one address: several open requests per holder.
            ConcurrencyOn();
            Globals.V2WithdrawalExpiryFixHeight = 1;
            SeedContract("conc:self", ("+", Requestor, 0.01M));
            SeedOpenRequest("conc:self", Requestor, "ownpriorhash");

            var (ok, message) = await TransactionValidatorService.VerifyTX(SingleRequest("conc:self", 0.001M, 10));

            Assert.False(ok);
            Assert.Equal("Signature cannot be null.", message);
        }

        // ── Consensus: multi request ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Multi_Active_InputBelowFloorRejectedNamingIt()
        {
            ConcurrencyOn();
            SeedContract("conc:m1", ("+", Requestor, 0.01M));
            SeedContract("conc:m2", ("+", Requestor, 0.01M));

            var (ok, message) = await TransactionValidatorService.VerifyTX(MultiRequest(new[] { ("conc:m1", 0.001M), ("conc:m2", 0.00002M) }, 30));

            Assert.False(ok);
            Assert.Contains("input conc:m2", message);
            Assert.Contains("cannot pay a Bitcoin withdrawal", message);
        }

        [Fact]
        public async Task Multi_Active_InputContractWithOtherHoldersRequestAllowed()
        {
            ConcurrencyOn();
            Globals.V2WithdrawalExpiryFixHeight = 1;
            SeedContract("conc:ma", ("+", Requestor, 0.01M));
            SeedContract("conc:mb", ("+", Requestor, 0.01M), ("+", OtherHolder, 0.01M));
            SeedOpenRequest("conc:mb", OtherHolder, "multiotherhash");

            var (ok, message) = await TransactionValidatorService.VerifyTX(MultiRequest(new[] { ("conc:ma", 0.001M), ("conc:mb", 0.001M) }, 10));

            Assert.False(ok);
            Assert.Equal("Signature cannot be null.", message);
        }

        // ── Coin selection (pure) ───────────────────────────────────────────────────────────────

        private static BlockchainScripthashListunspentResult Utxo(string txid, uint vout, ulong sats) =>
            new BlockchainScripthashListunspentResult { TxHash = txid, TxPos = vout, Value = sats, Height = 1 };

        [Fact]
        public void Selection_Active_SmallestCoveringCoinFirst()
        {
            var utxos = new[] { Utxo("aa", 0, 1_000_000), Utxo("bb", 0, 60_000), Utxo("cc", 0, 20_000), Utxo("dd", 0, 5_000) };

            var ordered = BitcoinTransactionService.OrderUtxosForSelection(utxos, 50_000, null, smallestSufficientFirst: true);

            Assert.Equal("bb", ordered[0].TxHash); // 60k: the smallest single coin covering 50k
            Assert.Equal(new[] { "aa", "cc", "dd" }, ordered.Skip(1).Select(u => u.TxHash));
        }

        [Fact]
        public void Selection_Active_NoSingleCoinCovers_LargestFirst()
        {
            var utxos = new[] { Utxo("aa", 0, 30_000), Utxo("bb", 0, 20_000), Utxo("cc", 0, 10_000) };

            var ordered = BitcoinTransactionService.OrderUtxosForSelection(utxos, 45_000, null, smallestSufficientFirst: true);

            Assert.Equal(new[] { "aa", "bb", "cc" }, ordered.Select(u => u.TxHash));
        }

        [Fact]
        public void Selection_Legacy_LargestFirst()
        {
            var utxos = new[] { Utxo("dd", 0, 5_000), Utxo("aa", 0, 1_000_000), Utxo("bb", 0, 60_000) };

            var ordered = BitcoinTransactionService.OrderUtxosForSelection(utxos, 50_000, null, smallestSufficientFirst: false);

            Assert.Equal(new[] { "aa", "bb", "dd" }, ordered.Select(u => u.TxHash));
        }

        [Fact]
        public void Selection_PreferredFirst_ThenLargestFirst()
        {
            var utxos = new[] { Utxo("aa", 0, 1_000_000), Utxo("bb", 0, 60_000), Utxo("cc", 1, 20_000) };

            var ordered = BitcoinTransactionService.OrderUtxosForSelection(utxos, 50_000, new HashSet<string> { "cc:1" }, smallestSufficientFirst: true);

            Assert.Equal(new[] { "cc", "aa", "bb" }, ordered.Select(u => u.TxHash));
        }

        [Fact]
        public void Selection_EqualValues_DeterministicTiebreak()
        {
            var a = new[] { Utxo("ff", 1, 60_000), Utxo("ee", 0, 60_000), Utxo("ff", 0, 60_000) };
            var b = a.Reverse().ToArray();

            var first = BitcoinTransactionService.OrderUtxosForSelection(a, 50_000, null, true).Select(u => $"{u.TxHash}:{u.TxPos}");
            var second = BitcoinTransactionService.OrderUtxosForSelection(b, 50_000, null, true).Select(u => $"{u.TxHash}:{u.TxPos}");

            Assert.Equal(first, second); // FIND-028: retries reproduce the same selection
        }

        // ── "Can this signed tx still confirm" (pure) ───────────────────────────────────────────

        [Fact]
        public void Spend_AllUnspent_NotSpent()
        {
            var verdict = BitcoinTransactionService.ClassifyOutpointSpends(new[] { "t:0", "t:1" }, new HashSet<string> { "t:0", "t:1" }, new HashSet<string>());
            Assert.Equal(BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, verdict);
        }

        [Fact]
        public void Spend_MissingOnlyBecauseAMempoolTxSpendsIt_NotDead()
        {
            // The double-payout case: the spender can still be evicted or replaced, and the signed tx confirm.
            var verdict = BitcoinTransactionService.ClassifyOutpointSpends(new[] { "t:0", "t:1" }, new HashSet<string>(), new HashSet<string> { "t:0", "t:1" });
            Assert.Equal(BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed, verdict);
        }

        [Fact]
        public void Spend_OneInputSpentByConfirmedTx_Dead()
        {
            // Partial overlap is enough: a tx whose input is gone for good can never confirm.
            var verdict = BitcoinTransactionService.ClassifyOutpointSpends(new[] { "t:0", "t:1" }, new HashSet<string> { "t:1" }, new HashSet<string>());
            Assert.Equal(BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed, verdict);
        }

        [Fact]
        public void Spend_MempoolUnreadable_Inconclusive()
        {
            var verdict = BitcoinTransactionService.ClassifyOutpointSpends(new[] { "t:0" }, new HashSet<string>(), null);
            Assert.Equal(BitcoinTransactionService.OutpointSpendVerdict.Inconclusive, verdict);
        }

        // ── Validator reclaim eligibility (pure) ────────────────────────────────────────────────

        private static VBTCWithdrawalRequest Row(bool completed = false, VBTCWithdrawalStatus status = VBTCWithdrawalStatus.Requested) =>
            new VBTCWithdrawalRequest { IsCompleted = completed, Status = status, TransactionHash = "h", SmartContractUID = "c" };

        [Fact]
        public void Reclaim_NoRow_NeverReclaimed()
        {
            // Bridge exits have no withdrawal row; their coins are never taken back.
            Assert.False(FrostStartup.IsPinReclaimEligible(null, 0, 100_000).Eligible);
        }

        [Fact]
        public void Reclaim_CompletedWithdrawal_NeverReclaimed()
        {
            // Its COMPLETE is mined: knocking out its tx would leave it finalized but unpaid.
            Assert.False(FrostStartup.IsPinReclaimEligible(Row(completed: true, status: VBTCWithdrawalStatus.Completed), 0, 100_000).Eligible);
            Assert.False(FrostStartup.IsPinReclaimEligible(Row(status: VBTCWithdrawalStatus.Completed), 0, 100_000).Eligible);
        }

        [Fact]
        public void Reclaim_WithinGrace_NotYet()
        {
            var now = 100_000L;
            Assert.False(FrostStartup.IsPinReclaimEligible(Row(), now - FrostStartup.RECLAIM_GRACE_SECONDS + 1, now).Eligible);
            Assert.True(FrostStartup.IsPinReclaimEligible(Row(), now - FrostStartup.RECLAIM_GRACE_SECONDS, now).Eligible);
        }

        // ── Validator signing tracker, concurrency mode ─────────────────────────────────────────

        private const string ScUID = "vbtc-contract-C";
        private static readonly List<string> Tx1 = new() { "txid1:0", "txid1:1" };

        [Fact]
        public void Tracker_Active_DisjointTxForDifferentWithdrawal_Allowed()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");

            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "w2", 0, "bb", null, new List<string> { "txid9:0" });

            Assert.False(blocked, reason);
        }

        [Fact]
        public void Tracker_Active_TakingALiveWithdrawalsCoin_Refused()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");

            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "w2", 0, "bb", null, new List<string> { "txid1:1", "txid9:0" });

            Assert.True(blocked);
            Assert.Contains(FrostWithdrawalSigningTracker.PinMarker, reason);
            Assert.Contains("txid1:1", reason);
        }

        [Fact]
        public void Tracker_Active_TakingAReclaimablePinsCoin_Allowed()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");

            var (blocked, reason) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "w2", 0, "bb", null,
                new List<string> { "txid1:1" }, new HashSet<string> { "w1" });

            Assert.False(blocked, reason);
        }

        [Fact]
        public void Tracker_Active_ReclaimOfOnePinDoesNotCoverAnother()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w3", "s3", 0, "cc", new List<string> { "txid3:0" }, "btc3");

            var (blocked, _) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "w2", 0, "bb", null,
                new List<string> { "txid1:0", "txid3:0" }, new HashSet<string> { "w1" });

            Assert.True(blocked);
        }

        [Fact]
        public void Tracker_Active_UnannouncedInputsRefusedWhileOthersArePinned()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");

            var (blocked, _) = FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "w2", 0, "bb");

            Assert.True(blocked);
        }

        [Fact]
        public void Tracker_Active_EachWithdrawalKeepsItsOwnPin()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w2", "s1", 0, "bb", new List<string> { "txid2:0" }, "btc2");

            var pins = FrostWithdrawalSigningTracker.GetContractPins(ScUID);
            Assert.Equal(2, pins.Count);

            FrostWithdrawalSigningTracker.ClearContractPin(ScUID, "w1", "test: confirmed");
            var left = Assert.Single(FrostWithdrawalSigningTracker.GetContractPins(ScUID));
            Assert.Equal("w2", left.WithdrawalRequestHash);
        }

        [Fact]
        public void Tracker_Legacy_LatestSigningReplacesThePin()
        {
            ConcurrencyOff();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w2", "s1", 0, "bb", new List<string> { "txid1:0", "txid5:0" }, "btc2");

            var pin = Assert.Single(FrostWithdrawalSigningTracker.GetContractPins(ScUID));
            Assert.Equal("w2", pin.WithdrawalRequestHash);
        }

        [Fact]
        public void Tracker_GetOverlappingPins_OnlyOtherWithdrawalsSharingACoin()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w2", "s1", 0, "bb", new List<string> { "txid2:0" }, "btc2");

            var overlapping = FrostWithdrawalSigningTracker.GetOverlappingPins(ScUID, "w2", new List<string> { "TXID1:1", "txid2:0" });

            var only = Assert.Single(overlapping);
            Assert.Equal("w1", only.WithdrawalRequestHash);
        }

        [Fact]
        public void Tracker_RestorePin_NeverReplacesALivePin()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");

            Assert.False(FrostWithdrawalSigningTracker.RestorePin(ScUID, "w1", "stale", new List<string> { "old:0" }, 1));
            Assert.True(FrostWithdrawalSigningTracker.RestorePin(ScUID, "w9", "btc9", new List<string> { "txid9:0" }, 12345));

            var restored = FrostWithdrawalSigningTracker.GetContractPins(ScUID).Single(p => p.WithdrawalRequestHash == "w9");
            Assert.Equal(12345, restored.PinnedAt); // grace is measured from the original signing
            Assert.Equal("btc1", FrostWithdrawalSigningTracker.GetContractPins(ScUID).Single(p => p.WithdrawalRequestHash == "w1").BtcTxId);
        }

        [Fact]
        public void Tracker_ForgetDeadTransaction_AllowsAReplacementNow()
        {
            // Once the old tx is proven dead (confirmed conflict), the same withdrawal may sign a new tx at once
            // instead of waiting out the 24h in-memory record.
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RecordSigningCompleted(ScUID, "w1", "s0", 0, "aa", Tx1, "btc1");
            Assert.True(FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "w1", 0, "bb").Blocked);

            FrostWithdrawalSigningTracker.ForgetDeadTransaction(ScUID, "w1", "test: conflicted by a confirmed tx");

            Assert.False(FrostWithdrawalSigningTracker.CheckWithdrawalSigning(ScUID, "w1", 0, "bb", null, new List<string> { "txid9:0" }).Blocked);
            Assert.Empty(FrostWithdrawalSigningTracker.GetContractPins(ScUID));
        }

        [Fact]
        public void Tracker_HasSignedTransaction_SeesEveryPinnedWithdrawal()
        {
            ConcurrencyOn();
            FrostWithdrawalSigningTracker.RestorePin(ScUID, "w7", "btc7", new List<string> { "txid7:0" }, 1);

            Assert.True(FrostWithdrawalSigningTracker.HasSignedTransaction(ScUID, "w7"));
        }
    }
}
