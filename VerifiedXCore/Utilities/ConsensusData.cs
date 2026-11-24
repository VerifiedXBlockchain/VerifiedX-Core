using System.Globalization;
using System.Numerics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using VerifiedXCore.Models;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// The one reader of a transaction's Data on the consensus paths from <see cref="Globals.StrictDataReaderHeight"/>.
    /// Data is read as RFC 8259 JSON with fixed settings, so every rule and every apply step that reads it reads the same
    /// values, and so does any other JSON reader:
    /// <list type="bullet">
    /// <item>a name used twice in one object is refused, and so are two names that differ only in the case of A-Z;</item>
    /// <item>comments, trailing commas, single quotes, unquoted names, hexadecimal and leading-zero numbers, NaN,
    /// undefined, constructors, unescaped control characters and anything after the value are refused;</item>
    /// <item>a number with a fraction or an exponent is read as a decimal, and refused when a decimal cannot hold it
    /// exactly (never read through double);</item>
    /// <item>strings stay strings (no date conversion), and an escape that is not valid UTF-16 is refused;</item>
    /// <item>nesting deeper than <see cref="MaxDepth"/> is refused;</item>
    /// <item>names are matched exactly: the indexers are ordinal, and <see cref="ToObject{T}(JToken)"/> refuses a name
    /// that matches a member only when case is ignored.</item>
    /// </list>
    /// Below the height the <c>Parse*</c> and <c>ToObject</c> entry points read exactly as the code they replace did.
    /// </summary>
    public static class ConsensusData
    {
        /// <summary>The deepest nesting accepted: an object or array inside 63 others.</summary>
        public const int MaxDepth = 64;

        /// <summary>The most significant digits a decimal holds exactly in every case.</summary>
        private const int MaxDecimalDigits = 28;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static readonly JsonLoadSettings LoadSettings = new()
        {
            CommentHandling = CommentHandling.Load,
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            LineInfoHandling = LineInfoHandling.Ignore,
        };

        /// <summary>The serializer typed reads use: no metadata properties, no type names, no date conversion.</summary>
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = MaxDepth,
        });

        /// <summary>Whether the strict reader is in force for a transaction judged or applied at this height.</summary>
        public static bool IsStrict(long height) => height >= Globals.StrictDataReaderHeight;

        /// <summary>
        /// Reads Data with the strict rules. False, with the reason, when it is refused; never throws.
        /// </summary>
        public static bool TryRead(string? data, out JToken? token, out string? reason)
        {
            token = null;
            if (data == null)
            {
                reason = "Data is empty.";
                return false;
            }
            reason = CheckSyntax(data);
            if (reason != null)
                return false;
            try
            {
                using var reader = new JsonTextReader(new StringReader(data))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                    MaxDepth = null, // CheckSyntax bounds the depth
                };
                token = JToken.ReadFrom(reader, LoadSettings);
                if (reader.Read())
                    throw new JsonReaderException("Additional content after the value.");
                return true;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or ArgumentException)
            {
                token = null;
                reason = $"Data is not strict JSON: {ex.Message}";
                return false;
            }
        }

        /// <summary>Reads Data with the strict rules; throws a <see cref="JsonReaderException"/> when it is refused.</summary>
        public static JToken Read(string? data) =>
            TryRead(data, out var token, out var reason) ? token! : throw new JsonReaderException(reason ?? "Data is refused.");

        /// <summary>Data as an object. From the height the strict read, below it <c>JObject.Parse</c>; throws as that does.</summary>
        public static JObject ParseObject(string? data, long height)
        {
            if (!IsStrict(height))
                return JObject.Parse(data!);
            return Read(data) as JObject ?? throw new JsonReaderException("Data is not a JSON object.");
        }

        /// <summary>
        /// Data as an array. From the height the strict read, below it <c>JsonConvert.DeserializeObject&lt;JArray&gt;</c>;
        /// throws as that does.
        /// </summary>
        public static JArray ParseArray(string? data, long height)
        {
            if (!IsStrict(height))
                return JsonConvert.DeserializeObject<JArray>(data!)!;
            return Read(data) as JArray ?? throw new JsonReaderException("Data is not a JSON array.");
        }

        /// <summary>
        /// A typed read of part of Data. From the height <see cref="ToObject{T}(JToken)"/>, below it
        /// <c>token.ToObject&lt;T&gt;()</c>.
        /// </summary>
        public static T? ToObject<T>(JToken token, long height) => IsStrict(height) ? ToObject<T>(token) : token.ToObject<T>();

        /// <summary>
        /// A typed read of a token the strict reader gave. Newtonsoft binds a name to a member ignoring case when no
        /// member has that exact name, so <c>"amount"</c> filled <c>Amount</c> for a typed reader and nothing for an
        /// indexer: a name that matches a member only when case is ignored is refused, at every level.
        /// </summary>
        public static T? ToObject<T>(JToken token)
        {
            var mismatch = CaseMismatch(token, typeof(T), 0);
            if (mismatch != null)
                throw new JsonSerializationException($"Data names \"{mismatch}\" in a case its reader does not use.");
            return token.ToObject<T>(Serializer);
        }

        /// <summary>
        /// Whether a default reader would see an object or an array at the start of Data (after any whitespace and
        /// comments). Such Data is read as a structure by some rule or apply step, so from the height it must pass the
        /// strict reader. Plain text, such as a memo, is not; nor is a lone value followed by text ("2 coffees").
        /// </summary>
        public static bool IsJsonShaped(string? data)
        {
            if (string.IsNullOrEmpty(data))
                return false;
            try
            {
                using var reader = new JsonTextReader(new StringReader(data)) { DateParseHandling = DateParseHandling.None, MaxDepth = null };
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.Comment)
                        continue;
                    if (reader.TokenType != JsonToken.StartObject && reader.TokenType != JsonToken.StartArray)
                        return false;
                    reader.Skip();
                    return true;
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or ArgumentException) { }
            return false;
        }

        /// <summary>
        /// From <see cref="Globals.StrictDataReaderHeight"/> a transaction's Data that is a JSON object or array
        /// (<see cref="IsJsonShaped"/>) passes the strict reader, on admission and in a block, so validation and state
        /// application only ever read Data the strict reader accepts. Private transactions' payloads have their own
        /// codec. Null when the rule passes.
        /// </summary>
        public static string? Refusal(Transaction tx, long height)
        {
            if (tx == null || !IsStrict(height) || string.IsNullOrEmpty(tx.Data)
                || Privacy.PrivateTransactionTypes.IsPrivateTransaction(tx.TransactionType))
                return null;
            if (!IsJsonShaped(tx.Data))
                return null;
            return TryRead(tx.Data, out _, out var reason) ? null : $"A transaction's Data must be strict JSON. {reason}";
        }

        // ── The syntax pass ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The RFC 8259 grammar, the name rules, the number rule and the depth, read token by token with
        /// System.Text.Json's reader (which allows none of Newtonsoft's extensions). Null when the text passes.
        /// </summary>
        private static string? CheckSyntax(string data)
        {
            byte[] bytes;
            try { bytes = StrictUtf8.GetBytes(data); }
            catch (EncoderFallbackException) { return "Data holds text that is not valid UTF-16."; }

            var reader = new System.Text.Json.Utf8JsonReader(bytes, new System.Text.Json.JsonReaderOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth,
            });
            // The names seen in each open object, exactly and with A-Z folded.
            var names = new Stack<(HashSet<string> Exact, HashSet<string> Folded)?>();
            try
            {
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case System.Text.Json.JsonTokenType.StartObject:
                            names.Push((new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)));
                            break;
                        case System.Text.Json.JsonTokenType.StartArray:
                            names.Push(null);
                            break;
                        case System.Text.Json.JsonTokenType.EndObject:
                        case System.Text.Json.JsonTokenType.EndArray:
                            names.Pop();
                            break;
                        case System.Text.Json.JsonTokenType.PropertyName:
                            {
                                var name = reader.GetString()!;
                                var seen = names.Peek()!.Value;
                                if (!seen.Exact.Add(name))
                                    return $"Data names \"{name}\" twice in one object.";
                                if (!seen.Folded.Add(FoldAsciiCase(name)))
                                    return $"Data names \"{name}\" in two spellings in one object.";
                                break;
                            }
                        case System.Text.Json.JsonTokenType.String:
                            reader.GetString(); // an escape that is not valid UTF-16 throws
                            break;
                        case System.Text.Json.JsonTokenType.Number:
                            {
                                var text = Encoding.ASCII.GetString(reader.ValueSpan);
                                if (!NumberFitsDecimal(text))
                                    return $"Data holds the number {text}, which a decimal cannot hold exactly.";
                                break;
                            }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                return $"Data is not strict JSON: {ex.Message}";
            }
            return null;
        }

        /// <summary>A-Z folded to a-z and nothing else, so the result never depends on a machine's culture tables.</summary>
        public static string FoldAsciiCase(string name)
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (chars[i] >= 'A' && chars[i] <= 'Z')
                    chars[i] = (char)(chars[i] + 32);
            return new string(chars);
        }

        /// <summary>
        /// Whether a JSON number reads exactly. An integer (no fraction, no exponent) is read exactly as a long or a
        /// BigInteger. Any other number is read as a decimal, so it must be one: its value, digits and power of ten,
        /// equal the decimal's (1E-30 and 0.1000000000000000000000000000001 would round silently).
        /// </summary>
        private static bool NumberFitsDecimal(string text)
        {
            if (text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
                return true;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false;
            var (digits, exponent) = Normalize(text);
            if (digits.Length == 0)
                return value == 0M;
            if (digits.Length > MaxDecimalDigits)
                return false;
            var (valueDigits, valueExponent) = Normalize(value);
            return valueDigits == digits && valueExponent == exponent;
        }

        /// <summary>A number's significant digits (no leading or trailing zeros) and the power of ten of the last one.</summary>
        private static (string Digits, long Exponent) Normalize(string text)
        {
            var body = text.TrimStart('-');
            long exponent = 0;
            var e = body.IndexOfAny(new[] { 'e', 'E' });
            if (e >= 0)
            {
                if (!long.TryParse(body[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                    return ("overflow", long.MinValue);
                body = body[..e];
            }
            var dot = body.IndexOf('.');
            if (dot >= 0)
            {
                exponent -= body.Length - dot - 1;
                body = body.Remove(dot, 1);
            }
            body = body.TrimStart('0');
            var trimmed = body.TrimEnd('0');
            exponent += body.Length - trimmed.Length;
            return (trimmed, exponent);
        }

        private static (string Digits, long Exponent) Normalize(decimal value)
        {
            var bits = decimal.GetBits(value);
            var scale = (bits[3] >> 16) & 0xFF;
            var mantissa = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
            var digits = mantissa.IsZero ? "" : mantissa.ToString(CultureInfo.InvariantCulture);
            var trimmed = digits.TrimEnd('0');
            return (trimmed, -scale + (digits.Length - trimmed.Length));
        }

        /// <summary>
        /// The first name, at any level, that matches a member of the type it is read into only when case is ignored;
        /// null when there is none.
        /// </summary>
        private static string? CaseMismatch(JToken token, Type type, int depth)
        {
            if (depth > MaxDepth)
                return null;
            var contract = Serializer.ContractResolver.ResolveContract(Nullable.GetUnderlyingType(type) ?? type);
            switch (contract)
            {
                case JsonObjectContract objectContract when token is JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        var exact = objectContract.Properties.FirstOrDefault(p => string.Equals(p.PropertyName, property.Name, StringComparison.Ordinal));
                        if (exact == null)
                        {
                            if (objectContract.Properties.Any(p => !p.Ignored && string.Equals(p.PropertyName, property.Name, StringComparison.OrdinalIgnoreCase)))
                                return property.Name;
                            continue;
                        }
                        if (exact.PropertyType != null && CaseMismatch(property.Value, exact.PropertyType, depth + 1) is { } inner)
                            return inner;
                    }
                    return null;
                case JsonArrayContract arrayContract when token is JArray array && arrayContract.CollectionItemType != null:
                    foreach (var item in array)
                        if (CaseMismatch(item, arrayContract.CollectionItemType, depth + 1) is { } inner)
                            return inner;
                    return null;
                case JsonDictionaryContract dictionaryContract when token is JObject dict && dictionaryContract.DictionaryValueType != null:
                    foreach (var property in dict.Properties())
                        if (CaseMismatch(property.Value, dictionaryContract.DictionaryValueType, depth + 1) is { } inner)
                            return inner;
                    return null;
                default:
                    return null;
            }
        }
    }
}
