using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A validator's records of what it signed are not in its peers' key backups. A share restored from them is marked
    /// with when it was restored, and on a vault the chain does not watch it gives no share for a payment opened before
    /// then: the machine it replaced may already have signed one transaction for it. A share this node kept is not
    /// affected.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostRestoredShareTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string? _priorValidator = Globals.ValidatorAddress;
        private readonly string _address;
        private const string Vault = "sc-unwatched-vault";
        private const string Request = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

        public FrostRestoredShareTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"restored_share_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _address = AccountData.GetHumanAddress(pub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = _address, PublicKey = pub, PrivateKey = "0" + key.secret.ToString("x") });
            Globals.ValidatorAddress = _address;
        }

        public void Dispose()
        {
            FrostKeyBackupService.FetchFromPeerForTests = null;
            Globals.ValidatorAddress = _priorValidator;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private FrostValidatorKeyStore Share(long restoredAt, string filedUnder = Vault)
        {
            Assert.True(FrostValidatorKeyStore.SaveKeyPackage(new FrostValidatorKeyStore
            {
                SmartContractUID = filedUnder, ValidatorAddress = _address, KeyPackage = "k", PubkeyPackage = "p", GroupPublicKey = "02" + new string('a', 64),
                ParticipantOrderJson = "[]", CreatedTimestamp = 1, RestoredFromPeersAt = restoredAt,
            }));
            return FrostValidatorKeyStore.GetKeyPackage(filedUnder, _address)!;
        }

        private static void RequestAt(long height, long blockTimestamp)
        {
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = height, Timestamp = blockTimestamp, Hash = "h" + height, PrevHash = "p", Validator = "v", Transactions = new List<Transaction>() });
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest { TransactionHash = Request, SmartContractUID = Vault, RequestBlockHeight = height }));
            Assert.Equal(height, VBTCWithdrawalRequest.GetByTransactionHash(Request, Vault)?.RequestBlockHeight);
            Assert.Equal(blockTimestamp, BlockchainData.GetBlockByHeight(height)?.Timestamp);
        }

        [Fact]
        public void A_share_restored_after_a_payment_opened_gives_no_share_for_it()
        {
            RequestAt(100, blockTimestamp: 1_000);
            var share = Share(restoredAt: 2_000);
            Assert.Contains("restored from peers", FrostStartup.RestoredShareRefusal(share, Vault, Request));
        }

        [Fact]
        public void A_share_restored_before_a_payment_opened_signs_it()
        {
            RequestAt(100, blockTimestamp: 3_000);
            var share = Share(restoredAt: 2_000);
            Assert.Null(FrostStartup.RestoredShareRefusal(share, Vault, Request));
        }

        [Fact]
        public void A_restored_share_gives_no_share_for_a_payment_whose_opening_is_not_known()
        {
            var share = Share(restoredAt: 2_000);
            Assert.NotNull(FrostStartup.RestoredShareRefusal(share, Vault, "unknown-payment"));
        }

        [Fact]
        public void A_share_this_node_kept_is_not_affected()
        {
            RequestAt(100, blockTimestamp: 1_000);
            var share = Share(restoredAt: 0);
            Assert.Null(FrostStartup.RestoredShareRefusal(share, Vault, Request));
        }

        [Fact]
        public void A_restored_share_filed_under_its_ceremony_id_is_judged_as_the_share_the_signing_uses()
        {
            RequestAt(100, blockTimestamp: 1_000);
            var share = Share(restoredAt: 2_000, filedUnder: "ceremony-session-id"); // found later by its group key or ceremony id
            Assert.Contains("restored from peers", FrostStartup.RestoredShareRefusal(share, Vault, Request));
        }

        [Fact]
        public void The_signing_start_judges_the_share_it_chose_before_making_a_nonce()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "VerifiedXCore", "Bitcoin", "FROST", "FrostStartup.cs"));
            var check = source.IndexOf("RestoredShareRefusal(keyStore, request.SmartContractUID, request.WithdrawalRequestHash)", StringComparison.Ordinal);
            var gate = source.IndexOf("Final gate: whichever path produced the key", StringComparison.Ordinal);
            var nonce = source.IndexOf("FrostNative.SignRound1Nonces(keyStore.KeyPackage)", StringComparison.Ordinal);
            Assert.True(gate > 0 && check > gate && nonce > check);
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

        [Fact]
        public async Task Recovery_marks_a_share_this_node_did_not_hold_and_keeps_the_mark_of_one_it_did()
        {
            var store = new FrostValidatorKeyStore { KeyPackage = "{}", PubkeyPackage = "{}", GroupPublicKey = "02" + new string('c', 64), ParticipantOrderJson = "[]" };
            var (blob, hash) = FrostKeyBackupService.EncryptKeyPackage(_address, "sc-recovered", store);
            FrostKeyBackupService.FetchFromPeerForTests = _ => Task.FromResult<List<FrostKeyBackupService.BackupRecoveryItem>?>(new()
            {
                new() { SmartContractUID = "sc-recovered", EncryptedBlob = blob!, PlaintextHash = hash!, Version = 1 },
            });
            var peers = new List<VBTCValidator> { new() { ValidatorAddress = "xPeer", IPAddress = "203.0.113.9" } };

            var before = TimeUtil.GetTime();
            var result = await FrostKeyBackupService.RecoverKeysFromPeers(_address, peers);
            Assert.Contains("sc-recovered", result.RestoredContracts);
            var restored = FrostValidatorKeyStore.GetKeyPackage("sc-recovered", _address)!;
            Assert.True(restored.RestoredFromPeersAt >= before);

            // Run again with the share held: it was restored then, not now.
            var firstMark = restored.RestoredFromPeersAt;
            await Task.Delay(1100);
            await FrostKeyBackupService.RecoverKeysFromPeers(_address, peers);
            Assert.Equal(firstMark, FrostValidatorKeyStore.GetKeyPackage("sc-recovered", _address)!.RestoredFromPeersAt);
        }
    }
}
