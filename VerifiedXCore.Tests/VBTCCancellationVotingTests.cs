using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Withdrawal-cancellation voting (Globals.VbtcCancellationVoteRulesHeight), the path that refunds the escrow of a
    /// withdrawal that will not be paid - for public and S3C contracts alike:
    ///  - consensus: voters and the denominator are the contract's voter set ACTIVE at the vote's height; a reject that
    ///    names a signed Bitcoin transaction rejects the cancellation; cancellations are decided or lapse; a rejected or
    ///    lapsed one can be filed again; the refund belongs to the vote that approved;
    ///  - validators: the vote comes from the validator's own signing records.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCCancellationVotingTests : IDisposable
    {
        private const long RulesHeight = 100;
        private const long CancelHeight = 200;
        private const string Vault = "c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1:1790000001";
        private const string LegacyVault = "c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2:1790000002";
        private const string S3CVault = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3:1790000003";
        private const string Deposit = "bc1pcancellationvotingvaultdeposit0000000000000000000000000";
        private const string Owner = "xVaultOwner0000000000000000000000001";
        private const string Requester = "xRequester00000000000000000000000001";
        private const string RequestHash = "reqhash-cancel-voting-0001";
        private const string SignedTx = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";

        private static readonly string[] Snapshot = { "xVal1", "xVal2", "xVal3", "xVal4", "xVal5" };
        private static readonly string[] S3CSnapshot = { "xS3C1", "xS3C2", "xS3C3" };

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorRulesHeight;
        private readonly long _priorEscrowHeight;
        private readonly string _priorValidatorAddress;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorResyncing = Globals.IsResyncing;
        private readonly bool _priorStopTimers = Globals.StopAllTimers;
        private int _txSeq;

        public VBTCCancellationVotingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtccancelvote_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            _priorRulesHeight = Globals.VbtcCancellationVoteRulesHeight;
            _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
            _priorValidatorAddress = Globals.ValidatorAddress;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            FrostWithdrawalSigningTracker.ResetForTests();

            Globals.VbtcCancellationVoteRulesHeight = RulesHeight;
            Globals.WithdrawalEscrowHeight = 1;
            Globals.LastBlock = new Block { Height = CancelHeight, Timestamp = TimeUtil.GetTime() };

            // Registered at heights 10..: xVal1-4 (xVal5 of the snapshot never registered: a dead validator),
            // the three S3C validators, and one public validator that is in no snapshot.
            long h = 10;
            foreach (var v in Snapshot.Take(4)) Register(v, isS3C: false, h++);
            foreach (var v in S3CSnapshot) Register(v, isS3C: true, h++);
            Register("xPublicOnly", isS3C: false, h++);

            SeedVault(Vault, Snapshot, isS3C: false);
            SeedVault(LegacyVault, Array.Empty<string>(), isS3C: false);
            SeedVault(S3CVault, S3CSnapshot, isS3C: true);
            SeedRequest(Vault, RequestHash);
        }

        public void Dispose()
        {
            FrostWithdrawalSigningTracker.ResetForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.VbtcCancellationVoteRulesHeight = _priorRulesHeight;
            Globals.WithdrawalEscrowHeight = _priorEscrowHeight;
            Globals.ValidatorAddress = _priorValidatorAddress;
            Globals.IsChainSynced = _priorSynced;
            Globals.IsResyncing = _priorResyncing;
            Globals.StopAllTimers = _priorStopTimers;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        // ── Fixtures ────────────────────────────────────────────────────────────────────────────

        private static void Register(string address, bool isS3C, long height) =>
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = height, Hash = $"h{height}-{Guid.NewGuid():N}",
                Transactions = new List<Transaction>
                {
                    new Transaction
                    {
                        TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = address, ToAddress = address, Hash = "r" + height,
                        Data = JsonConvert.SerializeObject(new { ValidatorAddress = address, IPAddress = "10.0.0.1", FrostPublicKey = "pk-" + address, IsS3C = isS3C }),
                    },
                },
            });

        private static void SeedVault(string uid, IEnumerable<string> snapshot, bool isS3C) =>
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.VaultContractData(uid, Owner, Deposit, isS3C: isS3C, snapshot: snapshot),
                MinterAddress = Owner, OwnerAddress = Owner,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = Owner, ToAddress = Requester, Amount = 0.001M },
                    new SmartContractStateTreiTokenizationTX { FromAddress = Requester, ToAddress = "-", Amount = -0.0004M }, // escrow
                },
            });

        private static void SeedRequest(string uid, string hash) =>
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = Requester, OriginalUniqueId = hash, SmartContractUID = uid, Amount = 0.0004M,
                BTCDestination = "tb1qdestination", FeeRate = 10, TransactionHash = hash, Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false, RequestBlockHeight = 150, Timestamp = TimeUtil.GetTime() - 600,
            }));

        private Transaction CancelTx(long height, string uid = Vault, string hash = RequestHash, string from = Requester) => new Transaction
        {
            Hash = $"canceltx{++_txSeq}", Height = height, Timestamp = TimeUtil.GetTime(), FromAddress = from, ToAddress = from,
            TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
            Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalCancel()", ContractUID = uid, WithdrawalRequestHash = hash }),
        };

        private Transaction VoteTx(string cancellationUid, string voter, bool approve, long height, string? signedTxId = null) => new Transaction
        {
            Hash = $"votetx{++_txSeq}", Height = height, Timestamp = TimeUtil.GetTime(), FromAddress = voter, ToAddress = voter,
            TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_VOTE,
            Data = JsonConvert.SerializeObject(new { CancellationUID = cancellationUid, Approve = approve, SignedBtcTxId = signedTxId }),
        };

        private VBTCWithdrawalCancellation OpenCancellation(long height = CancelHeight, string uid = Vault, string hash = RequestHash)
        {
            Assert.True(VBTCCancellationVoting.ApplyCancel(CancelTx(height, uid, hash), out var created));
            return created!;
        }

        private static decimal LedgerBalance(string uid, string address) =>
            SmartContractStateTrei.GetSmartContractState(uid)!.SCStateTreiTokenizationTXes
                .Where(x => x.FromAddress == address || x.ToAddress == address).Sum(x => x.Amount);

        private static void StateDataApply(string method, Transaction tx) =>
            typeof(StateData).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { tx });

        // ── Threshold and decision (pure) ───────────────────────────────────────────────────────

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 3)]
        [InlineData(5, 4)]
        [InlineData(8, 6)]
        [InlineData(20, 15)]
        public void RequiredApprovals_SeventyFivePercent_NeverFewerThanThreeOrAll(int active, int required) =>
            Assert.Equal(required, VBTCCancellationVoting.RequiredApprovals(active));

        [Fact]
        public void RequiredApprovals_NoActiveVoters_Unreachable() =>
            Assert.Equal(int.MaxValue, VBTCCancellationVoting.RequiredApprovals(0));

        [Fact]
        public void Decide_Outcomes()
        {
            Assert.Equal(VBTCCancellationVoting.Outcome.Pending, VBTCCancellationVoting.Decide(2, 0, 4, false));
            Assert.Equal(VBTCCancellationVoting.Outcome.Approved, VBTCCancellationVoting.Decide(3, 0, 4, false));
            Assert.Equal(VBTCCancellationVoting.Outcome.Approved, VBTCCancellationVoting.Decide(3, 1, 4, false));
            // Two of four rejected: the two left cannot reach three approvals.
            Assert.Equal(VBTCCancellationVoting.Outcome.Rejected, VBTCCancellationVoting.Decide(0, 2, 4, false));
            Assert.Equal(VBTCCancellationVoting.Outcome.Pending, VBTCCancellationVoting.Decide(1, 1, 4, false));
        }

        [Fact]
        public void Decide_RejectNamingASignedTx_OutranksAnyApprovals() =>
            Assert.Equal(VBTCCancellationVoting.Outcome.Rejected, VBTCCancellationVoting.Decide(19, 1, 20, true));

        [Fact]
        public void IsBtcTxId_SixtyFourHexOnly()
        {
            Assert.True(VBTCCancellationVoting.IsBtcTxId(SignedTx));
            Assert.False(VBTCCancellationVoting.IsBtcTxId(SignedTx[..63]));
            Assert.False(VBTCCancellationVoting.IsBtcTxId(SignedTx[..63] + "z"));
            Assert.False(VBTCCancellationVoting.IsBtcTxId(null));
        }

        // ── Voter set: one rule for public and S3C contracts ────────────────────────────────────

        [Fact]
        public void ActiveVoters_Snapshot_LimitedToValidatorsActiveAtTheHeight()
        {
            var voters = VBTCCancellationVoting.ActiveVoters(Vault, CancelHeight);
            Assert.Equal(new[] { "xVal1", "xVal2", "xVal3", "xVal4" }, voters.OrderBy(x => x)); // xVal5 is dead; xPublicOnly is not in the snapshot
        }

        [Fact]
        public void ActiveVoters_AreReadAtTheGivenHeight()
        {
            // At height 11 only xVal1 (10) and xVal2 (11) had registered.
            Assert.Equal(new[] { "xVal1", "xVal2" }, VBTCCancellationVoting.ActiveVoters(Vault, 11).OrderBy(x => x));
        }

        [Fact]
        public void ActiveVoters_S3CContract_OnlyItsOwnS3CValidators()
        {
            Assert.Equal(S3CSnapshot, VBTCCancellationVoting.ActiveVoters(S3CVault, CancelHeight).OrderBy(x => x));
        }

        [Fact]
        public void ActiveVoters_NoSnapshot_ThePublicValidators_NeverS3C()
        {
            var voters = VBTCCancellationVoting.ActiveVoters(LegacyVault, CancelHeight);
            Assert.Equal(new[] { "xPublicOnly", "xVal1", "xVal2", "xVal3", "xVal4" }, voters.OrderBy(x => x, StringComparer.Ordinal));
        }

        // ── Cancel ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Cancel_CreatesARecordStampedWithItsHeight()
        {
            var c = OpenCancellation();
            Assert.StartsWith("CANCEL_", c.CancellationUID);
            Assert.Equal(CancelHeight, c.RequestBlockHeight);
            Assert.True(VBTCWithdrawalCancellation.IsVoteWindowOpen(c, CancelHeight + 1));
        }

        [Fact]
        public void Cancel_NotTheRequester_Refused() =>
            Assert.Contains("Only the original withdrawal requestor", VBTCCancellationVoting.ValidateCancel(CancelTx(CancelHeight, from: "xSomeoneElse"), CancelHeight));

        [Fact]
        public void Cancel_WhileOneIsBeingVotedOn_Refused()
        {
            OpenCancellation();
            Assert.Contains("already being voted on", VBTCCancellationVoting.ValidateCancel(CancelTx(CancelHeight + 10), CancelHeight + 10));
        }

        [Fact]
        public void Cancel_AfterThePreviousOneLapsed_Allowed()
        {
            OpenCancellation();
            var after = CancelHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS + 1;
            Assert.Null(VBTCCancellationVoting.ValidateCancel(CancelTx(after), after));
        }

        [Fact]
        public void Cancel_AfterThePreviousOneWasRejected_Allowed()
        {
            var c = OpenCancellation();
            Assert.True(VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", false, CancelHeight + 1, SignedTx)).Applied);
            Assert.Null(VBTCCancellationVoting.ValidateCancel(CancelTx(CancelHeight + 5), CancelHeight + 5));
        }

        [Fact]
        public void Cancel_RecordFiledBeforeTheRules_DoesNotBlockANewOne()
        {
            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation
            {
                CancellationUID = "CANCEL_legacycancel", SmartContractUID = Vault, OwnerAddress = Requester, WithdrawalRequestHash = RequestHash,
                BTCTxHash = "", FailureProof = "", RequestTime = TimeUtil.GetTime(), ValidatorVotes = new Dictionary<string, bool>(),
            });
            Assert.Null(VBTCCancellationVoting.ValidateCancel(CancelTx(CancelHeight), CancelHeight));
        }

        // ── Vote validation ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Vote_FromOutsideTheContractsActiveVoters_Refused()
        {
            var c = OpenCancellation();
            Assert.Contains("voter set", VBTCCancellationVoting.ValidateVote(VoteTx(c.CancellationUID, "xPublicOnly", true, CancelHeight + 1), CancelHeight + 1));
            Assert.Contains("voter set", VBTCCancellationVoting.ValidateVote(VoteTx(c.CancellationUID, "xVal5", true, CancelHeight + 1), CancelHeight + 1)); // dead
            Assert.Contains("voter set", VBTCCancellationVoting.ValidateVote(VoteTx(c.CancellationUID, "xS3C1", true, CancelHeight + 1), CancelHeight + 1));
        }

        [Fact]
        public void Vote_Twice_Refused()
        {
            var c = OpenCancellation();
            Assert.True(VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", true, CancelHeight + 1)).Applied);
            Assert.Contains("already voted", VBTCCancellationVoting.ValidateVote(VoteTx(c.CancellationUID, "xVal1", false, CancelHeight + 2), CancelHeight + 2));
        }

        [Fact]
        public void Vote_AfterTheWindowClosed_Refused()
        {
            var c = OpenCancellation();
            var after = CancelHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS + 1;
            Assert.Contains("vote window", VBTCCancellationVoting.ValidateVote(VoteTx(c.CancellationUID, "xVal1", true, after), after));
        }

        [Fact]
        public void Vote_ApproveNamingATx_OrAMalformedTxId_Refused()
        {
            var c = OpenCancellation();
            Assert.Contains("approve vote cannot name", VBTCCancellationVoting.ValidateVote(VoteTx(c.CancellationUID, "xVal1", true, CancelHeight + 1, SignedTx), CancelHeight + 1));
            Assert.Contains("64 hex", VBTCCancellationVoting.ValidateVote(VoteTx(c.CancellationUID, "xVal1", false, CancelHeight + 1, "not-a-txid"), CancelHeight + 1));
        }

        [Fact]
        public void Vote_OnANodeLocalRecord_Refused()
        {
            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation
            {
                CancellationUID = Guid.NewGuid().ToString(), SmartContractUID = Vault, OwnerAddress = Requester, WithdrawalRequestHash = RequestHash,
                BTCTxHash = "", FailureProof = "", RequestTime = TimeUtil.GetTime(), RequestBlockHeight = CancelHeight, ValidatorVotes = new Dictionary<string, bool>(),
            });
            var local = VBTCWithdrawalCancellation.GetAllCancellations()!.Single();
            Assert.Contains("not found", VBTCCancellationVoting.ValidateVote(VoteTx(local.CancellationUID, "xVal1", true, CancelHeight + 1), CancelHeight + 1));
        }

        // ── Deciding ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ThreeOfFourActive_Approve_TheDeadSnapshotValidatorDoesNotCount()
        {
            // Five in the snapshot, one dead. Under the full-snapshot rule 3/5 = 60% never reached 75%.
            var c = OpenCancellation();
            Assert.Equal(VBTCCancellationVoting.Outcome.Pending, VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", true, CancelHeight + 1)).Outcome);
            Assert.Equal(VBTCCancellationVoting.Outcome.Pending, VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal2", true, CancelHeight + 1)).Outcome);
            var deciding = VoteTx(c.CancellationUID, "xVal3", true, CancelHeight + 2);
            var result = VBTCCancellationVoting.ApplyVote(deciding);

            Assert.Equal(VBTCCancellationVoting.Outcome.Approved, result.Outcome);
            Assert.Equal(4, result.ActiveVoters);
            var decided = VBTCWithdrawalCancellation.GetCancellation(c.CancellationUID)!;
            Assert.True(decided.IsProcessed && decided.IsApproved);
            Assert.Equal(deciding.Hash, decided.DecidedByTxHash);
            Assert.Equal(CancelHeight + 2, decided.DecidedAtHeight);

            var row = VBTCWithdrawalRequest.GetByTransactionHash(RequestHash, Vault)!;
            Assert.Equal(VBTCWithdrawalStatus.Cancelled, row.Status);
            Assert.True(row.IsCompleted);
        }

        [Fact]
        public void RejectNamingTheSignedTx_RejectsAtOnce_AndTheWithdrawalStaysOpen()
        {
            var c = OpenCancellation();
            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", true, CancelHeight + 1));
            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal2", true, CancelHeight + 1));
            var result = VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal3", false, CancelHeight + 2, SignedTx.ToUpperInvariant()));

            Assert.Equal(VBTCCancellationVoting.Outcome.Rejected, result.Outcome);
            var decided = VBTCWithdrawalCancellation.GetCancellation(c.CancellationUID)!;
            Assert.True(decided.IsProcessed);
            Assert.False(decided.IsApproved);
            Assert.Equal(SignedTx, decided.SignedBtcTxId);
            Assert.Equal("xVal3", decided.SignedTxReportedBy);

            // The withdrawal is payable: it stays open for its requester to complete. A later approve changes nothing.
            Assert.False(VBTCWithdrawalRequest.GetByTransactionHash(RequestHash, Vault)!.IsCompleted);
            Assert.False(VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal4", true, CancelHeight + 3)).Applied);
        }

        [Fact]
        public void PlainRejections_RejectOnceApprovalIsOutOfReach()
        {
            var c = OpenCancellation();
            Assert.Equal(VBTCCancellationVoting.Outcome.Pending, VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", false, CancelHeight + 1)).Outcome);
            Assert.Equal(VBTCCancellationVoting.Outcome.Rejected, VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal2", false, CancelHeight + 1)).Outcome);
        }

        [Fact]
        public void S3CContract_NeedsAllThreeOfItsValidators()
        {
            SeedRequest(S3CVault, "s3creq");
            var c = OpenCancellation(uid: S3CVault, hash: "s3creq");
            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xS3C1", true, CancelHeight + 1));
            Assert.Equal(VBTCCancellationVoting.Outcome.Pending, VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xS3C2", true, CancelHeight + 1)).Outcome);
            Assert.Equal(VBTCCancellationVoting.Outcome.Approved, VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xS3C3", true, CancelHeight + 1)).Outcome);
        }

        [Fact]
        public void Approval_OfAWithdrawalThatCompletedMeanwhile_LeavesItCompleted_NoRefund()
        {
            var c = OpenCancellation();
            var row = VBTCWithdrawalRequest.GetByTransactionHash(RequestHash, Vault)!;
            row.Status = VBTCWithdrawalStatus.Completed;
            row.IsCompleted = true;
            VBTCWithdrawalRequest.Save(row, true);

            // (Votes cast before the completion was mined are already on their way.)
            VBTCWithdrawalCancellation.AddVote(c.CancellationUID, "xVal1", true);
            VBTCWithdrawalCancellation.AddVote(c.CancellationUID, "xVal2", true);
            var deciding = VoteTx(c.CancellationUID, "xVal3", true, CancelHeight + 2);
            Assert.Equal(VBTCCancellationVoting.Outcome.Approved, VBTCCancellationVoting.ApplyVote(deciding).Outcome);

            Assert.Equal(VBTCWithdrawalStatus.Completed, VBTCWithdrawalRequest.GetByTransactionHash(RequestHash, Vault)!.Status);
            Assert.Null(VBTCCancellationVoting.RefundDueFor(deciding));
        }

        // ── The refund (StateData) ──────────────────────────────────────────────────────────────

        [Fact]
        public void StateData_TheApprovingVote_CreditsTheEscrowBack_Once()
        {
            StateDataApply("CancelVBTCV2Withdrawal", CancelTx(CancelHeight));
            var c = VBTCWithdrawalCancellation.GetOpenCancellation(RequestHash, Vault, CancelHeight + 1)!;
            Assert.Equal(0.0006M, LedgerBalance(Vault, Requester));

            StateDataApply("VoteOnVBTCV2Cancellation", VoteTx(c.CancellationUID, "xVal1", true, CancelHeight + 1));
            StateDataApply("VoteOnVBTCV2Cancellation", VoteTx(c.CancellationUID, "xVal2", true, CancelHeight + 1));
            Assert.Equal(0.0006M, LedgerBalance(Vault, Requester)); // not decided yet

            StateDataApply("VoteOnVBTCV2Cancellation", VoteTx(c.CancellationUID, "xVal3", true, CancelHeight + 2));
            Assert.Equal(0.001M, LedgerBalance(Vault, Requester));

            StateDataApply("VoteOnVBTCV2Cancellation", VoteTx(c.CancellationUID, "xVal4", true, CancelHeight + 3)); // late vote
            Assert.Equal(0.001M, LedgerBalance(Vault, Requester));
        }

        [Fact]
        public void StateData_VoteAlreadyCountedByTheStoreRebuild_StillCreditsTheRefund()
        {
            // Blocks are stored before their state is applied, so the rebuild can count the deciding vote first.
            var c = OpenCancellation();
            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", true, CancelHeight + 1));
            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal2", true, CancelHeight + 1));
            var deciding = VoteTx(c.CancellationUID, "xVal3", true, CancelHeight + 2);
            Assert.Equal(VBTCCancellationVoting.Outcome.Approved, VBTCCancellationVoting.ApplyVote(deciding).Outcome);
            Assert.Equal(0.0006M, LedgerBalance(Vault, Requester)); // the rebuild writes no ledger rows

            StateDataApply("VoteOnVBTCV2Cancellation", deciding);

            Assert.Equal(0.001M, LedgerBalance(Vault, Requester));
        }

        [Fact]
        public void StateData_RejectedCancellation_RefundsNothing()
        {
            StateDataApply("CancelVBTCV2Withdrawal", CancelTx(CancelHeight));
            var c = VBTCWithdrawalCancellation.GetOpenCancellation(RequestHash, Vault, CancelHeight + 1)!;
            StateDataApply("VoteOnVBTCV2Cancellation", VoteTx(c.CancellationUID, "xVal1", false, CancelHeight + 1, SignedTx));

            Assert.Equal(0.0006M, LedgerBalance(Vault, Requester));
            Assert.False(VBTCWithdrawalRequest.GetByTransactionHash(RequestHash, Vault)!.IsCompleted);
        }

        [Fact]
        public void BeforeTheRulesHeight_TheLegacyRecordCarriesNoHeight()
        {
            StateDataApply("CancelVBTCV2Withdrawal", CancelTx(RulesHeight - 1));
            var legacy = VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(RequestHash, Vault)!;
            Assert.Equal(0, legacy.RequestBlockHeight);
            Assert.Null(VBTCWithdrawalCancellation.GetOpenCancellation(RequestHash, Vault, RulesHeight - 1));
        }

        // ── Signing is blocked for exactly the vote window ──────────────────────────────────────

        [Fact]
        public void PendingCancellation_FollowsTheVoteWindowInBlocks()
        {
            OpenCancellation();
            Globals.LastBlock = new Block { Height = CancelHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS };
            Assert.True(VBTCWithdrawalCancellation.HasPendingCancellation(RequestHash, TimeUtil.GetTime(), Vault));

            // A day of wall clock with the chain stalled must not reopen signing.
            Assert.True(VBTCWithdrawalCancellation.HasPendingCancellation(RequestHash, TimeUtil.GetTime() + 200_000, Vault));

            Globals.LastBlock = new Block { Height = CancelHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS + 1 };
            Assert.False(VBTCWithdrawalCancellation.HasPendingCancellation(RequestHash, TimeUtil.GetTime(), Vault));
        }

        [Fact]
        public void PendingCancellation_DecidedOne_NoLongerBlocksSigning()
        {
            var c = OpenCancellation();
            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", false, CancelHeight + 1, SignedTx));
            Assert.False(VBTCWithdrawalCancellation.HasPendingCancellation(RequestHash, TimeUtil.GetTime(), Vault));
        }

        // ── The validator's own decision ────────────────────────────────────────────────────────

        private const long EpochBeforeRequest = 100; // the seeded requests are mined at 150
        private static VBTCWithdrawalRequest OpenRow() => new VBTCWithdrawalRequest { Status = VBTCWithdrawalStatus.Requested, IsCompleted = false, RequestBlockHeight = 150 };
        private static VBTCCancellationVoteService.SigningEvidence None => new() { HasEvidence = false };

        [Fact]
        public void Validator_NeverSigned_Approves() =>
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Approve, VBTCCancellationVoteService.Decide(OpenRow(), false, None, EpochBeforeRequest).Decision);

        [Fact]
        public void Validator_SignedATxThatCanStillPay_RejectsAndNamesIt()
        {
            var (decision, _, txid) = VBTCCancellationVoteService.Decide(OpenRow(), false, new() { HasEvidence = true, BtcTxId = SignedTx }, EpochBeforeRequest);
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.RejectSignedTx, decision);
            Assert.Equal(SignedTx, txid);
        }

        [Fact]
        public void Validator_SignedATxProvenDead_Approves() =>
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Approve,
                VBTCCancellationVoteService.Decide(OpenRow(), false, new() { HasEvidence = true, BtcTxId = SignedTx, ProvenDead = true }, EpochBeforeRequest).Decision);

        [Fact]
        public void Validator_CannotTellWhetherItsTxIsDead_Waits() =>
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Wait,
                VBTCCancellationVoteService.Decide(OpenRow(), false, new() { HasEvidence = true, BtcTxId = SignedTx, Inconclusive = true }, EpochBeforeRequest).Decision);

        [Fact]
        public void Validator_CeremonyRunning_Waits() =>
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Wait, VBTCCancellationVoteService.Decide(OpenRow(), true, None, EpochBeforeRequest).Decision);

        [Fact]
        public void Validator_EvidenceWithoutATxId_RejectsWithoutNamingOne()
        {
            var (decision, _, txid) = VBTCCancellationVoteService.Decide(OpenRow(), false, new() { HasEvidence = true }, EpochBeforeRequest);
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Reject, decision);
            Assert.Null(txid);
        }

        [Fact]
        public void Validator_CompletedWithdrawal_Rejects_MissingRow_Waits()
        {
            var done = new VBTCWithdrawalRequest { Status = VBTCWithdrawalStatus.Completed, IsCompleted = true };
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Reject, VBTCCancellationVoteService.Decide(done, false, None, EpochBeforeRequest).Decision);
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Wait, VBTCCancellationVoteService.Decide(null, false, None, EpochBeforeRequest).Decision);
        }

        // ── Where a validator's signing records begin ───────────────────────────────────────────

        [Fact]
        public void Validator_WithdrawalRequestedBeforeItsRecordsBegin_Abstains()
        {
            // A validator restored onto a new machine has its key shares but not its records: holding no record of a
            // withdrawal requested at 150 says nothing when the records begin at 151.
            var (decision, reason, _) = VBTCCancellationVoteService.Decide(OpenRow(), false, None, 151);
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Abstain, decision);
            Assert.Contains("before this validator's signing records begin", reason);
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Approve, VBTCCancellationVoteService.Decide(OpenRow(), false, None, 150).Decision);
        }

        [Fact]
        public void Validator_RecordsEpochNotEstablished_Waits() =>
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.Wait, VBTCCancellationVoteService.Decide(OpenRow(), false, None, null).Decision);

        [Fact]
        public void Validator_ARecordOfTheWithdrawalItself_IsNotLimitedByTheEpoch() =>
            Assert.Equal(VBTCCancellationVoteService.VoteDecision.RejectSignedTx,
                VBTCCancellationVoteService.Decide(OpenRow(), false, new() { HasEvidence = true, BtcTxId = SignedTx }, 9_999).Decision);

        // ── A node that is behind (resync) acts on nothing ──────────────────────────────────────

        private static void AtNetworkHeight(long height = CancelHeight + 1)
        {
            Globals.IsChainSynced = true;
            Globals.IsResyncing = false;
            Globals.StopAllTimers = false;
            Globals.LastBlock = new Block { Height = height, Timestamp = TimeUtil.GetTime() };
        }

        [Fact]
        public void NodeIsAtNetworkHeight_NeedsSyncAndARecentTip()
        {
            AtNetworkHeight();
            Assert.True(VBTCCancellationVoteService.NodeIsAtNetworkHeight().Ready);

            // Syncing from behind: the local tip is a block mined long ago.
            Globals.LastBlock = new Block { Height = CancelHeight + 1, Timestamp = TimeUtil.GetTime() - VBTCCancellationVoteService.MAX_TIP_AGE_SECONDS - 1 };
            Assert.False(VBTCCancellationVoteService.NodeIsAtNetworkHeight().Ready);

            AtNetworkHeight();
            Globals.IsChainSynced = false;
            Assert.False(VBTCCancellationVoteService.NodeIsAtNetworkHeight().Ready);

            AtNetworkHeight();
            Globals.IsResyncing = true;
            Assert.False(VBTCCancellationVoteService.NodeIsAtNetworkHeight().Ready);
        }

        [Fact]
        public async Task NodeBehind_SeesAnOpenCancellationAtItsLowTip_ButCastsNoVote()
        {
            var c = OpenCancellation();
            Globals.ValidatorAddress = "xVal1";
            FrostSignedWithdrawalEvidence.SetEpoch(EpochBeforeRequest, "test");

            // Replaying history: the cancellation of block 200 looks open at local tip 201, a day after the fact.
            Globals.IsChainSynced = true;
            Globals.LastBlock = new Block { Height = CancelHeight + 1, Timestamp = TimeUtil.GetTime() - 86_400 };
            Assert.Single(VBTCCancellationVoteService.GetCancellationsAwaitingMyVote());

            Assert.Equal(0, await VBTCCancellationVoteService.RunOnce());
            Assert.False(VBTCCancellationVoteService.HasPendingVote("xVal1", c.CancellationUID));
        }

        [Fact]
        public void RecordsEpoch_IsNotStampedWhileTheNodeIsBehind()
        {
            Globals.IsChainSynced = false;
            Assert.Null(VBTCCancellationVoteService.EnsureEvidenceEpoch());
            Assert.Null(FrostSignedWithdrawalEvidence.GetEpoch());
        }

        [Fact]
        public void RecordsEpoch_NoRecords_IsTheTipWhenFirstAtHeight_AndIsStampedOnce()
        {
            AtNetworkHeight(5_000);
            Assert.Equal(5_000, VBTCCancellationVoteService.EnsureEvidenceEpoch()!.EpochHeight);

            AtNetworkHeight(6_000);
            Assert.Equal(5_000, VBTCCancellationVoteService.EnsureEvidenceEpoch()!.EpochHeight);
        }

        [Fact]
        public void RecordsEpoch_WithRecords_IsTheBlockOfTheOldestOne()
        {
            var t0 = TimeUtil.GetTime() - 10_000;
            for (long h = 0; h <= 310; h++)
                if (BlockchainData.GetBlockByHeight(h) == null)
                    BlockchainData.GetBlocks().InsertSafe(new Block { Height = h, Hash = $"t{h}", Timestamp = t0 + h * 10, Transactions = new List<Transaction>() });
                else
                {
                    var existing = BlockchainData.GetBlockByHeight(h)!;
                    existing.Timestamp = t0 + h * 10;
                    BlockchainData.GetBlocks().UpdateSafe(existing);
                }
            Assert.Equal(305, VBTCCancellationVoteService.HeightAtOrBefore(t0 + 3_055, 310));

            FrostSignedWithdrawalEvidence.Record(Vault, "some-older-withdrawal", SignedTx, new[] { "txid1:0" });
            AtNetworkHeight(310);
            var epoch = VBTCCancellationVoteService.EnsureEvidenceEpoch()!;
            Assert.Equal(310, epoch.EpochHeight); // the record was written just now: at or after every block's time
            Assert.Contains("oldest signing record", epoch.Source);
        }

        // ── End to end: a validator's loop builds a vote the network accepts ────────────────────

        [Fact]
        public async Task RunOnce_BuildsSignsAndBroadcastsAnApproveVote_ThatConsensusAccepts()
        {
            var validator = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(validator);
            StateData.GetAccountStateTrei().Insert(new AccountStateTrei { Key = validator.Address, Balance = 10_000M, Nonce = 0 });
            Register(validator.Address, isS3C: false, 30);

            const string vault = "c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4c4:1790000004";
            SeedVault(vault, new[] { validator.Address, "xVal1", "xVal2" }, isS3C: false);
            SeedRequest(vault, "e2ereq");
            var c = OpenCancellation(uid: vault, hash: "e2ereq");

            Globals.ValidatorAddress = validator.Address;
            FrostSignedWithdrawalEvidence.SetEpoch(EpochBeforeRequest, "test");
            AtNetworkHeight();

            Assert.Equal(1, await VBTCCancellationVoteService.RunOnce());

            var vote = TransactionData.GetPool().FindOne(x => x.FromAddress == validator.Address && x.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_VOTE);
            Assert.NotNull(vote);
            var data = JObject.Parse(vote.Data);
            Assert.Equal(c.CancellationUID, (string?)data["CancellationUID"]);
            Assert.True((bool)data["Approve"]!);
            Assert.True(vote.Fee > 0M);

            // Not cast twice while the first waits in the mempool.
            Assert.Equal(0, await VBTCCancellationVoteService.RunOnce());

            // Mined: counted, and recorded as this validator's vote.
            vote.Height = CancelHeight + 2;
            Assert.True(VBTCCancellationVoting.ApplyVote(vote).Applied);
            var after = VBTCWithdrawalCancellation.GetCancellation(c.CancellationUID)!;
            Assert.True(VBTCWithdrawalCancellation.WasVoteCounted(after, validator.Address, vote.Hash));
            Assert.False(VBTCWithdrawalCancellation.WasVoteCounted(after, validator.Address, "some-other-vote"));
        }

        [Fact]
        public async Task Validator_EvidenceComesFromItsOwnSigningRecords()
        {
            Assert.False((await VBTCCancellationVoteService.GetSigningEvidenceAsync(Vault, RequestHash, null)).HasEvidence);

            FrostWithdrawalSigningTracker.RecordSigningStarted(Vault, RequestHash, "s0", 0, "aa", null);
            Assert.True(FrostWithdrawalSigningTracker.HasCeremonyInProgress(Vault, RequestHash));
            Assert.False(FrostWithdrawalSigningTracker.HasCeremonyInProgress(Vault, "other-request"));

            // A row that carries an announced build counts, as it does for the approve guard.
            var row = OpenRow();
            row.PinnedUnsignedTxHex = "0200";
            Assert.True((await VBTCCancellationVoteService.GetSigningEvidenceAsync(Vault, "row-only", row)).HasEvidence);
        }

        [Fact]
        public void VoteData_ApproveCarriesNoTxId_RejectCarriesItLowercased()
        {
            var c = OpenCancellation();
            var approve = JObject.Parse(VBTCCancellationVoteService.BuildVoteData(c, true, SignedTx, "r"));
            Assert.True((bool)approve["Approve"]!);
            Assert.Null(approve["SignedBtcTxId"]);

            var reject = JObject.Parse(VBTCCancellationVoteService.BuildVoteData(c, false, SignedTx.ToUpperInvariant(), "r"));
            Assert.False((bool)reject["Approve"]!);
            Assert.Equal(SignedTx, (string?)reject["SignedBtcTxId"]);
            Assert.Equal(c.CancellationUID, (string?)reject["CancellationUID"]);
        }

        [Fact]
        public void Validator_SeesOnlyTheCancellationsItMayVoteOn()
        {
            var c = OpenCancellation();
            SeedRequest(S3CVault, "s3creq2");
            OpenCancellation(uid: S3CVault, hash: "s3creq2");
            Globals.LastBlock = new Block { Height = CancelHeight + 1 };

            Globals.ValidatorAddress = "xVal2";
            Assert.Equal(c.CancellationUID, Assert.Single(VBTCCancellationVoteService.GetCancellationsAwaitingMyVote()).CancellationUID);

            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal2", true, CancelHeight + 1));
            Assert.Empty(VBTCCancellationVoteService.GetCancellationsAwaitingMyVote()); // voted

            Globals.ValidatorAddress = "xVal5"; // in the snapshot, not active
            Assert.Empty(VBTCCancellationVoteService.GetCancellationsAwaitingMyVote());
        }

        // ── API ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GetCancellationStatus_IsAReadAllowedOnALockedWallet_TheSigningRoutesAreNot()
        {
            Assert.True(VerifiedXCore.Controllers.LockedWalletPolicy.IsAllowedWhileLocked("VBTC", "GetCancellationStatus"));
            Assert.False(VerifiedXCore.Controllers.LockedWalletPolicy.IsAllowedWhileLocked("VBTC", "VoteOnCancellation"));
            Assert.False(VerifiedXCore.Controllers.LockedWalletPolicy.IsAllowedWhileLocked("VBTC", "CancelWithdrawal"));
        }

        [Fact]
        public async Task VoteOnCancellation_ForAnotherValidatorsAddress_Refused_RecordUntouched()
        {
            var c = OpenCancellation();
            Globals.ValidatorAddress = "xVal1";

            var r = JObject.Parse(await new VBTCController().VoteOnCancellation(new VBTCCancellationVotePayload
            {
                CancellationUID = c.CancellationUID, ValidatorAddress = "xVal2", Approve = true,
            }));

            Assert.False((bool)r["Success"]!);
            var after = VBTCWithdrawalCancellation.GetCancellation(c.CancellationUID)!;
            Assert.Equal(0, after.ApproveCount);
            Assert.False(after.IsProcessed);
        }

        [Fact]
        public async Task CancellationStatus_ShowsVotesAndWhatIsNeeded()
        {
            var c = OpenCancellation();
            VBTCCancellationVoting.ApplyVote(VoteTx(c.CancellationUID, "xVal1", true, CancelHeight + 1));

            var r = JObject.Parse(await new VBTCController().GetCancellationStatus(Vault, RequestHash));

            Assert.True((bool)r["Success"]!);
            Assert.Equal(4, (int)r["ActiveVoters"]!);
            Assert.Equal(3, (int)r["RequiredApprovals"]!);
            Assert.False((bool)r["CanCancelNow"]!);
            var entry = Assert.Single((JArray)r["Cancellations"]!);
            Assert.Equal("Voting", (string?)entry["Status"]);
            Assert.Equal(1, (int)entry["ApproveCount"]!);
            Assert.Equal(CancelHeight + VBTCWithdrawalCancellation.VOTE_WINDOW_BLOCKS, (long)entry["VotesCloseAtHeight"]!);
        }
    }
}
