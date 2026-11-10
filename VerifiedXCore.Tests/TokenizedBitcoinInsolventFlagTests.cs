using System;
using System.IO;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// AccountCheck re-judges every vault on each pass and records the result with SetInsolvent. A vault still insolvent on
    /// the next pass stays flagged (the old FlagInsolvent toggled it back), and one topped up again reads as solvent.
    /// </summary>
    [Collection("DbContextSequential")]
    public class TokenizedBitcoinInsolventFlagTests : IDisposable
    {
        private const string Deposit = "tb1qinsolventflagtestdepositaddress";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public TokenizedBitcoinInsolventFlagTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"tknz_insolvent_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            TokenizedBitcoin.GetDb().Insert(new TokenizedBitcoin
            {
                SmartContractUID = "sc-insolvent-flag-test",
                RBXAddress = "xInsolventFlagTestOwner",
                DepositAddress = Deposit,
                TokenName = "vBTC",
                TokenDescription = "",
            });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static bool Flagged() => TokenizedBitcoin.GetDb().FindOne(x => x.DepositAddress == Deposit).TokenHasBecomeInsolvent;

        [Fact]
        public async System.Threading.Tasks.Task RepeatedInsolventPassesKeepTheFlagSet()
        {
            Assert.False(Flagged());
            await TokenizedBitcoin.SetInsolvent(Deposit, true);
            Assert.True(Flagged());
            await TokenizedBitcoin.SetInsolvent(Deposit, true);
            Assert.True(Flagged());
        }

        [Fact]
        public async System.Threading.Tasks.Task AVaultToppedUpAgainReadsAsSolvent()
        {
            await TokenizedBitcoin.SetInsolvent(Deposit, true);
            Assert.True(Flagged());
            await TokenizedBitcoin.SetInsolvent(Deposit, false);
            Assert.False(Flagged());
        }
    }
}
