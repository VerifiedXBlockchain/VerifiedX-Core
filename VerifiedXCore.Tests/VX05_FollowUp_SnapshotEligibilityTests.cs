using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-05 (follow-up, found by the owner's audit review): received proofs are checked against the validator's live
    /// balance and the ABL (ValidateIncomingProof), but proofs a caster generates itself from the validator snapshot were
    /// not. A snapshot validator that moved its coins out while its node kept running was elected locally on every caster
    /// and refused from every other, so the round sat under quorum at each height where it had the lowest VRF.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX05_FollowUp_SnapshotEligibilityTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly List<ValidatorSnapshotEntry> _priorEntries;
        private readonly long _priorSnapshotHeight;
        private readonly bool _priorSweep;
        private static readonly FieldInfo EntriesField = typeof(ValidatorSnapshotService).GetField("_snapshotEntries", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly FieldInfo HeightField = typeof(ValidatorSnapshotService).GetField("_snapshotHeight", BindingFlags.NonPublic | BindingFlags.Static)!;

        public VX05_FollowUp_SnapshotEligibilityTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx05fu_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorEntries = (List<ValidatorSnapshotEntry>)EntriesField.GetValue(null)!;
            _priorSnapshotHeight = (long)HeightField.GetValue(null)!;
            _priorSweep = Globals.ValidatorLivenessSweepComplete;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 99, Hash = "prevhash99" };
            Globals.ValidatorLivenessSweepComplete = false; // no liveness filter: only eligibility decides
        }

        public void Dispose()
        {
            EntriesField.SetValue(null, _priorEntries);
            HeightField.SetValue(null, _priorSnapshotHeight);
            Globals.ValidatorLivenessSweepComplete = _priorSweep;
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (string Pub, string Address) NewValidator()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (pub, AccountData.GetHumanAddress(pub));
        }

        private static Task<List<Proof>> GenerateFromSnapshot() =>
            (Task<List<Proof>>)typeof(ProofUtility).GetMethod("GenerateProofsFromSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        [Fact]
        public async Task VX05_FollowUp_SnapshotValidatorThatMovedItsCoins_GetsNoLocalProof()
        {
            var funded = NewValidator();
            var drained = NewValidator(); // in the snapshot, then moved its coins out
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = funded.Address, Balance = 5_000M });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = drained.Address, Balance = 10M });
            EntriesField.SetValue(null, new List<ValidatorSnapshotEntry>
            {
                new() { Address = funded.Address, PublicKey = funded.Pub, Balance = 5_000M },
                new() { Address = drained.Address, PublicKey = drained.Pub, Balance = 5_000M },
            });
            HeightField.SetValue(null, ValidatorSnapshotService.SnapshotAnchor(100));

            var proofs = await GenerateFromSnapshot();

            Assert.Contains(proofs, p => p.Address == funded.Address);
            Assert.DoesNotContain(proofs, p => p.Address == drained.Address);
            // Every generated proof is one the other casters accept when it arrives.
            foreach (var p in proofs)
                Assert.True(ProofUtility.ValidateIncomingProof(p, 100, "prevhash99", out var reason), reason);
        }

        [Fact]
        public async Task VX05_FollowUp_SnapshotValidatorOnTheAbl_GetsNoLocalProof()
        {
            var banned = NewValidator();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = banned.Address, Balance = 5_000M });
            EntriesField.SetValue(null, new List<ValidatorSnapshotEntry> { new() { Address = banned.Address, PublicKey = banned.Pub, Balance = 5_000M } });
            HeightField.SetValue(null, ValidatorSnapshotService.SnapshotAnchor(100));
            Globals.ABL.Add(banned.Address);
            try { Assert.Empty(await GenerateFromSnapshot()); }
            finally { Globals.ABL.Remove(banned.Address); }
        }

        [Fact]
        public void VX05_FollowUp_OneEligibilityRule()
        {
            var funded = NewValidator();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = funded.Address, Balance = 5_000M });
            Assert.Null(ProofUtility.ProducerIneligibility(funded.Address));
            Assert.Equal("not-eligible", ProofUtility.ProducerIneligibility(NewValidator().Address));
            Assert.Equal("not-eligible", ProofUtility.ProducerIneligibility(null));
        }
    }
}
