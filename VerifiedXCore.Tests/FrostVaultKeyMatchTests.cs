using System;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Follow-up hardening: a key package may be used or relabelled for a contract only when its
    /// group public key IS the contract's on-chain vault key. Before this, sign/start's
    /// leader-supplied CeremonyId fallback relabelled ANY key record onto the declared contract,
    /// letting a leader shadow a vault's real key with a junk DKG's key (signing DoS for that vault).
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostVaultKeyMatchTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private const string Me = "xValidatorMe";

        public FrostVaultKeyMatchTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vaultkey_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        [Fact]
        public void DifferentGroupKey_Refused()
        {
            Assert.False(FrostDkgGuard.KeyPackageMatchesContract("JUNK-GROUP", "REAL-GROUP"));
        }

        [Fact]
        public void SameGroupKey_CaseInsensitive_Accepted()
        {
            Assert.True(FrostDkgGuard.KeyPackageMatchesContract("abcdef", "ABCDEF"));
            Assert.True(FrostDkgGuard.KeyPackageMatchesContract(" abcdef ", "abcdef"));
        }

        [Fact]
        public void UnknownOnEitherSide_FailsClosed()
        {
            Assert.False(FrostDkgGuard.KeyPackageMatchesContract("", "REAL-GROUP"));
            Assert.False(FrostDkgGuard.KeyPackageMatchesContract("REAL-GROUP", ""));
            Assert.False(FrostDkgGuard.KeyPackageMatchesContract(null, null));
        }

        [Fact]
        public void Relabel_RefusedWhenTargetContractAlreadyHasDifferentGroupKey()
        {
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore { SmartContractUID = "sc-real", ValidatorAddress = Me, KeyPackage = "real", PubkeyPackage = "p", GroupPublicKey = "REAL" }));
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore { SmartContractUID = "ceremony-junk", ValidatorAddress = Me, KeyPackage = "junk", PubkeyPackage = "p", GroupPublicKey = "JUNK" }));
            var junk = FrostValidatorKeyStore.GetKeyPackage("ceremony-junk", Me)!;

            Assert.False(FrostValidatorKeyStore.UpdateSmartContractUID(junk.Id, "sc-real"));

            var real = FrostValidatorKeyStore.GetKeyPackage("sc-real", Me);
            Assert.NotNull(real);
            Assert.Equal("REAL", real!.GroupPublicKey);
            Assert.Equal("real", real.KeyPackage);
            Assert.Equal("ceremony-junk", FrostValidatorKeyStore.GetKeyPackage("ceremony-junk", Me)!.SmartContractUID);
        }

        [Fact]
        public void Relabel_AllowedWhenNoCollision()
        {
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore { SmartContractUID = "ceremony-1", ValidatorAddress = Me, KeyPackage = "k", PubkeyPackage = "p", GroupPublicKey = "G1" }));
            var rec = FrostValidatorKeyStore.GetKeyPackage("ceremony-1", Me)!;
            Assert.True(FrostValidatorKeyStore.UpdateSmartContractUID(rec.Id, "sc-new"));
            Assert.NotNull(FrostValidatorKeyStore.GetKeyPackage("sc-new", Me));
        }
    }
}
