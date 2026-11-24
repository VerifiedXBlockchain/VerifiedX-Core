using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The strict reader of a transaction's Data (Utilities.ConsensusData) and Globals.StrictDataReaderHeight: what the
    /// reader accepts and refuses, the entry points reading exactly as before below the height, Data the reader refuses
    /// refused on admission and in a block from the height, and validation and state application reading the same values.
    /// </summary>
    public class StrictDataReaderRulesTests
    {
        [Theory]
        [InlineData("{\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\",\"Amount\":1.5}")]
        [InlineData("[{\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\"}]")]
        [InlineData(" {\n \"Function\" : \"Transfer()\" }\n")]
        [InlineData("{\"Amount\":0.1000000000000000000001}")]
        [InlineData("{\"Amount\":1E-8}")]
        [InlineData("{\"Amount\":-0.0}")]
        [InlineData("{\"Amount\":1E28}")]
        [InlineData("{\"Id\":123456789012345678901234567890}")]
        [InlineData("{\"When\":\"2026-11-24T10:00:00Z\"}")]
        [InlineData("{\"Memo\":\"\\u0041\\u00e9\\/ \\ud83d\\ude00\"}")]
        [InlineData("{\"a\":{\"A\":1},\"b\":{\"a\":1}}")] // the same name in different objects
        [InlineData("\"memo\"")]
        [InlineData("[]")]
        public void Accepts_StrictJson(string data)
        {
            Assert.True(ConsensusData.TryRead(data, out var token, out var reason), reason);
            Assert.NotNull(token);
        }

        [Theory]
        [InlineData("{\"Function\":\"Burn()\",\"Function\":\"Transfer()\"}", "twice")]
        [InlineData("{\"Function\":\"Transfer()\",\"function\":\"Burn()\"}", "two spellings")]
        [InlineData("[{\"Amount\":1,\"AMOUNT\":2}]", "two spellings")]
        [InlineData("/*c*/{\"Function\":\"Transfer()\"}", null)]
        [InlineData("//c\n{\"Function\":\"Transfer()\"}", null)]
        [InlineData("{\"Function\":/*c*/\"Transfer()\"}", null)]
        [InlineData("{\"Function\":\"Transfer()\"}/*c*/", null)]
        [InlineData("{\"Function\":\"Transfer()\"} x", null)]
        [InlineData("{\"Function\":\"Transfer()\"}{\"Function\":\"Burn()\"}", null)]
        [InlineData("[1,]", null)]
        [InlineData("{\"a\":1,}", null)]
        [InlineData("{'a':'b'}", null)]
        [InlineData("{a:1}", null)]
        [InlineData("[0x1F]", null)]
        [InlineData("[017]", null)]
        [InlineData("[1.]", null)]
        [InlineData("[NaN]", null)]
        [InlineData("[Infinity]", null)]
        [InlineData("[undefined]", null)]
        [InlineData("[new Date(1)]", null)]
        [InlineData("{\"a\":\"x\ny\"}", null)]
        [InlineData("[\"\\ud800\"]", null)]
        [InlineData("[1E-30]", "exactly")]
        [InlineData("[0.1000000000000000000000000000001]", "exactly")]
        [InlineData("[1e400]", "exactly")]
        [InlineData("[9E28]", "exactly")]
        [InlineData("", null)]
        [InlineData("{", null)]
        public void Refuses_EverythingElse(string data, string? reasonPart)
        {
            Assert.False(ConsensusData.TryRead(data, out var token, out var reason));
            Assert.Null(token);
            Assert.False(string.IsNullOrEmpty(reason));
            if (reasonPart != null)
                Assert.Contains(reasonPart, reason);
            Assert.Throws<JsonReaderException>(() => ConsensusData.Read(data));
        }

        [Fact]
        public void Refuses_TextThatIsNotValidUtf16()
        {
            Assert.False(ConsensusData.TryRead("{\"Memo\":\"a\ud800\"}", out _, out var reason));
            Assert.Contains("UTF-16", reason);
        }

        [Fact]
        public void Refuses_NestingDeeperThanTheLimit()
        {
            string Nested(int depth) => new string('[', depth) + new string(']', depth);
            Assert.True(ConsensusData.TryRead(Nested(ConsensusData.MaxDepth), out _, out var reason), reason);
            Assert.False(ConsensusData.TryRead(Nested(ConsensusData.MaxDepth + 1), out _, out _));
        }

        [Fact]
        public void Numbers_AreReadAsDecimals_AndStringsStayStrings()
        {
            var obj = (JObject)ConsensusData.Read("{\"Amount\":0.1000000000000000000001,\"Fee\":1.50,\"When\":\"2026-11-24T10:00:00Z\"}");
            Assert.Equal(0.1000000000000000000001M, obj["Amount"]!.ToObject<decimal>());
            Assert.Equal("1.50", obj["Fee"]!.ToObject<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(JTokenType.String, obj["When"]!.Type);
            Assert.Equal("2026-11-24T10:00:00Z", (string?)obj["When"]);
            // The default reader goes through double and turns the date-like string into a date.
            var lenient = JObject.Parse("{\"Amount\":0.1000000000000000000001,\"When\":\"2026-11-24T10:00:00Z\"}");
            Assert.Equal(0.1M, lenient["Amount"]!.ToObject<decimal>());
            Assert.Equal(JTokenType.Date, lenient["When"]!.Type);
        }

        private sealed class CallShape
        {
            public string? Function { get; set; }
            public string? ContractUID { get; set; }
            public decimal? Amount { get; set; }
            public List<CallShape>? Inputs { get; set; }
        }

        [Fact]
        public void TypedReads_MatchNamesExactly()
        {
            var exact = ConsensusData.ToObject<CallShape>(ConsensusData.Read("{\"Function\":\"Transfer()\",\"Amount\":0.1000000000000000000001,\"Other\":1}"))!;
            Assert.Equal("Transfer()", exact.Function);
            Assert.Equal(0.1000000000000000000001M, exact.Amount);

            Assert.Throws<JsonSerializationException>(() => ConsensusData.ToObject<CallShape>(ConsensusData.Read("{\"function\":\"Transfer()\"}")));
            Assert.Throws<JsonSerializationException>(() => ConsensusData.ToObject<CallShape>(ConsensusData.Read("{\"Inputs\":[{\"Amount\":1},{\"amount\":2}]}")));
            Assert.Throws<JsonSerializationException>(() => ConsensusData.ToObject<List<CallShape>>(ConsensusData.Read("[{\"contractUID\":\"uid:1\"}]")));
            // The default typed read binds the lower-case name.
            Assert.Equal("Transfer()", JToken.Parse("{\"function\":\"Transfer()\"}").ToObject<CallShape>()!.Function);
        }

        [Theory]
        [InlineData("{\"a\":1}", true)]
        [InlineData("[1]", true)]
        [InlineData("  {\"a\":1} x", true)]
        [InlineData("/*c*/{\"a\":1}", true)]
        [InlineData("{\"a\":1,\"a\":2}", true)]
        [InlineData("{'a':1}", true)]
        [InlineData("hello", false)]
        [InlineData("2 coffees", false)]
        [InlineData("[memo] hello", false)]
        [InlineData("{ not json", false)]
        [InlineData("\"memo\"", false)]
        [InlineData("123", false)]
        [InlineData("", false)]
        public void JsonShapedData_IsAnObjectOrArrayAtTheStart(string data, bool shaped)
        {
            Assert.Equal(shaped, ConsensusData.IsJsonShaped(data));
        }
    }

    /// <summary>The entry points read as before below the height, and the strict reader's way from it.</summary>
    [Collection("GlobalCasterState")]
    public class StrictDataReaderHeightTests : IDisposable
    {
        private const long Height = 5_000;
        private readonly long _prior = Globals.StrictDataReaderHeight;

        public StrictDataReaderHeightTests() => Globals.StrictDataReaderHeight = Height;

        public void Dispose() => Globals.StrictDataReaderHeight = _prior;

        private const string Call = "\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\",\"Amount\":1.5";

        /// <summary>The Data shapes of the campaign's one-reader differential, and what Core reads from each from the height.</summary>
        public static readonly (string Label, string Data, bool Refused, string? Function, string? Uid, string? Amount)[] Shapes =
        {
            ("plain object", "{" + Call + "}", false, "Transfer()", "uid:1", "1.5"),
            ("plain array", "[{" + Call + "}]", false, "Transfer()", "uid:1", "1.5"),
            ("whitespace", " {\n \"Function\" : \"Transfer()\" , \"ContractUID\" : \"uid:1\" , \"Amount\" : 1.5 }\n", false, "Transfer()", "uid:1", "1.5"),
            ("keys reordered", "{\"Amount\":1.5,\"ContractUID\":\"uid:1\",\"Function\":\"Transfer()\"}", false, "Transfer()", "uid:1", "1.5"),
            ("Function twice", "{\"Function\":\"Burn()\"," + Call + "}", true, null, null, null),
            ("Amount twice", "{" + Call + ",\"Amount\":9}", true, null, null, null),
            ("lower-case function before Function", "{\"function\":\"Burn()\"," + Call + "}", true, null, null, null),
            ("lower-case function after Function", "{" + Call + ",\"function\":\"Burn()\"}", true, null, null, null),
            ("lower-case keys only", "{\"function\":\"Transfer()\",\"contractUID\":\"uid:1\",\"amount\":1.5}", false, null, null, null),
            ("leading block comment", "/*c*/{" + Call + "}", true, null, null, null),
            ("leading line comment", "//c\n{" + Call + "}", true, null, null, null),
            ("comment inside", "{\"Function\":/*c*/\"Transfer()\",\"ContractUID\":\"uid:1\",\"Amount\":1.5}", true, null, null, null),
            ("trailing comment", "{" + Call + "}/*c*/", true, null, null, null),
            ("trailing text", "{" + Call + "} x", true, null, null, null),
            ("trailing second object", "{" + Call + "}{\"Function\":\"Burn()\"}", true, null, null, null),
            ("array, trailing second array", "[{" + Call + "}][{\"Function\":\"Burn()\"}]", true, null, null, null),
            ("long decimal Amount", "{\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\",\"Amount\":0.1000000000000000000001}", false, "Transfer()", "uid:1", "0.1000000000000000000001"),
            ("long decimal Amount, array", "[{\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\",\"Amount\":0.1000000000000000000001}]", false, "Transfer()", "uid:1", "0.1000000000000000000001"),
            ("Amount 1E-8", "{\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\",\"Amount\":1E-8}", false, "Transfer()", "uid:1", "0.00000001"),
            ("Amount quoted", "{\"Function\":\"Transfer()\",\"ContractUID\":\"uid:1\",\"Amount\":\"1.5\"}", false, "Transfer()", "uid:1", "1.5"),
            ("ContractUID a number", "{\"Function\":\"Transfer()\",\"ContractUID\":12,\"Amount\":1.5}", false, "Transfer()", "12", "1.5"),
            ("array of two calls", "[{" + Call + "},{\"Function\":\"Burn()\",\"ContractUID\":\"uid:2\"}]", false, "Transfer()", "uid:1", "1.5"),
        };

        public static IEnumerable<object[]> ShapeLabels() => Shapes.Select(s => new object[] { s.Label });

        private static (string Label, string Data, bool Refused, string? Function, string? Uid, string? Amount) Shape(string label) => Shapes.Single(s => s.Label == label);

        private static Transaction Tx(string data, TransactionType type = TransactionType.NFT_TX) => new() { Data = data, TransactionType = type, FromAddress = "xA", ToAddress = "xB" };

        private static string? Throws(Func<object?> read)
        {
            try { read(); return null; }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        [Theory]
        [MemberData(nameof(ShapeLabels))]
        public void BelowTheHeight_EveryEntryPointReadsAsBefore(string label)
        {
            var data = Shape(label).Data;
            Assert.Equal(LedgerIntegrityRules.DispatchedCall(data), LedgerIntegrityRules.DispatchedCall(data, Height - 1));
            Assert.Equal(SmartContractDeployBinding.ReadPayload(data), SmartContractDeployBinding.ReadPayload(data, Height - 1));
            Assert.Null(ConsensusData.Refusal(Tx(data), Height - 1));

            var legacyObject = Throws(() => JObject.Parse(data));
            Assert.Equal(legacyObject, Throws(() => ConsensusData.ParseObject(data, Height - 1)));
            if (legacyObject == null)
                Assert.True(JToken.DeepEquals(JObject.Parse(data), ConsensusData.ParseObject(data, Height - 1)));
            var legacyArray = Throws(() => JsonConvert.DeserializeObject<JArray>(data));
            Assert.Equal(legacyArray, Throws(() => ConsensusData.ParseArray(data, Height - 1)));
            if (legacyArray == null)
                Assert.True(JToken.DeepEquals(JsonConvert.DeserializeObject<JArray>(data), ConsensusData.ParseArray(data, Height - 1)));
        }

        /// <summary>
        /// From the height every reader of a contract call gives one reading, or none: the call the validator's dispatch
        /// reads (the strict read's call object), the call state application dispatches on (DispatchedCall), the payload
        /// the rules read (ReadPayload), the object the token and vBTC handlers read in both (ParseObject), and a typed
        /// read. Data the strict reader refuses is refused by the admission and block gate, and names no call to any of them.
        /// </summary>
        [Theory]
        [MemberData(nameof(ShapeLabels))]
        public void FromTheHeight_ValidationAndApplyReadOneCall(string label)
        {
            var (_, data, refused, function, uid, amount) = Shape(label);
            var gate = ConsensusData.Refusal(Tx(data), Height);
            Assert.Equal(refused, gate != null);
            Assert.Equal(gate == null, ConsensusData.TryRead(data, out var root, out _));

            // Validation's dispatch, state application's dispatch, the rules' payload.
            var validation = LedgerIntegrityRules.CallOf(root);
            var apply = LedgerIntegrityRules.DispatchedCall(data, Height);
            var payload = SmartContractDeployBinding.ReadPayload(data, Height);
            Assert.Equal((function, uid), validation);
            Assert.Equal((function, uid), apply);
            Assert.Equal((function, uid), (payload.Function, payload.ContractUID));

            if (refused)
            {
                Assert.NotNull(Throws(() => ConsensusData.ParseObject(data, Height)));
                Assert.NotNull(Throws(() => ConsensusData.ParseArray(data, Height)));
                return;
            }

            // The call object, and the amount the handlers read from it, are the same for every reader that reads one.
            var callObject = root is JArray arr ? arr[0] as JObject : root as JObject;
            Assert.NotNull(callObject);
            if (root is JObject)
            {
                var handlers = ConsensusData.ParseObject(data, Height);
                Assert.Equal(function, handlers["Function"]?.ToObject<string?>());
                Assert.Equal(amount, handlers["Amount"]?.ToObject<decimal?>()?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
                Assert.NotNull(Throws(() => ConsensusData.ParseObject(data, Height))); // object-only handlers refuse the array form, as before
            if (function != null)
            {
                Assert.Equal(amount, callObject!["Amount"]?.ToObject<decimal?>()?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var typed = ConsensusData.ToObject<TypedCall>(callObject!, Height)!;
                Assert.Equal((function, uid, amount), (typed.Function, typed.ContractUID, typed.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            else
                // No exact Function or ContractUID: no reader names a call, and a typed read refuses the other spelling.
                Assert.NotNull(Throws(() => ConsensusData.ToObject<TypedCall>(callObject!, Height)));
        }

        private sealed class TypedCall
        {
            public string? Function { get; set; }
            public string? ContractUID { get; set; }
            public decimal? Amount { get; set; }
        }

        [Fact]
        public void TheGate_LeavesPlainTextAndPrivatePayloadsAlone()
        {
            Assert.Null(ConsensusData.Refusal(Tx("2 coffees", TransactionType.TX), Height));
            Assert.Null(ConsensusData.Refusal(Tx("[memo] hello", TransactionType.TX), Height));
            Assert.Null(ConsensusData.Refusal(Tx("", TransactionType.TX), Height));
            Assert.NotNull(ConsensusData.Refusal(Tx("{\"Memo\":\"a\",\"Memo\":\"b\"}", TransactionType.TX), Height));
            Assert.Null(ConsensusData.Refusal(Tx("{\"Memo\":\"a\",\"Memo\":\"b\"}", TransactionType.VFX_PRIVATE_TRANSFER), Height));
        }

        /// <summary>Unset on testnet until a release sets it (a custom testnet's 401 is checked with every other height in CustomTestNetHeightsTests).</summary>
        [Fact]
        public void TheTestnetHeight_IsUnset() => Assert.Equal(long.MaxValue, Globals.StrictDataReaderTestnetHeight);
    }

    /// <summary>
    /// The gate in VerifyTX, on admission and in a block, and one token transfer judged and applied: from the height the
    /// validator and state application read the same exact amount.
    /// </summary>
    [Collection("DbContextSequential")]
    public class StrictDataReaderTransactionTests : IDisposable
    {
        private const string TokenX = "5a2e1d3b9c8f4a7e8d1c2b3a4f5e6d7c:1790300000";
        private const long Tip = 100;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight = Globals.StrictDataReaderHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _recipient = NewKey();

        public StrictDataReaderTransactionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"strict_data_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = Tip };

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = TokenX,
                ContractData = VbtcTestContracts.TokenContractData(TokenX, _holder.Address, 1000),
                MinterAddress = _holder.Address,
                OwnerAddress = _holder.Address,
                IsToken = true,
                TokenDetails = new TokenDetails { TokenName = "X", TokenTicker = "X", StartingSupply = 1000, CurrentSupply = 1000, ContractOwner = _holder.Address, DecimalPlaces = 18, TokenBurnable = true },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = _holder.Address, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(TokenX, "X", "X", 1000M, 18) },
            });
        }

        public void Dispose()
        {
            Globals.StrictDataReaderHeight = _priorHeight;
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private Transaction Signed(TransactionType type, string to, decimal amount, string data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _holder.Address, ToAddress = to, Amount = amount, Fee = 0, Nonce = 0,
                TransactionType = type, Data = data,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _holder.Key, _holder.Pub);
            return tx;
        }

        private string TransferData(string amount, string extra = "") =>
            "{\"Function\":\"TokenTransfer()\",\"ContractUID\":\"" + TokenX + "\",\"FromAddress\":\"" + _holder.Address + "\",\"ToAddress\":\"" + _recipient.Address
            + "\",\"Amount\":" + amount + extra + ",\"TokenTicker\":\"X\",\"TokenName\":\"X\"}";

        private const string Refused = "A transaction's Data must be strict JSON.";

        private static Task<(bool, string)> InBlock(Transaction tx, long height) => TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);

        [Fact]
        public async Task DataTheReaderRefuses_IsRefusedOnAdmissionAndInABlock_FromTheHeight()
        {
            var plain = Signed(TransactionType.TX, _recipient.Address, 1.0M, "{\"Memo\":\"a\",\"Memo\":\"b\"}");
            var token = Signed(TransactionType.FTKN_TX, _recipient.Address, 0M, TransferData("1", ",\"Amount\":900"));

            Globals.StrictDataReaderHeight = Tip + 1; // admission judges at the next block
            foreach (var tx in new[] { plain, token })
            {
                var (ok, why) = await TransactionValidatorService.VerifyTX(tx);
                Assert.False(ok);
                Assert.StartsWith(Refused, why);
                (ok, why) = await InBlock(tx, Tip + 1);
                Assert.False(ok);
                Assert.StartsWith(Refused, why);
                Assert.DoesNotContain(Refused, (await InBlock(tx, Tip)).Item2);
            }

            // Below the height the duplicate is read as before (the last wins).
            Globals.StrictDataReaderHeight = Tip + 2;
            var (plainOk, plainWhy) = await TransactionValidatorService.VerifyTX(plain);
            Assert.True(plainOk, plainWhy);
            var (tokenOk, tokenWhy) = await TransactionValidatorService.VerifyTX(token);
            Assert.True(tokenOk, tokenWhy);
        }

        [Fact]
        public async Task PlainTextData_IsAcceptedFromTheHeight()
        {
            Globals.StrictDataReaderHeight = Tip + 1;
            var memo = Signed(TransactionType.TX, _recipient.Address, 1.0M, "2 coffees");
            var (ok, why) = await TransactionValidatorService.VerifyTX(memo);
            Assert.True(ok, why);
            var strict = Signed(TransactionType.TX, _recipient.Address, 1.0M, "{\"Memo\":\"a\"}");
            (ok, why) = await TransactionValidatorService.VerifyTX(strict);
            Assert.True(ok, why);
        }

        private static decimal TokenBalance(string address) =>
            StateData.GetSpecificAccountStateTrei(address)?.TokenAccounts?.FirstOrDefault(t => t.SmartContractUID == TokenX)?.Balance ?? 0M;

        /// <summary>
        /// An amount with more digits than a double holds: below the height the validator and the apply both read it
        /// through double (0.1); from the height both read it exactly, and the balance moves by exactly that.
        /// </summary>
        [Theory]
        [InlineData(true, "0.100000000000000001", "0.100000000000000001")]
        [InlineData(false, "0.100000000000000001", "0.1")]
        public async Task ATokenTransfer_IsJudgedAndAppliedWithOneAmount(bool strict, string sent, string read)
        {
            Globals.StrictDataReaderHeight = strict ? Tip + 1 : Tip + 2;
            var expected = decimal.Parse(read, System.Globalization.CultureInfo.InvariantCulture);
            var tx = Signed(TransactionType.FTKN_TX, _recipient.Address, 0M, TransferData(sent));
            var (ok, why) = await InBlock(tx, Tip + 1);
            Assert.True(ok, why);

            tx.Height = Tip + 1;
            Assert.True(await StateData.UpdateTreis(new Block { Height = Tip + 1, StateRoot = "root", Transactions = new List<Transaction> { tx } }));
            Assert.Equal(expected, TokenBalance(_recipient.Address));
            Assert.Equal(1000M - expected, TokenBalance(_holder.Address));
        }

        /// <summary>From the height an amount with more decimal places than the token allows is read as sent, and refused.</summary>
        [Fact]
        public async Task ATokenTransfer_WithMoreDecimalsThanTheToken_IsRefusedFromTheHeight()
        {
            var tx = Signed(TransactionType.FTKN_TX, _recipient.Address, 0M, TransferData("0.1000000000000000001"));
            Globals.StrictDataReaderHeight = Tip + 1;
            var (ok, why) = await InBlock(tx, Tip + 1);
            Assert.False(ok);
            Assert.Contains("Too many decimals", why);
            Globals.StrictDataReaderHeight = Tip + 2;
            (ok, why) = await InBlock(tx, Tip + 1);
            Assert.True(ok, why); // read through double as 0.1
        }
    }
}
