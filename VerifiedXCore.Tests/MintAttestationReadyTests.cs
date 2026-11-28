using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A lock's collected mint progress is ready when both of the contract's thresholds are met, its validators' and
    /// its casters', not when a count of signatures passes one of them.
    /// </summary>
    public class MintAttestationReadyTests
    {
        [Fact]
        public void EnoughSignaturesFromOneGroup_IsNotReady()
        {
            // Three validator signatures against a validator threshold of two, and none of the casters the contract also requires.
            var state = new MintAttestationState
            {
                LockId = "lock", RequiredSignatures = 2, Status = "Pending",
                ValidatorSignatures = new Dictionary<string, string> { ["v1"] = "s", ["v2"] = "s", ["v3"] = "s" },
            };
            Assert.False(state.IsReady);
            Assert.Contains("\"IsReady\":false", JsonConvert.SerializeObject(state));

            state.Status = "Ready"; // what the collector sets once both thresholds are met
            Assert.True(state.IsReady);
            Assert.Contains("\"IsReady\":true", JsonConvert.SerializeObject(state));
        }

    }
}
