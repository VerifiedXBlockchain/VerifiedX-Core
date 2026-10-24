using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// An operator reset ends the era of the genesis the node held, and every older one: such a genesis stays validly signed
    /// forever, and any peer could hand it back. A newer era is still adopted, whenever it was minted. The reset used to
    /// remember the tip instead and refuse every genesis below it: a node reset after the seeds minted the current genesis
    /// (mainnet, genesis effective at 7,562,264, nodes reset near 7,568,000) could never adopt it, and waited for a record
    /// for good.
    /// </summary>
    [Collection("DbContextSequential")]
    public class OperatorResetGenesisTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorCertHeight = Globals.CertEnforceHeight;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _seedSigners = new();

        public OperatorResetGenesisTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"reset_genesis_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            CasterMembershipStore.InvalidateCache();
            for (var i = 0; i < CasterMembershipStore.GenesisMinSeedSignatures; i++)
            {
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                var address = AccountData.GetHumanAddress(pub);
                _seedSigners.Add((key, pub, address));
                Globals.BootstrapCasterAddresses.Add(address);
            }
        }

        public void Dispose()
        {
            foreach (var s in _seedSigners) Globals.BootstrapCasterAddresses.Remove(s.Address);
            Globals.LastBlock = _priorLastBlock;
            Globals.CertEnforceHeight = _priorCertHeight;
            CasterMembershipStore.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            CasterMembershipStore.InvalidateCache();
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>A genesis effective at <paramref name="height"/>, signed by enough seed-allowlisted keys to be valid.</summary>
        private CasterMembershipRecord SignedGenesis(long height)
        {
            var genesis = CasterMembershipStore.BuildGenesisRecord(height);
            var payload = CasterMembershipStore.CanonicalPayload(genesis);
            genesis.Signatures = _seedSigners
                .Select(s => new RecordSignature { SignerAddress = s.Address, Signature = SignatureService.CreateSignature(payload, s.Key, s.Pub) })
                .ToList();
            return genesis;
        }

        /// <summary>The node holds the era of a genesis effective at 1,000 and is reset at tip 5,000.</summary>
        private void HoldEraFrom1000_ThenResetAtTip5000()
        {
            Assert.True(CasterMembershipStore.TryAppend(SignedGenesis(1_000), out var held), held);
            Globals.LastBlock = new Block { Height = 5_000 };
            CasterMembershipStore.ClearAllForOperatorReset();
        }

        [Fact]
        public void AfterAReset_ANewerEraIsAdopted_EvenWhenItsGenesisTakesEffectBelowTheTip()
        {
            HoldEraFrom1000_ThenResetAtTip5000();

            Assert.True(CasterMembershipStore.TryAppend(SignedGenesis(2_000), out var reason), reason);
            Assert.Equal(2_000, CasterMembershipStore.GetCurrent()?.EffectiveFromHeight);
        }

        [Fact]
        public void AfterAReset_TheClearedEraAndOlderOnes_StayEnded()
        {
            HoldEraFrom1000_ThenResetAtTip5000();
            Assert.Equal(1_000, CasterMembershipStore.ClearedGenesisFromHeight());
            Assert.Equal(5_000, CasterMembershipStore.LastOperatorResetHeight());

            Assert.False(CasterMembershipStore.TryAppend(SignedGenesis(1_000), out var same));
            Assert.Contains("operator reset", same);
            Assert.False(CasterMembershipStore.TryAppend(SignedGenesis(800), out var older));
            Assert.Contains("operator reset", older);
            Assert.Null(CasterMembershipStore.GetCurrent());
        }

        [Fact]
        public void ALaterReset_KeepsTheNewestEndedEra()
        {
            HoldEraFrom1000_ThenResetAtTip5000();
            Assert.True(CasterMembershipStore.TryAppend(SignedGenesis(2_000), out var adopted), adopted);
            CasterMembershipStore.ClearAllForOperatorReset();

            Assert.Equal(2_000, CasterMembershipStore.ClearedGenesisFromHeight());
            Assert.False(CasterMembershipStore.TryAppend(SignedGenesis(1_500), out var between));
            Assert.Contains("operator reset", between);

            // A reset with no record held does not lower it.
            CasterMembershipStore.ClearAllForOperatorReset();
            Assert.Equal(2_000, CasterMembershipStore.ClearedGenesisFromHeight());
        }

        [Fact]
        public void AResetWithNoRecordHeld_EndsNoEra()
        {
            Globals.LastBlock = new Block { Height = 5_000 };
            CasterMembershipStore.ClearAllForOperatorReset();

            Assert.Equal(0, CasterMembershipStore.ClearedGenesisFromHeight());
            Assert.True(CasterMembershipStore.TryAppend(SignedGenesis(4_000), out var reason), reason);
        }
    }
}
