using System;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Security regression: the backup store request was signed over owner/contract/timestamp only,
    /// so a captured request could be replayed within the freshness window with a garbage blob and
    /// overwrite the honest backup on every peer. The signed message now commits to the blob hash.
    /// </summary>
    public class FrostBackupStoreSignatureTests
    {
        [Fact]
        public void Message_CommitsToBlob()
        {
            var a = FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 1_700_000_000, 1, "G", "PH", "blob-A");
            var b = FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 1_700_000_000, 1, "G", "PH", "blob-B");
            Assert.NotEqual(a, b);
            Assert.StartsWith("xOwner.sc-1.1700000000.1.g.ph.", a);
            Assert.Equal(64, a.Substring("xOwner.sc-1.1700000000.1.g.ph.".Length).Length); // sha256 hex
        }

        [Fact]
        public void Message_CommitsToGroupKey_PlaintextHash_AndVersion()
        {
            var baseMsg = FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 1, 1, "G1", "PH1", "blob");
            Assert.NotEqual(baseMsg, FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 1, 1, "G2", "PH1", "blob"));
            Assert.NotEqual(baseMsg, FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 1, 1, "G1", "PH2", "blob"));
            Assert.NotEqual(baseMsg, FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 1, 2, "G1", "PH1", "blob"));
            // case/whitespace-normalized labels do not change the message
            Assert.Equal(baseMsg, FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 1, 1, " g1 ", "ph1", "blob"));
        }

        [Fact]
        public void Message_IsDeterministic()
        {
            var a = FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 42, 1, "G", "PH", "blob");
            var b = FrostKeyBackupService.BuildStoreSignMessage("xOwner", "sc-1", 42, 1, "G", "PH", "blob");
            Assert.Equal(a, b);
        }

        [Fact]
        public void SignatureOverOneBlob_DoesNotVerifyForAnotherBlob()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var owner = AccountData.GetHumanAddress(pub);
            long ts = 1_700_000_000;

            var signedMsg = FrostKeyBackupService.BuildStoreSignMessage(owner, "sc-1", ts, 1, "G", "PH", "honest-blob");
            var sig = VerifiedXCore.Services.SignatureService.CreateSignature(signedMsg, key, pub);

            Assert.True(VerifiedXCore.Services.SignatureService.VerifySignature(owner, signedMsg, sig));

            var replayedWithGarbage = FrostKeyBackupService.BuildStoreSignMessage(owner, "sc-1", ts, 1, "G", "PH", "garbage-blob");
            Assert.False(VerifiedXCore.Services.SignatureService.VerifySignature(owner, replayedWithGarbage, sig));
        }
    }
}
