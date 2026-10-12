using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 8: what a vault owner can spend (docs/PLAN-vBTC-Bitcoin-Attestation.md, 4.2 and 6).
    ///
    /// A vault owner's balance is its vault's Bitcoin plus its ledger. The Bitcoin part used to be an Electrum answer,
    /// which a block cannot be judged by, so block validation took the producer's word for every owner debit: a
    /// validator that won a block could spend vBTC its vault did not hold. For a vault the chain watches, the Bitcoin
    /// part is now the attested coins in state. Every test here judges transactions as block validation does
    /// (VerifyTX with the block's height) and applies them with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest08_OwnerBalanceTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();

        public BtcAttest08_OwnerBalanceTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
        }

        private static long Tip => Globals.LastBlock.Height;

        /// <summary>A Bitcoin payment to the vault, mined, buried, reported by the validators and applied.</summary>
        private async Task<BtcTx> Deposit(NBitcoin.Script vault, long sats)
        {
            var tx = Pay(vault, sats);
            await Report(_btc.Mine(tx));
            return tx;
        }

        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            var report = Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, 0));
            await Apply(Tip + 1, Signed(report));
        }

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

        private VfxTx Transfer(string vault, decimal amount, V? from = null, V? to = null) =>
            SignedBy(from ?? Minter, (to ?? _holder).Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = (from ?? Minter).Address, ToAddress = (to ?? _holder).Address, Amount = amount });

        private VfxTx Withdrawal(string vault, decimal amount, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") });

        private VfxTx BridgeLock(string vault, decimal amount, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault, LockId = Guid.NewGuid().ToString("N"), Amount = amount, AmountSats = (long)(amount * 100_000_000M), EvmDestination = "0x" + new string('a', 40) });

        private async Task InBlock(VfxTx tx, bool accepted, string? because = null)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            if (accepted) Assert.True(ok, why);
            else
            {
                Assert.False(ok, "accepted in a block");
                if (because != null) Assert.Contains(because, why);
            }
        }

        [Fact]
        public async Task ABlock_CannotCarryAnOwnerSpendingMoreThanTheVaultHolds()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            Assert.True(BtcAttestBalance.IsAttestedVault(vault.Uid));
            Assert.Equal(Minter.Address, SmartContractStateTrei.GetSmartContractState(vault.Uid)!.OwnerAddress);
            await Deposit(vault.Script, 150_000);
            Assert.Equal(0.0015M, BtcAttestBalance.DepositBtc(vault.Uid));

            // An Electrum server that would say the vault holds 10 BTC: nobody asks it, in any mode.
            using var electrum = new FakeElectrum(("a", true, 10M));

            await InBlock(Transfer(vault.Uid, 0.0015M), accepted: true);
            await InBlock(Transfer(vault.Uid, 0.00150001M), accepted: false, "Insufficient vBTC balance");
            await InBlock(Transfer(vault.Uid, 5M), accepted: false, "Insufficient vBTC balance");

            // The same answers at admission and in a block proposal.
            Assert.True((await TransactionValidatorService.VerifyTX(Transfer(vault.Uid, 0.0015M))).Item1);
            Assert.False((await TransactionValidatorService.VerifyTX(Transfer(vault.Uid, 0.00150001M))).Item1);
            using (ElectrumCheckScope.Enter(ElectrumCheckMode.BlockProposal))
                Assert.False((await TransactionValidatorService.VerifyTX(Transfer(vault.Uid, 0.00150001M))).Item1);
            using (ElectrumCheckScope.Enter(ElectrumCheckMode.PeerAdmission))
                Assert.False((await TransactionValidatorService.VerifyTX(Transfer(vault.Uid, 0.00150001M))).Item1);
            Assert.Empty(electrum.Asked);
        }

        [Fact]
        public async Task EveryKindOfOwnerDebit_IsJudgedInTheBlock()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            using var electrum = new FakeElectrum(("a", true, 10M));

            await InBlock(Withdrawal(vault.Uid, 0.001M), accepted: true);
            await InBlock(Withdrawal(vault.Uid, 0.002M), accepted: false, "Insufficient vBTC balance");
            await InBlock(BridgeLock(vault.Uid, 0.001M), accepted: true);
            await InBlock(BridgeLock(vault.Uid, 0.002M), accepted: false, "Insufficient vBTC balance");
            await InBlock(SignedBy(Minter, _holder.Address, TransactionType.TKNZ_TX,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = _holder.Address, Amount = 0.002M }), accepted: false, "Insufficient vBTC balance");

            // The shapes that draw on several vaults in one transaction: each vault is judged on its own.
            var second = await CreateVault(Tip + 1);
            await Deposit(second.Script, 60_000);
            var priorGate = Globals.V2TransferMultiHeight;
            Globals.V2TransferMultiHeight = 1;
            try
            {
                VfxTx MultiTransfer(decimal fromFirst, decimal fromSecond) => SignedBy(Minter, _holder.Address, TransactionType.VBTC_V2_TRANSFER, new
                {
                    Function = VBTCService.MultiTransferFunction, FromAddress = Minter.Address, ToAddress = _holder.Address, TotalAmount = fromFirst + fromSecond,
                    Inputs = new[] { new { SCUID = vault.Uid, Amount = fromFirst }, new { SCUID = second.Uid, Amount = fromSecond } },
                });
                await InBlock(MultiTransfer(0.0015M, 0.0006M), accepted: true);
                await InBlock(MultiTransfer(0.0015M, 0.0007M), accepted: false, "Insufficient vBTC balance");
                await InBlock(MultiTransfer(0.0016M, 0.0001M), accepted: false, "Insufficient vBTC balance");

                VfxTx MultiWithdrawal(decimal fromFirst, decimal fromSecond) => SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, new
                {
                    Function = VBTCService.MultiWithdrawalFunction, RequestorAddress = Minter.Address, BTCAddress = BtcDest, TotalAmount = fromFirst + fromSecond, FeeRate = 10,
                    UniqueId = Guid.NewGuid().ToString("N"),
                    Inputs = new[] { new { SCUID = vault.Uid, Amount = fromFirst }, new { SCUID = second.Uid, Amount = fromSecond } },
                });
                await InBlock(MultiWithdrawal(0.0015M, 0.0006M), accepted: true);
                await InBlock(MultiWithdrawal(0.0015M, 0.0007M), accepted: false, "Insufficient vBTC balance");
            }
            finally { Globals.V2TransferMultiHeight = priorGate; }
            Assert.Empty(electrum.Asked);
        }

        [Fact]
        public async Task TheOwnersBalance_FollowsWhatBitcoinHolds()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);

            // A payment to the vault that is mined but not yet buried and reported counts for nothing.
            var first = Pay(vault.Script, 150_000);
            var at = _btc.Mine(first);
            await InBlock(Transfer(vault.Uid, 0.0001M), accepted: false, "Insufficient vBTC balance");
            await Report(at);
            await InBlock(Transfer(vault.Uid, 0.0015M), accepted: true);

            // The owner sends 0.001 to a holder: 0.0005 is left to the owner, 0.001 is the holder's.
            await Apply(Tip + 1, Transfer(vault.Uid, 0.001M));
            await InBlock(Transfer(vault.Uid, 0.0005M), accepted: true);
            await InBlock(Transfer(vault.Uid, 0.00050001M), accepted: false, "Insufficient vBTC balance");
            await InBlock(Transfer(vault.Uid, 0.001M, from: _holder, to: Minter), accepted: true);
            await InBlock(Transfer(vault.Uid, 0.00100001M, from: _holder, to: Minter), accepted: false);

            // A second deposit raises the owner's figure, and only the owner's.
            await Deposit(vault.Script, 50_000);
            await InBlock(Transfer(vault.Uid, 0.001M), accepted: true);
            await InBlock(Transfer(vault.Uid, 0.00100001M), accepted: false);
            await InBlock(Transfer(vault.Uid, 0.00100001M, from: _holder, to: Minter), accepted: false);

            // Bitcoin leaves the vault: the first coin is spent, 0.0004 comes back as change. The vault now holds
            // 0.0009 against the holder's 0.001, so the owner has nothing to spend.
            var spend = Pay(new[] { new OutPoint(first, 0) }, (Elsewhere(), 110_000), (vault.Script, 40_000));
            await Report(_btc.Mine(spend));
            Assert.Equal(0.0009M, BtcAttestBalance.DepositBtc(vault.Uid));
            await InBlock(Transfer(vault.Uid, 0.00000001M), accepted: false, "Insufficient vBTC balance");
        }

        [Fact]
        public async Task TwoOwnerDebitsInOneBlock_AreAddedUp()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);

            // Each is within the balance on its own; block validation checks each against the state before the block.
            var one = Transfer(vault.Uid, 0.001M);
            var two = BridgeLock(vault.Uid, 0.001M);
            await InBlock(one, accepted: true);
            await InBlock(two, accepted: true);

            // The block's debit guard is where they are added up. It used to leave owners out: their balance was not in state.
            var block = new SameBlockDebitGuard.State { BlockHeight = Tip + 1 };
            Assert.True(SameBlockDebitGuard.TryRegister(one, block).Ok);
            var (ok, why) = SameBlockDebitGuard.TryRegister(two, block);
            Assert.False(ok);
            Assert.Contains(Minter.Address, why);

            var within = new SameBlockDebitGuard.State { BlockHeight = Tip + 1 };
            Assert.True(SameBlockDebitGuard.TryRegister(Transfer(vault.Uid, 0.001M), within).Ok);
            Assert.True(SameBlockDebitGuard.TryRegister(BridgeLock(vault.Uid, 0.0005M), within).Ok);
        }

        [Fact]
        public async Task TheWalletFigure_IsTheAttestedOne_WithNoElectrum()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 150_000);
            await Apply(Tip + 1, Transfer(vault.Uid, 0.001M));
            using var electrum = new FakeElectrum(("a", false, 0M)); // no server answers at all

            var owner = await VBTCService.TryGetAvailableTransparentVbtcBalance(vault.Uid, Minter.Address);
            Assert.True(owner.success, owner.error);
            Assert.Equal(0.0005M, owner.availableBalance);
            var holder = await VBTCService.TryGetAvailableTransparentVbtcBalance(vault.Uid, _holder.Address);
            Assert.True(holder.success);
            Assert.Equal(0.001M, holder.availableBalance);
            Assert.Empty(electrum.Asked);
        }

        [Fact]
        public async Task AVaultCreatedBeforeTheRecordingHeight_KeepsTheEarlierRules()
        {
            var legacy = await CreateVault(H1 - 50);
            await Start();
            Assert.False(BtcAttestBalance.IsAttestedVault(legacy.Uid));

            // Admission asks Electrum, as before.
            using (var electrum = new FakeElectrum(("a", true, 1M)))
            {
                Assert.True((await TransactionValidatorService.VerifyTX(Transfer(legacy.Uid, 0.5M))).Item1);
                Assert.False((await TransactionValidatorService.VerifyTX(Transfer(legacy.Uid, 1.5M))).Item1);
                Assert.NotEmpty(electrum.Asked);
            }
            // Block validation still cannot judge it (this is the hole the attested vaults close; these vaults are
            // frozen at the enforcement height instead).
            using (var electrum = new FakeElectrum(("a", true, 0M)))
            {
                await InBlock(Transfer(legacy.Uid, 1.5M), accepted: true);
                Assert.Empty(electrum.Asked);
            }
        }
    }
}
