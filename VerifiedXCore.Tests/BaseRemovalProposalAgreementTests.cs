using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The admin nonce a pending removal was proposed under decides what becomes of the operator's record of a signed
    /// removal (it is tied to the pending proposal, or dropped), and with that whether this node vetoes. It is read from
    /// the RemovalProposed event, and not on one provider's word: it is known once two providers have the same sighting
    /// of the event, the same block, the same place in it and the same nonce. Every provider here serves this network's
    /// Base chain; one makes an event up, or leaves the last one out. Until two agree the nonce is not known, and the
    /// record is neither tied nor dropped.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseRemovalProposalAgreementTests : BaseProviderTestBase
    {
        private const long Hours48 = 48 * 3600;
        private const long ProposedIn = 2_000_000;
        private const long Height = 14_400;
        private static readonly string[] Named = { "0x00000000000000000000000000000000000000a1" };

        private readonly Func<long> _priorClock = BaseRemovalProposal.Clock;
        private readonly List<FakeRemovalChain> _chains = new();
        private long _clock = 1_900_000_000;

        public BaseRemovalProposalAgreementTests()
        {
            BaseRemovalProposal.Clock = () => _clock;
            BaseRemovalProposal.ForgetForTests();
        }

        public override void Dispose()
        {
            foreach (var chain in _chains) chain.Dispose();
            BaseRemovalProposal.Clock = _priorClock;
            BaseRemovalProposal.ForgetForTests();
            base.Dispose();
        }

        /// <summary>Three providers of one chain, each by itself, configured in this order.</summary>
        private FakeRemovalChain[] ThreeProviders()
        {
            var all = Enumerable.Range(0, 3).Select(_ => new FakeRemovalChain(Contract, ProposedIn + 90_000)).ToArray();
            _chains.AddRange(all);
            Configure(all.Select(c => c.Rpc).ToArray());
            return all;
        }

        private static BaseBridgeService.PendingRemoval Pending() =>
            new(Named, Array.Empty<string>(), new BigInteger(FakeRemovalChain.TimeOf(ProposedIn) + Hours48), Height);

        private static BaseRemovalOverride? Record(byte[] digest) =>
            DbContext.DB_vBTC.GetCollection<BaseRemovalOverride>("rsrv_base_removal_override").FindById(Convert.ToHexString(digest));

        /// <summary>The operator signed this removal at admin nonce 7, naming its validator; the contract has moved on to nonce 8.</summary>
        private static byte[] OperatorSigned(BaseBridgeService.PendingRemoval pending)
        {
            var digest = VbtcbTypedData.ProposeRemovalDigest(pending.Validators, pending.Casters, Height, 7, BaseBridgeService.BaseChainId, Contract);
            BaseRemovalOverride.Save(Contract, digest, 7, Named, new BaseBridgeService.PendingRemoval(Array.Empty<string>(), Array.Empty<string>(), 0));
            return digest;
        }

        /// <summary>What the veto loop does with the record, as LiveTargetOf does it: by the nonce known for the pending removal.</summary>
        private static IReadOnlyCollection<string> NamedFor(BaseBridgeService.PendingRemoval pending) =>
            BaseRemovalOverride.For(Contract, BaseBridgeService.BaseChainId, pending, adminNonce: 8, BaseRemovalProposal.ProposedUnder(pending));

        /// <summary>
        /// One provider serves an event the contract never emitted: the pending removal's fields under another nonce,
        /// later in the block, so that it reads as the removal's own. On that provider's word the operator's record would
        /// be dropped. No other provider has the event: the nonce stays unknown and the record stays as it is.
        /// </summary>
        [Fact]
        public async Task AnEventOneProviderMakesUp_DoesNotSayWhichNonceTheRemovalWasProposedUnder()
        {
            var all = ThreeProviders();
            var pending = Pending();
            foreach (var chain in all) chain.Proposed(ProposedIn, pending.Eta, underNonce: 7, Named, Height, logIndex: 1);
            all[0].Proposed(ProposedIn, pending.Eta, underNonce: 9, Named, Height, logIndex: 4);   // made up
            var digest = OperatorSigned(pending);

            // Beside one other provider: two sightings, not the same one.
            all[2].Rpc.Down = true;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));
            NamedFor(pending);
            Assert.NotNull(Record(digest));            // not dropped on the made-up nonce
            Assert.Null(Record(digest)!.BoundTo);      // and not tied to the proposal either

            // Asked again at once, neither provider is read again, and nothing changes.
            foreach (var chain in all) chain.Rpc.ForgetCalls();
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));
            Assert.Equal(0, all[0].Rpc.TotalCalls + all[1].Rpc.TotalCalls);

            // A second provider that has the event the contract emitted: that is the nonce, and the record is tied to it.
            all[2].Rpc.Down = false;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending));
            Assert.Equal(Named, NamedFor(pending));
            Assert.Equal(BaseRemovalProposal.IdentityOf(Contract, pending), Record(digest)!.BoundTo);
        }

        /// <summary>
        /// The removal was proposed, cancelled and proposed again in one block: two events carry what the contract shows,
        /// and the pending one's is the later. One provider leaves the later one out, so its search ends at the earlier
        /// event, which the others have too. That is not the same sighting as theirs: the earlier nonce is not taken.
        /// </summary>
        [Fact]
        public async Task AProviderThatLeavesTheLastEventOut_DoesNotMakeTheEarlierOneThePendingRemovals()
        {
            var all = ThreeProviders();
            var pending = Pending();
            foreach (var chain in all) chain.Proposed(ProposedIn, pending.Eta, underNonce: 5, Named, Height, logIndex: 1);
            foreach (var chain in all.Skip(1)) chain.Proposed(ProposedIn, pending.Eta, underNonce: 7, Named, Height, logIndex: 4);   // the first provider leaves this one out
            var digest = OperatorSigned(pending);

            all[2].Rpc.Down = true;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));   // one says nonce 5, one says nonce 7
            NamedFor(pending);
            Assert.NotNull(Record(digest));
            Assert.Null(Record(digest)!.BoundTo);

            all[2].Rpc.Down = false;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending));
        }

        /// <summary>
        /// A sighting no other provider came to share does not stand for good: after an hour that provider searches
        /// afresh, and what two providers then have alike is the answer.
        /// </summary>
        [Fact]
        public async Task ASightingNoOtherProviderShares_IsLookedForAgainLater()
        {
            var all = ThreeProviders();
            var pending = Pending();
            all[0].Proposed(ProposedIn, pending.Eta, underNonce: 5, Named, Height, logIndex: 1);
            all[1].Rpc.Down = all[2].Rpc.Down = true;

            // Only the first provider answers: it has the removal's event under nonce 5.
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));

            // The removal was replaced in that block (nonce 7). The second provider answers now and has both events; the
            // first is not asked again yet, and its sighting is still the earlier event: not the same one.
            all[1].Proposed(ProposedIn, pending.Eta, underNonce: 5, Named, Height, logIndex: 1);
            foreach (var chain in all.Take(2)) chain.Proposed(ProposedIn, pending.Eta, underNonce: 7, Named, Height, logIndex: 4);
            all[1].Rpc.Down = false;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));

            // An hour on, both search afresh and have the same event.
            _clock += BaseRemovalProposal.ReadAgainSeconds + 1;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending));
        }

        /// <summary>The same provider written into two settings is one provider: its sighting does not agree with itself.</summary>
        [Fact]
        public async Task OneProviderConfiguredTwice_IsOneSighting()
        {
            var chain = new FakeRemovalChain(Contract, ProposedIn + 90_000);
            _chains.Add(chain);
            var pending = Pending();
            chain.Proposed(ProposedIn, pending.Eta, underNonce: 7, Named, Height);
            BaseBridgeService.BaseRpcUrl = chain.Rpc.Url;
            BaseBridgeService.BaseRpcUrl2 = chain.Rpc.Url + "?key=another";
            BaseBridgeService.BaseRpcUrl3 = "";

            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));
            Assert.Equal(1, chain.LogReads);
        }
    }
}
