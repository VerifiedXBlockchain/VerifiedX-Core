using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 6 (Globals.WithdrawalUniqueIdRulesHeight): a withdrawal REQUEST reusing a
    /// (requester, UniqueId, contract) key under which a request was already mined is refused, and the row store never
    /// overwrites a mined row with a later request's (it used to reset the old row's status and keep its Amount, so a
    /// dust request reopened a completed withdrawal at the old amount for cancel-refund or payout).
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss06_WithdrawalUniqueIdTests : IDisposable
    {
        private const long Gate = 1000;
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _holder;
        private readonly string _vault;

        public FundLoss06_WithdrawalUniqueIdTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl06_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.WithdrawalUniqueIdRulesHeight;
            Globals.WithdrawalUniqueIdRulesHeight = Gate;
            DbContext.Initialize();
            _holder = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 1000M, Nonce = 0 });
            _vault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = _vault, ContractData = VbtcTestContracts.VaultContractData(_vault, "xMinter", BtcDest),
                MinterAddress = "xMinter", OwnerAddress = "xMinter", Nonce = 0,
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.WithdrawalUniqueIdRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private VBTCWithdrawalRequest MinedRow(string uniqueId, string txHash, decimal amount, long height, VBTCWithdrawalStatus status) => new VBTCWithdrawalRequest
        {
            RequestorAddress = _holder.Address, OriginalUniqueId = uniqueId, SmartContractUID = _vault, TransactionHash = txHash,
            Amount = amount, BTCDestination = BtcDest, FeeRate = 10, Timestamp = TimeUtil.GetTime(), RequestBlockHeight = height,
            Status = status, IsCompleted = status == VBTCWithdrawalStatus.Completed || status == VBTCWithdrawalStatus.Cancelled,
        };

        private Transaction Request(string uniqueId, decimal amount)
        {
            var tx = new Transaction
            {
                FromAddress = _holder.Address, ToAddress = _holder.Address, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCWithdrawalRequest()", ContractUID = _vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = uniqueId }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _holder.Key, _holder.Pub);
            return tx;
        }

        // ── Row store ─────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void LaterRequestWithTheSameKey_IsItsOwnRow_BelowTheGateAndAtIt()
        {
            // The store's rule does not depend on the height (validation's refusal does): a completed 1.0 withdrawal
            // used to be reopened, below the gate, by a 0.0001 request under the same key - the old row's status reset
            // with its old amount kept, refundable and payable, and the Completed row (its owner add-back) gone. Now the
            // mined row stays as it is and the later request gets its own row, below the gate as at it, so a node that
            // rebuilt its store from the chain holds the same rows as one that applied the blocks live.
            foreach (var (height, tag) in new[] { (Gate - 1, "below"), (Gate, "at") })
            {
                var uniqueId = Guid.NewGuid().ToString("N");
                Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "H1" + tag, 1.0M, Gate - 10, VBTCWithdrawalStatus.Completed)));
                Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "H2" + tag, 0.0001M, height, VBTCWithdrawalStatus.Requested), update: true));
                var old = VBTCWithdrawalRequest.GetByTransactionHash("H1" + tag, _vault);
                var later = VBTCWithdrawalRequest.GetByTransactionHash("H2" + tag, _vault);
                Assert.NotNull(old);
                Assert.Equal(VBTCWithdrawalStatus.Completed, old!.Status);
                Assert.Equal(1.0M, old.Amount);
                Assert.NotNull(later);
                Assert.Equal(0.0001M, later!.Amount);
                Assert.Equal(VBTCWithdrawalStatus.Requested, later.Status);
            }
        }

        [Fact]
        public void LocalPreRegistrationRow_IsStillUpgradedByItsMinedRecord_AtGate()
        {
            var uniqueId = Guid.NewGuid().ToString("N");
            var local = MinedRow(uniqueId, "", 0.5M, 0, VBTCWithdrawalStatus.Requested); // the API node's row: no hash yet
            Assert.True(VBTCWithdrawalRequest.Save(local));
            Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "H5", 0.5M, Gate, VBTCWithdrawalStatus.Requested), update: true));
            var rows = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.Query().Where(x => x.OriginalUniqueId == uniqueId).ToList();
            var row = Assert.Single(rows);
            Assert.Equal("H5", row.TransactionHash);
            Assert.Equal(Gate, row.RequestBlockHeight);
        }

        /// <summary>This node's note of a request (no hash yet) is refused under a key a mined request holds: a mined request is inserted on its own, a note is not.</summary>
        [Fact]
        public void LocalPreRegistration_UnderAKeyAMinedRequestHolds_IsRefused()
        {
            var uniqueId = Guid.NewGuid().ToString("N");
            Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "H9", 1.0M, Gate - 10, VBTCWithdrawalStatus.Completed)));
            Assert.False(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "", 0.5M, 0, VBTCWithdrawalStatus.Requested)));
            var row = Assert.Single(VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()!.Query().Where(x => x.OriginalUniqueId == uniqueId).ToList());
            Assert.Equal("H9", row.TransactionHash);
        }

        [Fact]
        public void ReuseError_MinedKeyOnly()
        {
            var uniqueId = Guid.NewGuid().ToString("N");
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, uniqueId, _vault, "HNEW"));
            Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "", 0.5M, 0, VBTCWithdrawalStatus.Requested)));
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, uniqueId, _vault, "HNEW")); // pre-registration only
            Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "H6", 0.5M, Gate, VBTCWithdrawalStatus.Completed), update: true));
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, uniqueId, _vault, "H6"));    // the same request re-validated
            Assert.NotNull(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, uniqueId, _vault, "HNEW")); // a different request
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(NewKey().Address, uniqueId, _vault, "HNEW"));   // another requester's key
            Assert.Null(VBTCWithdrawalRequest.UniqueIdReuseError(_holder.Address, uniqueId, "other:1", "HNEW")); // another contract
        }

        // ── Consensus ─────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task RequestReusingAMinedUniqueId_IsRefusedAtGate_NotBelow()
        {
            var uniqueId = Guid.NewGuid().ToString("N");
            Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "H7", 1.0M, Gate - 10, VBTCWithdrawalStatus.Completed)));

            Globals.LastBlock = new Block { Height = Gate - 2 };
            var (_, msgBelow) = await TransactionValidatorService.VerifyTX(Request(uniqueId, 0.0001M));
            Assert.DoesNotContain("cannot be reused", msgBelow);

            Globals.LastBlock = new Block { Height = Gate - 1 };
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Request(uniqueId, 0.0001M));
            Assert.False(ok);
            Assert.Contains("cannot be reused", msg);
            Assert.Contains("H7", msg);

            var (_, fresh) = await TransactionValidatorService.VerifyTX(Request(Guid.NewGuid().ToString("N"), 0.0001M));
            Assert.DoesNotContain("cannot be reused", fresh);
        }

        [Fact]
        public async Task BlockPath_JudgesAtTheBlocksHeight()
        {
            var uniqueId = Guid.NewGuid().ToString("N");
            Assert.True(VBTCWithdrawalRequest.Save(MinedRow(uniqueId, "H8", 1.0M, Gate - 10, VBTCWithdrawalStatus.Completed)));
            Globals.LastBlock = new Block { Height = Gate + 5000 };
            var (_, historical) = await TransactionValidatorService.VerifyTX(Request(uniqueId, 0.0001M), blockDownloads: true, blockVerify: true, blockHeight: Gate - 1);
            Assert.DoesNotContain("cannot be reused", historical);

            Globals.LastBlock = new Block { Height = 10 };
            var (ok, atGate) = await TransactionValidatorService.VerifyTX(Request(uniqueId, 0.0001M), blockVerify: true, blockHeight: Gate);
            Assert.False(ok);
            Assert.Contains("cannot be reused", atGate);
        }
    }
}
