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
using VerifiedXCore.Utilities;
using Xunit;
using static VerifiedXCore.Bitcoin.Services.BurnExitConsensusService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A burn is tracked under one spelling of its id. The look-up on Base reads an id with blanks around it, in another
    /// case, without "0x" or with a zero-padded position as the same burn, so alerts spelling one burn differently make
    /// one record, not one each; alerts hold a bounded number of one transaction's burns; and a pending burn the chain
    /// has redeemed is dropped. Alerts go through HandleBurnAlert, as the validator API route sends them.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BurnAlertOneSpellingTests : IDisposable
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

        public BurnAlertOneSpellingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"burn_alert_spelling_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private string Hash(int n) => $"0x{_run}{new string('0', 24)}{n:x8}";

        private static ConcurrentDictionary<string, ProcessedBurnRecord> Records =>
            (ConcurrentDictionary<string, ProcessedBurnRecord>)typeof(BurnExitConsensusService)
                .GetField("_processedBurns", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private List<ProcessedBurnRecord> KeptOfThisRun() => Records.Values.Where(r => r.BaseBurnTxHash.Contains(_run, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>A fresh pool-unlock alert for the burn spelled <paramref name="spelling"/>, signed by the caster over that spelling.</summary>
        private BurnAlert Alert((PrivateKey Key, string Pub, string Address) caster, string spelling)
        {
            var timestamp = TimeUtil.GetTime();
            var message = BridgeCasterConsensus.BuildAlertMessage(spelling, "POOL_EXIT", 100_000, VfxDestination, caster.Address, timestamp);
            _sent.Add(spelling);
            _sent.Add(BaseBridgeService.CanonicalBurnId(spelling) ?? spelling);
            return new BurnAlert
            {
                BaseBurnTxHash = spelling, ExitType = BurnExitType.VfxPoolUnlock, VfxLockId = "lock", VfxDestinationAddress = VfxDestination,
                Amount = 0.001M, BurnerAddress = "0x00000000000000000000000000000000000000aa", SenderCasterAddress = caster.Address,
                Timestamp = timestamp, Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, caster.Key, caster.Pub),
            };
        }

        [Fact]
        public async Task OneBurnSpelledManyWays_IsOneRecord()
        {
            var burn = Hash(1);
            var spellings = new List<string> { burn + " ", " " + burn, burn + "\t", burn + "\n", burn.ToUpperInvariant().Replace("0X", "0x"), "0X" + burn[2..], burn[2..] };
            for (var blanks = 2; blanks <= 40; blanks++) spellings.Add(burn + new string(' ', blanks));

            foreach (var spelling in spellings)
                await HandleBurnAlert(Alert(_caster, spelling));

            var kept = Assert.Single(KeptOfThisRun());
            Assert.Equal(burn, kept.BaseBurnTxHash);
            Assert.Same(kept, ProcessedBurnForTests(burn));
            Assert.All(spellings, spelling => Assert.Null(ProcessedBurnForTests(spelling)));
        }

        [Fact]
        public async Task OneOfSeveralBurnsInATransaction_SpelledManyWays_IsOneRecord()
        {
            var second = Hash(2) + ":1";
            foreach (var spelling in new[] { Hash(2) + ":01", Hash(2) + ":001", Hash(2) + ":1 ", Hash(2).ToUpperInvariant().Replace("0X", "0x") + ":1", second })
                await HandleBurnAlert(Alert(_otherCaster, spelling));

            var kept = Assert.Single(KeptOfThisRun());
            Assert.Equal(second, kept.BaseBurnTxHash);
        }

        [Fact]
        public async Task AnAlertInTheSpellingTheWatchOfBaseUses_IsKeptUnderIt()
        {
            var single = Hash(3);
            var positioned = Hash(4) + ":2";
            await HandleBurnAlert(Alert(_caster, single));
            await HandleBurnAlert(Alert(_otherCaster, positioned));

            Assert.Equal(_caster.Address, ProcessedBurnForTests(single)?.AlertedBy);
            Assert.Equal(_otherCaster.Address, ProcessedBurnForTests(positioned)?.AlertedBy);
            Assert.Equal(BurnExitStatus.Pending, ProcessedBurnForTests(single)!.Status);
            Assert.Equal(2, KeptOfThisRun().Count);
        }

        [Theory]
        [InlineData("not-a-burn")]
        [InlineData("0x1234")]
        [InlineData(":1")]
        public async Task TextThatIsNotABurnId_IsNotKept(string notAnId)
        {
            foreach (var text in new[] { notAnId, Hash(5) + ":", Hash(5) + ":x", Hash(5) + ":1:2", Hash(5) + "g", Hash(5)[..40] })
            {
                await HandleBurnAlert(Alert(_caster, text));
                Assert.Null(ProcessedBurnForTests(text));
            }
            Assert.Empty(KeptOfThisRun());
        }

        [Fact]
        public async Task OneCastersAlerts_HoldABoundedNumberOfOneTransactionsBurns_AndDoNotHoldBackAnotherCasterOrTransaction()
        {
            for (var position = 0; position < 3 * MaxUnverifiedAlertsPerTransaction; position++)
                await HandleBurnAlert(Alert(_caster, $"{Hash(6)}:{position}"));
            Assert.Equal(MaxUnverifiedAlertsPerTransaction, KeptOfThisRun().Count);

            // Another caster's alert for a burn of that transaction is taken, and so is this caster's for another transaction.
            await HandleBurnAlert(Alert(_otherCaster, $"{Hash(6)}:900"));
            await HandleBurnAlert(Alert(_caster, Hash(7)));
            Assert.Equal(_otherCaster.Address, ProcessedBurnForTests($"{Hash(6)}:900")?.AlertedBy);
            Assert.NotNull(ProcessedBurnForTests(Hash(7)));

            // A burn of it found on Base no longer counts against the caster.
            KeptOfThisRun().First(r => r.AlertedBy == _caster.Address && r.BaseBurnTxHash.StartsWith(Hash(6))).FoundOnBase = true;
            await HandleBurnAlert(Alert(_caster, $"{Hash(6)}:901"));
            Assert.NotNull(ProcessedBurnForTests($"{Hash(6)}:901"));
        }

        [Fact]
        public async Task APendingBurnWhoseRedemptionTheChainCompleted_IsDropped_AndOthersKeepTheirRound()
        {
            var redeemed = Hash(8);
            var open = Hash(9);
            var started = Hash(10);
            var inARound = Hash(11);
            foreach (var burn in new[] { redeemed, open, started, inARound })
                await HandleBurnAlert(Alert(_caster, burn));
            ProcessedBurnForTests(started)!.CompletionVfxTxHash = "sent-by-this-node"; // this node's own exit for it is under way
            ProcessedBurnForTests(inARound)!.Status = BurnExitStatus.InConsensus;
            foreach (var burn in new[] { redeemed, started, inARound })
                Assert.True(VerifiedXCore.Bitcoin.Models.VBTCBridgeConsumedBurn.TryMarkConsumed(burn, "POOL_UNLOCK", "vfx-tx-" + burn[^2..], 100));

            var sweep = WithoutRedeemed(KeptOfThisRun());

            Assert.Equal(new[] { open, started, inARound }.OrderBy(h => h), sweep.Select(r => r.BaseBurnTxHash).OrderBy(h => h));
            Assert.Null(ProcessedBurnForTests(redeemed));
            Assert.All(new[] { open, started, inARound }, burn => Assert.NotNull(ProcessedBurnForTests(burn)));

            // And once redeemed, no spelling of it is taken again.
            await HandleBurnAlert(Alert(_otherCaster, redeemed + "  "));
            Assert.Null(ProcessedBurnForTests(redeemed));
        }

        [Fact]
        public async Task APendingBurnWhoseExitTheChainStillHoldsOpen_IsKept()
        {
            // The exit another caster sent is on chain and not yet paid: it can still fail and need this caster's vote again.
            var burn = Hash(12);
            await HandleBurnAlert(Alert(_caster, burn));
            VerifiedXCore.Bitcoin.Models.VBTCBridgeBtcExitState.GetCollection().Insert(new VerifiedXCore.Bitcoin.Models.VBTCBridgeBtcExitState { BaseBurnTxHash = burn, ExitTxHash = "exit-tx" });
            Assert.True(VerifiedXCore.Bitcoin.Models.VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(burn));

            var sweep = WithoutRedeemed(KeptOfThisRun());

            Assert.Same(ProcessedBurnForTests(burn), Assert.Single(sweep));
        }

        [Theory]
        [InlineData("0xAB", "0xab")]
        [InlineData(" 0xab ", "0xab")]
        [InlineData("AB", "0xab")]
        [InlineData("0Xab:007", "0xab:7")]
        [InlineData("0xab:0", "0xab:0")]
        public void ABurnIdHasOneSpelling(string shortForm, string expectedShortForm)
        {
            // The 64 digits are written out from the two given.
            string Full(string s) => s.Replace("ab", string.Concat(Enumerable.Repeat("ab", 32)), StringComparison.Ordinal).Replace("AB", string.Concat(Enumerable.Repeat("AB", 32)), StringComparison.Ordinal);
            Assert.Equal(Full(expectedShortForm), BaseBridgeService.CanonicalBurnId(Full(shortForm)));
            Assert.Equal(Full(expectedShortForm), BaseBridgeService.CanonicalBurnId(Full(expectedShortForm)));
        }
    }
}
