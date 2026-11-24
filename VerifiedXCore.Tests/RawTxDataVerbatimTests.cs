using System.Linq;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.RawDataVerbatimHeight the raw-transaction routes keep a Data sent as a JSON object or array exactly as
    /// it appears in the request body: that text is hashed and stored. Below it the node writes such a Data back after a
    /// default parse (numbers through double, whitespace removed, escapes decoded, date-like strings in the node's time
    /// zone). A JSON string, null or missing Data reads as before at every height, and a string holding JSON is still
    /// refused on a call-type transaction.
    /// </summary>
    [Collection("DbContextSequential")]
    public class RawTxDataVerbatimTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorVerbatimHeight;
        private readonly long _priorMultiHeight;
        private readonly bool _priorApiEnabled = Startup.APIEnabled;
        private readonly System.Security.SecureString? _priorToken = Globals.APIToken;
        private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _receiver = NewKey();

        private const long Tip = 100;

        /// <summary>Data texts a client sends as a JSON value, each one a default parse and re-write would change.</summary>
        public static IEnumerable<object[]> DataTexts => new[]
        {
            new object[] { "{ \"Memo\" : \"a  b\",\n  \"Note\" : \"x\" }" },          // whitespace
            new object[] { "{\"b\":1,\"a\":2,\"Memo\":\"m\"}" },                     // key order (kept by both, a control)
            new object[] { "{\"Amount\":0.1000000000000000000001}" },               // long decimal
            new object[] { "{\"Amount\":1.50}" },                                   // trailing zero
            new object[] { "{\"Amount\":1E2}" },                                    // exponent
            new object[] { "{\"Memo\":\"\\u0041\\u00e9\\/\"}" },                    // escapes
            new object[] { "{\"When\":\"2026-11-24T10:00:00.1234567+01:00\"}" },    // date with an offset
            new object[] { "{\"When\":\"2026-11-24T10:00:00-05:00\"}" },            // date with a negative offset
            new object[] { "[ \"memo\" , 1.0 ]" },                                  // array with spaces
        };

        public RawTxDataVerbatimTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rawverb_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            _priorVerbatimHeight = Globals.RawDataVerbatimHeight;
            _priorMultiHeight = Globals.V2TransferMultiHeight;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            MempoolEvictionUtility.InvalidateAdmissionStats();
            Globals.LastBlock = new Block { Height = Tip };
            Globals.RawDataVerbatimHeight = Tip + 1; // active for the next block
            Globals.V2TransferMultiHeight = 1;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            Startup.APIEnabled = _priorApiEnabled;
            Globals.APIToken = _priorToken;
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            Globals.RawDataVerbatimHeight = _priorVerbatimHeight;
            Globals.V2TransferMultiHeight = _priorMultiHeight;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>A transfer carrying <paramref name="data"/> as its Data, hashed and signed over it.</summary>
        private Transaction Transfer(string? data, TransactionType type = TransactionType.TX)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _sender.Address,
                ToAddress = _receiver.Address,
                Amount = type == TransactionType.TX ? 1M.ToNormalizeDecimal() : 0.0M,
                Fee = 0.0002M,
                Nonce = 0,
                TransactionType = type,
                Data = data,
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        /// <summary>The request body as text: the transaction's envelope with <paramref name="dataJson"/> put in as Data, character for character.</summary>
        private static string BodyText(Transaction tx, string dataJson)
        {
            var envelope = JObject.Parse(JsonConvert.SerializeObject(tx));
            envelope["Data"] = "__DATA__";
            return envelope.ToString(Formatting.None).Replace("\"__DATA__\"", dataJson);
        }

        /// <summary>The body as the API binds [FromBody] object: System.Text.Json with the MVC defaults gives a JsonElement.</summary>
        private static object Bound(string bodyText) =>
            System.Text.Json.JsonSerializer.Deserialize<object>(bodyText, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        private static string HashOver(Transaction template, string? data)
        {
            var tx = new Transaction
            {
                Timestamp = template.Timestamp,
                FromAddress = template.FromAddress,
                ToAddress = template.ToAddress,
                Amount = template.Amount,
                Fee = template.Fee,
                Nonce = template.Nonce,
                TransactionType = template.TransactionType,
                Data = data,
            };
            tx.Build();
            return tx.Hash;
        }

        [Theory]
        [MemberData(nameof(DataTexts))]
        public async Task FromTheHeight_GetTxHash_IsOverDataAsSent(string dataJson)
        {
            var tx = Transfer(dataJson);

            var answer = JObject.Parse(await new TXV1Controller().GetTxHash(Bound(BodyText(tx, dataJson))));

            Assert.Equal("Success", (string?)answer["Result"]);
            Assert.Equal(tx.Hash, (string?)answer["Hash"]);
        }

        [Theory]
        [MemberData(nameof(DataTexts))]
        public void FromTheHeight_RawData_GivesTheTextAsSent(string dataJson)
        {
            var bodyText = BodyText(Transfer(dataJson), dataJson);
            var bound = Bound(bodyText);
            var token = JToken.Parse(bound.ToString()!);

            Assert.Null(TXV1Controller.RawData(token, bound));
            Assert.Equal(dataJson, (string?)token["Data"]);
            // The route's own read of the token keeps it too.
            Assert.Equal(dataJson, JsonConvert.DeserializeObject<Transaction>(token.ToString())!.Data);
            // The body's text works as well as the bound element.
            var fromText = JToken.Parse(bodyText);
            Assert.Null(TXV1Controller.RawData(fromText, bodyText));
            Assert.Equal(dataJson, (string?)fromText["Data"]);
        }

        [Theory]
        [MemberData(nameof(DataTexts))]
        public async Task BelowTheHeight_DataIsWrittenBackAsBefore(string dataJson)
        {
            Globals.RawDataVerbatimHeight = Tip + 2;
            var template = Transfer(dataJson);
            var bodyText = BodyText(template, dataJson);
            var before = JToken.Parse(bodyText)["Data"]!.ToString(Formatting.None);

            var answer = JObject.Parse(await new TXV1Controller().GetTxHash(Bound(bodyText)));

            Assert.Equal(HashOver(template, before), (string?)answer["Hash"]);
        }

        [Fact]
        public void BelowTheHeight_TheChangedSpellingsDiffer()
        {
            // A control on the cases above: below the height these come back changed.
            foreach (var dataJson in new[] { "{\"Amount\":0.1000000000000000000001}", "{\"Amount\":1.50}", "{\"Amount\":1E2}", "{ \"Memo\" : \"a\" }", "{\"Memo\":\"\\u0041\"}" })
                Assert.NotEqual(dataJson, JToken.Parse("{\"Data\":" + dataJson + "}")["Data"]!.ToString(Formatting.None));
        }

        [Fact]
        public async Task FromTheHeight_SendRawTransaction_StoresWhatGetTxHashHashed()
        {
            const string dataJson = "{ \"Memo\" : \"a  b\", \"Amount\" : 1.50, \"When\" : \"2026-11-24T10:00:00+01:00\" }";
            var tx = Transfer(dataJson);
            var bodyText = BodyText(tx, dataJson);
            var api = new TXV1Controller();

            var hash = JObject.Parse(await api.GetTxHash(Bound(bodyText)));
            var sent = JObject.Parse(await api.SendRawTransaction(Bound(bodyText)));

            Assert.Equal(tx.Hash, (string?)hash["Hash"]);
            Assert.True("Success" == (string?)sent["Result"], sent.ToString());
            Assert.Equal(tx.Hash, (string?)sent["Hash"]);
            var pooled = TransactionData.GetPool().FindAll().Single();
            Assert.Equal(dataJson, pooled.Data);
            Assert.Equal(tx.Hash, pooled.Hash);
        }

        [Fact]
        public async Task FromTheHeight_ThroughTheApi_GetTxHashIsOverDataAsSent()
        {
            // The whole request path: Startup's MVC binding of [FromBody] object, then the route.
            Startup.APIEnabled = true;
            Globals.APIToken = null;
            const string dataJson = "{ \"Amount\" : 0.1000000000000000000001,\n \"When\" : \"2026-11-24T10:00:00-05:00\", \"Memo\" : \"\\u0041\" }";
            var tx = Transfer(dataJson);

            using var server = new TestServer(new WebHostBuilder().UseStartup<Startup>());
            var response = await server.CreateClient().PostAsync("/txapi/TXV1/GetTxHash", new StringContent(BodyText(tx, dataJson), Encoding.UTF8, "application/json"));
            var answer = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal("Success", (string?)answer["Result"]);
            Assert.Equal(tx.Hash, (string?)answer["Hash"]);
        }

        [Fact]
        public async Task FromTheHeight_OtherDataValues_ReadAsBefore()
        {
            var api = new TXV1Controller();

            // A string: its JSON text, as before.
            var withString = Transfer(JsonConvert.SerializeObject("memo  x"));
            var stringBody = BodyText(withString, "\"memo  x\"");
            Assert.Equal(withString.Hash, (string?)JObject.Parse(await api.GetTxHash(Bound(stringBody)))["Hash"]);

            // Null and missing: no Data.
            var withoutData = Transfer(null);
            Assert.Equal(withoutData.Hash, (string?)JObject.Parse(await api.GetTxHash(Bound(BodyText(withoutData, "null"))))["Hash"]);
            var missing = JObject.Parse(JsonConvert.SerializeObject(withoutData));
            missing.Remove("Data");
            Assert.Equal(withoutData.Hash, (string?)JObject.Parse(await api.GetTxHash(Bound(missing.ToString(Formatting.None))))["Hash"]);
        }

        [Fact]
        public async Task FromTheHeight_AStringHoldingJson_IsStillRefusedOnACall()
        {
            const string call = "{\"Function\":\"TransferVBTCMultiV2()\",\"Inputs\":[]}";
            var tx = Transfer(call, TransactionType.VBTC_V2_TRANSFER);
            var bodyText = BodyText(tx, JsonConvert.SerializeObject(call));
            var api = new TXV1Controller();

            var answers = new[]
            {
                await api.GetRawTxFee(Bound(bodyText)),
                await api.GetTxHash(Bound(bodyText)),
                await api.VerifyRawTransaction(Bound(bodyText)),
                await api.SendRawTransaction(Bound(bodyText)),
            };

            foreach (var answer in answers.Select(JObject.Parse))
            {
                Assert.Equal("Fail", (string?)answer["Result"]);
                Assert.Equal(TXV1Controller.StringifiedDataError, (string?)answer["Message"]);
            }
            Assert.Empty(TransactionData.GetPool().FindAll());
        }

        [Fact]
        public void FromTheHeight_RepeatedData_TheLastIsKept()
        {
            const string bodyText = "{\"TransactionType\":0,\"Data\":{\"a\":1},\"Data\":{ \"b\" : 2 }}";
            var token = JToken.Parse(bodyText);

            Assert.Null(TXV1Controller.RawData(token, Bound(bodyText)));
            Assert.Equal("{ \"b\" : 2 }", (string?)token["Data"]);
        }

        [Fact]
        public void FromTheHeight_WithoutTheRequestBody_AnObjectDataIsRefused()
        {
            var token = JToken.Parse("{\"TransactionType\":0,\"Data\":{\"a\":1}}");

            Assert.Equal(TXV1Controller.DataNotReadableAsSentError, TXV1Controller.RawData(token));
            Assert.Equal(TXV1Controller.DataNotReadableAsSentError, TXV1Controller.RawData(token, new JObject()));
        }
    }
}
