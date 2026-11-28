using System;
using System.Linq;
using System.Threading.Tasks;
using Nethereum.Util;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A Base provider is read only while it serves this network's Base chain, by its own eth_chainId: asked when the
    /// node starts and before a provider is read, a provider on another chain is left out, and is asked again later
    /// rather than left out for good. Driven through the node's own readers (the casters' burn watch, the operator's
    /// signing of a contract change) against providers on loopback.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseProviderChainTests : BaseProviderTestBase
    {
        // ── When the node starts ─────────────────────────────────────────────────────────

        [Fact]
        public async Task AtStart_EachProviderIsAskedItsChainId_AndOneOnAnotherChainIsNotReadAfterwards()
        {
            var onBase = Provider();
            var elsewhere = Provider(OtherChain);
            var down = Provider();
            down.Down = true;
            Configure(elsewhere, down, onBase);

            var checks = await BaseRpc.CheckProvidersAtStartAsync();

            Assert.Equal(new[] { BaseProviderCheck.OtherChain, BaseProviderCheck.NoAnswer, BaseProviderCheck.OnBase }, checks.Select(c => c.Check));
            Assert.Equal(1, elsewhere.CallsTo("eth_chainId"));

            // The first read after it passes the provider on another chain over without asking it anything.
            var burn = NewBurn();
            elsewhere.AddBurn(burn, Contract, vfxExit: true, 500_000, "xDestination");
            var (ok, _, reason) = await BaseBridgeService.TryGetBurnEventAsync(burn, vfxExit: true);
            Assert.False(ok);
            Assert.True(BaseBridgeService.IsNotYet(reason), reason);
            Assert.Equal(0, elsewhere.CallsTo("eth_getTransactionReceipt"));
            Assert.Equal(1, elsewhere.CallsTo("eth_chainId"));
            Assert.Equal(1, onBase.CallsTo("eth_getTransactionReceipt"));
        }

        // ── The casters' burn watch ──────────────────────────────────────────────────────

        [Fact]
        public async Task TheBurnWatch_DoesNotReadAProviderOnAnotherChain_AndReadsItAgainOnceItServesBase()
        {
            var provider = Provider(OtherChain);
            var other = Provider();
            new[] { provider, other }.At(5_000);
            Configure(provider, other);

            // The poll the loop runs: the provider is asked its chain id and nothing else, and the cursor does not move
            // (one provider on Base is not enough to read from).
            await Assert.ThrowsAsync<BaseProviderException>(() => BaseBridgeExitWatchService.ReadUntilCaughtUpAsync());
            Assert.Equal(1, provider.CallsTo("eth_chainId"));
            Assert.Equal(0, provider.FinalizedAsked);
            Assert.Equal(0, provider.CallsTo("eth_getLogs"));
            Assert.Equal(0, other.CallsTo("eth_getLogs"));
            Assert.Equal(0, BridgeExitSyncState.GetOrCreate().LastScannedBlock);

            // It serves Base again. Inside the set-aside time it is not asked; after it, it is asked and read.
            provider.ChainId = null;
            Now += BaseRpc.SetAsideSeconds - 1;
            await Assert.ThrowsAsync<BaseProviderException>(() => BaseBridgeExitWatchService.ReadUntilCaughtUpAsync());
            Assert.Equal(1, provider.CallsTo("eth_chainId"));
            Assert.Equal(0, provider.FinalizedAsked);

            Now += 2;
            await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
            Assert.Equal(2, provider.CallsTo("eth_chainId"));
            Assert.Equal(1, provider.FinalizedAsked);
            Assert.Equal(4_999, BridgeExitSyncState.GetOrCreate().LastScannedBlock); // the cursor's first position, one below the head
        }

        [Fact]
        public async Task AProviderFoundOnBase_IsAskedItsChainIdAgainLater_AndLeftOutIfItChanged()
        {
            var all = Providers(2);
            all.At(5_000);
            var provider = all[0];

            await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
            await BaseBridgeExitWatchService.ReadUntilCaughtUpAsync();
            Assert.Equal(1, provider.CallsTo("eth_chainId")); // kept between reads
            Assert.Equal(2, provider.FinalizedAsked);

            provider.ChainId = OtherChain;
            Now += BaseRpc.VerifiedForSeconds + 1;
            await Assert.ThrowsAsync<BaseProviderException>(() => BaseBridgeExitWatchService.ReadUntilCaughtUpAsync());
            Assert.Equal(2, provider.CallsTo("eth_chainId"));
            Assert.Equal(2, provider.FinalizedAsked);
        }

        /// <summary>
        /// A block holding a burn log that names another chain was not read from Base by the provider that served it. While
        /// that leaves too few providers that read the block, the block waits, however many polls find it so, and is never
        /// passed over the way a block the providers cannot serve is. That holds when every provider serves the log, and
        /// when one does and too few others are left. A burn of Base's own in the same block is taken, at once from a
        /// provider that is right, and the block is passed once the providers are set right.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TheBurnWatch_NeverPassesABlockWhoseBurnLogNamesAnotherChain_HoweverOftenItIsRead(bool everyProviderServesIt)
        {
            var all = Providers(2);
            all.At(5_000);
            foreach (var p in all) p.LogRangeCap = 1; // the providers read one block at a time, so the watch narrows to single blocks
            var state = BridgeExitSyncState.GetOrCreate();
            state.LastScannedBlock = 4_990;
            BridgeExitSyncState.Save(state);
            // Block 4,995 holds a burn of Base's and a burn log that names another chain.
            var real = NewBurn();
            var foreign = NewBurn();
            all.AddBurn(real, Contract, vfxExit: true, 300_000, "xDestination", blockNumber: 4_995);
            all.Take(everyProviderServesIt ? 2 : 1).AddBurn(foreign, Contract, vfxExit: false, 400_000, "bc1qdestination", chainId: OtherChain, blockNumber: 4_995);

            // Polled far past the count at which a block that cannot be read is passed over (the providers read the block
            // after it without trouble).
            var taken = 0;
            for (var i = 0; i < 5 * BaseBridgeExitWatchService.SingleBlockFailuresBeforeSkip; i++)
                taken += (await BaseBridgeExitWatchService.PollOnce()).Processed;

            Assert.Equal(1, BaseBridgeExitWatchService.CurrentSpan);                          // narrowed to the one block
            Assert.Equal(4_994, BridgeExitSyncState.GetOrCreate().LastScannedBlock);          // which waits; the blocks before it were read
            if (everyProviderServesIt) Assert.Equal(0, taken);
            else Assert.True(taken > 0, "the burn the other provider serves is taken while the block waits for a second read");
            Assert.True(all[0].CallsTo("eth_getLogs") > 2 * BaseBridgeExitWatchService.SingleBlockFailuresBeforeSkip);

            // The providers are set right: the block is read by both and passed, with the burn in it.
            foreach (var p in all)
                lock (p.Logs) p.Logs.RemoveAll(l => l["transactionHash"]!.ToString() == foreign);
            var takenAfter = 0;
            for (var i = 0; i < 12 && BridgeExitSyncState.GetOrCreate().LastScannedBlock < 5_000; i++)
                takenAfter += (await BaseBridgeExitWatchService.PollOnce()).Processed;
            Assert.Equal(5_000, BridgeExitSyncState.GetOrCreate().LastScannedBlock);
            Assert.Equal(1, takenAfter);
        }

        // ── A burn's receipt ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task ABurnWhoseEventNamesAnotherChain_IsNotTakenFromThatProvider()
        {
            var all = Providers(3);
            var burn = NewBurn();
            all[0].AddBurn(burn, Contract, vfxExit: true, 500_000, "xDestination", chainId: OtherChain);

            var (ok, info, reason) = await BaseBridgeService.TryGetBurnEventAsync(burn, vfxExit: true);
            Assert.False(ok);
            Assert.Null(info);
            // No answer about the burn, as when Base could not be read: it is asked again, not failed.
            Assert.True(BurnExitConsensusService.BaseCouldNotBeRead(reason), reason);

            // The other providers' receipts, which name Base's chain, are the ones taken.
            all.Skip(1).AddBurn(burn, Contract, vfxExit: true, 500_000, "xDestination");
            (ok, info, reason) = await BaseBridgeService.TryGetBurnEventAsync(burn, vfxExit: true);
            Assert.True(ok, reason);
            Assert.Equal(500_000, info!.AmountSats);
            Assert.Equal("xDestination", info.Destination);
        }

        // ── The contract's views and code, as the operator's signing reads them ────────────

        [Fact]
        public async Task TheOperatorsSigning_ReadsTheContractFromProvidersOnBase_NotFromOneOnAnotherChain()
        {
            var myBase = ThisNodeIsAValidator();
            const string implementation = "0xd2bB4BdA89A23c63eaaD464F15658845a2114Ffa";

            // A provider on another chain serves a contract at admin nonce 99 and other code; those on Base, nonce 5.
            var elsewhere = ContractProvider(OtherChain, myBase, nonce: 99, codeHex: "0xdeadbeef");
            var onBase = ContractProvider(null, myBase, nonce: 5, codeHex: "0x6080604052");
            var alsoOnBase = ContractProvider(null, myBase, nonce: 5, codeHex: "0x6080604052");

            Configure(elsewhere);
            var alone = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
            Assert.False(alone.Success);
            Assert.Equal("Could not read the Base contract", alone.Error);
            Assert.Equal(0, elsewhere.CallsTo("eth_call"));

            Configure(elsewhere, onBase, alsoOnBase);
            var cancel = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.CancelPending });
            Assert.True(cancel.Success, cancel.Error);
            Assert.Equal("5", cancel.AdminNonce);
            Assert.Equal(4, cancel.MessageVersion);

            var upgrade = await BaseAdminSigningService.SignAsync(new BaseAdminSigningService.Request { Action = BaseAdminSigningService.ProposeUpgrade, Implementation = implementation });
            Assert.True(upgrade.Success, upgrade.Error);
            Assert.Equal("0x" + Convert.ToHexString(Sha3Keccack.Current.CalculateHash(Convert.FromHexString("6080604052"))).ToLowerInvariant(), upgrade.ImplementationCodeHash);
            Assert.Equal(0, elsewhere.CallsTo("eth_call"));
            Assert.Equal(0, elsewhere.CallsTo("eth_getCode"));
            Assert.True(onBase.CallsTo("eth_call") >= 6);
            Assert.Equal(1, onBase.CallsTo("eth_getCode"));
        }
    }
}
