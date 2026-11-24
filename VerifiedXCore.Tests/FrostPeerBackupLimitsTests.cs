using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A peer's key backup is kept only with fields of the sizes the owner's node writes, so a row is a few KB whoever
    /// sent it, and the check comes before anything is looked up. A row kept before these limits that exceeds them is
    /// deleted when it is read. Recovery answers being sent are bounded for each network and in all.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FrostPeerBackupLimitsTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;

        private static readonly string Hash = new('a', 64);
        private static readonly string GroupKey = "02" + new string('b', 64);

        public FrostPeerBackupLimitsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"peerbackup_limits_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.FrostFFIAvailable = true;
        }

        public void Dispose()
        {
            Globals.FrostFFIAvailable = _priorLibrary;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static async Task<(HttpStatusCode Status, string Body)> StoreAsync(string owner, string uid, string blob, string hash, string groupKey)
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var body = JsonConvert.SerializeObject(new
            {
                OwnerAddress = owner, SmartContractUID = uid, EncryptedBlob = blob, PlaintextHash = hash, GroupPublicKey = groupKey,
                Version = 1, Timestamp = TimeUtil.GetTime(), Signature = "sig",
            });
            var answer = await server.CreateClient().PostAsync("/frost/backup/store", new StringContent(body, Encoding.UTF8, "application/json"));
            return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("owner")]
        [InlineData("uid")]
        [InlineData("hash")]
        [InlineData("groupKey")]
        public async Task A_backup_with_a_field_longer_than_its_owner_writes_is_refused_before_anything_is_looked_up(string field)
        {
            var longText = new string('c', 20_000);
            var (status, body) = await StoreAsync(
                field == "owner" ? longText : "xOwner",
                field == "uid" ? longText : "sc-1",
                "blob",
                field == "hash" ? longText : Hash,
                field == "groupKey" ? longText : GroupKey);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.DoesNotContain("registered active validator", body); // refused before the registry was asked
        }

        [Fact]
        public async Task A_backup_with_the_fields_its_owner_writes_goes_on_to_the_owner_check()
        {
            var (status, body) = await StoreAsync("xNotAValidator", "sc-" + new string('9', 60), new string('e', 2000), Hash, GroupKey);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Contains("registered active validator", body);

            var (prefixed, prefixedBody) = await StoreAsync("xNotAValidator", "sc-1", "blob", Hash, "0x" + GroupKey);
            Assert.Equal(HttpStatusCode.Forbidden, prefixed);
            Assert.Contains("registered active validator", prefixedBody);
        }

        [Fact]
        public void A_row_kept_before_the_limits_that_exceeds_them_is_deleted_when_it_is_read()
        {
            Assert.True(FrostPeerKeyBackup.SaveBackup(new FrostPeerKeyBackup
            {
                OwnerAddress = "xOwner", SmartContractUID = "sc-old", EncryptedBlob = "blob", PlaintextHash = new string('a', 2_000_000), GroupPublicKey = GroupKey,
            }));
            Assert.True(FrostPeerKeyBackup.SaveBackup(new FrostPeerKeyBackup
            {
                OwnerAddress = "xOwner", SmartContractUID = "sc-honest", EncryptedBlob = "blob", PlaintextHash = Hash, GroupPublicKey = GroupKey,
            }));

            var kept = FrostPeerKeyBackup.GetBackupsForOwnerWithinLimits("xOwner");
            Assert.Equal(new[] { "sc-honest" }, kept.Select(b => b.SmartContractUID));
            Assert.Single(FrostPeerKeyBackup.GetBackupsForOwner("xOwner"));
        }

        [Fact]
        public void Recovery_answers_in_progress_are_bounded_for_each_network_and_in_all()
        {
            var rows = Enumerable.Range(0, 400).Select(i => new FrostPeerKeyBackup
            {
                SmartContractUID = "sc-" + i, EncryptedBlob = new string('e', FrostPeerKeyBackup.MaxBlobChars), PlaintextHash = Hash,
            }).ToList();
            var bytes = FrostPeerKeyBackup.AnswerBytes(rows); // about 26 MB held to serve 400 full rows
            Assert.InRange(bytes, 20L * 1024 * 1024, 32L * 1024 * 1024);

            var answers = FrostPeerKeyBackup.AnswersInProgress;
            var before = answers.Held;
            using var first = answers.Hold("203.0.113", bytes, null);
            Assert.True(first.Granted);
            using var second = answers.Hold("203.0.113", bytes, null);
            Assert.False(second.Granted); // the same network, its share used
            using var other = answers.Hold("198.51.100", bytes, null);
            Assert.True(other.Granted); // another network is not held up by it
            first.Dispose();
            other.Dispose();
            Assert.Equal(before, answers.Held);
        }
    }
}
