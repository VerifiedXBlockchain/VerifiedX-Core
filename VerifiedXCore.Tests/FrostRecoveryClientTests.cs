using System.Diagnostics;
using System.Threading;
using System.Text;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A validator recovering its key shares asks every peer, several at once, each with a request signed for it alone,
    /// and reads each answer item by item: one peer that answers slowly, or with a flood of items under one id, does not
    /// hold up the rest or keep the node busy, and every honest copy is still recovered.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostRecoveryClientTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _address;

        public FrostRecoveryClientTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"recovery_client_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _address = AccountData.GetHumanAddress(pub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = _address, PublicKey = pub, PrivateKey = "0" + key.secret.ToString("x") });
        }

        public void Dispose()
        {
            FrostKeyBackupService.FetchFromPeerForTests = null;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private FrostKeyBackupService.BackupRecoveryItem Honest(string uid)
        {
            var (blob, hash) = FrostKeyBackupService.EncryptKeyPackage(_address, uid,
                new FrostValidatorKeyStore { KeyPackage = "{}", PubkeyPackage = "{}", GroupPublicKey = "02" + new string('d', 64), ParticipantOrderJson = "[]" });
            return new() { SmartContractUID = uid, EncryptedBlob = blob!, PlaintextHash = hash!, Version = 1 };
        }

        private static List<VBTCValidator> Peers(int n) =>
            Enumerable.Range(0, n).Select(i => new VBTCValidator { ValidatorAddress = "xPeer" + i, IPAddress = "203.0.113." + (i + 1) }).ToList();

        [Fact]
        public async Task A_flood_of_items_under_one_id_does_not_keep_the_node_busy_or_hide_the_honest_copy()
        {
            var honest = Honest("sc-flooded");
            var flood = Enumerable.Range(0, 50_000).Select(i => new FrostKeyBackupService.BackupRecoveryItem { SmartContractUID = "sc-flooded", EncryptedBlob = "junk" + i }).ToList();
            FrostKeyBackupService.FetchFromPeerForTests = p => Task.FromResult<List<FrostKeyBackupService.BackupRecoveryItem>?>(
                p.ValidatorAddress == "xPeer0" ? flood : new() { honest });

            var clock = Stopwatch.StartNew();
            var result = await FrostKeyBackupService.RecoverKeysFromPeers(_address, Peers(3));
            Assert.True(clock.ElapsedMilliseconds < 5_000, $"took {clock.ElapsedMilliseconds} ms");
            Assert.Contains("sc-flooded", result.RestoredContracts);
        }

        [Fact]
        public async Task A_slow_peer_does_not_hold_up_the_others()
        {
            var honest = Honest("sc-slow");
            FrostKeyBackupService.FetchFromPeerForTests = async p =>
            {
                if (p.ValidatorAddress == "xPeer0") await Task.Delay(3_000);
                return new() { honest };
            };
            var clock = Stopwatch.StartNew();
            var result = await FrostKeyBackupService.RecoverKeysFromPeers(_address, Peers(9));
            Assert.Contains("sc-slow", result.RestoredContracts);
            Assert.True(clock.ElapsedMilliseconds < 3_000 + 2_000, $"took {clock.ElapsedMilliseconds} ms"); // not nine times the slow one
            Assert.Equal(9, result.PeersAsked);
        }

        [Fact]
        public async Task An_answer_is_read_item_by_item_and_over_long_items_are_left_out()
        {
            var honest = Honest("sc-read");
            var answer = JsonConvert.SerializeObject(new
            {
                Success = true,
                Backups = new object[]
                {
                    honest,
                    new { SmartContractUID = new string('u', 5_000), EncryptedBlob = "x", PlaintextHash = "", Version = 1 },
                    new { SmartContractUID = "sc-big", EncryptedBlob = new string('b', 200_000), PlaintextHash = "", Version = 1 },
                },
            });
            var items = await FrostKeyBackupService.ReadRecoveryAnswerAsync(new MemoryStream(Encoding.UTF8.GetBytes(answer)));
            Assert.Single(items!);
            Assert.Equal("sc-read", items![0].SmartContractUID);

            var refused = await FrostKeyBackupService.ReadRecoveryAnswerAsync(new MemoryStream(Encoding.UTF8.GetBytes("{\"Success\":false,\"Message\":\"no\"}")));
            Assert.Null(refused);
        }

        private sealed class EndlessStream : Stream
        {
            private readonly byte[] _start;
            private int _at;
            public EndlessStream(string start) => _start = Encoding.UTF8.GetBytes(start);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override async Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken t)
            {
                if (_at < _start.Length) { var n = Math.Min(c, _start.Length - _at); Array.Copy(_start, _at, b, o, n); _at += n; return n; }
                await Task.Delay(Timeout.Infinite, t); // the body is held open
                return 0;
            }
        }

        [Fact]
        public async Task A_peer_that_holds_its_answer_open_is_given_up_on_at_the_deadline()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                FrostKeyBackupService.ReadRecoveryAnswerAsync(new EndlessStream("{\"Success\":true,\"Backups\":["), deadline.Token, _ => true));
            Assert.True(clock.ElapsedMilliseconds < 3_000);
        }

        [Fact]
        public async Task An_answer_keeps_one_item_a_contract_and_few_under_ids_that_name_none()
        {
            var items = new List<object>();
            for (var i = 0; i < 1_000; i++) items.Add(new { SmartContractUID = "sc-real", EncryptedBlob = "junk" + i, PlaintextHash = "", Version = 1 });
            for (var i = 0; i < 1_000; i++) items.Add(new { SmartContractUID = "made-up-" + i, EncryptedBlob = "x", PlaintextHash = "", Version = 1 });
            var answer = JsonConvert.SerializeObject(new { Success = true, Backups = items });
            var kept = await FrostKeyBackupService.ReadRecoveryAnswerAsync(new MemoryStream(Encoding.UTF8.GetBytes(answer)), default, uid => uid == "sc-real");
            Assert.Single(kept!, i => i.SmartContractUID == "sc-real");
            Assert.Equal(FrostKeyBackupService.MaxUnboundItemsPerAnswer, kept!.Count(i => i.SmartContractUID.StartsWith("made-up-")));
        }

        [Fact]
        public async Task An_answer_longer_than_any_honest_one_is_read_no_further()
        {
            var big = "{\"Success\":true,\"Backups\":[" + string.Join(",", Enumerable.Range(0, 3_000).Select(i =>
                JsonConvert.SerializeObject(new { SmartContractUID = "sc-" + i, EncryptedBlob = new string('e', 16 * 1024), PlaintextHash = "", Version = 1 }))) + "]}";
            Assert.True(big.Length > FrostKeyBackupService.MaxAnswerBytes);
            var kept = await FrostKeyBackupService.ReadRecoveryAnswerAsync(new MemoryStream(Encoding.UTF8.GetBytes(big)), default, _ => true);
            Assert.InRange(kept!.Count, 1, (int)(FrostKeyBackupService.MaxAnswerBytes / (16 * 1024)));
        }

        [Fact]
        public void Every_peer_is_asked_with_a_request_signed_for_it_alone()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "VerifiedXCore", "Bitcoin", "Services", "FrostKeyBackupService.cs"));
            var loop = source.IndexOf("await Task.WhenAll(peers.Select(async validator =>", StringComparison.Ordinal);
            Assert.True(loop > 0);
            Assert.InRange(source.IndexOf("SignedRecoveryRequest(validatorAddress)", loop, StringComparison.Ordinal) - loop, 1, 400);
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
    }
}
