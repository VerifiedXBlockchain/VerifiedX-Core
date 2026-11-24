using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A request body is read once and judged by its shape before any route or model binder builds objects from it.
    /// The bodies honest nodes send pass at their largest sizes, through the hosts' own pipelines; bodies made of many
    /// small objects, or longer lists than a route's callers send, are answered 400 before a route sees them.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BoundedJsonBodiesTests : IDisposable
    {
        private const string Refused = "The request body is refused";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly string? _priorValidatorAddress = Globals.ValidatorAddress;
        private readonly bool _priorLibrary = Globals.FrostFFIAvailable;
        private readonly bool _priorBound = Globals.BoundRequestBodies;

        public BoundedJsonBodiesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bounded_json_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.ValidatorAddress ??= "xBoundedJsonValidator";
        }

        public void Dispose()
        {
            Globals.BoundRequestBodies = _priorBound;
            Globals.FrostFFIAvailable = _priorLibrary;
            Globals.ValidatorAddress = _priorValidatorAddress;
            DbContext.CloseDB();
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static string? Judge(object body, Type model) => BoundedJson.Refusal(JsonConvert.SerializeObject(body), BoundedJson.ValidatorApi, model);
        private static string? JudgeWithTextJson(object body, Type model) => BoundedJson.Refusal(System.Text.Json.JsonSerializer.Serialize(body, model), BoundedJson.ValidatorApi, model);

        private static string Address(int i) => "x" + i.ToString("D33");

        // -----------------------------------------------------------------------------------------------------------
        // What honest nodes send passes
        // -----------------------------------------------------------------------------------------------------------

        [Fact]
        public void A_block_at_the_transaction_cap_with_a_vault_sized_transaction_passes()
        {
            var block = new Block
            {
                Height = 100, Hash = new string('a', 64), PrevHash = new string('b', 64), Validator = Address(1),
                Transactions = Enumerable.Range(0, Globals.MaxTransactionsPerBlock).Select(i => new Transaction
                {
                    FromAddress = Address(i), ToAddress = Address(i + 1), Amount = 1.5M, Fee = 0.00001M, Nonce = i,
                    Hash = new string('c', 64), Signature = new string('s', 140), Timestamp = 1_700_000_000 + i,
                    Data = i == 0 ? new string('d', 256 * 1024) : "{\"Function\":\"Transfer()\"}",
                }).ToList(),
            };
            Assert.Null(Judge(block, typeof(Block)));
            Assert.Null(JudgeWithTextJson(block, typeof(Block)));
        }

        [Fact]
        public void A_validator_list_of_every_validator_passes_and_a_longer_one_is_refused()
        {
            var entries = Enumerable.Range(0, BoundedJson.ValidatorItems)
                .Select(i => new ValidatorListEntry { Address = Address(i), IPAddress = "203.0.113.1", PublicKey = new string('p', 130), LastSeen = i }).ToList();
            Assert.Null(Judge(new ValidatorListExchangeRequest { Validators = entries, Signature = "s" }, typeof(ValidatorListExchangeRequest)));

            entries.Add(new ValidatorListEntry { Address = "x" });
            Assert.Contains("list holds more than", Judge(new ValidatorListExchangeRequest { Validators = entries }, typeof(ValidatorListExchangeRequest)));
        }

        [Fact]
        public void A_probe_with_its_largest_record_run_passes()
        {
            var records = Enumerable.Range(0, 8).Select(seq => new CasterMembershipRecord
            {
                RecordSeq = seq, RecordHash = new string('h', 64), PrevRecordHash = new string('g', 64),
                Casters = Enumerable.Range(0, 5).Select(i => new CasterInfo { Address = Address(i), PeerIP = "203.0.113.2", PublicKey = new string('p', 130) }).ToList(),
                Signatures = Enumerable.Range(0, 10).Select(i => new RecordSignature { SignerAddress = Address(i), Signature = new string('s', 140) }).ToList(),
            }).ToList();
            Assert.Null(Judge(new VreeProbe { Height = 5, Hash = "h", Records = records }, typeof(VreeProbe)));
        }

        [Fact]
        public void A_committee_list_takes_one_entry_a_seat_with_room_and_no_more()
        {
            var approvals = Enumerable.Range(0, BoundedJson.CommitteeItems).Select(i => new CasterResumeApproval { SignerAddress = Address(i), Signature = "s" }).ToList();
            Assert.Null(Judge(new CasterResumedNotice { Approvals = approvals }, typeof(CasterResumedNotice)));

            approvals.Add(new CasterResumeApproval());
            Assert.Contains("list holds more than 64", Judge(new CasterResumedNotice { Approvals = approvals }, typeof(CasterResumedNotice)));
        }

        [Fact]
        public void A_commitment_set_and_a_share_batch_for_every_participant_pass()
        {
            var package = JsonConvert.SerializeObject(new
            {
                header = new { version = 0, ciphersuite = "FROST-secp256k1-SHA256-TR-v1" },
                commitment = Enumerable.Range(0, 342).Select(_ => new string('0', 66)).ToArray(), // two thirds of the participants
                proof_of_knowledge = new string('1', 128),
            });
            Assert.Null(BoundedJson.Refusal(package, FrostRequestBodyLimits.PackageShape));

            var set = Enumerable.Range(0, FrostSessionStorage.MAX_PARTICIPANTS).ToDictionary(Address, _ => package);
            Assert.Null(BoundedJson.Refusal(JsonConvert.SerializeObject(set), FrostRequestBodyLimits.CeremonyShape));
            Assert.Null(FrostRequestBodyLimits.CommitmentSetRefusal(set));

            var batch = new
            {
                LeaderAddress = Address(1), Timestamp = 1_700_000_000, LeaderSignature = new string('s', 140), Recipient = Address(2),
                AllGeneratedShares = Enumerable.Range(0, FrostSessionStorage.MAX_PARTICIPANTS).ToDictionary(Address, _ => new string('e', 400)),
            };
            Assert.Null(BoundedJson.Refusal(JsonConvert.SerializeObject(batch), FrostRequestBodyLimits.CeremonyShape));
        }

        [Fact]
        public void A_signing_start_with_as_many_inputs_as_its_body_holds_passes()
        {
            var inputs = 4_000; // as many as fit in the route's 1 MB body
            var start = new FrostSigningStartRequest
            {
                SessionId = "s", MessageHash = new string('m', 64), SmartContractUID = "sc", LeaderAddress = Address(1), LeaderSignature = "sig",
                SignerAddresses = Enumerable.Range(0, 3).Select(Address).ToList(),
                AllInputSighashes = Enumerable.Range(0, inputs).Select(_ => new string('h', 64)).ToList(),
                TxInputOutpoints = Enumerable.Range(0, inputs).Select(i => new string('t', 64) + ":" + i).ToList(),
                Prevouts = Enumerable.Range(0, inputs).Select(i => new Bitcoin.Models.PinnedWithdrawalCoin { TxId = "t", Vout = (uint)i, ValueSats = 1000, ScriptPubKeyHex = "51" }).ToList(),
            };
            var json = JsonConvert.SerializeObject(start);
            Assert.True(json.Length < FrostRequestBodyLimits.SigningBodyBytes);
            Assert.Null(BoundedJson.Refusal(json, FrostRequestBodyLimits.SigningShape));
        }

        // -----------------------------------------------------------------------------------------------------------
        // What is refused, without building anything
        // -----------------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("{\"a\":1 /* note */}", "not plain JSON")]
        [InlineData("{\"a\":1,}", "not plain JSON")]
        [InlineData("{\"a\":1} {\"b\":2}", "not plain JSON")]
        [InlineData("{'a':1}", "not plain JSON")]
        [InlineData("{a:1}", "not plain JSON")]
        public void Only_plain_json_passes(string text, string reason)
        {
            Assert.Contains(reason, BoundedJson.Refusal(text, BoundedJson.ValidatorApi, typeof(VreeProbe)));
            Assert.Contains(reason, BoundedJson.Refusal(text, FrostRequestBodyLimits.SmallShape));
        }

        [Fact]
        public void Nesting_long_numbers_long_text_and_wide_unknown_members_are_refused()
        {
            Assert.NotNull(BoundedJson.Refusal("{\"a\":" + new string('[', 40) + new string(']', 40) + "}", FrostRequestBodyLimits.SmallShape));
            Assert.Contains("number is longer", BoundedJson.Refusal("{\"Height\":" + new string('9', 65) + "}", BoundedJson.ValidatorApi, typeof(VreeProbe)));
            Assert.Contains("text value is longer", BoundedJson.Refusal("{\"Hash\":\"" + new string('a', 64 * 1024 + 1) + "\"}", BoundedJson.ValidatorApi, typeof(VreeProbe)));
            Assert.Contains("list holds more than", BoundedJson.Refusal("{\"NotAMember\":[" + string.Join(",", Enumerable.Repeat("{}", 300)) + "]}", BoundedJson.ValidatorApi, typeof(VreeProbe)));
            Assert.Contains("not an object", BoundedJson.Refusal("[" + string.Join(",", Enumerable.Repeat("{}", 10)) + "]", FrostRequestBodyLimits.SigningShape));
        }

        [Fact]
        public void A_member_name_in_another_letter_case_is_held_to_the_same_bound()
        {
            var records = "[" + string.Join(",", Enumerable.Repeat("{}", BoundedJson.CommitteeItems + 1)) + "]";
            Assert.Contains("list holds more than 64", BoundedJson.Refusal("{\"rEcOrDs\":" + records + "}", BoundedJson.ValidatorApi, typeof(VreeProbe)));
        }

        [Fact]
        public void A_package_made_of_small_objects_is_refused_before_it_is_parsed()
        {
            var wide = "{\"commitment\":[" + string.Join(",", Enumerable.Repeat("{}", 5000)) + "]}";
            Assert.NotNull(BoundedJson.Refusal(wide, FrostRequestBodyLimits.PackageShape));
            Assert.Throws<JsonReaderException>(() => BoundedJson.Parse(wide, FrostRequestBodyLimits.PackageShape));
            Assert.NotNull(FrostRequestBodyLimits.CommitmentSetRefusal(new Dictionary<string, string> { ["x1"] = wide }));
        }

        // -----------------------------------------------------------------------------------------------------------
        // Through the hosts' own pipelines
        // -----------------------------------------------------------------------------------------------------------

        private static TestServer ValidatorApi() => new(new WebHostBuilder().UseStartup<StartupP2PCaster>());

        private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

        private static string ProbeWith(int records) =>
            "{\"Height\":1,\"Hash\":\"h\",\"Records\":[" + string.Join(",", Enumerable.Repeat("{}", records)) + "]}";

        [Fact]
        public async Task The_validator_api_answers_a_probe_with_too_many_records_400_before_binding_it()
        {
            using var server = ValidatorApi();
            var answer = await server.CreateClient().PostAsync("/valapi/Validator/ProbeTip", Json(ProbeWith(100_000)));
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
            Assert.Contains(Refused, await answer.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task The_validator_api_passes_an_honest_probe_to_its_route()
        {
            using var server = ValidatorApi();
            var answer = await server.CreateClient().PostAsync("/valapi/Validator/ProbeTip", Json(ProbeWith(8)));
            Assert.DoesNotContain(Refused, await answer.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task The_check_can_be_turned_off_by_config()
        {
            Globals.BoundRequestBodies = false;
            using var server = ValidatorApi();
            var answer = await server.CreateClient().PostAsync("/valapi/Validator/ProbeTip", Json(ProbeWith(BoundedJson.CommitteeItems + 1)));
            Assert.DoesNotContain(Refused, await answer.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task A_key_ceremony_refuses_a_commitment_set_with_a_wide_entry_before_parsing_any_entry()
        {
            Globals.FrostFFIAvailable = true;
            var sessionId = $"bounded-json-{Guid.NewGuid():N}";
            FrostSessionStorage.DKGSessions[sessionId] = new DKGSession
            {
                SessionId = sessionId, SmartContractUID = "sc-bounded-json", LeaderAddress = "xLeader", LeaderSigned = false,
                ParticipantAddresses = new List<string> { "x1", "x2" }, StartTimestamp = TimeUtil.GetTime(), Round1SecretPackage = "secret",
            };
            try
            {
                using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
                var wide = "{\"commitment\":[" + string.Join(",", Enumerable.Repeat("{}", 5000)) + "]}";
                var set = JsonConvert.SerializeObject(new Dictionary<string, string> { ["x1"] = wide });
                var answer = await server.CreateClient().PostAsync($"/frost/dkg/round2/{sessionId}", Json(set));
                Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
                Assert.Contains("The commitment for x1 is refused", await answer.Content.ReadAsStringAsync());
            }
            finally { FrostSessionStorage.DKGSessions.TryRemove(sessionId, out _); }
        }

        [Fact]
        public async Task The_frost_server_answers_a_body_of_the_wrong_shape_400()
        {
            Globals.FrostFFIAvailable = true;
            using var server = new TestServer(new WebHostBuilder().UseStartup<FrostStartup>());
            var answer = await server.CreateClient().PostAsync("/frost/backup/check", Json("[" + string.Join(",", Enumerable.Repeat("{}", 1000)) + "]"));
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
            Assert.Contains(Refused, await answer.Content.ReadAsStringAsync());
        }
    }
}
