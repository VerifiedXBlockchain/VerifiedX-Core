using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1 (burn, owner decision): each listed address loses min(balance, listed amount) of VFX; nothing
    /// is credited anywhere. The burn runs in block application: at exactly Globals.ForgedVfxBurnHeight, and at the first
    /// block applied after it on a state that has not been burned. Whether a state has been burned is recorded in the
    /// state's own world record, so it follows a rebuild, a snapshot and a restore.
    ///
    /// Every test drives the production entry point (StateData.UpdateTreis) and leaves Globals.LastBlock at its startup
    /// initialiser (Height -1): the fourth review found the earlier startup catch-up never ran because it read the tip
    /// before it was loaded, and its tests had set the tip by hand.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss11_ForgedVfxBurnTests : IDisposable
    {
        private const long BurnHeight = 1000;
        private const string Thief = "RB3eeBH258arkuePCJf5NMVMSaUyhyy9g6";
        private const string Other = "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH";
        private const decimal Forged = 100_027.99999396M;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorHeight;
        private readonly Dictionary<string, decimal> _priorBurns;
        private readonly Block _priorLastBlock;

        public FundLoss11_ForgedVfxBurnTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl11_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorHeight = Globals.ForgedVfxBurnHeight;
            _priorBurns = new Dictionary<string, decimal>(Globals.ForgedVfxBurns, StringComparer.Ordinal);
            _priorLastBlock = Globals.LastBlock;
            Globals.ForgedVfxBurnHeight = BurnHeight;
            Globals.LastBlock = new Block { Height = -1 }; // exactly what a starting node holds before the tip is loaded
            StateWriteContext.Clear();
            DbContext.Initialize();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Thief, Balance = Forged, Nonce = 3 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Other, Balance = 500M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            StateWriteContext.Clear();
            Globals.ForgedVfxBurnHeight = _priorHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.ForgedVfxBurns.Clear();
            foreach (var (k, v) in _priorBurns) Globals.ForgedVfxBurns[k] = v;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static decimal Balance(string key) => StateData.GetSpecificAccountStateTrei(key)!.Balance;
        private static long Marker() => WorldTrei.GetWorldTrei().FindOne(x => true)?.ForgedVfxBurnAppliedHeight ?? 0;

        private static void SetBalance(string key, decimal balance)
        {
            var acct = StateData.GetSpecificAccountStateTrei(key)!;
            acct.Balance = balance;
            StateData.GetAccountStateTrei().UpdateSafe(acct);
        }

        private static void ClearMarker()
        {
            var col = WorldTrei.GetWorldTrei();
            var rec = col.FindOne(x => true);
            if (rec == null) return;
            rec.ForgedVfxBurnAppliedHeight = 0;
            col.UpdateSafe(rec);
        }

        private static Block EmptyBlock(long height) => new Block { Height = height, StateRoot = "root" + height, Timestamp = 1_700_000_000, Transactions = new List<Transaction>() };

        [Fact]
        public void TheMainnetList_IsTheForgedTotal()
        {
            var fee = Globals.PrivateTxFixedFee;
            Assert.Equal((2M + fee) + (2M + fee) + (29M + fee) + (100_000M + fee), _priorBurns[Thief]);
            Assert.Single(_priorBurns);
        }

        [Fact]
        public async Task AtTheBurnHeight_TheBalanceGoesToZero_NothingElseMoves()
        {
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight - 1)));
            Assert.Equal(Forged, Balance(Thief)); // not yet
            Assert.Equal(0, Marker());

            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Equal(0M, Balance(Thief));   // the address held less than was forged: all of it goes
            Assert.Equal(500M, Balance(Other)); // nobody else is touched, nothing is credited
            Assert.Equal(BurnHeight, Marker()); // recorded in the state, and it survives the world record's per-block update
            Assert.True(StateData.ForgedVfxBurnApplied());

            SetBalance(Thief, 3M); // received after the freeze
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 1)));
            Assert.Equal(3M, Balance(Thief));   // once
            Assert.Equal(BurnHeight, Marker());
        }

        [Fact]
        public async Task ABalanceAboveTheListedAmount_LosesExactlyTheListedAmount()
        {
            SetBalance(Thief, _priorBurns[Thief] + 50M);
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Equal(50M, Balance(Thief));
        }

        [Fact]
        public async Task AnUnknownOrEmptyAddress_IsSkipped()
        {
            Globals.ForgedVfxBurns.Clear();
            Globals.ForgedVfxBurns["RUnknownAddressThatHoldsNothing00000"] = 10M;
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Equal(Forged, Balance(Thief)); // not listed any more
            Assert.Equal(BurnHeight, Marker());   // the step ran

            Globals.ForgedVfxBurns.Clear();       // testnet: no list, no step, no marker
            ClearMarker();
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 1)));
            Assert.Equal(0, Marker());
        }

        /// <summary>
        /// A node that crossed the height on an older build: its state is past the height and unburned. The first block it
        /// applies with this code burns, once, with the write stamped for the snapshot diff. Globals.LastBlock is never set.
        /// </summary>
        [Fact]
        public async Task ALateUpgrader_BurnsAtItsNextBlock_Once_AndStampsTheWrite()
        {
            Assert.Equal(-1, Globals.LastBlock.Height);
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 40)));
            Assert.Equal(0M, Balance(Thief));
            Assert.Equal(500M, Balance(Other));
            Assert.Equal(BurnHeight + 40, Marker());
            // The snapshot diff copies records by this stamp; a write stamped below the slot's height would be missed
            // and a later restore would bring the forged balance back.
            Assert.Equal(BurnHeight + 40, StateData.GetSpecificAccountStateTrei(Thief)!.LastModifiedHeight);

            SetBalance(Thief, 7M);
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 41)));
            Assert.Equal(7M, Balance(Thief)); // the marker: never twice
        }

        /// <summary>
        /// A restore brings back the balances AND the world record of the snapshot. A snapshot from before the burn has no
        /// marker, so the block applied after the restore burns again; one from after it keeps the marker and does not.
        /// </summary>
        [Fact]
        public async Task AfterAStateRestore_TheMarkerFollowsTheState()
        {
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 5)));
            Assert.Equal(0M, Balance(Thief));

            // Restore of a snapshot taken BEFORE the catch-up: the forged balance and an unmarked world record come back.
            SetBalance(Thief, Forged);
            ClearMarker();
            Assert.False(StateData.ForgedVfxBurnApplied());
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 6)));
            Assert.Equal(0M, Balance(Thief));
            Assert.Equal(BurnHeight + 6, Marker());

            // Restore of a snapshot taken AFTER it: marked, with whatever the address has received since.
            SetBalance(Thief, 2M);
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 7)));
            Assert.Equal(2M, Balance(Thief));
        }

        /// <summary>A full rebuild wipes the world record with the balances, and the replay burns at the height again.</summary>
        [Fact]
        public async Task AFullRebuild_ReplaysTheBurnAtTheHeight()
        {
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Equal(BurnHeight, Marker());

            WorldTrei.GetWorldTrei().DeleteAllSafe();      // BlockRollbackUtility.WipeChainDerivedState
            SetBalance(Thief, Forged);                      // ...and the replay rebuilds the balance up to the height
            Assert.False(StateData.ForgedVfxBurnApplied());
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight - 1)));
            Assert.Equal(Forged, Balance(Thief));
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Equal(0M, Balance(Thief));
            Assert.Equal(BurnHeight, Marker());
        }
    }
}
