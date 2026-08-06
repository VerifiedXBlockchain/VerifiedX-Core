using System;
using System.IO;
using LiteDB;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Withdrawal-lockout fixes:
    /// - Read-time expiry on the per-user gates (GetActiveRequest / GetIncompleteWithdrawalAmount),
    ///   including the Timestamp fallback that self-heals legacy 0-height rows (the stuck mainnet
    ///   incident: requester locked out of a contract forever after a stalled withdrawal).
    /// - Height-gated consensus fixes in HasActiveContractRequest (legacy 0-height release,
    ///   local-only row exclusion for consensus callers).
    /// - Per-requestor repeat-request cooldown (anti-griefing).
    /// - Save dedup convergence: the mined record updates a raw pre-registration row instead of
    ///   inserting a second, forever-incomplete row.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCWithdrawalExpiryAndCooldownTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly long _savedActivationHeight;
        private const string ScA = "vbtc-contract-A";
        private const string Requester = "xReq1111111111111111111111111111111";

        public VBTCWithdrawalExpiryAndCooldownTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"vbtc_wr_exp_test_{Guid.NewGuid():N}.db");
            DbContext.DB_VBTCWithdrawalRequests = new LiteDatabase(
                new ConnectionString { Filename = _dbPath, Connection = ConnectionType.Direct });
            _savedActivationHeight = Globals.V2WithdrawalExpiryFixHeight;
        }

        public void Dispose()
        {
            Globals.V2WithdrawalExpiryFixHeight = _savedActivationHeight;
            try { DbContext.DB_VBTCWithdrawalRequests?.Dispose(); } catch { }
            DbContext.DB_VBTCWithdrawalRequests = null;
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }

        private static ILiteCollection<VBTCWithdrawalRequest> Col()
            => DbContext.DB_VBTCWithdrawalRequests.GetCollection<VBTCWithdrawalRequest>(
                DbContext.RSRV_VBTC_WITHDRAWAL_REQUESTS);

        private static void Insert(string scUID, string requester, long height, string txHash,
            decimal amount = 0.001M, long timestamp = 0, bool completed = false)
        {
            Col().Insert(new VBTCWithdrawalRequest
            {
                RequestorAddress = requester,
                SmartContractUID = scUID,
                OriginalUniqueId = string.IsNullOrEmpty(txHash) ? Guid.NewGuid().ToString() : txHash,
                TransactionHash = txHash,
                Amount = amount,
                Timestamp = timestamp,
                Status = completed ? VBTCWithdrawalStatus.Completed : VBTCWithdrawalStatus.Requested,
                IsCompleted = completed,
                RequestBlockHeight = height
            });
        }

        // ---------- Per-user gate: GetActiveRequest ----------

        [Fact]
        public void GetActiveRequest_FreshRequest_Blocks()
        {
            Insert(ScA, Requester, height: 1000, txHash: "tx-fresh");
            var active = VBTCWithdrawalRequest.GetActiveRequest(Requester, ScA, currentHeight: 1100, currentTime: TimeUtil.GetTime());
            Assert.NotNull(active);
        }

        [Fact]
        public void GetActiveRequest_ExpiredRequest_NoLongerBlocks()
        {
            // The core lockout fix: past the anti-grief window the requester is free again —
            // previously this blocked FOREVER (no expiry at all in GetActiveRequest).
            Insert(ScA, Requester, height: 1000, txHash: "tx-stale");
            var active = VBTCWithdrawalRequest.GetActiveRequest(Requester, ScA,
                currentHeight: 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1, currentTime: TimeUtil.GetTime());
            Assert.Null(active);
        }

        [Fact]
        public void GetActiveRequest_StuckMainnetRowShape_SelfHeals()
        {
            // Fixture reproducing the 5 stuck mainnet rows: created by pre-upgrade code, so
            // RequestBlockHeight deserializes as 0 and Timestamp is months old. The Timestamp
            // fallback must release the requester with NO data migration.
            var monthsAgo = TimeUtil.GetTime() - (86400L * 60); // ~May 29 vintage
            Insert(ScA, Requester, height: 0, txHash: "tx-legacy-stuck", timestamp: monthsAgo);

            var active = VBTCWithdrawalRequest.GetActiveRequest(Requester, ScA,
                currentHeight: 5_000_000, currentTime: TimeUtil.GetTime());
            Assert.Null(active);
        }

        [Fact]
        public void GetActiveRequest_ZeroHeightButRecent_StillBlocks()
        {
            // A 0-height row inside the wall-clock window (e.g. just submitted, not yet mined)
            // must still block its requester — fail toward locked.
            Insert(ScA, Requester, height: 0, txHash: "tx-recent-unmined", timestamp: TimeUtil.GetTime());
            var active = VBTCWithdrawalRequest.GetActiveRequest(Requester, ScA,
                currentHeight: 5_000_000, currentTime: TimeUtil.GetTime());
            Assert.NotNull(active);
        }

        // ---------- Per-user gate: GetIncompleteWithdrawalAmount ----------

        [Fact]
        public void IncompleteAmount_ExpiredRequest_NoLongerSuppressesBalance()
        {
            // The stuck rows also suppressed AvailableBalance forever (balance - pending).
            Insert(ScA, Requester, height: 1000, txHash: "tx-old", amount: 0.5M);
            var now = TimeUtil.GetTime();

            var withinWindow = VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(Requester, ScA, 1100, now);
            Assert.Equal(0.5M, withinWindow);

            var pastWindow = VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(
                Requester, ScA, 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1, now);
            Assert.Equal(0M, pastWindow);
        }

        // ---------- Consensus gate: legacy 0-height release (height-gated) ----------

        [Fact]
        public void ContractGate_Legacy0HeightMinedRow_BlocksForeverBeforeActivation()
        {
            Globals.V2WithdrawalExpiryFixHeight = 999_999_999_999L; // gate inert
            Insert(ScA, Requester, height: 0, txHash: "tx-legacy-mined");
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(ScA, currentHeight: 9_000_000));
        }

        [Fact]
        public void ContractGate_Legacy0HeightMinedRow_ReleasesAfterActivationGraceWindow()
        {
            Globals.V2WithdrawalExpiryFixHeight = 1_000_000;
            Insert(ScA, Requester, height: 0, txHash: "tx-legacy-mined");

            // Inside the grace window (activation + EXPIRY_BLOCKS): still blocking.
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(
                ScA, currentHeight: 1_000_000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS));

            // Past the grace window: released permanently — this is what unsticks the contracts
            // held hostage by pre-upgrade rows.
            Assert.False(VBTCWithdrawalRequest.HasActiveContractRequest(
                ScA, currentHeight: 1_000_000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1));
        }

        // ---------- Consensus gate: local-only row exclusion (fork-vector fix) ----------

        [Fact]
        public void ContractGate_LocalOnlyRow_IgnoredByConsensusAfterActivation()
        {
            Globals.V2WithdrawalExpiryFixHeight = 1_000_000;
            // Raw pre-registration row: TransactionHash == "", exists only on the serving API node.
            Insert(ScA, Requester, height: 2_000_000, txHash: "");

            // Consensus callers (includeLocalOnlyRows: false) must not see it — otherwise this one
            // node rejects a block every other node accepts.
            Assert.False(VBTCWithdrawalRequest.HasActiveContractRequest(
                ScA, currentHeight: 2_000_100, includeLocalOnlyRows: false));

            // Local gates still see it (fast feedback for the serving node's own API users).
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(
                ScA, currentHeight: 2_000_100, includeLocalOnlyRows: true));
        }

        [Fact]
        public void ContractGate_LocalOnlyRow_StillBlocksConsensusBeforeActivation()
        {
            // Pre-activation behavior must stay byte-identical for historical replay.
            Globals.V2WithdrawalExpiryFixHeight = 999_999_999_999L;
            Insert(ScA, Requester, height: 2_000_000, txHash: "");
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest(
                ScA, currentHeight: 2_000_100, includeLocalOnlyRows: false));
        }

        // ---------- Anti-griefing: per-requestor repeat cooldown ----------

        [Fact]
        public void RepeatCooldown_InactiveBelowActivationHeight()
        {
            Globals.V2WithdrawalExpiryFixHeight = 999_999_999_999L;
            Insert(ScA, Requester, height: 1000, txHash: "tx-expired");
            Assert.False(VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(
                Requester, ScA, currentHeight: 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 10));
        }

        [Fact]
        public void RepeatCooldown_AppliesAfterExpiredIncompleteRequest()
        {
            Globals.V2WithdrawalExpiryFixHeight = 1;
            Insert(ScA, Requester, height: 1000, txHash: "tx-expired");
            var expiredAt = 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS;

            // While active (not yet expired): no cooldown (the contract gate handles blocking).
            Assert.False(VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(Requester, ScA, expiredAt));

            // Just expired: griefer would re-request here — cooldown must bite.
            Assert.True(VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(Requester, ScA, expiredAt + 1));
            Assert.True(VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(
                Requester, ScA, expiredAt + VBTCWithdrawalRequest.REPEAT_REQUEST_COOLDOWN_BLOCKS));

            // Cooldown served: free again.
            Assert.False(VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(
                Requester, ScA, expiredAt + VBTCWithdrawalRequest.REPEAT_REQUEST_COOLDOWN_BLOCKS + 1));
        }

        [Fact]
        public void RepeatCooldown_IgnoresOtherRequestersAndCompletedRows()
        {
            Globals.V2WithdrawalExpiryFixHeight = 1;
            Insert(ScA, "xOtherAddr", height: 1000, txHash: "tx-other");
            Insert(ScA, Requester, height: 1000, txHash: "tx-done", completed: true);

            var expiredAt = 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS;
            Assert.False(VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(Requester, ScA, expiredAt + 10));
        }

        [Fact]
        public void RepeatCooldown_IgnoresLocalOnlyAndUnminedRows()
        {
            // Only MINED rows (real height + real tx hash) count — consensus must never depend on
            // rows other nodes can't see.
            Globals.V2WithdrawalExpiryFixHeight = 1;
            Insert(ScA, Requester, height: 1000, txHash: ""); // local-only shape
            Insert(ScA, Requester, height: 0, txHash: "tx-unmined"); // unmined shape

            Assert.False(VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(
                Requester, ScA, 1000 + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 10));
        }

        // ---------- Save dedup: mined record converges onto the raw pre-registration row ----------

        [Fact]
        public void Save_MinedRecordUpdatesRawPreRegistrationRow_NoDuplicate()
        {
            const string uniqueId = "web-wallet-uid-123";

            // 1. RequestWithdrawalRaw pre-registers: local row with empty TransactionHash.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = Requester,
                SmartContractUID = ScA,
                OriginalUniqueId = uniqueId,
                TransactionHash = "",
                Amount = 0.01M,
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = 900
            }));

            // 2. The mined TX (UniqueId carried in tx.Data) processes via StateData on this node:
            //    same composite key, update: true.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = Requester,
                SmartContractUID = ScA,
                OriginalUniqueId = uniqueId,
                TransactionHash = "mined-tx-hash",
                Amount = 0.01M,
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = 950
            }, update: true));

            // ONE row, carrying the real hash + mined height — completion/cancellation can now
            // find and clear it (previously a second row was inserted and the raw row stayed
            // incomplete forever).
            var rows = Col().Query().Where(x => x.SmartContractUID == ScA).ToList();
            Assert.Single(rows);
            Assert.Equal("mined-tx-hash", rows[0].TransactionHash);
            Assert.Equal(950, rows[0].RequestBlockHeight);

            // 3. Completion clears the (single) row.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = Requester,
                SmartContractUID = ScA,
                OriginalUniqueId = uniqueId,
                TransactionHash = "mined-tx-hash",
                Status = VBTCWithdrawalStatus.Completed,
                IsCompleted = true
            }, update: true));

            var final = VBTCWithdrawalRequest.GetByTransactionHash("mined-tx-hash")!;
            Assert.True(final.IsCompleted);
            Assert.Null(VBTCWithdrawalRequest.GetActiveRequest(Requester, ScA, 951, TimeUtil.GetTime()));
        }
    }
}
