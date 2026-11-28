using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethereum.ABI;
using Nethereum.Util;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A Base provider on loopback (<see cref="FakeBaseRpc"/>) that serves what the search for a removal's
    /// RemovalProposed event reads: a chain that makes a block every two seconds from 1,700,000,000 on, up to its head; a
    /// block by number or tag with its time; upgradeDelay() as <see cref="Delay"/>; and logs by range, refusing a range
    /// wider than <see cref="RangeCap"/>. It keeps every range of logs it was asked for.
    /// </summary>
    internal sealed class FakeRemovalChain : IDisposable
    {
        public const long Genesis = 1_700_000_000, BlockSeconds = 2;
        private static readonly string EventTopic = "0x" + Sha3Keccack.Current.CalculateHash("RemovalProposed(address[],address[],uint256,uint256,uint256)");

        private readonly string _contract;
        public readonly FakeBaseRpc Rpc = new();
        public long Delay = 48 * 3600;
        public int? RangeCap;
        public readonly List<(long From, long To)> LogRanges = new();
        public readonly List<(long From, long To)> Refused = new();

        public static long TimeOf(long block) => Genesis + block * BlockSeconds;

        public FakeRemovalChain(string contract, long head)
        {
            _contract = contract;
            Rpc.Head = head;
            Rpc.On = (method, args) =>
            {
                switch (method)
                {
                    case "eth_getBlockByNumber":
                        var tag = args[0]!.ToString();
                        var number = tag is "latest" or "finalized" ? Rpc.Head : Convert.ToInt64(tag, 16);
                        if (number > Rpc.Head) return JValue.CreateNull();
                        return new JObject
                        {
                            ["number"] = FakeBaseRpc.Hex(number), ["hash"] = FakeBaseRpc.BlockHash("block" + number),
                            ["parentHash"] = FakeBaseRpc.BlockHash("block" + (number - 1)), ["timestamp"] = FakeBaseRpc.Hex(TimeOf(number)), ["transactions"] = new JArray(),
                        };
                    case "eth_call":
                        return "0x" + new BigInteger(Delay).ToString("x").PadLeft(64, '0');          // upgradeDelay(): the only call the search makes
                    case "eth_getLogs":
                        var filter = (JObject)args[0]!;
                        var (from, to) = (Convert.ToInt64(filter["fromBlock"]!.ToString(), 16), Convert.ToInt64(filter["toBlock"]!.ToString(), 16));
                        if (RangeCap is { } cap && to - from + 1 > cap)
                        {
                            lock (Refused) Refused.Add((from, to));
                            throw new InvalidOperationException($"eth_getLogs is limited to a {cap} range");
                        }
                        lock (LogRanges) LogRanges.Add((from, to));
                        return null;                                                                 // served from Logs
                    default:
                        return null;
                }
            };
        }

        public void Dispose() => Rpc.Dispose();

        /// <summary>A proposal's event, as the contract emits it, in <paramref name="block"/> at <paramref name="logIndex"/>.</summary>
        public void Proposed(long block, BigInteger eta, int underNonce, IEnumerable<string> validators, long vfxBlockHeight, int logIndex = 0, IEnumerable<string>? casters = null)
        {
            var data = new ABIEncode().GetABIEncoded(
                new ABIValue("address[]", validators.ToList()),
                new ABIValue("address[]", (casters ?? Array.Empty<string>()).ToList()),
                new ABIValue("uint256", new BigInteger(vfxBlockHeight)),
                new ABIValue("uint256", eta),
                new ABIValue("uint256", new BigInteger(underNonce)));
            lock (Rpc.Logs)
                Rpc.Logs.Add(new JObject
                {
                    ["address"] = _contract, ["topics"] = new JArray(EventTopic), ["data"] = "0x" + Convert.ToHexString(data).ToLowerInvariant(),
                    ["blockNumber"] = FakeBaseRpc.Hex(block), ["blockHash"] = FakeBaseRpc.BlockHash("block" + block),
                    ["transactionHash"] = "0x" + logIndex.ToString("x").PadLeft(64, '7'), ["transactionIndex"] = FakeBaseRpc.Hex(logIndex), ["logIndex"] = FakeBaseRpc.Hex(logIndex), ["removed"] = false,
                });
        }

        public int LogReads { get { lock (LogRanges) return LogRanges.Count; } }
    }

    /// <summary>
    /// Which admin nonce the contract's pending removal was proposed under is read from its RemovalProposed event
    /// (BaseRemovalProposal), through the node's own Base layer against providers on loopback. The event is the one that
    /// carries what the contract shows of the pending removal (eta, VFX block height, validators, casters), the last of
    /// them when several do. It is found at the block the delay now in force points to, and when the delay was changed
    /// since, in the window every delay the contract allows leaves (eta - 30 days to eta - 48 hours), read a provider's
    /// range of blocks at a time, a bounded number of reads an attempt, going on in the next attempt from where the last
    /// one stopped.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseRemovalProposalReadTests : IDisposable
    {
        private const string Contract = "0x3669EE313f9Aa14b9eBb787DfdB0E97Ca67A0510";
        private const long BlockSeconds = FakeRemovalChain.BlockSeconds;
        private const long Hours48 = 48 * 3600, Day = 24 * 3600;
        private const long ProposedIn = 2_000_000;                                  // the proposing block
        private const long Height = 14_400;                                         // the VFX block height the removal was proposed with
        private static readonly string[] Named = { "0x00000000000000000000000000000000000000a1" };
        private static readonly string[] Others = { "0x00000000000000000000000000000000000000a1", "0x00000000000000000000000000000000000000a2" };

        private readonly string _priorRpc = BaseBridgeService.BaseRpcUrl;
        private readonly string _priorRpc2 = BaseBridgeService.BaseRpcUrl2;
        private readonly string _priorRpc3 = BaseBridgeService.BaseRpcUrl3;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly Func<long> _priorClock = BaseRemovalProposal.Clock;
        private readonly List<FakeRemovalChain> _providers = new();

        public BaseRemovalProposalReadTests()
        {
            BaseBridgeService.ContractAddress = Contract;
            BaseBridgeService.BaseRpcUrl = BaseBridgeService.BaseRpcUrl2 = BaseBridgeService.BaseRpcUrl3 = "";
            BaseRpc.ForgetChainChecksForTests();
            BaseRemovalProposal.ForgetForTests();
        }

        public void Dispose()
        {
            foreach (var p in _providers) p.Dispose();
            BaseRemovalProposal.Clock = _priorClock;
            BaseRemovalProposal.ForgetForTests();
            BaseRpc.ForgetChainChecksForTests();
            BaseBridgeService.BaseRpcUrl = _priorRpc;
            BaseBridgeService.BaseRpcUrl2 = _priorRpc2;
            BaseBridgeService.BaseRpcUrl3 = _priorRpc3;
            BaseBridgeService.ContractAddress = _priorContract;
        }

        private static long TimeOf(long block) => FakeRemovalChain.TimeOf(block);

        private FakeRemovalChain NewProvider(long head)
        {
            var provider = new FakeRemovalChain(Contract, head);
            _providers.Add(provider);
            if (_providers.Count == 1) BaseBridgeService.BaseRpcUrl = provider.Rpc.Url;
            else if (_providers.Count == 2) BaseBridgeService.BaseRpcUrl2 = provider.Rpc.Url;
            else BaseBridgeService.BaseRpcUrl3 = provider.Rpc.Url;
            return provider;
        }

        /// <summary>The pending removal as the contract shows it: proposed in the proposing block while the delay was <paramref name="delayThen"/>.</summary>
        private static BaseBridgeService.PendingRemoval Pending(long delayThen, string[]? validators = null, long height = Height) =>
            new(validators ?? Named, Array.Empty<string>(), new BigInteger(TimeOf(ProposedIn) + delayThen), height);

        [Theory]
        [InlineData(7)]      // the nonce the operator's signature was good at
        [InlineData(9)]      // another: the read says what Base says, and the signed removal is told from this one by it
        public async Task TheEvent_IsFoundAtTheBlockTheDelayPointsTo_AndGivesTheNonceTheRemovalWasProposedUnder(int underNonce)
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var pending = Pending(Hours48);
            provider.Proposed(ProposedIn, pending.Eta, underNonce, Named, Height);

            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));                     // nothing is read until an attempt is made
            Assert.Equal(0, provider.Rpc.TotalCalls);
            await BaseRemovalProposal.SearchAsync(pending);

            Assert.Equal(new BigInteger(underNonce), BaseRemovalProposal.ProposedUnder(pending));
            Assert.Equal((ProposedIn, ProposedIn + BaseRemovalProposal.LikelyBlocks - 1), Assert.Single(provider.LogRanges));   // one read of logs, at the proposing block

            // Found once, it is kept: no further attempt reads Base.
            provider.Rpc.ForgetCalls();
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(0, provider.Rpc.TotalCalls);
        }

        /// <summary>
        /// A proposal, its cancellation and another proposal fit in one Base block, and both proposals then have the same
        /// eta. The pending one is the later: the event that carries what the contract shows of it, and of two that carry
        /// the same, the one later in the block.
        /// </summary>
        [Fact]
        public async Task OfTwoProposalsWithOneEta_ThePendingOnesEventIsRead_ByItsFieldsAndThenByItsPlaceInTheBlock()
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var eta = Pending(Hours48).Eta;
            provider.Proposed(ProposedIn, eta, underNonce: 70, Others, Height, logIndex: 0);     // cancelled in the same block (nonce 71)
            provider.Proposed(ProposedIn, eta, underNonce: 72, Named, Height, logIndex: 2);      // replaced it
            var pending = Pending(Hours48, Named);
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(new BigInteger(72), BaseRemovalProposal.ProposedUnder(pending));        // not the first event with that eta

            // The same again where the two are alike in every field: the later one in the block is the pending one.
            BaseRemovalProposal.ForgetForTests();
            lock (provider.Rpc.Logs) provider.Rpc.Logs.Clear();
            provider.Proposed(ProposedIn, eta, underNonce: 72, Named, Height, logIndex: 5);      // served out of order
            provider.Proposed(ProposedIn, eta, underNonce: 70, Named, Height, logIndex: 1);
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(new BigInteger(72), BaseRemovalProposal.ProposedUnder(pending));

            // And where the later one is in a later block of the same second's window: read above the likely place too.
            BaseRemovalProposal.ForgetForTests();
            lock (provider.Rpc.Logs) provider.Rpc.Logs.Clear();
            provider.Delay = 3 * Day;                                                            // the earlier was proposed under three days,
            var longer = Pending(3 * Day, Named);
            provider.Proposed(ProposedIn, longer.Eta, underNonce: 70, Named, Height);
            provider.Proposed(ProposedIn + (3 * Day - Hours48) / BlockSeconds, longer.Eta, underNonce: 74, Named, Height);   // the later under 48 hours: the same eta
            for (var attempt = 0; attempt < 20 && BaseRemovalProposal.ProposedUnder(longer) == null; attempt++)
                await BaseRemovalProposal.SearchAsync(longer);
            Assert.Equal(new BigInteger(74), BaseRemovalProposal.ProposedUnder(longer));
        }

        /// <summary>
        /// What is found for one pending removal is kept for that removal only: another with the same eta (it replaced the
        /// first in the same block) is not given the first one's nonce, before its own event is looked for or after.
        /// </summary>
        [Fact]
        public async Task ANonceFoundForOneRemoval_IsNotUsedForAnotherWithTheSameEta()
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var first = Pending(Hours48, Others);
            provider.Proposed(ProposedIn, first.Eta, underNonce: 70, Others, Height, logIndex: 0);
            await BaseRemovalProposal.SearchAsync(first);
            Assert.Equal(new BigInteger(70), BaseRemovalProposal.ProposedUnder(first));

            // Replaced in that block by another removal, with the same eta.
            var second = Pending(Hours48, Named);
            Assert.Equal(first.Eta, second.Eta);
            provider.Proposed(ProposedIn, second.Eta, underNonce: 72, Named, Height, logIndex: 2);
            Assert.Null(BaseRemovalProposal.ProposedUnder(second));                      // not the first one's
            Assert.Null(BaseRemovalProposal.ProposedUnder(Pending(Hours48, Others, Height + 1)));   // nor for another height
            await BaseRemovalProposal.SearchAsync(second);
            Assert.Equal(new BigInteger(72), BaseRemovalProposal.ProposedUnder(second));
            Assert.Equal(new BigInteger(70), BaseRemovalProposal.ProposedUnder(first));
            Assert.Equal(1, BaseRemovalProposal.AttemptsFor(second));                    // and its search was its own
        }

        [Fact]
        public async Task WithNoSuchEvent_TheWholeWindowIsRead_ABoundedNumberOfReadsAnAttempt_AndThenLeftForAnHour()
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var pending = Pending(Hours48);                                              // and no event anywhere
            long now = 1_900_000_000;
            BaseRemovalProposal.Clock = () => now;

            var low = ProposedIn - (30 * Day - Hours48 + BaseRemovalProposal.MarginSeconds) / BlockSeconds;   // eta - 30 days - the margin
            var top = ProposedIn;                                                                                 // eta - 48 hours
            var attempts = 0;
            for (var before = -1; provider.LogReads != before; attempts++)
            {
                before = provider.LogReads;
                await BaseRemovalProposal.SearchAsync(pending);
                Assert.InRange(provider.LogReads - before, 0, BaseRemovalProposal.PagesPerAttempt);
                Assert.Null(BaseRemovalProposal.ProposedUnder(pending));
            }

            // The likely place, then the window from its newest block down to its oldest, each block once.
            var window = provider.LogRanges.Skip(1).ToList();
            Assert.Equal(top, window[0].To);
            Assert.Equal(low, window[^1].From);
            for (var i = 1; i < window.Count; i++) Assert.Equal(window[i - 1].From - 1, window[i].To);
            Assert.All(window, r => Assert.InRange(r.To - r.From + 1, 1, BaseRemovalProposal.MaxSpan));
            Assert.True(attempts > 2, $"{attempts} attempts");

            // Read whole, it is left alone, and read again an hour later (a provider may have lacked the logs).
            provider.Rpc.ForgetCalls();
            now += BaseRemovalProposal.ReadAgainSeconds - 1;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(0, provider.Rpc.TotalCalls);
            now += 2;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.True(provider.Rpc.CallsTo("eth_getLogs") > 0);
        }

        [Fact]
        public async Task AProviderThatIsDown_IsPassedOver_AndWithEveryProviderDownNothingIsKnownOrThrown()
        {
            var (first, second) = (NewProvider(head: ProposedIn + 90_000), NewProvider(head: ProposedIn + 90_000));
            var pending = Pending(Hours48);
            first.Proposed(ProposedIn, pending.Eta, 7, Named, Height);
            second.Proposed(ProposedIn, pending.Eta, 7, Named, Height);

            first.Rpc.Down = second.Rpc.Down = true;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));

            second.Rpc.Down = false;                                                     // the first is still down
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending));
            Assert.Equal(0, first.LogReads);
            Assert.Equal(1, second.LogReads);
        }

        [Fact]
        public async Task AfterTheDelayWasChanged_TheEventIsFoundInTheWindow_NotWhereTheNewDelayPoints()
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var pending = Pending(Hours48);                                              // proposed while the delay was 48 hours
            provider.Proposed(ProposedIn, pending.Eta, 7, Named, Height);
            provider.Delay = 7 * Day;                                                    // and raised to a week since

            await BaseRemovalProposal.SearchAsync(pending);

            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending));
            Assert.Equal(1, BaseRemovalProposal.AttemptsFor(pending));
            var likely = ProposedIn - (7 * Day - Hours48) / BlockSeconds;                // where a week's delay points: not there
            Assert.Equal((likely, likely + BaseRemovalProposal.LikelyBlocks - 1), provider.LogRanges[0]);
            Assert.Equal(2, provider.LogReads);                                          // then the window's newest blocks, which hold it
            Assert.InRange(ProposedIn, provider.LogRanges[1].From, provider.LogRanges[1].To);
        }

        /// <summary>
        /// The delay was four days when the removal was proposed and is 48 hours now, so the proposing block lies two days
        /// of blocks below the window's newest, and this provider answers 500 blocks of logs at a time. One attempt's reads
        /// do not reach it: the next attempts go on from where the last one stopped, read no block of the window twice, and
        /// find it.
        /// </summary>
        [Fact]
        public async Task AChangedDelay_FarFromTheWindowsEnd_IsFoundByLaterAttemptsThatGoOnWhereTheLastStopped()
        {
            var pending = Pending(4 * Day);
            var likely = ProposedIn + (4 * Day - Hours48) / BlockSeconds;               // where 48 hours points: the window's newest block
            var provider = NewProvider(head: likely + 90_000);
            provider.Proposed(ProposedIn, pending.Eta, 7, Named, Height);
            provider.RangeCap = 500;

            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending));                     // not within one attempt's reads
            var afterFirst = provider.LogReads;
            Assert.InRange(afterFirst + provider.Refused.Count, 1, BaseRemovalProposal.PagesPerAttempt);

            for (var attempt = 2; attempt <= 20 && BaseRemovalProposal.ProposedUnder(pending) == null; attempt++)
                await BaseRemovalProposal.SearchAsync(pending);

            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending));
            Assert.True(BaseRemovalProposal.AttemptsFor(pending) > 1, "found in one attempt");
            // The window's ranges, newest first, each beginning where the one before ended: across attempts too.
            var window = provider.LogRanges.Skip(1).ToList();                            // after the likely place
            Assert.Equal(likely, window[0].To);
            for (var i = 1; i < window.Count; i++) Assert.Equal(window[i - 1].From - 1, window[i].To);
            Assert.InRange(ProposedIn, window[^1].From, window[^1].To);
            Assert.All(window, r => Assert.InRange(r.To - r.From + 1, 1, 500));
            Assert.Equal(500, window[^1].To - window[^1].From + 1);                      // settled at what the provider answers
        }
    }
}
