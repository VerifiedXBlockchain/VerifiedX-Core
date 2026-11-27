using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using static VerifiedXCore.Bitcoin.Services.BurnExitConsensusService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// What a committee caster's burn alerts can make a node keep is bounded: no text field longer than a real one, at
    /// most MaxUnverifiedAlertsPerCaster burns per caster that the node has not found on Base, and those are dropped
    /// after UNVERIFIED_ALERT_TTL_MINUTES. Alerts go through HandleBurnAlert, as the validator API route sends them.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BurnAlertBoundsTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorIsTestNet;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _caster;
        private readonly (PrivateKey Key, string Pub, string Address) _otherCaster;
        private readonly List<string> _sent = new();
        private readonly string _run = Guid.NewGuid().ToString("N");

        public BurnAlertBoundsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"burn_alert_bounds_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorIsTestNet = Globals.IsTestNet;
            Globals.IsTestNet = false; // the keys made here have mainnet addresses, and the fallback committee is this network's
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };
            _caster = NewKey();
            _otherCaster = NewKey();
            Globals.BootstrapCasterAddresses.Add(_caster.Address);
            Globals.BootstrapCasterAddresses.Add(_otherCaster.Address);
        }

        public void Dispose()
        {
            var records = (ConcurrentDictionary<string, ProcessedBurnRecord>)typeof(BurnExitConsensusService)
                .GetField("_processedBurns", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            foreach (var hash in _sent) records.TryRemove(hash, out _);
            Globals.BootstrapCasterAddresses.Remove(_caster.Address);
            Globals.BootstrapCasterAddresses.Remove(_otherCaster.Address);
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.IsTestNet = _priorIsTestNet;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>A fresh alert the caster signed, for a burn that is not on Base.</summary>
        private BurnAlert Alert((PrivateKey Key, string Pub, string Address) caster, int n, string? lockId = null, string? burner = null)
        {
            var hash = $"0x{_run}{new string('0', 24)}{n:x8}";
            var timestamp = TimeUtil.GetTime();
            const string destination = "xDestinationAddressForTheUnlock000";
            var message = BridgeCasterConsensus.BuildAlertMessage(hash, "POOL_EXIT", 100_000, destination, caster.Address, timestamp);
            _sent.Add(hash);
            return new BurnAlert
            {
                BaseBurnTxHash = hash, ExitType = BurnExitType.VfxPoolUnlock, VfxLockId = lockId ?? "lock", VfxDestinationAddress = destination,
                Amount = 0.001M, BurnerAddress = burner ?? "0x00000000000000000000000000000000000000aa", SenderCasterAddress = caster.Address,
                Timestamp = timestamp, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, caster.Key, caster.Pub),
            };
        }

        private int Kept() => _sent.Count(hash => ProcessedBurnForTests(hash) != null);

        [Fact]
        public async Task OneCastersAlerts_ForBurnsNotOnBase_AreKeptUpToTheLimit()
        {
            for (var i = 0; i < MaxUnverifiedAlertsPerCaster * 4; i++)
                await HandleBurnAlert(Alert(_caster, i));

            Assert.Equal(MaxUnverifiedAlertsPerCaster, Kept());
        }

        [Fact]
        public async Task ACasterAtItsLimit_DoesNotHoldBackAnotherCaster()
        {
            for (var i = 0; i < MaxUnverifiedAlertsPerCaster + 5; i++)
                await HandleBurnAlert(Alert(_caster, i));
            var fromOther = Alert(_otherCaster, 1000);
            await HandleBurnAlert(fromOther);

            Assert.Equal(_otherCaster.Address, ProcessedBurnForTests(fromOther.BaseBurnTxHash)?.AlertedBy);
        }

        [Fact]
        public async Task BurnsFoundOnBase_DoNotCountAgainstTheirCaster()
        {
            for (var i = 0; i < MaxUnverifiedAlertsPerCaster; i++)
                await HandleBurnAlert(Alert(_caster, i));
            foreach (var hash in _sent.ToList())
                ProcessedBurnForTests(hash)!.FoundOnBase = true; // as a consensus round marks a burn it verified

            var next = Alert(_caster, 2000);
            await HandleBurnAlert(next);

            Assert.NotNull(ProcessedBurnForTests(next.BaseBurnTxHash));
        }

        [Fact]
        public async Task AlertsLookedUpAndNotOnBase_StillCountAgainstTheirCaster()
        {
            // Base answers that none of these is a burn; a consensus round on each marks it failed.
            BaseBridgeService.BurnEventsForTests = (hash, vfxExit) => (false, null, "No matching burn event in receipt");
            try
            {
                var round = typeof(BurnExitConsensusService).GetMethod("RunConsensusRound", BindingFlags.NonPublic | BindingFlags.Static)!;
                for (var i = 0; i < MaxUnverifiedAlertsPerCaster; i++)
                {
                    var alert = Alert(_caster, i);
                    await HandleBurnAlert(alert);
                    await (Task)round.Invoke(null, new object[] { alert.BaseBurnTxHash })!;
                    Assert.Equal(BurnExitStatus.Failed, ProcessedBurnForTests(alert.BaseBurnTxHash)!.Status);
                }

                for (var i = 100; i < 100 + MaxUnverifiedAlertsPerCaster; i++)
                    await HandleBurnAlert(Alert(_caster, i));

                Assert.Equal(MaxUnverifiedAlertsPerCaster, Kept());
            }
            finally { BaseBridgeService.BurnEventsForTests = null; }
        }

        [Fact]
        public async Task ABurnBeingLookedUp_StillCountsAgainstItsCaster()
        {
            for (var i = 0; i < MaxUnverifiedAlertsPerCaster; i++)
                await HandleBurnAlert(Alert(_caster, i));
            ProcessedBurnForTests(_sent[0])!.Status = BurnExitStatus.InConsensus; // as a round holds it while Base is asked

            await HandleBurnAlert(Alert(_caster, 4000));

            Assert.Equal(MaxUnverifiedAlertsPerCaster, Kept());

            // And it is not dropped from under its round, however old.
            ProcessedBurnForTests(_sent[0])!.DetectedAt = DateTime.UtcNow.AddMinutes(-(UNVERIFIED_ALERT_TTL_MINUTES + 1));
            PruneExpiredRecords();
            Assert.NotNull(ProcessedBurnForTests(_sent[0]));
        }

        [Fact]
        public async Task AnAlertedBurnThisNodesWatchReadsFromBase_IsKeptPastTheTime_AndFreesItsCaster()
        {
            for (var i = 0; i < MaxUnverifiedAlertsPerCaster; i++)
                await HandleBurnAlert(Alert(_caster, i));
            var seen = _sent[0];

            Assert.True(IsAlreadyProcessed(seen)); // the watch of Base asks this for each burn it reads
            ProcessedBurnForTests(seen)!.DetectedAt = DateTime.UtcNow.AddMinutes(-(UNVERIFIED_ALERT_TTL_MINUTES + 1));
            PruneExpiredRecords();

            Assert.NotNull(ProcessedBurnForTests(seen));
            var next = Alert(_caster, 3000);
            await HandleBurnAlert(next);
            Assert.NotNull(ProcessedBurnForTests(next.BaseBurnTxHash));
            Assert.False(IsAlreadyProcessed("0xnot-tracked"));
        }

        [Fact]
        public async Task AnAlertWithAnOversizedField_IsNotKept_AndAnOrdinaryOneIs()
        {
            var huge = new string('A', 1024 * 1024);
            var longLock = Alert(_caster, 1, lockId: huge);
            var longBurner = Alert(_caster, 2, burner: huge);
            var atTheLimit = Alert(_caster, 3, lockId: new string('a', MaxAlertFieldLength));
            var ordinary = Alert(_caster, 4);

            foreach (var alert in new[] { longLock, longBurner, atTheLimit, ordinary })
                await HandleBurnAlert(alert);

            Assert.Null(ProcessedBurnForTests(longLock.BaseBurnTxHash));
            Assert.Null(ProcessedBurnForTests(longBurner.BaseBurnTxHash));
            Assert.NotNull(ProcessedBurnForTests(atTheLimit.BaseBurnTxHash));
            var kept = ProcessedBurnForTests(ordinary.BaseBurnTxHash);
            Assert.NotNull(kept);
            Assert.Equal(BurnExitStatus.Pending, kept!.Status);
            Assert.Equal(0.001M, kept.Amount);
        }

        [Fact]
        public async Task AnAlertedBurnNeverFoundOnBase_IsDroppedAfterItsTime_AndOneFoundIsKept()
        {
            var never = Alert(_caster, 1);
            var found = Alert(_caster, 2);
            var recent = Alert(_caster, 3);
            foreach (var alert in new[] { never, found, recent })
                await HandleBurnAlert(alert);
            var old = DateTime.UtcNow.AddMinutes(-(UNVERIFIED_ALERT_TTL_MINUTES + 1));
            ProcessedBurnForTests(never.BaseBurnTxHash)!.DetectedAt = old;
            ProcessedBurnForTests(found.BaseBurnTxHash)!.DetectedAt = old;
            ProcessedBurnForTests(found.BaseBurnTxHash)!.FoundOnBase = true;

            PruneExpiredRecords();

            Assert.Null(ProcessedBurnForTests(never.BaseBurnTxHash));
            Assert.NotNull(ProcessedBurnForTests(found.BaseBurnTxHash));
            Assert.NotNull(ProcessedBurnForTests(recent.BaseBurnTxHash));

            // Its caster has room again.
            for (var i = 10; i < 10 + MaxUnverifiedAlertsPerCaster; i++)
                await HandleBurnAlert(Alert(_caster, i));
            Assert.Equal(MaxUnverifiedAlertsPerCaster, _sent.Count(h => ProcessedBurnForTests(h) is { FoundOnBase: false }));
        }
    }
}
