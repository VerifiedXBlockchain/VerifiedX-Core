using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-27 (found by the sixth independent review; CRITICAL, pre-existing): VerifyTX returned (true, "") first thing for
    /// any transaction whose carried Hash was on Globals.BadTxList / BadNFTTxList, and nothing binds a carried hash to the
    /// content (the merkle root is built from the stored hashes). Any content carrying one of the three built-in hashes -
    /// e.g. a victim's whole balance sent to an attacker, unsigned - verified at admission and in blocks. The built-in
    /// entries (a 2023 testnet bypass; in neither mainnet to 4,589,149 nor the current testnet) are removed; an entry an
    /// operator adds is honoured only in block validation and only for the content its hash commits to.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW27_WhitelistBypassTests : IDisposable
    {
        private const string BuiltInHash = "9065618ff356dc1dcef8cd5413ffe826f8ab45ca8b6bb9c8f9853d1de0b576ae";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly (PrivateKey Key, string Pub, string Address) _victim = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _attacker = NewKey();

        public NEW27_WhitelistBypassTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new27_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            foreach (var k in new[] { _victim, _attacker })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 2000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>The victim's whole balance to the attacker, carrying a listed hash, with no valid signature.</summary>
        private Transaction Forged(string hash) => new Transaction
        {
            Timestamp = TimeUtil.GetTime(), FromAddress = _victim.Address, ToAddress = _attacker.Address, Amount = 1999M, Fee = 0.00001M,
            Nonce = 0, TransactionType = TransactionType.TX, Hash = hash, Signature = "forged",
        };

        [Fact]
        public async Task NEW27_PoC_ForgedContentCarryingABuiltInWhitelistedHash_Refused()
        {
            var forged = Forged(BuiltInHash);
            Assert.False((await TransactionValidatorService.VerifyTX(forged)).Item1);                                      // admission
            Assert.False((await TransactionValidatorService.VerifyTX(forged, false, true, false, null, false, 100)).Item1); // block
        }

        [Fact]
        public void NEW27_BuiltInEntries_Removed()
        {
            Assert.DoesNotContain(BuiltInHash, Globals.BadTxList);
            Assert.DoesNotContain("b05b230c9f7fb6f9014c0a9a4a5b1c9ddaf36a96462635d628272b8c62e2e5b3", Globals.BadTxList);
            Assert.DoesNotContain("70e34dd1b5d646addc5328f971b4ab370095985dcf4bce1d0e1ea222824daa6d", Globals.BadNFTTxList);
        }

        [Fact]
        public async Task NEW27_OperatorEntry_ForgedContentWithItsHash_RefusedEvenInABlock()
        {
            // The operator whitelisted a real transaction; forged content carrying that hash must not ride on it.
            var real = new Transaction { Timestamp = TimeUtil.GetTime(), FromAddress = _victim.Address, ToAddress = _attacker.Address, Amount = 1M, Fee = 0.00001M, Nonce = 0, TransactionType = TransactionType.TX };
            real.Build();
            Globals.BadTxList.Add(real.Hash);
            try
            {
                var forged = Forged(real.Hash);
                Assert.False((await TransactionValidatorService.VerifyTX(forged, false, true, false, null, false, 100)).Item1);
                Assert.False(LedgerIntegrityRules.IsHonoredWhitelistEntry(forged, 100));
            }
            finally { Globals.BadTxList.Remove(real.Hash); }
        }

        [Fact]
        public async Task NEW27_OperatorEntry_HonouredOnlyInBlockValidation_ForItsOwnContent()
        {
            // An operator whitelists a transaction that fails validation (here: unsigned) to move past a stuck block.
            var stuck = new Transaction { Timestamp = TimeUtil.GetTime(), FromAddress = _victim.Address, ToAddress = _attacker.Address, Amount = 1M, Fee = 0.00001M, Nonce = 0, TransactionType = TransactionType.TX };
            stuck.Build();
            stuck.Signature = "bad";
            Assert.False((await TransactionValidatorService.VerifyTX(stuck, false, true, false, null, false, 100)).Item1); // precondition
            Globals.BadTxList.Add(stuck.Hash);
            try
            {
                Assert.True((await TransactionValidatorService.VerifyTX(stuck, false, true, false, null, false, 100)).Item1);  // block
                Assert.False((await TransactionValidatorService.VerifyTX(stuck)).Item1);                                       // admission
                Assert.True(LedgerIntegrityRules.IsHonoredWhitelistEntry(stuck, 100));
                Assert.False(LedgerIntegrityRules.IsHonoredWhitelistEntry(stuck, null));
            }
            finally { Globals.BadTxList.Remove(stuck.Hash); }
        }
    }
}
