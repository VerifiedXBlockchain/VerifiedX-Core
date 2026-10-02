using System;
using System.Collections.Generic;
using System.IO;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The coordinator-side lookup behind <see cref="FrostAggregateParticipantOrderTests"/>: a coordinator outside the
    /// vault's DKG reads the participant list from the contract. Spyglass's node usually has no local vault record, so
    /// the State Trei path is the one web wallet withdrawals depend on.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostAggregateContractLookupTests : IDisposable
    {
        private const string Vault = "c0ffee00c0ffee00c0ffee00c0ffee00:1790822389";
        private const string Owner = "xVaultOwner";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public FrostAggregateContractLookupTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"frost_lookup_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void SaveVaultToStateTrei(IEnumerable<string> snapshot) =>
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault,
                ContractData = VbtcTestContracts.VaultContractData(Vault, Owner, "bc1pvault", snapshot: snapshot),
                MinterAddress = Owner,
                OwnerAddress = Owner,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>(),
            });

        [Fact]
        public void StateTreiSnapshot_IsChosenAndAggregates()
        {
            SaveVaultToStateTrei(FrostAggregateParticipantOrderTests.DkgParticipants);
            var (_, pubkeyPackage) = FrostAggregateParticipantOrderTests.RealDkg();

            var participants = FrostMPCService.GetContractDkgParticipants(Vault);
            Assert.Equal(FrostAggregateParticipantOrderTests.DkgParticipants, participants);

            var order = FrostMPCService.ResolveAggregationOrder(
                null, participants, pubkeyPackage, FrostAggregateParticipantOrderTests.SignersWithoutBravo, out var source);
            Assert.Equal("contract DKG participants", source);
            Assert.Equal(FrostNative.SUCCESS,
                FrostAggregateParticipantOrderTests.SignAndAggregate(FrostAggregateParticipantOrderTests.SignersWithoutBravo, order));
        }

        [Fact]
        public void LocalContractSnapshot_TakesPrecedenceOverStateTrei()
        {
            SaveVaultToStateTrei(new List<string> { "xStateTreiOnly" });
            VBTCContractV2.SaveContract(new VBTCContractV2
            {
                SmartContractUID = Vault,
                OwnerAddress = Owner,
                DepositAddress = "bc1pvault",
                ValidatorAddressesSnapshot = new List<string>(FrostAggregateParticipantOrderTests.DkgParticipants),
                FrostGroupPublicKey = "02" + new string('a', 64),
                DKGProof = "proof",
            });

            Assert.Equal(FrostAggregateParticipantOrderTests.DkgParticipants, FrostMPCService.GetContractDkgParticipants(Vault));
        }

        [Fact]
        public void UnknownContract_FallsBackToTheSigners()
        {
            var participants = FrostMPCService.GetContractDkgParticipants("deadbeefdeadbeefdeadbeefdeadbeef:1");
            Assert.Null(participants);

            var order = FrostMPCService.ResolveAggregationOrder(
                null, participants, "{}", FrostAggregateParticipantOrderTests.SignersWithoutBravo, out var source);
            Assert.Same(FrostAggregateParticipantOrderTests.SignersWithoutBravo, order);
            Assert.StartsWith("signer addresses", source);
        }
    }
}
