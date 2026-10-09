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
    /// Fund-loss audit item 1 (burn, owner decision): at exactly Globals.ForgedVfxBurnHeight, before the block's
    /// transactions, each listed address loses min(balance, listed amount) of VFX; nothing is credited anywhere.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss11_ForgedVfxBurnTests : IDisposable
    {
        private const long BurnHeight = 1000;
        private const string Thief = "RB3eeBH258arkuePCJf5NMVMSaUyhyy9g6";
        private const string Other = "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorHeight;
        private readonly Dictionary<string, decimal> _priorBurns;

        public FundLoss11_ForgedVfxBurnTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl11_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorHeight = Globals.ForgedVfxBurnHeight;
            _priorBurns = new Dictionary<string, decimal>(Globals.ForgedVfxBurns, StringComparer.Ordinal);
            Globals.ForgedVfxBurnHeight = BurnHeight;
            DbContext.Initialize();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Thief, Balance = 100_027.99999396M, Nonce = 3 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Other, Balance = 500M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.ForgedVfxBurnHeight = _priorHeight;
            Globals.ForgedVfxBurns.Clear();
            foreach (var (k, v) in _priorBurns) Globals.ForgedVfxBurns[k] = v;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static decimal Balance(string key) => StateData.GetSpecificAccountStateTrei(key)!.Balance;
        private static Block EmptyBlock(long height) => new Block { Height = height, StateRoot = "root", Timestamp = 1_700_000_000, Transactions = new List<Transaction>() };

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
            Assert.Equal(100_027.99999396M, Balance(Thief)); // not yet

            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Equal(0M, Balance(Thief));   // the address held less than was forged: all of it goes
            Assert.Equal(500M, Balance(Other)); // nobody else is touched, nothing is credited

            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight + 1)));
            Assert.Equal(0M, Balance(Thief));   // once
        }

        [Fact]
        public async Task ABalanceAboveTheListedAmount_LosesExactlyTheListedAmount()
        {
            var account = StateData.GetSpecificAccountStateTrei(Thief)!;
            account.Balance = 100_040M;
            StateData.GetAccountStateTrei().UpdateSafe(account);
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Equal(100_040M - 100_033.000012M, Balance(Thief));
        }

        [Fact]
        public async Task AnUnknownOrEmptyAddress_IsSkipped()
        {
            Globals.ForgedVfxBurns["RBnobodyXXXXXXXXXXXXXXXXXXXXXXXXXXX"] = 5M;
            Assert.True(await StateData.UpdateTreis(EmptyBlock(BurnHeight)));
            Assert.Null(StateData.GetSpecificAccountStateTrei("RBnobodyXXXXXXXXXXXXXXXXXXXXXXXXXXX"));
            Assert.Equal(0M, Balance(Thief));
        }
    }
}
