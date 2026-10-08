using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1, stage 1 (Globals.PrivateTxSupplyRulesHeight): a VFX unshield may not take more than the
    /// shielded pool holds, must spend at least one note against a Merkle root, and its outer recipient/amount must equal
    /// the payload's. The audit's forged unshield (no proof, no nullifier, no root, any amount) is refused at the gate
    /// and - for replay - still accepted below it. Two unshields in one block are judged against one supply.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss01_PrivateTxSupplyRulesTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly string _root;

        public FundLoss01_PrivateTxSupplyRulesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl01_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.PrivateTxSupplyRulesHeight;
            Globals.PrivateTxSupplyRulesHeight = Gate;
            DbContext.Initialize();

            _root = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pool = ShieldedPoolService.GetOrCreateState("VFX");
            pool.TotalShieldedSupply = 0.5M;
            pool.CurrentMerkleRoot = _root;
            PrivacyDbContext.PoolState().Update(pool);
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxSupplyRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string NewAddress()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return AccountData.GetHumanAddress(pub);
        }

        private static string RandomScalar() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        /// <summary>An unshield shaped the way the honest builder shapes it (nullifier, position, root, bound outer fields).</summary>
        private Transaction Unshield(string to, decimal amount, bool withNullifier = true, bool withRoot = true,
            string? payloadTo = null, decimal? payloadAmount = null)
        {
            var payload = new PrivateTxPayload
            {
                Version = 1,
                Kind = "unshield",
                SubType = "Unshield",
                Asset = "VFX",
                NullsB64 = withNullifier ? new() { RandomScalar() } : new(),
                SpentCommitmentTreePositions = withNullifier ? new() { 0L } : new(),
                MerkleRootB64 = withRoot ? _root : null,
                TransparentOutput = payloadTo ?? to,
                TransparentAmount = payloadAmount ?? amount,
                Fee = Globals.PrivateTxFixedFee,
            };
            var tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress,
                ToAddress = to,
                Amount = amount,
                Fee = 0M,
                Nonce = 0,
                Timestamp = TimeUtil.GetTime(),
                TransactionType = TransactionType.VFX_UNSHIELD,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload),
                Signature = PrivacyConstants.PlonkSignatureSentinel,
            };
            tx.BuildPrivate();
            return tx;
        }

        private Transaction PrivateTransfer()
        {
            var payload = new PrivateTxPayload
            {
                Version = 1, Kind = "private_transfer", SubType = "Transfer", Asset = "VFX",
                NullsB64 = new() { RandomScalar() }, SpentCommitmentTreePositions = new() { 0L },
                MerkleRootB64 = _root, Fee = Globals.PrivateTxFixedFee,
            };
            var tx = new Transaction
            {
                FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = PrivacyConstants.ShieldedPoolAddress,
                Amount = 0M, Fee = 0M, Nonce = 0, Timestamp = TimeUtil.GetTime(),
                TransactionType = TransactionType.VFX_PRIVATE_TRANSFER,
                Data = PrivateTxPayloadCodec.SerializeToJson(payload), Signature = PrivacyConstants.PlonkSignatureSentinel,
            };
            tx.BuildPrivate();
            return tx;
        }

        private static void TipBelowGate() => Globals.LastBlock = new Block { Height = Gate - 2 }; // next block = Gate - 1
        private static void TipAtGate() => Globals.LastBlock = new Block { Height = Gate - 1 };   // next block = Gate

        // ── The audit's forged unshield ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ForgedUnshield_NoNoteNoRoot_AnyAmount_IsAcceptedBelowGate_AndRefusedAtGate()
        {
            var to = NewAddress();
            TipBelowGate();
            var (okBelow, msgBelow) = await TransactionValidatorService.VerifyTX(Unshield(to, 100_000M, withNullifier: false, withRoot: false));
            Assert.True(okBelow, msgBelow); // replay of the Sept 8 transactions below the gate stays as mined

            TipAtGate();
            var (okAt, msgAt) = await TransactionValidatorService.VerifyTX(Unshield(to, 100_000M, withNullifier: false, withRoot: false));
            Assert.False(okAt);
            Assert.Contains("at least one note", msgAt);
        }

        [Fact]
        public async Task Unshield_BeyondSupply_IsRefusedAtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), 0.6M));
            Assert.False(ok);
            Assert.StartsWith(PrivateTxSupplyRules.SupplyReasonPrefix, msg);
        }

        [Fact]
        public async Task Unshield_WithinSupply_IsAcceptedAtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), 0.4M));
            Assert.True(ok, msg);
        }

        [Fact]
        public async Task Unshield_ExactlyTheSupplyLessFee_IsAccepted_OneSatoshiMore_IsRefused()
        {
            TipAtGate();
            var exact = 0.5M - Globals.PrivateTxFixedFee;
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), exact));
            Assert.True(ok, msg);
            var (ok2, msg2) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), exact + 0.00000001M));
            Assert.False(ok2);
            Assert.StartsWith(PrivateTxSupplyRules.SupplyReasonPrefix, msg2);
        }

        [Fact]
        public async Task Unshield_WithoutMerkleRoot_IsRefusedAtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), 0.1M, withRoot: false));
            Assert.False(ok);
            Assert.Contains("Merkle root", msg);
        }

        [Fact]
        public async Task Unshield_OuterRecipientDiffersFromPayload_IsRefusedAtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), 0.1M, payloadTo: NewAddress()));
            Assert.False(ok);
            Assert.Contains("transparent_output", msg);
        }

        [Fact]
        public async Task Unshield_OuterAmountDiffersFromPayload_IsRefusedAtGate()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), 0.1M, payloadAmount: 0.01M));
            Assert.False(ok);
            Assert.Contains("transparent_amount", msg);
        }

        [Fact]
        public async Task PrivateTransfer_FeeWithinSupply_Accepted_EmptyPool_Refused()
        {
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(PrivateTransfer());
            Assert.True(ok, msg);

            var pool = ShieldedPoolService.GetOrCreateState("VFX");
            pool.TotalShieldedSupply = 0M;
            PrivacyDbContext.PoolState().Update(pool);
            var (ok2, msg2) = await TransactionValidatorService.VerifyTX(PrivateTransfer());
            Assert.False(ok2);
            Assert.StartsWith(PrivateTxSupplyRules.SupplyReasonPrefix, msg2);
        }

        // ── Replay determinism: block validation judges at the block's own height ─────────────────────────────────

        [Fact]
        public async Task BlockPath_UsesBlockHeight_NotTip()
        {
            var forged = Unshield(NewAddress(), 100_000M, withNullifier: false, withRoot: false);
            Globals.LastBlock = new Block { Height = Gate + 5000 };
            var (okHistorical, msg) = await TransactionValidatorService.VerifyTX(forged, blockDownloads: true, blockVerify: true, blockHeight: Gate - 1);
            Assert.True(okHistorical, msg);

            Globals.LastBlock = new Block { Height = 10 };
            var (okAtGate, _) = await TransactionValidatorService.VerifyTX(Unshield(NewAddress(), 100_000M, withNullifier: false, withRoot: false), blockVerify: true, blockHeight: Gate);
            Assert.False(okAtGate);
        }

        // ── Same block: two unshields are judged against one supply ───────────────────────────────────────────────

        [Fact]
        public void SameBlock_TwoUnshieldsEachWithinSupply_TogetherBeyondIt_SecondIsRefused()
        {
            var a = Unshield(NewAddress(), 0.3M); a.Height = Gate;
            var b = Unshield(NewAddress(), 0.3M); b.Height = Gate;
            var state = new SameBlockDebitGuard.State(SameBlockDebitGuard.CommittedBalance) { BlockHeight = Gate };
            var (ok1, _) = SameBlockDebitGuard.TryRegister(a, state);
            Assert.True(ok1);
            var (ok2, reason) = SameBlockDebitGuard.TryRegister(b, state);
            Assert.False(ok2);
            Assert.StartsWith(SameBlockDebitGuard.ReasonPrefix, reason);
            Assert.Contains("VFX", reason);
        }

        [Fact]
        public void SameBlock_PoolDebitIsNotCountedBelowGate()
        {
            var a = Unshield(NewAddress(), 0.3M); a.Height = Gate - 1;
            Assert.DoesNotContain(SameBlockDebitGuard.GetDebits(a), d => d.Key.Kind == SameBlockDebitGuard.LedgerKind.ShieldedPool);
            a.Height = Gate;
            var debit = Assert.Single(SameBlockDebitGuard.GetDebits(a));
            Assert.Equal(SameBlockDebitGuard.LedgerKind.ShieldedPool, debit.Key.Kind);
            Assert.Equal("VFX", debit.Key.ContractUid);
            Assert.Equal(0.3M + Globals.PrivateTxFixedFee, debit.Amount);
        }

        // ── Pure rule unit checks ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void PoolDebit_PerType()
        {
            var payload = new PrivateTxPayload { Asset = "VFX", Fee = 0.000003M, VbtcTransparentAmount = 0.02M };
            Assert.Equal(1.000003M, PrivateTxSupplyRules.PoolDebit(new Transaction { TransactionType = TransactionType.VFX_UNSHIELD, Amount = 1M }, payload));
            Assert.Equal(0.000003M, PrivateTxSupplyRules.PoolDebit(new Transaction { TransactionType = TransactionType.VFX_PRIVATE_TRANSFER }, payload));
            Assert.Equal(0.02M, PrivateTxSupplyRules.PoolDebit(new Transaction { TransactionType = TransactionType.VBTC_V2_UNSHIELD }, payload));
            Assert.Equal(0M, PrivateTxSupplyRules.PoolDebit(new Transaction { TransactionType = TransactionType.VFX_SHIELD, Amount = 1M }, payload));
        }

        [Fact]
        public void AppliesTo_OnlyZkTypes_AtOrAboveGate()
        {
            Assert.True(PrivateTxSupplyRules.AppliesTo(new Transaction { TransactionType = TransactionType.VFX_UNSHIELD }, Gate));
            Assert.False(PrivateTxSupplyRules.AppliesTo(new Transaction { TransactionType = TransactionType.VFX_UNSHIELD }, Gate - 1));
            Assert.False(PrivateTxSupplyRules.AppliesTo(new Transaction { TransactionType = TransactionType.VFX_SHIELD }, Gate));
            Assert.False(PrivateTxSupplyRules.AppliesTo(new Transaction { TransactionType = TransactionType.TX }, Gate));
        }
    }
}
