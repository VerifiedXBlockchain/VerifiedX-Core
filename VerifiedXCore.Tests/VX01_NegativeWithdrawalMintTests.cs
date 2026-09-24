using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
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
    /// VX-01 (CRITICAL): "A negative withdrawal amount creates vBTC from nothing".
    ///
    /// Audit PoC: VBTC_V2_WITHDRAWAL_REQUEST with Data.Amount = -10.0 from an address holding zero
    /// vBTC (not the owner) was verified, mined, and the escrow apply wrote "+" → attacker for +10.0
    /// (WriteWithdrawalLedgerRow(-amount) with amount negative). Audit controls: +10 from the same
    /// empty address is rejected for insufficient funds; +0.5 from a funded address debits it.
    ///
    /// Covers every surface of the fix: the consensus validator (single + multi withdrawal, single +
    /// multi transfer, bridge lock — amount rule and vBTC-contract rule), and the StateData apply
    /// (request refuses non-positive amounts; debit/credit helpers refuse non-positive amounts).
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX01_NegativeWithdrawalMintTests : IDisposable
    {
        private const long TestHeight = 1000;
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorEscrowHeight;
        private readonly Block _priorLastBlock;

        private readonly PrivateKey _key;
        private readonly string _pub;
        private readonly string _attacker; // holds no vBTC, not the owner
        private readonly PrivateKey _funderKey;
        private readonly string _funderPub;
        private readonly string _funded;   // holds 4.0 vBTC via the ledger, not the owner

        public VX01_NegativeWithdrawalMintTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx01_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();

            (_key, _pub, _attacker) = NewKey();
            (_funderKey, _funderPub, _funded) = NewKey();

            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _attacker, Balance = 100M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _funded, Balance = 100M, Nonce = 0 });

            Globals.LastBlock = new Block { Height = TestHeight + 1 };
            Globals.WithdrawalEscrowHeight = 1; // escrow path active (testnet value)
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.WithdrawalEscrowHeight = _priorEscrowHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private void SeedContract(string scUid, string contractData, params (string From, string To, decimal Amount)[] rows)
        {
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = scUid,
                OwnerAddress = "xContractOwnerNotTheAttacker0000001",
                ContractData = contractData,
                SCStateTreiTokenizationTXes = rows
                    .Select(r => new SmartContractStateTreiTokenizationTX { FromAddress = r.From, ToAddress = r.To, Amount = r.Amount })
                    .ToList(),
            });
        }

        private static Transaction Signed(TransactionType type, string from, string to, object data, PrivateKey key, string pub, long height = 0)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = from,
                ToAddress = to,
                Amount = 0.0M,
                Fee = 0.00001M,
                Nonce = 0,
                TransactionType = type,
                Data = JsonConvert.SerializeObject(data),
                Height = height,
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, key, pub);
            return tx;
        }

        private Transaction WithdrawalRequest(string scUid, decimal amount, string from, PrivateKey key, string pub, int feeRate = 10, long height = 0)
            => Signed(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, from, from,
                new { Function = "WithdrawalRequest()", ContractUID = scUid, BTCAddress = BtcDest, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString() },
                key, pub, height);

        private static decimal Ledger(string scUid, string address) =>
            SmartContractStateTrei.GetSmartContractState(scUid)!.SCStateTreiTokenizationTXes!
                .Where(r => r.FromAddress == address || r.ToAddress == address).Sum(r => r.Amount);

        private static void InvokeApply(string method, Transaction tx)
        {
            var m = typeof(StateData).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            m!.Invoke(null, new object[] { tx });
        }

        // ── Follow-up (independent review): the TransferVBTCV2() function path ─────────────

        private Transaction FunctionTransfer(string scUid, decimal amount) =>
            Signed(TransactionType.TKNZ_TX, _funded, _attacker,
                new { Function = "TransferVBTCV2()", ContractUID = scUid, FromAddress = _funded, ToAddress = _attacker, Amount = amount },
                _funderKey, _funderPub);

        [Fact]
        public async Task VX01_FunctionPath_OnANonVbtcContract_Refused()
        {
            // Same ledger move as VBTC_V2_TRANSFER, reachable through the function dispatcher, which lacked the
            // contract-type check VX-01 added to the typed transaction.
            SeedContract("nft-not-vbtc:1", VbtcTestContracts.PlainNftContractData, (_funded, _funded, 4.0M));
            var (ok, message) = await TransactionValidatorService.VerifyTX(FunctionTransfer("nft-not-vbtc:1", 1.0M));
            Assert.False(ok);
            Assert.Contains("not a vBTC V2 contract", message);
        }

        [Fact]
        public async Task VX01_FunctionPath_MoreThan8Decimals_Refused()
        {
            SeedContract("vbtc-v2-fn:1", VbtcTestContracts.VbtcV2ContractData, (_funded, _funded, 4.0M));
            var (ok, _) = await TransactionValidatorService.VerifyTX(FunctionTransfer("vbtc-v2-fn:1", 0.000000001M));
            Assert.False(ok);
        }

        // ── Audit PoC and controls, consensus validator ─────────────────────────────────────

        [Fact]
        public async Task VX01_AuditAttack_NegativeAmount_FromZeroBalanceAddress_Rejected()
        {
            SeedContract("vx01poc:1", VbtcTestContracts.VbtcV2ContractData);

            var (ok, message) = await TransactionValidatorService.VerifyTX(WithdrawalRequest("vx01poc:1", -10.0M, _attacker, _key, _pub));

            Assert.False(ok);
            Assert.Contains("Amount must be greater than zero", message);
        }

        [Fact]
        public async Task VX01_AuditAttack_NegativeAmount_BlockVerifyPath_Rejected()
        {
            // The audit notes the block-level path repeats the same comparison; VerifyTX with
            // blockVerify=true and an explicit block height is what BlockValidatorService runs.
            SeedContract("vx01blk:1", VbtcTestContracts.VbtcV2ContractData);

            var (ok, message) = await TransactionValidatorService.VerifyTX(
                WithdrawalRequest("vx01blk:1", -0.5M, _attacker, _key, _pub), blockDownloads: false, blockVerify: true, blockHeight: TestHeight + 1);

            Assert.False(ok);
            Assert.Contains("Amount must be greater than zero", message);
        }

        [Fact]
        public async Task VX01_Control_PositiveAmount_FromZeroBalanceAddress_RejectedInsufficient()
        {
            SeedContract("vx01ctl:1", VbtcTestContracts.VbtcV2ContractData);

            var (ok, message) = await TransactionValidatorService.VerifyTX(WithdrawalRequest("vx01ctl:1", 10.0M, _attacker, _key, _pub));

            Assert.False(ok);
            Assert.Contains("Insufficient vBTC balance", message);
        }

        [Fact]
        public async Task VX01_Control_PositiveAmount_FromFundedAddress_Accepted()
        {
            SeedContract("vx01fund:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 4.0M));

            var (ok, message) = await TransactionValidatorService.VerifyTX(WithdrawalRequest("vx01fund:1", 0.5M, _funded, _funderKey, _funderPub));

            Assert.True(ok, message);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-0.00000001")]
        [InlineData("0.000000001")]      // 9 decimal places
        [InlineData("1.123456789")]
        public async Task VX01_InvalidAmounts_Rejected(string amountText)
        {
            SeedContract("vx01amt:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 4.0M));
            var amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);

            var (ok, message) = await TransactionValidatorService.VerifyTX(WithdrawalRequest("vx01amt:1", amount, _funded, _funderKey, _funderPub));

            Assert.False(ok);
            Assert.Contains("Amount", message);
        }

        [Fact]
        public async Task VX01_ZeroFeeRate_Rejected()
        {
            SeedContract("vx01fee:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 4.0M));

            var (ok, message) = await TransactionValidatorService.VerifyTX(WithdrawalRequest("vx01fee:1", 0.5M, _funded, _funderKey, _funderPub, feeRate: 0));

            Assert.False(ok);
            Assert.Contains("FeeRate must be greater than zero", message);
        }

        // ── Contract-type rule: any minted smart contract used to be a valid target ─────────

        [Fact]
        public async Task VX01_Withdrawal_AgainstNonVbtcContract_Rejected()
        {
            // Funded ledger rows on an ordinary NFT contract: without the contract-type rule the
            // positive request would pass the balance gate.
            SeedContract("vx01nft:1", VbtcTestContracts.PlainNftContractData, ("+", _funded, 4.0M));

            var (ok, message) = await TransactionValidatorService.VerifyTX(WithdrawalRequest("vx01nft:1", 0.5M, _funded, _funderKey, _funderPub));

            Assert.False(ok);
            Assert.Contains("is not a vBTC V2 contract", message);
        }

        [Fact]
        public async Task VX01_Withdrawal_AgainstContractWithNoCode_Rejected()
        {
            SeedContract("vx01nocode:1", contractData: null!, ("+", _funded, 4.0M));

            var (ok, message) = await TransactionValidatorService.VerifyTX(WithdrawalRequest("vx01nocode:1", 0.5M, _funded, _funderKey, _funderPub));

            Assert.False(ok);
            Assert.Contains("is not a vBTC V2 contract", message);
        }

        [Fact]
        public async Task VX01_MultiWithdrawal_AgainstNonVbtcContract_Rejected()
        {
            SeedContract("vx01mnft:1", VbtcTestContracts.PlainNftContractData, ("+", _funded, 4.0M));
            var tx = Signed(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, _funded, _funded, new
            {
                Function = VBTCService.MultiWithdrawalFunction,
                BTCAddress = BtcDest,
                TotalAmount = 0.5M,
                FeeRate = 10,
                Inputs = new[] { new { SCUID = "vx01mnft:1", Amount = 0.5M } },
            }, _funderKey, _funderPub);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("is not a vBTC V2 contract", message);
        }

        [Fact]
        public async Task VX01_Transfer_AgainstNonVbtcContract_Rejected()
        {
            SeedContract("vx01tnft:1", VbtcTestContracts.PlainNftContractData, ("+", _funded, 4.0M));
            var tx = Signed(TransactionType.VBTC_V2_TRANSFER, _funded, _attacker,
                new { Function = "TransferVBTCV2()", ContractUID = "vx01tnft:1", FromAddress = _funded, ToAddress = _attacker, Amount = 0.5M },
                _funderKey, _funderPub);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("is not a vBTC V2 contract", message);
        }

        [Fact]
        public async Task VX01_Transfer_NineDecimalAmount_Rejected()
        {
            SeedContract("vx01t9:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 4.0M));
            var tx = Signed(TransactionType.VBTC_V2_TRANSFER, _funded, _attacker,
                new { Function = "TransferVBTCV2()", ContractUID = "vx01t9:1", FromAddress = _funded, ToAddress = _attacker, Amount = 0.123456789M },
                _funderKey, _funderPub);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("8 decimal places", message);
        }

        [Fact]
        public async Task VX01_Transfer_Control_ValidTransfer_Accepted()
        {
            SeedContract("vx01tok:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 4.0M));
            var tx = Signed(TransactionType.VBTC_V2_TRANSFER, _funded, _attacker,
                new { Function = "TransferVBTCV2()", ContractUID = "vx01tok:1", FromAddress = _funded, ToAddress = _attacker, Amount = 0.5M },
                _funderKey, _funderPub);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.True(ok, message);
        }

        [Fact]
        public async Task VX01_MultiTransfer_AgainstNonVbtcContract_Rejected()
        {
            var priorMultiHeight = Globals.V2TransferMultiHeight;
            Globals.V2TransferMultiHeight = 1;
            try
            {
                SeedContract("vx01mtnft:1", VbtcTestContracts.PlainNftContractData, ("+", _funded, 4.0M));
                var tx = Signed(TransactionType.VBTC_V2_TRANSFER, _funded, _attacker, new
                {
                    Function = VBTCService.MultiTransferFunction,
                    FromAddress = _funded,
                    ToAddress = _attacker,
                    TotalAmount = 0.5M,
                    Inputs = new[] { new { SCUID = "vx01mtnft:1", Amount = 0.5M } },
                }, _funderKey, _funderPub);

                var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

                Assert.False(ok);
                Assert.Contains("is not a vBTC V2 contract", message);
            }
            finally { Globals.V2TransferMultiHeight = priorMultiHeight; }
        }

        [Fact]
        public async Task VX01_BridgeLock_AgainstNonVbtcContract_Rejected()
        {
            SeedContract("vx01bnft:1", VbtcTestContracts.PlainNftContractData, ("+", _funded, 4.0M));
            var tx = Signed(TransactionType.VBTC_V2_BRIDGE_LOCK, _funded, _funded, new
            {
                Function = "VBTCBridgeLock()",
                ContractUID = "vx01bnft:1",
                LockId = Guid.NewGuid().ToString("N"),
                Amount = 0.5M,
                AmountSats = 50_000_000L,
                EvmDestination = "0x" + new string('1', 40),
            }, _funderKey, _funderPub);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("is not a vBTC V2 contract", message);
        }

        [Fact]
        public async Task VX01_BridgeLock_SubSatoshiAmount_Rejected()
        {
            SeedContract("vx01bsub:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 4.0M));
            var tx = Signed(TransactionType.VBTC_V2_BRIDGE_LOCK, _funded, _funded, new
            {
                Function = "VBTCBridgeLock()",
                ContractUID = "vx01bsub:1",
                LockId = Guid.NewGuid().ToString("N"),
                Amount = 0.000000019M,
                AmountSats = 1L, // the truncated value the old check compared against
                EvmDestination = "0x" + new string('1', 40),
            }, _funderKey, _funderPub);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("8 decimal places", message);
        }

        // ── StateData apply (consensus, all nodes) ──────────────────────────────────────────

        [Fact]
        public void VX01_Apply_NegativeRequest_DoesNotCreditRequester_AndWritesNoRecord()
        {
            SeedContract("vx01apply:1", VbtcTestContracts.VbtcV2ContractData);
            var tx = WithdrawalRequest("vx01apply:1", -10.0M, _attacker, _key, _pub, height: 97);

            InvokeApply("RequestVBTCV2Withdrawal", tx);

            Assert.Equal(0M, Ledger("vx01apply:1", _attacker));
            Assert.Null(VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash));
        }

        [Fact]
        public void VX01_Apply_Control_PositiveRequest_DebitsRequester()
        {
            SeedContract("vx01apctl:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 4.0M));
            var tx = WithdrawalRequest("vx01apctl:1", 0.5M, _funded, _funderKey, _funderPub, height: 463);

            InvokeApply("RequestVBTCV2Withdrawal", tx);

            Assert.Equal(3.5M, Ledger("vx01apctl:1", _funded));
            var rows = SmartContractStateTrei.GetSmartContractState("vx01apctl:1")!.SCStateTreiTokenizationTXes!;
            Assert.Contains(rows, r => r.FromAddress == _funded && r.ToAddress == "-" && r.Amount == -0.5M);
            Assert.DoesNotContain(rows, r => r.FromAddress == "+" && r.ToAddress == _funded && r.Amount != 4.0M);
        }

        [Fact]
        public void VX01_Apply_LegacyComplete_NonPositiveStoredAmount_DoesNotCredit()
        {
            // Pre-escrow COMPLETE burned "stored * -1"; a negative stored amount would have become a
            // credit. The apply already refused non-positive stored amounts before VX-01; this pins it.
            Globals.WithdrawalEscrowHeight = 999_999_999_999L;
            SeedContract("vx01legacy:1", VbtcTestContracts.VbtcV2ContractData);
            var reqHash = new string('b', 64);
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = _attacker,
                SmartContractUID = "vx01legacy:1",
                Amount = -10.0M,
                BTCDestination = BtcDest,
                FeeRate = 10,
                OriginalUniqueId = Guid.NewGuid().ToString(),
                OriginalRequestTime = TimeUtil.GetTime(),
                OriginalSignature = "",
                Timestamp = TimeUtil.GetTime(),
                TransactionHash = reqHash,
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = 5,
            }));

            var complete = Signed(TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE, _attacker, _attacker,
                new { Function = "WithdrawalComplete()", ContractUID = "vx01legacy:1", WithdrawalRequestHash = reqHash, BTCTransactionHash = new string('a', 64) },
                _key, _pub, height: 12);
            InvokeApply("CompleteVBTCV2Withdrawal", complete);

            Assert.Equal(0M, Ledger("vx01legacy:1", _attacker));
        }

        [Theory]
        [InlineData("WriteWithdrawalEscrowDebit")]
        [InlineData("WriteWithdrawalRefundCredit")]
        public void VX01_LedgerHelpers_RefuseNonPositiveAmounts(string helper)
        {
            SeedContract("vx01helper:1", VbtcTestContracts.VbtcV2ContractData, ("+", _funded, 1.0M));
            var m = typeof(StateData).GetMethod(helper, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);

            Assert.False((bool)m!.Invoke(null, new object[] { "vx01helper:1", _funded, -10.0M })!);
            Assert.False((bool)m.Invoke(null, new object[] { "vx01helper:1", _funded, 0M })!);
            Assert.Equal(1.0M, Ledger("vx01helper:1", _funded));
        }

        [Fact]
        public void VX01_SignedLedgerWriter_NoLongerExists()
        {
            // The single signed-amount writer let a caller's "-amount" invert into a credit. It is
            // replaced by debit/credit helpers that each take a positive amount.
            Assert.Null(typeof(StateData).GetMethod("WriteWithdrawalLedgerRow", BindingFlags.NonPublic | BindingFlags.Static));
        }

        [Fact]
        public void VX01_AmountRule_Helper()
        {
            Assert.Null(VBTCService.GetVbtcAmountError(0.00000001M, "x"));
            Assert.Null(VBTCService.GetVbtcAmountError(21_000_000M, "x"));
            Assert.NotNull(VBTCService.GetVbtcAmountError(null, "x"));
            Assert.NotNull(VBTCService.GetVbtcAmountError(0M, "x"));
            Assert.NotNull(VBTCService.GetVbtcAmountError(-1M, "x"));
            Assert.NotNull(VBTCService.GetVbtcAmountError(0.000000001M, "x"));
        }

        [Fact]
        public void VX01_IsVbtcV2Contract_Classification()
        {
            Assert.True(VBTCService.IsVbtcV2Contract(new SmartContractStateTrei { SmartContractUID = "c:v2", ContractData = VbtcTestContracts.VbtcV2ContractData }));
            Assert.False(VBTCService.IsVbtcV2Contract(new SmartContractStateTrei { SmartContractUID = "c:nft", ContractData = VbtcTestContracts.PlainNftContractData }));
            Assert.False(VBTCService.IsVbtcV2Contract(new SmartContractStateTrei { SmartContractUID = "c:none", ContractData = null! }));
            Assert.False(VBTCService.IsVbtcV2Contract(new SmartContractStateTrei { SmartContractUID = "c:junk", ContractData = "not-base64-gzip" }));
            Assert.False(VBTCService.IsVbtcV2Contract(null));
        }
    }
}
