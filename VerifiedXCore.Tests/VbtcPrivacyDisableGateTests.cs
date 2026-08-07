using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Consensus gate that temporarily disables vBTC privacy TXs from
    /// <see cref="Globals.VbtcPrivacyDisableHeight"/> onward. Deterministic under replay:
    /// block validation gates on the block's own height (blockHeight param); mempool
    /// admission gates on the height the tx would mine into (tip + 1).
    /// Mutates Globals.LastBlock, so serialized via the GlobalCasterState collection.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class VbtcPrivacyDisableGateTests : IDisposable
    {
        private const string DisabledMessage = "vBTC privacy transactions are temporarily disabled.";

        private readonly Block _originalLastBlock;
        private readonly long _originalDisableHeight;

        public VbtcPrivacyDisableGateTests()
        {
            _originalLastBlock = Globals.LastBlock;
            _originalDisableHeight = Globals.VbtcPrivacyDisableHeight;
        }

        public void Dispose()
        {
            Globals.LastBlock = _originalLastBlock;
            Globals.VbtcPrivacyDisableHeight = _originalDisableHeight;
        }

        /// <summary>Oversized Data makes VerifyPrivateTX fail deterministically at its first check,
        /// proving the TX passed (or was stopped by) the disable gate without touching DB state.</summary>
        private static Transaction MakeTx(TransactionType type) => new Transaction
        {
            TransactionType = type,
            FromAddress = "someAddress",
            ToAddress = "Shielded_Pool",
            Hash = "vbtc_privacy_gate_test_" + type,
            Data = new string('A', Globals.MaxPrivateTxDataSize + 1)
        };

        [Theory]
        [InlineData(TransactionType.VBTC_V2_SHIELD)]
        [InlineData(TransactionType.VBTC_V2_UNSHIELD)]
        [InlineData(TransactionType.VBTC_V2_PRIVATE_TRANSFER)]
        public async Task VerifyTX_RejectsVbtcPrivacyTypes_WhenGateActive(TransactionType type)
        {
            Globals.VbtcPrivacyDisableHeight = 1000;
            Globals.LastBlock = new Block { Height = 1001 };

            var (ok, message) = await TransactionValidatorService.VerifyTX(MakeTx(type));

            Assert.False(ok);
            Assert.Equal(DisabledMessage, message);
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_SHIELD)]
        [InlineData(TransactionType.VBTC_V2_UNSHIELD)]
        [InlineData(TransactionType.VBTC_V2_PRIVATE_TRANSFER)]
        public async Task VerifyTX_AllowsVbtcPrivacyTypes_BelowGateBoundary(TransactionType type)
        {
            Globals.VbtcPrivacyDisableHeight = 1000;
            Globals.LastBlock = new Block { Height = 998 }; // next block = 999 < gate

            var (ok, message) = await TransactionValidatorService.VerifyTX(MakeTx(type));

            Assert.False(ok);
            Assert.NotEqual(DisabledMessage, message);
            Assert.Contains("MaxPrivateTxDataSize", message);
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_SHIELD)]
        [InlineData(TransactionType.VBTC_V2_UNSHIELD)]
        [InlineData(TransactionType.VBTC_V2_PRIVATE_TRANSFER)]
        public async Task VerifyTX_MempoolRejects_WhenNextBlockWouldCrossGate(TransactionType type)
        {
            Globals.VbtcPrivacyDisableHeight = 1000;
            Globals.LastBlock = new Block { Height = 999 }; // next block = 1000 >= gate

            var (ok, message) = await TransactionValidatorService.VerifyTX(MakeTx(type));

            Assert.False(ok);
            Assert.Equal(DisabledMessage, message);
        }

        /// <summary>Replay determinism: a historical block below the gate must revalidate the
        /// same way regardless of the live chain tip.</summary>
        [Theory]
        [InlineData(TransactionType.VBTC_V2_SHIELD)]
        [InlineData(TransactionType.VBTC_V2_UNSHIELD)]
        [InlineData(TransactionType.VBTC_V2_PRIVATE_TRANSFER)]
        public async Task VerifyTX_BlockPath_UsesBlockHeightNotTip_AllowsHistorical(TransactionType type)
        {
            Globals.VbtcPrivacyDisableHeight = 1000;
            Globals.LastBlock = new Block { Height = 5000 }; // tip far past the gate

            var (ok, message) = await TransactionValidatorService.VerifyTX(MakeTx(type), blockHeight: 500);

            Assert.False(ok);
            Assert.NotEqual(DisabledMessage, message);
            Assert.Contains("MaxPrivateTxDataSize", message);
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_SHIELD)]
        [InlineData(TransactionType.VBTC_V2_UNSHIELD)]
        [InlineData(TransactionType.VBTC_V2_PRIVATE_TRANSFER)]
        public async Task VerifyTX_BlockPath_RejectsAtOrAboveGate_EvenWhenTipBelow(TransactionType type)
        {
            Globals.VbtcPrivacyDisableHeight = 1000;
            Globals.LastBlock = new Block { Height = 10 }; // tip below the gate

            var (ok, message) = await TransactionValidatorService.VerifyTX(MakeTx(type), blockHeight: 1000);

            Assert.False(ok);
            Assert.Equal(DisabledMessage, message);
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_SHIELD)]
        [InlineData(TransactionType.VBTC_V2_UNSHIELD)]
        [InlineData(TransactionType.VBTC_V2_PRIVATE_TRANSFER)]
        public async Task VerifyTX_AllowsVbtcPrivacyTypes_WhenGateAtSentinel(TransactionType type)
        {
            Globals.VbtcPrivacyDisableHeight = 999_999_999_999L;
            Globals.LastBlock = new Block { Height = 5_000_000 };

            var (ok, message) = await TransactionValidatorService.VerifyTX(MakeTx(type));

            Assert.False(ok);
            Assert.NotEqual(DisabledMessage, message);
        }

        [Theory]
        [InlineData(TransactionType.VFX_SHIELD)]
        [InlineData(TransactionType.VFX_UNSHIELD)]
        [InlineData(TransactionType.VFX_PRIVATE_TRANSFER)]
        public async Task VerifyTX_VfxPrivacyTypes_UnaffectedByGate(TransactionType type)
        {
            Globals.VbtcPrivacyDisableHeight = 1000;
            Globals.LastBlock = new Block { Height = 1001 };

            var (ok, message) = await TransactionValidatorService.VerifyTX(MakeTx(type));

            Assert.False(ok);
            Assert.NotEqual(DisabledMessage, message);
            Assert.Contains("MaxPrivateTxDataSize", message);
        }

        [Fact]
        public void IsVbtcPrivateTransaction_MatchesOnlyVbtcPrivacyTypes()
        {
            Assert.True(PrivateTransactionTypes.IsVbtcPrivateTransaction(TransactionType.VBTC_V2_SHIELD));
            Assert.True(PrivateTransactionTypes.IsVbtcPrivateTransaction(TransactionType.VBTC_V2_UNSHIELD));
            Assert.True(PrivateTransactionTypes.IsVbtcPrivateTransaction(TransactionType.VBTC_V2_PRIVATE_TRANSFER));

            Assert.False(PrivateTransactionTypes.IsVbtcPrivateTransaction(TransactionType.VFX_SHIELD));
            Assert.False(PrivateTransactionTypes.IsVbtcPrivateTransaction(TransactionType.VFX_UNSHIELD));
            Assert.False(PrivateTransactionTypes.IsVbtcPrivateTransaction(TransactionType.VFX_PRIVATE_TRANSFER));
            Assert.False(PrivateTransactionTypes.IsVbtcPrivateTransaction(TransactionType.VBTC_V2_TRANSFER));
        }
    }
}
