using System;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Models;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From the proof-rules height a node without its PLONK verifier refuses every block with a private transaction, so it
    /// does not offer to produce or cast until the verifier is loaded. Below the height nothing changes.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class ProofVerifierReadinessTests : IDisposable
    {
        private readonly long _priorRulesHeight = Globals.PrivateTxProofRulesHeight;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly Func<bool> _priorVerifier = PlonkProofVerifier.VerifierAvailable;
        private readonly bool _priorIsCaster = Globals.IsBlockCaster;
        private readonly string? _priorValidator = Globals.ValidatorAddress;

        public ProofVerifierReadinessTests()
        {
            Globals.LastBlock = new Block { Height = 1_000 };
        }

        public void Dispose()
        {
            Globals.PrivateTxProofRulesHeight = _priorRulesHeight;
            Globals.LastBlock = _priorLastBlock;
            PlonkProofVerifier.VerifierAvailable = _priorVerifier;
            Globals.IsBlockCaster = _priorIsCaster;
            Globals.ValidatorAddress = _priorValidator;
        }

        [Fact]
        public void AboveTheProofRulesHeight_ANodeWithoutTheVerifier_IsNotReady()
        {
            Globals.PrivateTxProofRulesHeight = 500;
            PlonkProofVerifier.VerifierAvailable = () => false;
            Assert.False(Globals.ProofVerifierReady);
            Assert.False(Globals.ProducerReady);

            PlonkProofVerifier.VerifierAvailable = () => true;
            Assert.True(Globals.ProofVerifierReady);
        }

        [Fact]
        public void BelowTheProofRulesHeight_TheVerifierIsNotRequired()
        {
            Globals.PrivateTxProofRulesHeight = 999_999_999_999;
            PlonkProofVerifier.VerifierAvailable = () => false;
            Assert.True(Globals.ProofVerifierReady);
        }

        [Fact]
        public void ACasterWithoutTheVerifier_ReportsItselfNotReady()
        {
            Globals.IsBlockCaster = true;
            Globals.ValidatorAddress = "xCaster";
            Globals.PrivateTxProofRulesHeight = 500;

            PlonkProofVerifier.VerifierAvailable = () => false;
            Assert.False(Ready(new ValidatorController().CasterReadyCheck(1_000)));

            PlonkProofVerifier.VerifierAvailable = () => true;
            Assert.True(Ready(new ValidatorController().CasterReadyCheck(1_000)));
        }

        private static bool Ready(ActionResult<string> result) =>
            (bool)JObject.Parse((string)((OkObjectResult)result.Result!).Value!)["Ready"]!;
    }
}
