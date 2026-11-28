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
    /// Which admin nonce the contract's pending removal was proposed under is read from its RemovalProposed event
    /// (BaseRemovalProposal), through the node's own Base layer against providers on loopback. The event is found at the
    /// block the delay now in force points to, and when the delay was changed since, in the window every delay the
    /// contract allows leaves (eta - 30 days to eta - 48 hours), read a provider's range of blocks at a time, a bounded
    /// number of reads an attempt, going on in the next attempt from where the last one stopped.
    ///
    /// The providers' chain makes a block every two seconds from 1,700,000,000 on.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseRemovalProposalReadTests : IDisposable
    {
        private const string Contract = "0x3669EE313f9Aa14b9eBb787DfdB0E97Ca67A0510";
        private const long Genesis = 1_700_000_000, BlockSeconds = 2;
        private const long Hours48 = 48 * 3600, Day = 24 * 3600;
        private const long ProposedIn = 2_000_000;                                  // the proposing block
        private static readonly string EventTopic = "0x" + Sha3Keccack.Current.CalculateHash("RemovalProposed(address[],address[],uint256,uint256,uint256)");

        private readonly string _priorRpc = BaseBridgeService.BaseRpcUrl;
        private readonly string _priorRpc2 = BaseBridgeService.BaseRpcUrl2;
        private readonly string _priorRpc3 = BaseBridgeService.BaseRpcUrl3;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly Func<long> _priorClock = BaseRemovalProposal.Clock;
        private readonly List<Provider> _providers = new();

        public BaseRemovalProposalReadTests()
        {
            BaseBridgeService.ContractAddress = Contract;
            BaseBridgeService.BaseRpcUrl = BaseBridgeService.BaseRpcUrl2 = BaseBridgeService.BaseRpcUrl3 = "";
            BaseRpc.ForgetChainChecksForTests();
            BaseRemovalProposal.ForgetForTests();
        }

        public void Dispose()
        {
            foreach (var p in _providers) p.Rpc.Dispose();
            BaseRemovalProposal.Clock = _priorClock;
            BaseRemovalProposal.ForgetForTests();
            BaseRpc.ForgetChainChecksForTests();
            BaseBridgeService.BaseRpcUrl = _priorRpc;
            BaseBridgeService.BaseRpcUrl2 = _priorRpc2;
            BaseBridgeService.BaseRpcUrl3 = _priorRpc3;
            BaseBridgeService.ContractAddress = _priorContract;
        }

        private static long TimeOf(long block) => Genesis + block * BlockSeconds;

        /// <summary>
        /// A provider of that chain up to <see cref="FakeBaseRpc.Head"/>: it answers a block by number or tag with its time,
        /// upgradeDelay() with <see cref="Delay"/>, and logs by range (refusing a range wider than <see cref="RangeCap"/>),
        /// and keeps every range of logs it was asked for.
        /// </summary>
        private sealed class Provider
        {
            public readonly FakeBaseRpc Rpc = new();
            public long Delay = Hours48;
            public int? RangeCap;
            public readonly List<(long From, long To)> LogRanges = new();
            public readonly List<(long From, long To)> Refused = new();

            public Provider(long head)
            {
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
                            return "0x" + new BigInteger(Delay).ToString("x").PadLeft(64, '0');          // upgradeDelay(): the only call the read makes
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

            /// <summary>The proposal's event, as the contract emits it, in <paramref name="block"/>.</summary>
            public void Proposed(long block, BigInteger eta, int underNonce)
            {
                var data = new ABIEncode().GetABIEncoded(
                    new ABIValue("address[]", new List<string> { "0x00000000000000000000000000000000000000a1" }),
                    new ABIValue("address[]", new List<string>()),
                    new ABIValue("uint256", new BigInteger(14_400)),
                    new ABIValue("uint256", eta),
                    new ABIValue("uint256", new BigInteger(underNonce)));
                lock (Rpc.Logs)
                    Rpc.Logs.Add(new JObject
                    {
                        ["address"] = Contract, ["topics"] = new JArray(EventTopic), ["data"] = "0x" + Convert.ToHexString(data).ToLowerInvariant(),
                        ["blockNumber"] = FakeBaseRpc.Hex(block), ["blockHash"] = FakeBaseRpc.BlockHash("block" + block),
                        ["transactionHash"] = "0x" + new string('7', 64), ["transactionIndex"] = "0x0", ["logIndex"] = "0x0", ["removed"] = false,
                    });
            }

            public int LogReads { get { lock (LogRanges) return LogRanges.Count; } }
        }

        private Provider NewProvider(long head)
        {
            var provider = new Provider(head);
            _providers.Add(provider);
            if (_providers.Count == 1) BaseBridgeService.BaseRpcUrl = provider.Rpc.Url;
            else if (_providers.Count == 2) BaseBridgeService.BaseRpcUrl2 = provider.Rpc.Url;
            else BaseBridgeService.BaseRpcUrl3 = provider.Rpc.Url;
            return provider;
        }

        /// <summary>The pending removal as the contract shows it: proposed in the proposing block while the delay was <paramref name="delayThen"/>.</summary>
        private static BaseBridgeService.PendingRemoval Pending(long delayThen) =>
            new(new[] { "0x00000000000000000000000000000000000000a1" }, Array.Empty<string>(), new BigInteger(TimeOf(ProposedIn) + delayThen), 14_400);

        [Theory]
        [InlineData(7)]      // the nonce the operator's signature was good at
        [InlineData(9)]      // another: the read says what Base says, and the signed removal is told from this one by it
        public async Task TheEvent_IsFoundAtTheBlockTheDelayPointsTo_AndGivesTheNonceTheRemovalWasProposedUnder(int underNonce)
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var pending = Pending(Hours48);
            provider.Proposed(ProposedIn, pending.Eta, underNonce);

            Assert.Null(BaseRemovalProposal.ProposedUnder(pending.Eta));                 // nothing is read until an attempt is made
            Assert.Equal(0, provider.Rpc.TotalCalls);
            await BaseRemovalProposal.SearchAsync(pending);

            Assert.Equal(new BigInteger(underNonce), BaseRemovalProposal.ProposedUnder(pending.Eta));
            Assert.Equal((ProposedIn, ProposedIn + BaseRemovalProposal.LikelyBlocks - 1), Assert.Single(provider.LogRanges));   // one read of logs, at the proposing block

            // Found once, it is kept: no further attempt reads Base.
            provider.Rpc.ForgetCalls();
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(0, provider.Rpc.TotalCalls);
        }

        [Fact]
        public async Task WithNoSuchEvent_TheWholeWindowIsRead_ABoundedNumberOfReadsAnAttempt_AndThenLeftForAnHour()
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var pending = Pending(Hours48);                                              // and no event anywhere
            long now = 1_900_000_000;
            BaseRemovalProposal.Clock = () => now;

            var low = ProposedIn - (30 * Day - Hours48 + BaseRemovalProposal.MarginSeconds) / BlockSeconds;   // eta - 30 days - the margin
            var top = ProposedIn + BaseRemovalProposal.MarginSeconds / BlockSeconds;                              // eta - 48 hours + the margin
            var attempts = 0;
            for (var before = -1; provider.LogReads != before; attempts++)
            {
                before = provider.LogReads;
                await BaseRemovalProposal.SearchAsync(pending);
                Assert.InRange(provider.LogReads - before, 0, BaseRemovalProposal.PagesPerAttempt);
                Assert.Null(BaseRemovalProposal.ProposedUnder(pending.Eta));
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
            first.Proposed(ProposedIn, pending.Eta, 7);
            second.Proposed(ProposedIn, pending.Eta, 7);

            first.Rpc.Down = second.Rpc.Down = true;
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending.Eta));

            second.Rpc.Down = false;                                                     // the first is still down
            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending.Eta));
            Assert.Equal(0, first.LogReads);
            Assert.Equal(1, second.LogReads);
        }

        [Fact]
        public async Task AfterTheDelayWasChanged_TheEventIsFoundInTheWindow_NotWhereTheNewDelayPoints()
        {
            var provider = NewProvider(head: ProposedIn + 90_000);
            var pending = Pending(Hours48);                                              // proposed while the delay was 48 hours
            provider.Proposed(ProposedIn, pending.Eta, 7);
            provider.Delay = 7 * Day;                                                    // and raised to a week since

            await BaseRemovalProposal.SearchAsync(pending);

            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending.Eta));
            Assert.Equal(1, BaseRemovalProposal.AttemptsFor(pending.Eta));
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
            var likely = ProposedIn + (4 * Day - Hours48) / BlockSeconds;               // where 48 hours points
            var provider = NewProvider(head: likely + 90_000);
            provider.Proposed(ProposedIn, pending.Eta, 7);
            provider.RangeCap = 500;

            await BaseRemovalProposal.SearchAsync(pending);
            Assert.Null(BaseRemovalProposal.ProposedUnder(pending.Eta));                 // not within one attempt's reads
            var afterFirst = provider.LogReads;
            Assert.InRange(afterFirst + provider.Refused.Count, 1, BaseRemovalProposal.PagesPerAttempt);

            for (var attempt = 2; attempt <= 20 && BaseRemovalProposal.ProposedUnder(pending.Eta) == null; attempt++)
                await BaseRemovalProposal.SearchAsync(pending);

            Assert.Equal(new BigInteger(7), BaseRemovalProposal.ProposedUnder(pending.Eta));
            Assert.True(BaseRemovalProposal.AttemptsFor(pending.Eta) > 1, "found in one attempt");
            // The window's ranges, newest first, each beginning where the one before ended: across attempts too.
            var window = provider.LogRanges.Skip(1).ToList();                            // after the likely place
            Assert.Equal(likely + BaseRemovalProposal.MarginSeconds / BlockSeconds, window[0].To);
            for (var i = 1; i < window.Count; i++) Assert.Equal(window[i - 1].From - 1, window[i].To);
            Assert.InRange(ProposedIn, window[^1].From, window[^1].To);
            Assert.All(window, r => Assert.InRange(r.To - r.From + 1, 1, 500));
            Assert.Equal(500, window[^1].To - window[^1].From + 1);                      // settled at what the provider answers
        }
    }
}
