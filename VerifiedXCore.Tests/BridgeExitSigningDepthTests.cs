using System;
using System.Collections.Generic;
using System.IO;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using Block = VerifiedXCore.Models.Block;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A bridge exit on a vault the chain does not watch waits for its EXIT_TO_BTC to be as deep as a withdrawal request
    /// must be, and still in its block, before a share is given, at ceremony start and at Round 2. Otherwise a reorg that
    /// drops the EXIT_TO_BTC after its Bitcoin is signed has the FAIL and re-exit pay the same burn from another vault.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeExitSigningDepthTests : IDisposable
    {
        private static readonly Network Net = Network.TestNet;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Network? _priorNetwork;
        private readonly Block _priorLastBlock;
        private readonly Func<long, string, bool> _priorHolds;
        private readonly string _leader;

        private readonly BitcoinAddress _vaultAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        private readonly BitcoinAddress _destAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
        private readonly string _vault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
        private const string Owner = "xWithdrawalOwner000000000000000001";
        private const string Wrh = "withdrawal-request-hash-1";
        private const decimal Amount = 0.5M;
        private const long AmountSats = 50_000_000L;
        private const long Tip = 500L;
        private const long AppliedAt = Tip - 1; // inside MAX_REORG_DEPTH
        private readonly string _burn = new string('e', 64); // Base burn tx hash (normalized, no 0x)

        public BridgeExitSigningDepthTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"exitdepth_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorNetwork = Globals.BTCNetwork;
            Globals.BTCNetwork = Net;
            _priorLastBlock = Globals.LastBlock;
            Globals.LastBlock = new Block { Height = Tip };
            _priorHolds = FrostSigningAuthorization.BlockHoldsRequest;
            FrostSigningAuthorization.BlockHoldsRequest = (_, _) => true;
            DbContext.Initialize();

            // The bridge-exit leader must be a committee caster; with no membership record the committee
            // falls back to this network's seed allowlist (prefix-filtered BootstrapCasterAddresses).
            _leader = (Globals.IsTestNet ? "x" : "R") + "ExitDepthCommitteeCaster00000000";
            Globals.BootstrapCasterAddresses.Add(_leader);

            // One unwatched vault (no BtcVaultKeys row, not an attested vault), read from chain state.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = _vault,
                ContractData = VbtcTestContracts.VaultContractData(_vault, Owner, _vaultAddr.ToString(), "02" + new string('a', 64)),
                MinterAddress = Owner, OwnerAddress = Owner, IsLocked = false, Nonce = 0,
            });

            // A withdrawal in the same block, which the rule already held back.
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = Owner, OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = _vault,
                TransactionHash = Wrh, Amount = Amount, BTCDestination = _destAddr.ToString(), FeeRate = 10,
                Timestamp = TimeUtil.GetTime(), RequestBlockHeight = AppliedAt,
                Status = VBTCWithdrawalStatus.Requested, IsCompleted = false,
            }));

            // An EXIT_TO_BTC applied in that block, drawing one lock of this (unwatched) vault.
            var allocations = new List<PoolUnlockAllocation>
            {
                new PoolUnlockAllocation { LockId = "L1", SmartContractUID = _vault, UnlockAmount = Amount },
            };
            Assert.True(VBTCBridgeBtcExitState.TryInsert(new VBTCBridgeBtcExitState
            {
                BaseBurnTxHash = _burn, LockId = "L1", SmartContractUID = _vault, OwnerAddress = Owner,
                Amount = Amount, AmountSats = AmountSats, BtcDestination = _destAddr.ToString(),
                ExitTxHash = "exit-tx-1", ExitVfxHeight = AppliedAt, CreatedTimestamp = TimeUtil.GetTime(),
                IsComplete = false,
                AllocationsJson = JsonConvert.SerializeObject(allocations),
                DrawnAllocationsJson = JsonConvert.SerializeObject(allocations), // applying it drew every part
            }));
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = "L1", SmartContractUID = _vault, OwnerAddress = Owner, Amount = Amount,
                AmountSats = AmountSats, EvmDestination = "0xabc", LockTxHash = "lt-L1", LockTimestamp = 1,
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.BootstrapCasterAddresses.Remove(_leader);
            FrostSigningAuthorization.BlockHoldsRequest = _priorHolds;
            Globals.LastBlock = _priorLastBlock;
            Globals.BTCNetwork = _priorNetwork;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>One vault coin in, the payout to <paramref name="destSats"/> at the destination, no change (fee = coin - payout).</summary>
        private (NBitcoin.Transaction Tx, List<PinnedWithdrawalCoin> Prevouts, List<string> Sighashes) Spend(long coinSats, long destSats)
        {
            var prevTxid = new uint256(RandomUtils.GetBytes(32));
            var tx = Net.CreateTransaction();
            tx.Inputs.Add(new OutPoint(prevTxid, 1));
            tx.Outputs.Add(Money.Satoshis(destSats), _destAddr);
            var prevouts = new List<PinnedWithdrawalCoin> { new() { TxId = prevTxid.ToString(), Vout = 1, ValueSats = (ulong)coinSats, ScriptPubKeyHex = _vaultAddr.ScriptPubKey.ToHex() } };
            var spent = new[] { new TxOut(Money.Satoshis(coinSats), _vaultAddr.ScriptPubKey) };
            var pre = tx.PrecomputeTransactionData(spent);
            var sighash = Convert.ToHexString(tx.GetSignatureHashTaproot(pre, new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant();
            return (tx, prevouts, new List<string> { sighash });
        }

        private FrostSigningStartRequest Request(string reference, string leader, NBitcoin.Transaction tx, List<PinnedWithdrawalCoin> prevouts, List<string> sighashes) => new()
        {
            SessionId = Guid.NewGuid().ToString("N"), SmartContractUID = _vault, WithdrawalRequestHash = reference,
            LeaderAddress = leader, Timestamp = TimeUtil.GetTime(), LeaderSignature = "sig",
            SignerAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 2,
            InputIndex = 0, InputCount = 1, UnsignedTxHex = tx.ToHex(), Prevouts = prevouts, AllInputSighashes = sighashes, MessageHash = sighashes[0],
        };

        private string ExitReference => VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ExitSigningReference(_burn, _vault);

        private (bool Ok, string Reason) AuthorizeExit()
        {
            var fee = BitcoinTransactionService.WithdrawalFeeSats(1, FrostSigningAuthorization.LegacyExitFeeRate);
            var (tx, prevouts, sighashes) = Spend(AmountSats, AmountSats - fee);
            return FrostSigningAuthorization.Authorize(Request(ExitReference, _leader, tx, prevouts, sighashes));
        }

        [Fact]
        public void AnExitOneBlockBelowTheTip_GetsNoShare_AsAWithdrawalInTheSameBlockGetsNone()
        {
            var fee = BitcoinTransactionService.WithdrawalFeeSats(1, 10);
            var (tx, prevouts, sighashes) = Spend(AmountSats, AmountSats - fee);
            Assert.Contains("signing starts at 12 blocks", FrostSigningAuthorization.Authorize(Request(Wrh, Owner, tx, prevouts, sighashes)).Reason);

            var (ok, reason) = AuthorizeExit();
            Assert.False(ok);
            Assert.Contains("signing starts at 12 blocks", reason);
            Assert.Contains("signing starts at 12 blocks", FrostSigningAuthorization.BridgeExitNoLongerSignable(ExitReference, _vault)!);
        }

        [Fact]
        public void AnExitTwelveBlocksDown_IsSigned_UnlessItsBlockNoLongerHoldsIt()
        {
            Globals.LastBlock = new Block { Height = AppliedAt + FrostSigningAuthorization.WithdrawalSigningDepth };
            var (ok, reason) = AuthorizeExit();
            Assert.True(ok, reason);
            Assert.Null(FrostSigningAuthorization.BridgeExitNoLongerSignable(ExitReference, _vault));

            FrostSigningAuthorization.BlockHoldsRequest = (_, _) => false; // a reorg took the EXIT_TO_BTC out
            Assert.Contains("no longer in block", AuthorizeExit().Reason);
            Assert.Contains("no longer in block", FrostSigningAuthorization.BridgeExitNoLongerSignable(ExitReference, _vault)!);
        }
    }
}
