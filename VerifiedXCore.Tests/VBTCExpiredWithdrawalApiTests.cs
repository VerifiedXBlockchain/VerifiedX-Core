using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    /// Tester follow-up (MTI#2.4–2.6): a withdrawal request that can never be paid (0.000001 BTC at 30 sat/vB), mined and
    /// escrowed, after its 360-block window ran out:
    ///  - Complete failed on every retry with a fee error and no way forward → it now says the request is unpayable and
    ///    to cancel it, before any ceremony (and the web-wallet prepare step says so before anything is signed);
    ///  - the escrowed amount was invisible (PendingWithdrawals 0, balance already reduced) → the balance endpoints list
    ///    escrow in open requests and how much of it is in expired ones;
    ///  - GetContractDetails returned the local record's withdrawal fields, never cleared on expiry → reported from the
    ///    withdrawal table with the expiry applied.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCExpiredWithdrawalApiTests : IDisposable
    {
        private const string Vault = "d3c501f9351f4565a930fb6407fce731:1787335542";
        private const string Deposit = "bc1pexpiredwithdrawalvaultdeposit00000000000000000000000000";
        private const string Owner = "xMjrfrzkOwner00000000000000000000001";
        private const string Holder = "xSe1KMcNrcfoQwHEQ7YbBKDCRy3YFcgany";
        private const string RequestHash = "925879d7569f00684bb60724f955d0a5cad2fc1b7ea7aa8c670eb45fc605e8d0";
        private const long RequestHeight = 1_017_408;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorEscrowHeight;
        private readonly VBTCController _api = new VBTCController();

        public VBTCExpiredWithdrawalApiTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcexpired_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.WithdrawalEscrowHeight = 1;
            Globals.LastBlock = new Block { Height = 1_017_812, Timestamp = TimeUtil.GetTime() }; // the tester's second check

            // The holder's ledger: received 0.00009, then the request's escrow debit of 0.000001.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault, ContractData = VbtcTestContracts.VaultContractData(Vault, Owner, Deposit),
                MinterAddress = Owner, OwnerAddress = Owner,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = Owner, ToAddress = Holder, Amount = 0.00009M },
                    new SmartContractStateTreiTokenizationTX { FromAddress = Holder, ToAddress = "-", Amount = -0.000001M },
                },
            });
            Assert.True(VBTCWithdrawalRequest.Save(Request(RequestHash, 0.000001M, 30)));
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.WithdrawalEscrowHeight = _priorEscrowHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static VBTCWithdrawalRequest Request(string hash, decimal amount, int feeRate, long height = RequestHeight) => new VBTCWithdrawalRequest
        {
            RequestorAddress = Holder, OriginalUniqueId = hash, SmartContractUID = Vault, Amount = amount,
            BTCDestination = "tb1qrdch62tavgpq7jc6zx096mltyfuxkl4rptxuqx", FeeRate = feeRate, TransactionHash = hash,
            Status = VBTCWithdrawalStatus.Requested, IsCompleted = false, RequestBlockHeight = height, Timestamp = TimeUtil.GetTime() - 4000,
        };

        // ── 1. Unpayable requests say so ────────────────────────────────────────────────────────

        [Fact]
        public void UnpayableMessage_ForTheReportedRequest_PointsAtCancellation()
        {
            var message = VBTCService.GetUnpayableWithdrawalMessage(VBTCWithdrawalRequest.GetByTransactionHash(RequestHash, Vault));

            Assert.NotNull(message);
            Assert.StartsWith(VBTCService.UnpayableWithdrawalMarker, message);
            Assert.Contains("Cancel it", message);
            Assert.Contains("0.000001 vBTC held in escrow", message);
            Assert.Contains(Holder, message);
        }

        [Fact]
        public void UnpayableMessage_PayableOrCompletedRequest_Null()
        {
            Assert.Null(VBTCService.GetUnpayableWithdrawalMessage(Request("payable", 0.001M, 30)));
            var done = Request("done", 0.000001M, 30);
            done.IsCompleted = true;
            Assert.Null(VBTCService.GetUnpayableWithdrawalMessage(done));
        }

        [Fact]
        public void UnpayableMessage_BeforeEscrow_DoesNotPromiseARefund()
        {
            Globals.WithdrawalEscrowHeight = 999_999_999_999L; // request burned at COMPLETE, nothing held
            var message = VBTCService.GetUnpayableWithdrawalMessage(Request("legacyreq", 0.000001M, 30));
            Assert.NotNull(message);
            Assert.DoesNotContain("escrow", message);
        }

        [Fact]
        public async Task CompleteWithdrawal_Unpayable_AnswersBeforeAnyCeremony()
        {
            // No validator registry in this test: without the early answer this fails on "No active validators".
            var (success, _, _, error, ceremony) = await VBTCService.CompleteWithdrawal(Vault, RequestHash);

            Assert.False(success);
            Assert.StartsWith(VBTCService.UnpayableWithdrawalMarker, error);
            Assert.Null(ceremony);
        }

        [Fact]
        public async Task PrepareCompleteWithdrawalRaw_Unpayable_RefusedBeforeSigning()
        {
            var r = JObject.Parse(await _api.PrepareCompleteWithdrawalRaw(new PrepareCompleteWithdrawalRawPayload
            {
                OwnerAddress = Holder, SmartContractUID = Vault, WithdrawalRequestHash = RequestHash,
            }));

            Assert.False((bool)r["Success"]!);
            Assert.True((bool)r["Unpayable"]!);
        }

        // ── 2. Escrow is visible ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Balance_ShowsTheExpiredEscrow()
        {
            var r = JObject.Parse(await _api.GetVBTCBalance(Holder, Vault));

            Assert.True((bool)r["Success"]!);
            Assert.Equal(0.000089M, (decimal)r["Balance"]!);          // already net of the escrow debit
            Assert.Equal(0M, (decimal)r["PendingWithdrawals"]!);      // (why the tester saw nothing)
            Assert.Equal(0.000001M, (decimal)r["EscrowedWithdrawalAmount"]!);
            Assert.Equal(0.000001M, (decimal)r["ExpiredEscrowAmount"]!);

            var entry = Assert.Single((JArray)r["EscrowedWithdrawals"]!);
            Assert.Equal(RequestHash, (string?)entry["RequestHash"]);
            Assert.True((bool)entry["Expired"]!);
            Assert.True((bool)entry["Unpayable"]!);
            Assert.False((bool)entry["CancellationPending"]!);
            Assert.Equal(RequestHeight + VBTCWithdrawalRequest.EXPIRY_BLOCKS, (long)entry["ExpiresAtHeight"]!);
        }

        [Fact]
        public async Task Balance_OpenRequestIsEscrowedButNotExpired()
        {
            Assert.True(VBTCWithdrawalRequest.Save(Request("fresh", 0.00005M, 10, height: Globals.LastBlock.Height - 5)));

            var r = JObject.Parse(await _api.GetVBTCBalance(Holder, Vault));

            Assert.Equal(0.000051M, (decimal)r["EscrowedWithdrawalAmount"]!);
            Assert.Equal(0.000001M, (decimal)r["ExpiredEscrowAmount"]!);
        }

        [Fact]
        public async Task AllBalances_ListAContractWhoseWholeBalanceIsInEscrow()
        {
            const string Other = "xOnlyEscrowHolder00000000000000000001";
            const string OtherVault = "e4e4e4e4e4e4e4e4e4e4e4e4e4e4e4e4:1787335543";
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = OtherVault, ContractData = VbtcTestContracts.VaultContractData(OtherVault, Owner, Deposit),
                MinterAddress = Owner, OwnerAddress = Owner,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = Owner, ToAddress = Other, Amount = 0.00002M },
                    new SmartContractStateTreiTokenizationTX { FromAddress = Other, ToAddress = "-", Amount = -0.00002M },
                },
            });
            var req = Request("all-in", 0.00002M, 10);
            req.RequestorAddress = Other;
            req.SmartContractUID = OtherVault;
            Assert.True(VBTCWithdrawalRequest.Save(req));

            var r = JObject.Parse(await _api.GetAllVBTCBalances(Other));

            var c = Assert.Single((JArray)r["Contracts"]!);
            Assert.Equal(0M, (decimal)c["Balance"]!);
            Assert.Equal(0.00002M, (decimal)c["EscrowedWithdrawalAmount"]!);
        }

        // ── Cancelling: on chain, never a node-local record ────────────────────────────────────

        private static VBTCWithdrawalCancellation Cancellation(string uid) => new VBTCWithdrawalCancellation
        {
            CancellationUID = uid, SmartContractUID = Vault, OwnerAddress = Holder, WithdrawalRequestHash = RequestHash,
            BTCTxHash = "", FailureProof = "", RequestTime = TimeUtil.GetTime(), ValidatorVotes = new Dictionary<string, bool>(),
        };

        [Fact]
        public void LocalOnlyCancellationRecord_IgnoredByTheConsensusLookups()
        {
            // What older builds' /CancelWithdrawal left behind: a GUID-keyed record on this node only.
            VBTCWithdrawalCancellation.SaveCancellation(Cancellation(Guid.NewGuid().ToString()));

            Assert.Null(VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(RequestHash, Vault));
            Assert.False(VBTCWithdrawalCancellation.HasPendingCancellation(RequestHash, TimeUtil.GetTime(), Vault));

            // A record made from a mined cancel still counts.
            VBTCWithdrawalCancellation.SaveCancellation(Cancellation("CANCEL_minedcanceltx"));
            Assert.NotNull(VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash(RequestHash, Vault));
            Assert.True(VBTCWithdrawalCancellation.HasPendingCancellation(RequestHash, TimeUtil.GetTime(), Vault));
        }

        [Fact]
        public async Task CancelWithdrawal_AddressWithoutALocalKey_PointsAtTheRawBuilder_SavesNothing()
        {
            var r = JObject.Parse(await _api.CancelWithdrawal(new VBTCCancellationPayload
            {
                SmartContractUID = Vault, OwnerAddress = Holder, WithdrawalRequestHash = RequestHash,
            }));

            Assert.False((bool)r["Success"]!);
            Assert.Contains("GetRawCancelWithdrawalTxData", (string?)r["Message"]);
            Assert.Null(VBTCWithdrawalCancellation.GetAllCancellations());
        }

        [Fact]
        public async Task CancelWithdrawal_LocalRequester_BroadcastsTheOnChainCancel()
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(account);
            StateData.GetAccountStateTrei().Insert(new AccountStateTrei { Key = account.Address, Balance = 10M, Nonce = 0 });
            var req = Request("localcancelreq", 0.000001M, 30);
            req.RequestorAddress = account.Address;
            Assert.True(VBTCWithdrawalRequest.Save(req));

            var r = JObject.Parse(await _api.CancelWithdrawal(new VBTCCancellationPayload
            {
                SmartContractUID = Vault, OwnerAddress = account.Address, WithdrawalRequestHash = "localcancelreq",
            }));

            Assert.True((bool)r["Success"]!, (string?)r["Message"]);
            var cancelTxHash = (string?)r["CancelTxHash"];
            Assert.False(string.IsNullOrEmpty(cancelTxHash));
            Assert.Equal($"CANCEL_{cancelTxHash}", (string?)r["CancellationUID"]);

            var pooled = TransactionData.GetPool().FindOne(x => x.Hash == cancelTxHash);
            Assert.NotNull(pooled);
            Assert.Equal(TransactionType.VBTC_V2_WITHDRAWAL_CANCEL, pooled.TransactionType);
            Assert.Null(VBTCWithdrawalCancellation.GetAllCancellations()); // the record appears only when it is mined
        }

        [Fact]
        public async Task CancelWithdrawal_SomeoneElsesRequest_Refused()
        {
            var account = AccountData.CreateNewAccount(skipSave: true);
            AccountData.GetAccounts().Insert(account);

            var r = JObject.Parse(await _api.CancelWithdrawal(new VBTCCancellationPayload
            {
                SmartContractUID = Vault, OwnerAddress = account.Address, WithdrawalRequestHash = RequestHash, // Holder's request
            }));

            Assert.False((bool)r["Success"]!);
            Assert.Contains("Only the original withdrawal requestor", (string?)r["Message"]);
        }

        // ── 4. Contract details stop reporting a dead request ──────────────────────────────────

        [Fact]
        public async Task ContractDetails_LocalRecordsStaleWithdrawal_ReportedExpired()
        {
            // The local record exactly as the tester's GUI read it: Requested, with the dead request filled in.
            VBTCContractV2.SaveContract(new VBTCContractV2
            {
                SmartContractUID = Vault, OwnerAddress = Owner, DepositAddress = Deposit,
                ValidatorAddressesSnapshot = new List<string>(), FrostGroupPublicKey = "", DKGProof = "",
                WithdrawalHistory = new List<VBTCWithdrawalHistory>(),
                WithdrawalStatus = VBTCWithdrawalStatus.Requested,
                ActiveWithdrawalAmount = 0.000001M, ActiveWithdrawalBTCDestination = "tb1qrdch62tavgpq7jc6zx096mltyfuxkl4rptxuqx",
                ActiveWithdrawalRequestHash = RequestHash, ActiveWithdrawalFeeRate = 30, WithdrawalRequestBlock = null,
            });

            var r = JObject.Parse(await _api.GetContractDetails(Vault));
            var c = r["Contract"]!;

            Assert.True((bool)r["Success"]!);
            Assert.Equal((int)VBTCWithdrawalStatus.None, (int)c["WithdrawalStatus"]!);
            Assert.Equal(JTokenType.Null, c["ActiveWithdrawalAmount"]!.Type);
            Assert.Equal(JTokenType.Null, c["ActiveWithdrawalRequestHash"]!.Type);

            // Reporting only: the stored record is untouched.
            Assert.Equal(VBTCWithdrawalStatus.Requested, VBTCContractV2.GetContract(Vault)!.WithdrawalStatus);
        }

        [Fact]
        public async Task ContractDetails_OpenRequest_StillReported()
        {
            VBTCContractV2.SaveContract(new VBTCContractV2
            {
                SmartContractUID = Vault, OwnerAddress = Owner, DepositAddress = Deposit,
                ValidatorAddressesSnapshot = new List<string>(), FrostGroupPublicKey = "", DKGProof = "",
                WithdrawalHistory = new List<VBTCWithdrawalHistory>(),
            });
            Assert.True(VBTCWithdrawalRequest.Save(Request("open-now", 0.001M, 10, height: Globals.LastBlock.Height - 10)));

            var c = JObject.Parse(await _api.GetContractDetails(Vault))["Contract"]!;

            Assert.Equal((int)VBTCWithdrawalStatus.Requested, (int)c["WithdrawalStatus"]!);
            Assert.Equal("open-now", (string?)c["ActiveWithdrawalRequestHash"]);
            Assert.Equal(0.001M, (decimal)c["ActiveWithdrawalAmount"]!);
        }
    }
}
