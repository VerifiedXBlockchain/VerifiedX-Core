using System;
using System.Security.Cryptography;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A sealed note matches a leaf only for an amount the chain's 8-decimal scale holds exactly. Scaling truncates, so
    /// amounts that differ below the 8th decimal all matched one leaf, each was a different note to the wallet, and the
    /// scanner credited each.
    /// </summary>
    public class NoteAmountAliasTests
    {
        [Fact]
        public void OnlyTheExactAmount_MatchesTheLeaf_NotAnAliasBelowTheEighthDecimal()
        {
            Assert.True(PoseidonV1.IsAvailable, "the note hash needs the native library");
            var r = new byte[32];
            RandomNumberGenerator.Fill(r);
            r = PrivacyField.FromUInt64(123_456_789UL); // a canonical field element
            var rec = new CommitmentRecord { NoteHash = Convert.ToBase64String(NoteHashService.Compute(500_000_000UL, r)) };
            var keys = new ShieldedKeyMaterial { ViewingKey32 = PrivacyField.FromUInt64(987_654_321UL) };

            Assert.True(PrivacyApiHelper.NoteMatchesRecord(rec, 5M, r, keys));
            Assert.True(PrivacyApiHelper.NoteMatchesRecord(rec, 5.00000000M, r, keys));
            Assert.False(PrivacyApiHelper.NoteMatchesRecord(rec, 5.0000000001M, r, keys));
            Assert.False(PrivacyApiHelper.NoteMatchesRecord(rec, 5.0000000099M, r, keys));
        }
    }
}
