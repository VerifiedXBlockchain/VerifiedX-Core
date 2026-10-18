using System;
using System.Collections.Generic;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From PrivateTxProofRulesHeight an unshield's amount has at most 8 decimal places: the proof binds the amount scaled
    /// by 10^8, so a credit with more digits paid out what was not proven. Below the height nothing changes.
    /// </summary>
    [Collection("PrivacySequential")]
    public class PrivacyStage2_UnshieldAmountPrecisionTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly long _priorProofGate = Globals.PrivateTxProofRulesHeight;

        public PrivacyStage2_UnshieldAmountPrecisionTests() => Globals.PrivateTxProofRulesHeight = Gate;

        public void Dispose() => Globals.PrivateTxProofRulesHeight = _priorProofGate;

        private static string Field() => Convert.ToBase64String(PrivacyField.RandomCanonical());

        private static (Transaction tx, PrivateTxPayload payload) Unshield(decimal amount)
        {
            var payload = new PrivateTxPayload
            {
                Version = 1, Kind = "unshield", Asset = "VFX",
                NullsB64 = new() { Field(), PrivacyEpoch.DummyNullifierB64 },
                Outs = new() { new PrivateShieldedOutput { Index = 0, NoteHashB64 = Field() } },
                TransparentAmount = amount,
            };
            return (new Transaction { Amount = amount, TransactionType = TransactionType.VFX_UNSHIELD }, payload);
        }

        [Fact]
        public void InTheEpoch_AnUnshieldPastTheEighthDecimal_IsRefused()
        {
            var (tx, payload) = Unshield(1.000000009M);
            Assert.Equal(PrivateTxProofRules.UnshieldAmountDecimalsReason, PrivateTxProofRules.Check(tx, payload, Gate + 1));
        }

        [Fact]
        public void InTheEpoch_AnUnshieldOfWholeSatoshis_Passes()
        {
            var (tx, payload) = Unshield(1.00000001M);
            Assert.Null(PrivateTxProofRules.Check(tx, payload, Gate + 1));
        }

        [Fact]
        public void BelowTheEpoch_TheAmountIsNotJudged()
        {
            var (tx, payload) = Unshield(1.000000009M);
            Assert.Null(PrivateTxProofRules.Check(tx, payload, Gate - 1));
        }
    }
}
