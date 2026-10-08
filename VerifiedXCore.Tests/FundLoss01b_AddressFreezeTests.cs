using System;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1 (freeze): from Globals.AddressFreezeHeight nothing is sent from an address in
    /// Globals.FrozenAddresses. Below the height (and for every other sender) validation is unchanged.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class FundLoss01b_AddressFreezeTests : IDisposable
    {
        private const long Gate = 1000;
        private const string Frozen = "RB3eeBH258arkuePCJf5NMVMSaUyhyy9g6";
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;

        public FundLoss01b_AddressFreezeTests()
        {
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.AddressFreezeHeight;
            Globals.AddressFreezeHeight = Gate;
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            Globals.AddressFreezeHeight = _priorGate;
        }

        private static Transaction Send(string from, TransactionType type = TransactionType.TX) => new Transaction
        {
            TransactionType = type,
            FromAddress = from,
            ToAddress = "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH",
            Amount = 1M,
            Fee = 0.00000100M,
            Nonce = 0,
            Timestamp = Utilities.TimeUtil.GetTime(),
            Hash = Guid.NewGuid().ToString("N"),
        };

        /// <summary>
        /// A shield whose oversized Data makes VerifyPrivateTX fail deterministically at its first check, which comes
        /// AFTER the freeze rule: proves a transaction got past (or was stopped by) the freeze without touching any
        /// database (same device as VbtcPrivacyDisableGateTests).
        /// </summary>
        private static Transaction DbFreeProbe(string from) => new Transaction
        {
            TransactionType = TransactionType.VFX_SHIELD,
            FromAddress = from,
            ToAddress = "Shielded_Pool",
            Hash = Guid.NewGuid().ToString("N"),
            Data = new string('A', Globals.MaxPrivateTxDataSize + 1),
        };

        [Fact]
        public void TheMainnetAddressIsListed()
        {
            Assert.Contains(Frozen, Globals.FrozenAddresses);
        }

        [Theory]
        [InlineData(TransactionType.TX)]
        [InlineData(TransactionType.NFT_TX)]
        [InlineData(TransactionType.NFT_SALE)]
        [InlineData(TransactionType.VBTC_V2_TRANSFER)]
        public async Task FrozenSender_IsRefusedAtGate_WhateverTheType(TransactionType type)
        {
            Globals.LastBlock = new Block { Height = Gate - 1 }; // next block = Gate
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Send(Frozen, type));
            Assert.False(ok);
            Assert.Equal($"Transactions from {Frozen} are frozen.", msg);
        }

        [Fact]
        public async Task FrozenSender_IsNotRefusedByTheFreeze_BelowGate()
        {
            Globals.LastBlock = new Block { Height = Gate - 2 }; // next block = Gate - 1
            var (ok, msg) = await TransactionValidatorService.VerifyTX(DbFreeProbe(Frozen));
            Assert.False(ok);
            Assert.DoesNotContain("frozen", msg);
            Assert.Contains("MaxPrivateTxDataSize", msg); // got past the freeze, stopped by the probe's own defect
        }

        [Fact]
        public async Task BlockPath_JudgesAtTheBlocksHeight()
        {
            Globals.LastBlock = new Block { Height = Gate + 5000 };
            var (_, historical) = await TransactionValidatorService.VerifyTX(DbFreeProbe(Frozen), blockDownloads: true, blockVerify: true, blockHeight: Gate - 1);
            Assert.DoesNotContain("frozen", historical);
            Assert.Contains("MaxPrivateTxDataSize", historical);

            Globals.LastBlock = new Block { Height = 10 };
            var (ok, atGate) = await TransactionValidatorService.VerifyTX(DbFreeProbe(Frozen), blockVerify: true, blockHeight: Gate);
            Assert.False(ok);
            Assert.Contains("frozen", atGate);
        }

        [Fact]
        public async Task OtherSenders_AreUnaffected()
        {
            Globals.LastBlock = new Block { Height = Gate + 1 };
            var (_, msg) = await TransactionValidatorService.VerifyTX(DbFreeProbe("RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH"));
            Assert.DoesNotContain("frozen", msg);
            Assert.Contains("MaxPrivateTxDataSize", msg);
        }

        [Fact]
        public void Rule_IsPure()
        {
            Assert.Null(LedgerIntegrityRules.FrozenSender(new Transaction { FromAddress = Frozen }, Gate - 1));
            Assert.NotNull(LedgerIntegrityRules.FrozenSender(new Transaction { FromAddress = Frozen }, Gate));
            Assert.Null(LedgerIntegrityRules.FrozenSender(new Transaction { FromAddress = "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH" }, Gate));
            Assert.Null(LedgerIntegrityRules.FrozenSender(new Transaction { FromAddress = Frozen.ToLowerInvariant() }, Gate)); // exact match only, as the ledger keys addresses
        }
    }
}
