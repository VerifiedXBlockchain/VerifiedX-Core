using System.Collections.Generic;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A shield spends no note, so from Globals.ShieldSpendsNothingHeight (and at admission at every height) it names no
    /// nullifier, fee-leg nullifier, spent position or spent commitment: the apply recorded what it named, so a nullifier
    /// seen in the mempool and attached to a shield stranded that note for good.
    /// </summary>
    public class ShieldSpendsNothingTests
    {
        private static PrivateTxPayload Shield() => new() { Kind = "shield", SubType = "Shield", Asset = "VFX", Outs = { new PrivateShieldedOutput { NoteHashB64 = "AA==" } } };
        private static Transaction Tx(TransactionType type) => new() { TransactionType = type };

        [Theory]
        [InlineData(TransactionType.VFX_SHIELD)]
        [InlineData(TransactionType.VBTC_V2_SHIELD)]
        public void AShieldNamingAnythingItWouldSpend_IsRefused(TransactionType type)
        {
            Assert.Null(PrivateTxProofRules.ShieldSpendsNothing(Tx(type), Shield())); // the honest shield

            var withNullifier = Shield(); withNullifier.NullsB64 = new List<string> { "c29tZW9uZSBlbHNlJ3Mgbm90ZQ==" };
            var withFeeLeg = Shield(); withFeeLeg.FeeInputNullifierB64 = "c29tZW9uZSBlbHNlJ3Mgbm90ZQ==";
            var withPosition = Shield(); withPosition.SpentCommitmentTreePositions = new List<long> { 7 };
            var withCommitment = Shield(); withCommitment.SpentCommitmentB64s = new List<string> { "AA==" };
            foreach (var p in new[] { withNullifier, withFeeLeg, withPosition, withCommitment })
                Assert.Equal(PrivateTxProofRules.ShieldSpendsNothingReason, PrivateTxProofRules.ShieldSpendsNothing(Tx(type), p));
        }

        [Fact]
        public void ASpendingType_IsNotAShieldsBusiness()
        {
            var unshield = Shield(); unshield.NullsB64 = new List<string> { "AA==", "AQ==" };
            Assert.Null(PrivateTxProofRules.ShieldSpendsNothing(Tx(TransactionType.VFX_UNSHIELD), unshield));
            Assert.Null(PrivateTxProofRules.ShieldSpendsNothing(Tx(TransactionType.VFX_PRIVATE_TRANSFER), unshield));
        }
    }
}
