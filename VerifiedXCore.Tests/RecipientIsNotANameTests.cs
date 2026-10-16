using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.RecipientIsAddressHeight no transaction is addressed to a registered name: every apply credits or
    /// assigns the ToAddress as written.
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
            var prior = Globals.RecipientIsAddressHeight;
            try
            {
                Globals.RecipientIsAddressHeight = Height;
                check();
            }
            finally { Globals.RecipientIsAddressHeight = prior; }
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

        [Fact]
        public void TheRuleShipsNotActive_UntilAHeightIsChosen()
        {
            Assert.Equal(999_999_999_999, Globals.RecipientIsAddressHeight);
        }
    }
}
