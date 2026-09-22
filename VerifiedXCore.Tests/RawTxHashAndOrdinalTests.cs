using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Guards two contracts external integrators depend on and cannot see from the API:
    /// the pinned <see cref="TransactionType"/> ordinals that appear as integers in block JSON,
    /// and the exact hash recipe documented for HSM pre-signing verification
    /// (docs/vBTC-Exchange-Multi-Transfer-Integration.md §3.3). Pure — no DB, no globals.
    /// </summary>
    public class RawTxHashAndOrdinalTests
    {
        // ── Ordinals ─────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(TransactionType.TX, 0)]
        [InlineData(TransactionType.VBTC_V2_TRANSFER, 26)]
        [InlineData(TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, 27)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_LOCK, 37)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_UNLOCK, 38)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, 39)]
        [InlineData(TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL, 42)]
        public void TransactionTypeOrdinals_ArePinned(TransactionType type, int expected)
        {
            // An insert above any of these would silently break every deposit scanner on upgrade.
            Assert.Equal(expected, (int)type);
        }

        // ── Hash recipe ──────────────────────────────────────────────────────────────────────

        /// <summary>Independent implementation of the documented recipe: SHA-256 of the UTF-8
        /// preimage → lowercase hex → SHA-256 of the UTF-8 bytes of THAT HEX TEXT → lowercase hex.</summary>
        private static string DocumentedDoubleSha256(string preimage)
        {
            using var sha = SHA256.Create();
            var first = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(preimage))).ToLowerInvariant();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(first))).ToLowerInvariant();
        }

        private static Transaction SampleMultiTransfer() => new Transaction
        {
            Timestamp = 1758400000,
            FromAddress = "RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7",
            ToAddress = "RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7",
            Amount = 0.0M,
            Fee = 0.00004776M,
            Nonce = 12,
            TransactionType = TransactionType.VBTC_V2_TRANSFER,
            Data = "{\"Function\":\"TransferVBTCMultiV2()\",\"FromAddress\":\"RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7\",\"ToAddress\":\"RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7\",\"TotalAmount\":0.60,\"Inputs\":[{\"SCUID\":\"a:1\",\"Amount\":0.40},{\"SCUID\":\"b:2\",\"Amount\":0.20}]}",
        };

        [Fact]
        public void Preimage_IsPlainConcatenation_WithEnumNameAndDecimalStrings()
        {
            var tx = SampleMultiTransfer();

            // Amount "0.0" (not "0"), Fee at its own scale, TransactionType as the NAME, no separators.
            var expected = "1758400000"
                + tx.FromAddress + tx.ToAddress
                + "0.0" + "0.00004776" + "12"
                + "VBTC_V2_TRANSFER"
                + tx.Data;

            Assert.Equal(expected, tx.GetHashPreimage());
        }

        [Fact]
        public void Hash_MatchesDocumentedRecipe_OverThePreimage()
        {
            var tx = SampleMultiTransfer();
            tx.Build();

            Assert.Equal(DocumentedDoubleSha256(tx.GetHashPreimage()), tx.Hash);
            Assert.Equal(tx.GetHash(), tx.Hash);
        }

        [Fact]
        public void SecondPassHashesHexText_NotBytes()
        {
            // Sanity: hashing the raw bytes of the first digest would give a different answer, so a
            // signer who implements "double SHA-256" the Bitcoin way gets a mismatch — by design.
            var tx = SampleMultiTransfer();
            using var sha = SHA256.Create();
            var firstBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(tx.GetHashPreimage()));
            var bitcoinStyle = Convert.ToHexString(sha.ComputeHash(firstBytes)).ToLowerInvariant();

            Assert.NotEqual(bitcoinStyle, tx.GetHash());
        }

        [Fact]
        public void Preimage_AppendsUnlockTime_OnlyWhenSet()
        {
            var tx = SampleMultiTransfer();
            var without = tx.GetHashPreimage();

            tx.UnlockTime = 1758486400;
            Assert.Equal(without + "1758486400", tx.GetHashPreimage());
        }
    }
}
