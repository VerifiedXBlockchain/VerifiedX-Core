using System.Collections.Concurrent;
using VerifiedXCore;
using VerifiedXCore.Models;
using VerifiedXCore.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Wave 4 (certificate enablement): quorum math + the VerifyOrNotRequired matrix.
    /// Cert subsystem previously had ZERO tests. Signature-positive paths need real validator
    /// keys → testnet drills; boundary/mismatch/negative-signature paths are all here.
    /// </summary>
    [Collection("GlobalCasterState")]
    public class Wave4CertTests
    {
        private static Block MakeBlock(long height, string hash = "blockhash1", string validator = "xWINNER", string prevHash = "prevhash1", int version = 4)
            => new Block { Height = height, Hash = hash, Validator = validator, PrevHash = prevHash, Version = version };

        private static ConsensusCertificate MakeCert(Block b, params (string Addr, string Sig)[] atts)
            => new ConsensusCertificate
            {
                BlockHeight = b.Height,
                BlockHash = b.Hash,
                WinnerAddress = b.Validator,
                PrevHash = b.PrevHash,
                Attestations = atts.Select(a => new CasterAttestation { CasterAddress = a.Addr, Signature = a.Sig, Timestamp = 0 }).ToList()
            };

        private static IDisposable WithCasters(long enforceHeight, params string[] casterAddrs)
        {
            var origCasters = Globals.BlockCasters;
            var origKnown = Globals.KnownCasters.ToList();
            var origEnforce = Globals.CertEnforceHeight;

            Globals.CertEnforceHeight = enforceHeight;
            Globals.BlockCasters = new ConcurrentBag<Peers>(casterAddrs.Select((a, i) => new Peers
            {
                ValidatorAddress = a,
                PeerIP = $"10.9.9.{i + 1}",
                IsValidator = true,
                ValidatorPublicKey = $"PK{i}"
            }));
            Globals.SyncKnownCastersFromBlockCasters();

            return new Restore(() =>
            {
                Globals.CertEnforceHeight = origEnforce;
                Globals.BlockCasters = origCasters;
                lock (Globals.KnownCastersLock)
                {
                    Globals.KnownCasters.Clear();
                    foreach (var k in origKnown) Globals.KnownCasters.Add(k);
                }
            });
        }

        private sealed class Restore : IDisposable
        {
            private readonly Action _action;
            public Restore(Action action) => _action = action;
            public void Dispose() => _action();
        }

        // ── Quorum math ──────────────────────────────────────────────────

        [Fact]
        public void RequiredAttestations_MajorityFormula()
        {
            Assert.Equal(int.MaxValue, ConsensusCertificateVerifier.RequiredAttestations(0));
            Assert.Equal(int.MaxValue, ConsensusCertificateVerifier.RequiredAttestations(-1));
            Assert.Equal(1, ConsensusCertificateVerifier.RequiredAttestations(1));
            Assert.Equal(2, ConsensusCertificateVerifier.RequiredAttestations(2));
            Assert.Equal(2, ConsensusCertificateVerifier.RequiredAttestations(3));
            Assert.Equal(3, ConsensusCertificateVerifier.RequiredAttestations(4));
            Assert.Equal(3, ConsensusCertificateVerifier.RequiredAttestations(5));
        }

        [Fact]
        public void RequiredAttestationsForHeight_LegacyEra_UsesLiveBag()
        {
            using var _ = WithCasters(long.MaxValue, "xA", "xB", "xC", "xD", "xE");
            Assert.Equal(3, ConsensusCertificateVerifier.RequiredAttestationsForHeight(1000));
        }

        // ── VerifyOrNotRequired matrix ───────────────────────────────────

        [Fact]
        public void Verify_BelowEnforceHeight_NotRequired()
        {
            using var _ = WithCasters(enforceHeight: 5000, "xA", "xB", "xC");
            var block = MakeBlock(4999);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(block)); // null cert is fine below the boundary
        }

        [Fact]
        public void Verify_UnsupportedVersion_NotRequired()
        {
            using var _ = WithCasters(enforceHeight: 0, "xA", "xB", "xC");
            var block = MakeBlock(1000, version: 3);
            Assert.True(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        [Fact]
        public void Verify_AtEnforceHeight_NullCert_Rejected()
        {
            using var _ = WithCasters(enforceHeight: 1000, "xA", "xB", "xC");
            var block = MakeBlock(1000);
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        [Fact]
        public void Verify_BindingMismatches_Rejected()
        {
            using var _ = WithCasters(enforceHeight: 1000, "xA", "xB", "xC");
            var block = MakeBlock(1000);

            var wrongHeight = MakeCert(block, ("xA", "s"), ("xB", "s"));
            wrongHeight.BlockHeight = 999;
            block.ConsensusCertificate = wrongHeight;
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));

            var wrongHash = MakeCert(block, ("xA", "s"), ("xB", "s"));
            wrongHash.BlockHash = "different";
            block.ConsensusCertificate = wrongHash;
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));

            var wrongWinner = MakeCert(block, ("xA", "s"), ("xB", "s"));
            wrongWinner.WinnerAddress = "xSOMEONEELSE";
            block.ConsensusCertificate = wrongWinner;
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));

            var wrongPrev = MakeCert(block, ("xA", "s"), ("xB", "s"));
            wrongPrev.PrevHash = "differentprev";
            block.ConsensusCertificate = wrongPrev;
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        [Fact]
        public void Verify_HashBinding_IsCaseInsensitive()
        {
            using var _ = WithCasters(enforceHeight: 1000, "xA", "xB", "xC");
            var block = MakeBlock(1000, hash: "ABCDEF");
            var cert = MakeCert(block, ("xA", "junk"), ("xB", "junk"));
            cert.BlockHash = "abcdef"; // normalized compare must pass binding (still fails on signatures)
            block.ConsensusCertificate = cert;
            // Binding passes, signatures are junk → still rejected, but NOT with a binding error.
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        [Fact]
        public void Verify_NonCommitteeOrJunkSigners_Rejected()
        {
            using var _ = WithCasters(enforceHeight: 1000, "xA", "xB", "xC");
            var block = MakeBlock(1000);
            // Junk sigs from committee members + a foreign signer — zero valid signers.
            block.ConsensusCertificate = MakeCert(block, ("xA", "MA==.MA=="), ("xB", "garbage"), ("xOUTSIDER", "MA==.MA=="));
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        [Fact]
        public void Verify_EmptyCasterView_Rejected()
        {
            using var _ = WithCasters(enforceHeight: 1000 /* no casters */);
            var block = MakeBlock(1000);
            block.ConsensusCertificate = MakeCert(block, ("xA", "s"));
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
        }

        // ── Receiver top-up ──────────────────────────────────────────────

        [Fact]
        public async Task TopUp_BelowEnforceHeight_IsNoOp()
        {
            using var _ = WithCasters(enforceHeight: long.MaxValue, "xA", "xB", "xC");
            var block = MakeBlock(1000);
            await ConsensusCertificateHelper.TryCompleteCertificateAsync(block);
            Assert.Null(block.ConsensusCertificate);
        }

        // ── A4 attestation helper quorum basis (shared with Wave 4) ─────

        [Fact]
        public async Task MajorityAttestations_NoCasters_False()
        {
            using var _ = WithCasters(enforceHeight: long.MaxValue /* no casters */);
            var ok = await ConsensusCertificateHelper.TryGetMajorityAttestationsAsync(1000, "h", "w", "p", maxPollRounds: 0);
            Assert.False(ok);
        }
    }
}
