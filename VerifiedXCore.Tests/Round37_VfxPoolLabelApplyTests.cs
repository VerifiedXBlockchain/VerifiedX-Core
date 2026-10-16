using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LiteDB;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: from Globals.PathAuditRulesHeight a VFX private transaction that names another pool than "VFX" (which
    /// validation refuses) is applied to nothing when a block carries it: no VFX is credited for an unshield, and no
    /// pool row moves. Below the height, a block is applied as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_VfxPoolLabelApplyTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly string _privacyPath;
        private readonly LiteDatabase _privacyDb;
        private const string Recipient = "xUnshieldRecipient00000000000000001";

        public Round37_VfxPoolLabelApplyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37vfxlabel_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            _privacyPath = Path.Combine(_tempRoot, "privacy.db");
            _privacyDb = new LiteDatabase(new ConnectionString { Filename = _privacyPath, Connection = ConnectionType.Direct });
            PrivacyDbContext.EnsurePrivacyIndexes(_privacyDb);
        }

        public void Dispose()
        {
            _privacyDb.Dispose();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Unshield(string asset)
        {
            var payload = new PrivateTxPayload { Asset = asset, Kind = "z2t" };
            var tx = new Transaction
            {
                Timestamp = 50, FromAddress = PrivacyConstants.ShieldedPoolAddress, ToAddress = Recipient, Amount = 1.0m, Fee = 0m, Nonce = 0,
                TransactionType = TransactionType.VFX_UNSHIELD, Signature = "sig", Data = JsonConvert.SerializeObject(payload),
            };
            tx.BuildPrivate();
            return tx;
        }

        [Theory]
        [InlineData(0, false)]  // at the rules' height: applied to nothing
        [InlineData(-1, true)]  // the block before: applied as before
        public async Task AVfxUnshieldNamingAVbtcPool_CreditsNoVfx_FromTheRulesHeight(long offset, bool credited)
        {
            var block = new Block { Height = Globals.PathAuditRulesHeight + offset, StateRoot = "sr", Transactions = new List<Transaction>() };
            await PrivateTxLedgerService.ApplyBlockTransactionAsync(Unshield("VBTC:5e1f0c2a9b8d4e7f:1760000000"), block, _privacyDb);

            var balance = StateData.GetSpecificAccountStateTrei(Recipient)?.Balance ?? 0m;
            Assert.Equal(credited ? 1.0m : 0m, balance);
        }

        [Fact]
        public async Task AVfxUnshieldNamingTheVfxPool_IsStillCredited()
        {
            var block = new Block { Height = Globals.PathAuditRulesHeight, StateRoot = "sr", Transactions = new List<Transaction>() };
            await PrivateTxLedgerService.ApplyBlockTransactionAsync(Unshield("VFX"), block, _privacyDb);
            Assert.Equal(1.0m, StateData.GetSpecificAccountStateTrei(Recipient)?.Balance ?? 0m);
        }
    }
}
