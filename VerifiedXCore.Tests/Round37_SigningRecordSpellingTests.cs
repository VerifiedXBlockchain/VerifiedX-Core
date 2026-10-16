using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Block = VerifiedXCore.Models.Block;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: a validator's signing record for a payment is found under any spelling of the payment's reference an
    /// earlier version signed it under (a bridge exit named by a longer, shorter, 0x-prefixed or other-case burn hash;
    /// a padded withdrawal hash), so the prior-signature check before a new signing sees it.
    /// No Electrum server answers here: a found record whose transaction cannot be shown dead refuses the new signing.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_SigningRecordSpellingTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly List<ClientSettings> _priorClients;
        private const string Vault = "5e1f0c2a9b8d4e7f8a6b5c4d3e2f1a0b:1760000000";
        private const string BurnHash = "0x9c2e4b7a1d3f5e6c8b0a2d4f6e8c1a3b5d7f9e0c2a4b6d8f1e3c5a7b9d0f2e4c";
        private const string Wrh = "4f1e3a9c7b2d5e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f50617";
        private const string SignedTx = "aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11";
        private const string NewTx = "bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22";
        private static readonly List<string> Coins = new() { "cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33:0" };

        public Round37_SigningRecordSpellingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37srs_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            Globals.LastBlock = new Block { Height = 500 };
            _priorClients = Globals.ClientSettings;
            Globals.ClientSettings = new List<ClientSettings> { new() { Host = "127.0.0.1", Port = 1, UseSsl = true } };
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.ClientSettings = _priorClients;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>The reference the casters name this vault's part of the exit by.</summary>
        private static string ExitReference => VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ExitSigningReference(BurnHash, Vault);

        public static IEnumerable<object[]> OtherSpellings()
        {
            yield return new object[] { $"btcexit_{BurnHash}_{Vault[..8]}", true };                 // the whole burn hash
            yield return new object[] { $"btcexit_{BurnHash[2..]}_{Vault[..8]}", true };            // without its 0x
            yield return new object[] { $"BTCEXIT_{BurnHash[..20].ToUpperInvariant()}_{Vault}", true }; // other case, whole vault UID
            yield return new object[] { $" btcexit_{BurnHash[..10]}_{Vault[..8]} ", true };         // a shorter prefix, padded
            yield return new object[] { $"  {Wrh}\t", false };                                       // a padded withdrawal hash
        }

        [Theory]
        [MemberData(nameof(OtherSpellings))]
        public async Task ASignatureRecordedUnderAnotherSpelling_StillHoldsBackASecondTransaction(string recordedUnder, bool isExit)
        {
            Assert.True(FrostSignedWithdrawalEvidence.Record(Vault, recordedUnder, SignedTx, Coins));
            var reference = isExit ? ExitReference : Wrh;

            var (ok, _) = await FrostStartup.CheckPriorSignedTransactionAsync(Vault, reference, NewTx);
            Assert.False(ok, "a second transaction was allowed while the first, recorded under another spelling, may still pay");

            // The same transaction again (a retry) is still allowed.
            Assert.True((await FrostStartup.CheckPriorSignedTransactionAsync(Vault, reference, SignedTx)).Ok);
            Assert.Equal(SignedTx, FrostSignedWithdrawalEvidence.Get(Vault, reference)?.BtcTxId);
        }

        [Fact]
        public async Task ARecordForAnotherPayment_IsNotThisOnes()
        {
            const string otherBurn = "0x1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff";
            Assert.True(FrostSignedWithdrawalEvidence.Record(Vault, VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ExitSigningReference(otherBurn, Vault), SignedTx, Coins));
            Assert.True(FrostSignedWithdrawalEvidence.Record(Vault, "5f1e3a9c7b2d5e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f50617", SignedTx, Coins));
            Assert.True(FrostSignedWithdrawalEvidence.Record("otheruid0000:1760000000", ExitReference, SignedTx, Coins));

            Assert.True((await FrostStartup.CheckPriorSignedTransactionAsync(Vault, ExitReference, NewTx)).Ok);
            Assert.True((await FrostStartup.CheckPriorSignedTransactionAsync(Vault, Wrh, NewTx)).Ok);
        }
    }
}
