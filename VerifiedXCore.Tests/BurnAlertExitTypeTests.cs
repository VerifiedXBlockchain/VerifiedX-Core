using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using static VerifiedXCore.Bitcoin.Services.BurnExitConsensusService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A burn alert is kept only with one of the two exit types, and with the type its sender signed: the record's type
    /// is what the node later votes on and pays by. Alerts go through HandleBurnAlert, as the validator API route sends them.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BurnAlertExitTypeTests : IDisposable
    {
        private const string BtcDestination = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private const string VfxDestination = "xDestinationAddressForTheUnlock000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorIsTestNet;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _caster;
        private readonly (PrivateKey Key, string Pub, string Address) _otherCaster;
        private readonly List<string> _sent = new();
        private readonly string _run = Guid.NewGuid().ToString("N");

        public BurnAlertExitTypeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"burn_alert_type_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private string Burn(int n)
        {
            var hash = $"0x{_run}{new string('0', 24)}{n:x8}";
            if (!_sent.Contains(hash)) _sent.Add(hash);
            return hash;
        }

        /// <summary>A fresh alert carrying <paramref name="carried"/> as its type, signed for <paramref name="signedAs"/> ("BTC_EXIT" or "POOL_EXIT").</summary>
        private static BurnAlert Alert((PrivateKey Key, string Pub, string Address) caster, string hash, BurnExitType carried, string signedAs)
        {
            var timestamp = TimeUtil.GetTime();
            var destination = carried == BurnExitType.VfxPoolUnlock ? VfxDestination : BtcDestination;
            var message = BridgeCasterConsensus.BuildAlertMessage(hash, signedAs, 100_000, destination, caster.Address, timestamp);
            return new BurnAlert
            {
                BaseBurnTxHash = hash, ExitType = carried, VfxLockId = "lock", BtcDestination = BtcDestination, VfxDestinationAddress = VfxDestination,
                Amount = 0.001M, BurnerAddress = "0x00000000000000000000000000000000000000aa", SenderCasterAddress = caster.Address,
                Timestamp = timestamp, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, caster.Key, caster.Pub),
            };
        }

        [Theory]
        [InlineData(7)]
        [InlineData(2)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public async Task AnAlertWithATypeThatIsNotOne_IsNotKept_AndTheBurnIsStillTakenFromAnHonestAlert(int type)
        {
            var burn = Burn(1);
            // Signed as either word: neither makes the number a type.
            await HandleBurnAlert(Alert(_caster, burn, (BurnExitType)type, "POOL_EXIT"));
            await HandleBurnAlert(Alert(_caster, burn, (BurnExitType)type, "BTC_EXIT"));
            Assert.Null(ProcessedBurnForTests(burn));

            await HandleBurnAlert(Alert(_otherCaster, burn, BurnExitType.BtcExit, "BTC_EXIT"));
            var kept = ProcessedBurnForTests(burn);
            Assert.NotNull(kept);
            Assert.Equal(BurnExitType.BtcExit, kept!.ExitType);
            Assert.Equal(_otherCaster.Address, kept.AlertedBy);
        }

        [Fact]
        public async Task AnAlertOfEitherType_IsKeptWithTheTypeItsSenderSigned()
        {
            var bitcoin = Burn(1);
            var pool = Burn(2);
            await HandleBurnAlert(Alert(_caster, bitcoin, BurnExitType.BtcExit, "BTC_EXIT"));
            await HandleBurnAlert(Alert(_caster, pool, BurnExitType.VfxPoolUnlock, "POOL_EXIT"));

            Assert.Equal(BurnExitType.BtcExit, ProcessedBurnForTests(bitcoin)!.ExitType);
            Assert.Equal(BtcDestination, ProcessedBurnForTests(bitcoin)!.BtcDestination);
            Assert.Equal(BurnExitType.VfxPoolUnlock, ProcessedBurnForTests(pool)!.ExitType);
            Assert.Equal(VfxDestination, ProcessedBurnForTests(pool)!.VfxDestinationAddress);
        }

        [Fact]
        public async Task AnAlertCarryingOneTypeAndSignedForTheOther_IsNotKept()
        {
            var burn = Burn(1);
            await HandleBurnAlert(Alert(_caster, burn, BurnExitType.BtcExit, "POOL_EXIT"));
            await HandleBurnAlert(Alert(_caster, burn, BurnExitType.VfxPoolUnlock, "BTC_EXIT"));

            Assert.Null(ProcessedBurnForTests(burn));
        }
    }
}
