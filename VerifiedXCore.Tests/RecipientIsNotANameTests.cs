using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// C-123: from Globals.PathAuditRulesHeight no transaction is addressed to a registered name: every apply credits or
    /// assigns the ToAddress as written, so a name accepted by validation (resolved through the node's own table) left the
    /// VFX, token, NFT or name on the literal string.
    /// </summary>
    [Collection("DbContextSequential")]
    public class RecipientIsNotANameTests
    {
        private const long Height = 5000;

        private static string NewAddress()
        {
            var key = new PrivateKey("secp256k1");
            return AccountData.GetHumanAddress("04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
        }

        private static Transaction To(string to, TransactionType type) =>
            new Transaction { FromAddress = NewAddress(), ToAddress = to, TransactionType = type, Data = "{}" };

        private static void AtHeight(Action check)
        {
            var prior = Globals.PathAuditRulesHeight;
            try
            {
                Globals.PathAuditRulesHeight = Height;
                check();
            }
            finally { Globals.PathAuditRulesHeight = prior; }
        }

        [Theory]
        [InlineData(TransactionType.ADNR, "alice.vfx")]
        [InlineData(TransactionType.NFT_TX, "bob.rbx")]
        [InlineData(TransactionType.FTKN_TX, "Carol.VFX")]
        [InlineData(TransactionType.TX, "dave.vfx")]
        public void FromTheHeight_ANameAsRecipientIsRefused(TransactionType type, string name) => AtHeight(() =>
        {
            Assert.NotNull(LedgerIntegrityRules.RecipientIsNotAName(To(name, type), Height));
        });

        [Fact]
        public void BeforeTheHeight_ANameAsRecipientIsLeftToTheOlderRules() => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.RecipientIsNotAName(To("alice.vfx", TransactionType.ADNR), Height - 1));
        });

        [Theory]
        [InlineData(TransactionType.ADNR)]
        [InlineData(TransactionType.NFT_TX)]
        [InlineData(TransactionType.TX)]
        public void FromTheHeight_AnAddressAsRecipientStillPasses(TransactionType type) => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.RecipientIsNotAName(To(NewAddress(), type), Height));
        });

        [Theory]
        [InlineData("Adnr_Base", TransactionType.ADNR)]
        [InlineData("Token_Base", TransactionType.FTKN_MINT)]
        [InlineData("DecShop_Base", TransactionType.DSTR)]
        public void FromTheHeight_PlaceholderRecipientsStillPass(string to, TransactionType type) => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.RecipientIsNotAName(To(to, type), Height));
        });

        [Fact]
        public void FromTheHeight_PrivateTransactionsKeepTheirOwnAddressRules() => AtHeight(() =>
        {
            Assert.Null(LedgerIntegrityRules.RecipientIsNotAName(To("alice.vfx", TransactionType.VFX_PRIVATE_TRANSFER), Height));
        });

        /// <summary>Through VerifyTX, as block validation (at the block's height) and admission (at tip + 1) judge it.</summary>
        [Theory]
        [InlineData(TransactionType.NFT_TX)]
        [InlineData(TransactionType.ADNR)]
        [InlineData(TransactionType.FTKN_TX)]
        public async Task VerifyTX_RefusesANameAsRecipient_FromThePathAuditRules(TransactionType type)
        {
            var root = Path.Combine(Path.GetTempPath(), $"recipname_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            var priorPath = Globals.CustomPath;
            var priorTip = Globals.LastBlock;
            var priorGate = Globals.PathAuditRulesHeight;
            Globals.CustomPath = root;
            DbContext.Initialize();
            Globals.PathAuditRulesHeight = Height;
            Globals.LastBlock = new Block { Height = Height - 1, Timestamp = TimeUtil.GetTime() };
            try
            {
                var tx = To("averyveryverylongregisteredname1.vfx", type);
                var (blockOk, blockWhy) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Height);
                Assert.False(blockOk);
                Assert.Equal(LedgerIntegrityRules.RecipientIsANameReason, blockWhy);
                var (admitted, admissionWhy) = await TransactionValidatorService.VerifyTX(tx);
                Assert.False(admitted);
                Assert.Equal(LedgerIntegrityRules.RecipientIsANameReason, admissionWhy);
                var (_, belowWhy) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Height - 1);
                Assert.NotEqual(LedgerIntegrityRules.RecipientIsANameReason, belowWhy);   // history replays as it was
            }
            finally
            {
                try { DbContext.CloseDB(); } catch { }
                Globals.PathAuditRulesHeight = priorGate;
                Globals.LastBlock = priorTip;
                Globals.CustomPath = priorPath;
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
