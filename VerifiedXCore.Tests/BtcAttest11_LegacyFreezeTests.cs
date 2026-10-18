using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 11: what ends at the enforcement height
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 8.2 and 8.4). A vault created before the recording height cannot be
    /// judged from state, so from that height nothing debits a balance on it; and the transactions only the earlier way
    /// of working needs are refused for every vault. One block earlier nothing has changed.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest11_LegacyFreezeTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const long H2 = H1 + 300;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();

        public BtcAttest11_LegacyFreezeTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BitcoinAttestationHeight = H2; // restored by the base class
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
        }

        private static long Tip => Globals.LastBlock.Height;

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private VfxTx Transfer(string vault, decimal amount, V from, V to) =>
            SignedBy(from, to.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = from.Address, ToAddress = to.Address, Amount = amount });

        private VfxTx Request(string vault, decimal amount, V from) =>
            SignedBy(from, from.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") });

        private VfxTx BridgeLock(string vault, decimal amount, V from) =>
            SignedBy(from, from.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault, LockId = Guid.NewGuid().ToString("N"), Amount = amount, AmountSats = (long)(amount * 100_000_000M), EvmDestination = "0x" + new string('a', 40) });

        private async Task Frozen(VfxTx tx, long height)
        {
            var (ok, why) = await Verify(tx, height);
            Assert.False(ok, "accepted on a frozen vault");
            Assert.Contains("has been frozen since block " + H2, why);
        }

        [Fact]
        public async Task FromTheEnforcementHeight_NothingDebitsAVaultCreatedBeforeTheRecordingHeight()
        {
            var legacy = await CreateVault(H1 - 50);
            await Start();
            var watched = await CreateVault(H1 + 1);
            Assert.True(BtcLegacyFreeze.IsLegacyVault(legacy.Uid));
            Assert.False(BtcLegacyFreeze.IsLegacyVault(watched.Uid));
            Assert.False(BtcLegacyFreeze.IsLegacyVault("no-such-contract"));
            // The owner gives a holder vBTC on the old vault while it still can.
            await Apply(Tip + 1, Transfer(legacy.Uid, 0.01M, Minter, _holder));

            // One block before: the earlier rules, untouched (a block is not judged on the owner's deposit; a holder is
            // judged on the ledger).
            Assert.True((await Verify(Transfer(legacy.Uid, 0.5M, Minter, _holder), H2 - 1)).Ok);
            Assert.True((await Verify(Transfer(legacy.Uid, 0.01M, _holder, Minter), H2 - 1)).Ok);
            Assert.True((await Verify(Request(legacy.Uid, 0.01M, _holder), H2 - 1)).Ok);
            Assert.True((await Verify(BridgeLock(legacy.Uid, 0.01M, _holder), H2 - 1)).Ok);

            // From the height on: nobody, for anything.
            foreach (var height in new[] { H2, H2 + 1, H2 + 100_000 })
            {
                await Frozen(Transfer(legacy.Uid, 0.5M, Minter, _holder), height);
                await Frozen(Transfer(legacy.Uid, 0.01M, _holder, Minter), height);
                await Frozen(Request(legacy.Uid, 0.01M, _holder), height);
                await Frozen(Request(legacy.Uid, 0.01M, Minter), height);
                await Frozen(BridgeLock(legacy.Uid, 0.01M, _holder), height);
                await Frozen(SignedBy(_holder, Minter.Address, TransactionType.TKNZ_TX,
                    new { Function = "TransferVBTCV2()", ContractUID = legacy.Uid, FromAddress = _holder.Address, ToAddress = Minter.Address, Amount = 0.01M }), height);
                await Frozen(SignedBy(_holder, _holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                    new { Function = "VBTCWithdrawalCancel()", ContractUID = legacy.Uid, WithdrawalRequestHash = new string('a', 64), FailureProof = "" }), height);
            }

            // A transfer that draws on a frozen vault and a watched one together is refused for the frozen one.
            var priorGate = Globals.V2TransferMultiHeight;
            Globals.V2TransferMultiHeight = 1;
            try
            {
                await Frozen(SignedBy(Minter, _holder.Address, TransactionType.VBTC_V2_TRANSFER, new
                {
                    Function = VerifiedXCore.Bitcoin.Services.VBTCService.MultiTransferFunction, FromAddress = Minter.Address, ToAddress = _holder.Address, TotalAmount = 0.002M,
                    Inputs = new[] { new { SCUID = watched.Uid, Amount = 0.001M }, new { SCUID = legacy.Uid, Amount = 0.001M } },
                }), H2);
            }
            finally { Globals.V2TransferMultiHeight = priorGate; }

            // Admission judges for the next block: at tip H2 - 1 the freeze already applies.
            await Apply(H2 - 1);
            var (admitted, why) = await TransactionValidatorService.VerifyTX(Transfer(legacy.Uid, 0.01M, _holder, Minter));
            Assert.False(admitted);
            Assert.Contains("has been frozen", why);
        }

        private VfxTx Complete(string vault, string requestHash, V from) =>
            SignedBy(from, from.Address, TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                new { Function = "VBTCWithdrawalComplete()", ContractUID = vault, WithdrawalRequestHash = requestHash, BTCTransactionHash = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant() + new string('0', 32) });

        private static decimal Ledger(string vault, V holder) =>
            SmartContractStateTrei.GetSmartContractState(vault)!.SCStateTreiTokenizationTXes!
                .Where(t => t.FromAddress == holder.Address || t.ToAddress == holder.Address).Sum(t => t.Amount);

        [Fact]
        public async Task BetweenTheRecordingAndTheEnforcementHeights_AnOlderVaultsWithdrawalsWorkAsBefore_AndFromTheLatterAnOpenOneIsLeftAsItIs()
        {
            var legacy = await CreateVault(H1 - 50);
            await Start();
            Assert.True(BtcLegacyFreeze.IsLegacyVault(legacy.Uid));
            await Apply(Tip + 1, Transfer(legacy.Uid, 0.01M, Minter, _holder));

            // In the window a holder requests a withdrawal: its vBTC is held from the request, and the request completes.
            var request = Request(legacy.Uid, 0.004M, _holder);
            var (ok, why) = await Verify(request, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, request);
            Assert.Equal(0.006M, Ledger(legacy.Uid, _holder));
            var row = VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, legacy.Uid)!;
            Assert.False(row.IsCompleted);

            var complete = Complete(legacy.Uid, request.Hash, _holder);
            (ok, why) = await Verify(complete, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, complete);
            row = VBTCWithdrawalRequest.GetByTransactionHash(request.Hash, legacy.Uid)!;
            Assert.True(row.IsCompleted);
            Assert.Equal(VBTCWithdrawalStatus.Completed, row.Status);
            Assert.Equal(0.006M, Ledger(legacy.Uid, _holder));

            // A request still open at the enforcement height: its completion is no longer taken, its vBTC stays held, and
            // nothing else moves on the vault (it is completed or settled by hand, not by a transaction).
            var open = Request(legacy.Uid, 0.002M, _holder);
            (ok, why) = await Verify(open, H2 - 1);
            Assert.True(ok, why);
            await Apply(H2 - 1, open);
            Assert.Equal(0.004M, Ledger(legacy.Uid, _holder));
            (ok, why) = await Verify(Complete(legacy.Uid, open.Hash, _holder), H2);
            Assert.False(ok);
            Assert.Contains("no longer accepted (from block " + H2, why);
            await Frozen(SignedBy(_holder, _holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                new { Function = "VBTCWithdrawalCancel()", ContractUID = legacy.Uid, WithdrawalRequestHash = open.Hash, FailureProof = "" }), H2);
            await Apply(H2);
            Assert.False(VBTCWithdrawalRequest.GetByTransactionHash(open.Hash, legacy.Uid)!.IsCompleted);
            Assert.Equal(0.004M, Ledger(legacy.Uid, _holder));
        }

        [Fact]
        public async Task AWatchedVault_IsNotTouchedByTheFreeze()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = Pay(vault.Script, 150_000);
            var at = _btc.Mine(deposit);
            _btc.MineEmpty(BtcAttestRules.Depth);
            await Apply(Tip + 1, Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(at), at, _btc.HeaderHexes(Checkpoint + 1, at + BtcAttestRules.Depth), 0))));

            foreach (var height in new[] { H2 - 1, H2, H2 + 1 })
            {
                Assert.True((await Verify(Transfer(vault.Uid, 0.001M, Minter, _holder), height)).Ok);
                Assert.True((await Verify(Request(vault.Uid, 0.001M, Minter), height)).Ok);
                Assert.False((await Verify(Transfer(vault.Uid, 0.002M, Minter, _holder), height)).Ok); // its own rule: the attested coins
            }
        }

        [Fact]
        public async Task TheTransactionsOfTheEarlierWay_AreRefusedForEveryVault_FromTheHeight()
        {
            var legacy = await CreateVault(H1 - 50);
            await Start();
            var complete = SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                new { Function = "VBTCWithdrawalComplete()", ContractUID = legacy.Uid, WithdrawalRequestHash = new string('a', 64), BTCTransactionHash = new string('b', 64) });
            var vote = SignedBy(Signers[0], Signers[0].Address, TransactionType.VBTC_V2_WITHDRAWAL_VOTE,
                new { Function = "VBTCWithdrawalCancelVote()", CancellationUID = "CANCEL_x", Approve = true, ContractUID = legacy.Uid, WithdrawalRequestHash = new string('a', 64) });
            var exitComplete = SignedBy(Signers[0], Signers[0].Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE,
                new { Function = "VBTCBridgeExitToBTCComplete()", ExitBurnTxHash = "0x" + new string('c', 64), BTCTransactionHash = new string('d', 64) });

            foreach (var tx in new[] { complete, vote, exitComplete })
            {
                Assert.Null(BtcLegacyFreeze.Refusal(tx, H2 - 1)); // still judged by its own rules
                Assert.Contains("no longer accepted", BtcLegacyFreeze.Refusal(tx, H2));
                var (ok, why) = await Verify(tx, H2);
                Assert.False(ok);
                Assert.Contains("no longer accepted (from block " + H2, why);
            }
        }
    }
}
