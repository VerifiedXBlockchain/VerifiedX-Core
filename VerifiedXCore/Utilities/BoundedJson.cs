using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// What a JSON text from another party may hold before any of it is turned into objects. A parser builds an object
    /// for every <c>{}</c> it reads, at three bytes of text each, and a route then works through every entry of every
    /// list: a body of a few megabytes became gigabytes of objects and seconds of work before the route could look at
    /// who sent it. A text is therefore read once, as tokens and without building anything, and refused at the first
    /// place it holds more than its shape allows.
    /// </summary>
    /// <param name="MaxDepth">How deeply objects and lists may nest.</param>
    /// <param name="MaxItems">How many entries a list or members an object may hold, where nothing narrower applies.</param>
    /// <param name="MaxContainers">How many objects and lists the whole text may hold.</param>
    /// <param name="MaxStringBytes">How long a text value may be, in bytes as written, where nothing narrower applies.</param>
    /// <param name="ObjectRoot">Whether the text must be one object, as a request whose route reads named fields is.</param>
    public sealed record JsonShape(int MaxDepth, int MaxItems, int MaxContainers, int MaxStringBytes, bool ObjectRoot = false);

    /// <summary>What the length of a list in a request model is bounded by.</summary>
    public enum ListSize
    {
        /// <summary>One entry per caster or committee member, at most a few per seat: <see cref="BoundedJson.CommitteeItems"/>.</summary>
        Committee,
        /// <summary>One entry per validator: grows with the validator set (<see cref="BoundedJson.ValidatorItems"/>).</summary>
        Validators,
        /// <summary>One entry per transaction of a block (<see cref="BoundedJson.BlockTransactionItems"/>).</summary>
        BlockTransactions,
    }

    /// <summary>
    /// The bound on a request model's list or text, read by <see cref="BoundedJson"/>. A list that names none holds at
    /// most <see cref="ListSize.Committee"/> entries: a new model is narrow until it says otherwise.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class JsonBoundAttribute : Attribute
    {
        public JsonBoundAttribute() { }
        public JsonBoundAttribute(ListSize items) { Items = items; }

        public ListSize? Items { get; }
        /// <summary>The longest text value, in bytes as written; 0 for the shape's own limit.</summary>
        public int Bytes { get; set; }
    }

    public static class BoundedJson
    {
        /// <summary>Casters are at most a handful (<c>CasterDiscoveryService.MaxCasters</c>), and a record lists each signer at most twice.</summary>
        public const int CommitteeItems = 64;
        /// <summary>A member name is a property name: never long.</summary>
        public const int MaxNameBytes = 256;
        /// <summary>
        /// A number is at most a decimal's 29 digits with its sign, point and exponent. A longer one costs a parser that
        /// reads it as a big integer time that grows with the square of its length.
        /// </summary>
        public const int MaxNumberBytes = 64;

        /// <summary>One entry per validator, with room for the set to grow fourfold before it is next measured.</summary>
        public static int ValidatorItems => Math.Max(2048, 4 * Globals.NetworkValidators.Count);

        /// <summary>A block holds at most <see cref="Globals.MaxTransactionsPerBlock"/> transactions; older blocks are read too.</summary>
        public static int BlockTransactionItems => 4 * Globals.MaxTransactionsPerBlock;

        /// <summary>
        /// The validator API's bodies: each list is held to its model's bound. Objects have a few dozen members, and the
        /// largest body, a block of transactions, holds two objects a transaction.
        /// </summary>
        public static readonly JsonShape ValidatorApi = new(MaxDepth: 32, MaxItems: 256, MaxContainers: 65_536, MaxStringBytes: 64 * 1024);

        /// <summary>
        /// Bodies the validator API holds at once: three of its largest from one network, and every caster sending its
        /// largest several times over.
        /// </summary>
        public static readonly BoundedJsonBodies.BodiesInProgress ValidatorApiBodies = new(perSource: 16L * 1024 * 1024, total: 256L * 1024 * 1024);

        /// <summary>Bodies the Arbiter holds at once.</summary>
        public static readonly BoundedJsonBodies.BodiesInProgress ArbiterBodies = new(perSource: 4L * 1024 * 1024, total: 64L * 1024 * 1024);

        /// <summary>The Arbiter's signing request: one Bitcoin transaction and the coins it spends.</summary>
        public static readonly JsonShape Arbiter = new(MaxDepth: 16, MaxItems: 16_384, MaxContainers: 16_448, MaxStringBytes: 1024 * 1024, ObjectRoot: true);

        public static int ItemsFor(ListSize size) => size switch
        {
            ListSize.Validators => ValidatorItems,
            ListSize.BlockTransactions => BlockTransactionItems,
            _ => CommitteeItems,
        };

        /// <summary>
        /// Why <paramref name="utf8"/> is not a text of <paramref name="shape"/>, or null when it is. With a
        /// <paramref name="model"/> each list is held to the bound its property names (<see cref="JsonBoundAttribute"/>),
        /// as the model's own parser would read it: member names in any letter case, members the model does not have
        /// held to the shape. Only plain JSON passes: no comments, no trailing commas, one value.
        /// </summary>
        public static string? Refusal(ReadOnlySpan<byte> utf8, JsonShape shape, Type? model = null)
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                MaxDepth = shape.MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var frames = new Frame[shape.MaxDepth + 1];
            var depth = 0;
            var containers = 0;
            var next = Expectation.ForRoot(model, shape);
            try
            {
                while (reader.Read())
                {
                    if (shape.ObjectRoot && depth == 0 && containers == 0 && reader.TokenType != JsonTokenType.StartObject)
                        return "the body is not an object";
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            {
                                ref var obj = ref frames[depth - 1];
                                if (++obj.Count > obj.Limit) return $"an object holds more than {obj.Limit} members";
                                if (reader.ValueSpan.Length > MaxNameBytes) return $"a member name is longer than {MaxNameBytes} bytes";
                                next = obj.MembersAreValues ? obj.Element : obj.Element.ForMember(ref reader, shape);
                                continue;
                            }
                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            depth--;
                            continue;
                    }

                    // A value: in a list it is one more entry, and the list says what it may be.
                    if (depth > 0 && frames[depth - 1].IsList)
                    {
                        ref var list = ref frames[depth - 1];
                        if (++list.Count > list.Limit) return $"a list holds more than {list.Limit} entries";
                        next = list.Element;
                    }

                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            if (++containers > shape.MaxContainers) return $"the text holds more than {shape.MaxContainers} objects and lists";
                            if (depth >= frames.Length) return $"objects and lists nest deeper than {shape.MaxDepth}";
                            frames[depth++] = reader.TokenType == JsonTokenType.StartArray ? next.OpenList(shape) : next.OpenObject(shape);
                            break;
                        case JsonTokenType.String:
                            if (reader.ValueSpan.Length > next.StringBytes) return $"a text value is longer than {next.StringBytes} bytes";
                            break;
                        case JsonTokenType.Number:
                            if (reader.ValueSpan.Length > MaxNumberBytes) return $"a number is longer than {MaxNumberBytes} characters";
                            break;
                    }
                }
                return null;
            }
            catch (JsonException ex)
            {
                return "the body is not plain JSON (" + (ex.Message.Length > 120 ? ex.Message.Substring(0, 120) : ex.Message) + ")";
            }
        }

        /// <summary>Why <paramref name="text"/> is not a text of <paramref name="shape"/>, or null when it is.</summary>
        public static string? Refusal(string? text, JsonShape shape, Type? model = null)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
            try
            {
                var length = Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
                return Refusal(new ReadOnlySpan<byte>(buffer, 0, length), shape, model);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        /// <summary>
        /// <see cref="Newtonsoft.Json.Linq.JToken.Parse(string)"/> for a text another party sent, refused as malformed
        /// (with <see cref="Newtonsoft.Json.JsonReaderException"/>, which callers already handle) when it is not of
        /// <paramref name="shape"/>.
        /// </summary>
        public static Newtonsoft.Json.Linq.JToken Parse(string text, JsonShape shape)
        {
            if (Refusal(text, shape) is { } refusal) throw new Newtonsoft.Json.JsonReaderException("Refused: " + refusal);
            return Newtonsoft.Json.Linq.JToken.Parse(text);
        }

        /// <summary>A list or object being read: how many entries it has and may have, and what each may be.</summary>
        private struct Frame
        {
            public bool IsList;
            /// <summary>An object read as a dictionary: every member's value is <see cref="Element"/>.</summary>
            public bool MembersAreValues;
            public int Count;
            public int Limit;
            /// <summary>A list's entries, a dictionary's values, or for any other object the object itself.</summary>
            public Expectation Element;
        }

        /// <summary>
        /// What the next value may be. For a model: the value's type, and the bound its property names. Without a
        /// model, the shape's own limits.
        /// </summary>
        private readonly struct Expectation
        {
            private readonly Type? _type;
            private readonly int _items;
            public int StringBytes { get; }

            private Expectation(Type? type, int items, int stringBytes)
            {
                _type = type;
                _items = items;
                StringBytes = stringBytes;
            }

            public static Expectation ForRoot(Type? model, JsonShape shape) =>
                new(Unwrap(model), model == null ? shape.MaxItems : CommitteeItems, shape.MaxStringBytes);

            private static Expectation Generic(JsonShape shape) => new(null, shape.MaxItems, shape.MaxStringBytes);

            public Frame OpenList(JsonShape shape)
            {
                var element = _type == null ? null : Model.ElementOf(_type);
                return new Frame
                {
                    IsList = true,
                    Limit = _items,
                    // A list's text entries take the list's own text bound (a list of addresses, of signatures).
                    Element = element == null ? new Expectation(null, shape.MaxItems, StringBytes) : new Expectation(Unwrap(element), CommitteeItems, StringBytes),
                };
            }

            public Frame OpenObject(JsonShape shape)
            {
                if (_type != null && Model.ValueOfDictionary(_type) is { } value)
                    return new Frame { MembersAreValues = true, Limit = _items, Element = new Expectation(Unwrap(value), CommitteeItems, StringBytes) };
                // A model's object has members; any name it does not have is read, and bounded, as plain JSON.
                return new Frame { Limit = shape.MaxItems, Element = _type == null ? Generic(shape) : this };
            }

            /// <summary>For an object of this expectation's type, what the member just named may be.</summary>
            public Expectation ForMember(ref Utf8JsonReader reader, JsonShape shape)
            {
                if (_type == null) return Generic(shape);
                var property = Model.Property(_type, reader.GetString() ?? "");
                if (property == null) return Generic(shape);
                var bound = property.GetCustomAttribute<JsonBoundAttribute>();
                var items = bound?.Items is { } size ? ItemsFor(size) : CommitteeItems;
                var bytes = bound?.Bytes > 0 ? bound.Bytes : shape.MaxStringBytes;
                return new Expectation(Unwrap(property.PropertyType), items, bytes);
            }

            private static Type? Unwrap(Type? type)
            {
                if (type == null) return null;
                type = Nullable.GetUnderlyingType(type) ?? type;
                return Model.IsOpen(type) ? null : type;
            }
        }

        /// <summary>The members of request models, as System.Text.Json and Newtonsoft name them.</summary>
        private static class Model
        {
            private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> Properties = new();

            public static PropertyInfo? Property(Type type, string name) =>
                Properties.GetOrAdd(type, t =>
                {
                    var byName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        var written = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                            ?? p.GetCustomAttribute<Newtonsoft.Json.JsonPropertyAttribute>()?.PropertyName ?? p.Name;
                        byName.TryAdd(written, p);
                    }
                    return byName;
                }).TryGetValue(name, out var property) ? property : null;

            /// <summary>Types read as plain JSON: they hold whatever the text holds.</summary>
            public static bool IsOpen(Type type) =>
                type == typeof(object) || typeof(Newtonsoft.Json.Linq.JToken).IsAssignableFrom(type)
                || type == typeof(JsonElement) || type == typeof(JsonDocument);

            public static Type? ValueOfDictionary(Type type)
            {
                foreach (var i in new[] { type }.Concat(type.GetInterfaces()))
                    if (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
                        return i.GetGenericArguments()[1];
                return null;
            }

            public static Type? ElementOf(Type type)
            {
                if (type == typeof(string) || ValueOfDictionary(type) != null) return null;
                if (type.IsArray) return type.GetElementType();
                foreach (var i in new[] { type }.Concat(type.GetInterfaces()))
                    if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        return i.GetGenericArguments()[0];
                return null;
            }
        }
    }

    /// <summary>
    /// HTTP middleware that has every request body read once, and judged by <see cref="BoundedJson"/>, before any route
    /// or model binder turns it into objects. A body that is not of its route's shape is answered 400 and goes no
    /// further. Hosts register it once; a route added later is covered without being listed. Operators can turn it off
    /// with <c>BoundRequestBodies=false</c> in config.txt (<see cref="Globals.BoundRequestBodies"/>).
    /// </summary>
    public static class BoundedJsonBodies
    {
        /// <summary>
        /// For MVC hosts, after routing: the body of an action that binds one is held to the action's own model,
        /// within <paramref name="shape"/>.
        /// </summary>
        public static Func<HttpContext, Func<Task>, Task> ForActions(JsonShape shape, BodiesInProgress? inProgress = null) => (context, next) =>
        {
            var body = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.Parameters
                .FirstOrDefault(p => p.BindingInfo?.BindingSource == BindingSource.Body);
            return body == null ? next() : CheckAsync(context, next, shape, body.ParameterType, inProgress);
        };

        /// <summary>
        /// For hosts whose routes read their bodies themselves: each path's shape, or null to leave it alone. A host that
        /// already bounds the bodies it holds (the FROST server) passes no <paramref name="inProgress"/>.
        /// </summary>
        public static Func<HttpContext, Func<Task>, Task> ForPaths(Func<PathString, JsonShape?> shapeFor, BodiesInProgress? inProgress = null) => (context, next) =>
            shapeFor(context.Request.Path) is { } shape ? CheckAsync(context, next, shape, null, inProgress) : next();

        private static async Task CheckAsync(HttpContext context, Func<Task> next, JsonShape shape, Type? model, BodiesInProgress? inProgress)
        {
            var canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
                ?? (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"));
            if (!Globals.BoundRequestBodies || !canHaveBody || context.Request.ContentLength == 0)
            {
                await next();
                return;
            }

            // A body longer than the server or the route takes, or cut off, ends this read with the error the route's own
            // read would have met; the host answers it as before (CutOffBodyQuieting, FrostRequestBodyLimits).
            // A length is announced by a Content-Length that stands alone: sent in pieces, a body is as long as its pieces.
            var announced = context.Request.Headers.ContainsKey("Transfer-Encoding") ? null : context.Request.ContentLength;
            // Read without the request's abort token: a body cut off then ends in the server's own error, which the hosts
            // count and answer quietly, as they did when routes read the body themselves.
            var serverLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
            using var held = inProgress?.Hold(RemoteIp.Group(context.Connection.RemoteIpAddress) ?? "", announced, serverLimit);
            if (held is { Granted: false })
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new { Success = false, Message = BodiesInProgress.Busy }));
                return;
            }

            var (bytes, length) = await ReadAsync(context.Request.Body, announced, serverLimit);

            if (length > 0 && BoundedJson.Refusal(new ReadOnlySpan<byte>(bytes, 0, length), shape, model) is { } refusal)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(new { Success = false, Message = "The request body is refused: " + refusal + "." }));
                return;
            }

            context.Request.Body = new ReadBody(bytes, length);
            await next();
        }

        /// <summary>
        /// How much request body one host holds at once. A body is held from the moment it is read until its request is
        /// answered, so without a bound a sender that opens many requests and sends each slowly holds as much as it
        /// sends. A body up to <see cref="UncountedBytes"/> is not counted (one is held per connection). A longer one is
        /// counted at the length it announces, or at the server's limit when it is sent in pieces, for its network (a
        /// /24 or /64) and for the host, until its request is answered. One that does not fit is answered 503 before
        /// any of it is read, which a caller may ask again.
        /// </summary>
        public sealed class BodiesInProgress
        {
            public const int UncountedBytes = 64 * 1024;
            internal const string Busy = "Busy with other requests; ask again.";

            private readonly long _perSource;
            private readonly long _total;
            private readonly object _gate = new();
            private readonly Dictionary<string, long> _bySource = new(StringComparer.Ordinal);
            private long _all;

            public BodiesInProgress(long perSource, long total)
            {
                _perSource = perSource;
                _total = total;
            }

            /// <summary>What this host holds now, for tests and diagnostics.</summary>
            public long Held { get { lock (_gate) return _all; } }

            /// <summary>Room for a body of the announced length, or of the server's limit when it is sent in pieces.</summary>
            internal Place Hold(string source, long? announced, long? serverLimit)
            {
                var bytes = announced ?? serverLimit ?? _perSource;
                // A body the server will not take is refused by the server as it is read, and is not counted here.
                if (bytes <= UncountedBytes || (serverLimit != null && bytes > serverLimit)) return new Place(this, source, 0, granted: true);
                lock (_gate)
                {
                    _bySource.TryGetValue(source, out var ofSource);
                    if (ofSource + bytes > _perSource || _all + bytes > _total) return new Place(this, source, 0, granted: false);
                    _bySource[source] = ofSource + bytes;
                    _all += bytes;
                    return new Place(this, source, bytes, granted: true);
                }
            }

            private void Release(string source, long bytes)
            {
                if (bytes == 0) return;
                lock (_gate)
                {
                    _all -= bytes;
                    if (_bySource.TryGetValue(source, out var ofSource) && ofSource > bytes) _bySource[source] = ofSource - bytes;
                    else _bySource.Remove(source);
                }
            }

            /// <summary>One body's place, given back when its request is answered.</summary>
            internal sealed class Place : IDisposable
            {
                private readonly BodiesInProgress _owner;
                private readonly string _source;
                private long _bytes;
                public bool Granted { get; }

                public Place(BodiesInProgress owner, string source, long bytes, bool granted)
                {
                    _owner = owner;
                    _source = source;
                    _bytes = bytes;
                    Granted = granted;
                }

                public void Dispose() => _owner.Release(_source, Interlocked.Exchange(ref _bytes, 0));
            }
        }

        /// <summary>
        /// The body as it was read, for the route to read again. A route that closes its reader closes this stream with it,
        /// as it closed the server's before, and may then read it again: closing it does nothing.
        /// </summary>
        private sealed class ReadBody : MemoryStream
        {
            public ReadBody(byte[] bytes, int length) : base(bytes, 0, length, writable: false) { }
            protected override void Dispose(bool disposing) { }
        }

        /// <summary>
        /// The whole body. The server's own limit on its length still applies while it is read: room for an announced
        /// length is made only when the server takes a body that long, so a length announced past it is refused by the
        /// server's read, not allocated here first.
        /// </summary>
        private static async Task<(byte[] Bytes, int Length)> ReadAsync(Stream body, long? announced, long? serverLimit)
        {
            if (announced is > 0 && serverLimit != null && announced <= serverLimit && announced <= int.MaxValue)
            {
                var exact = new byte[announced.Value];
                var read = 0;
                while (read < exact.Length)
                {
                    var got = await body.ReadAsync(exact.AsMemory(read));
                    if (got == 0) break;
                    read += got;
                }
                return (exact, read);
            }
            using var copy = new MemoryStream();
            await body.CopyToAsync(copy);
            return (copy.GetBuffer(), (int)copy.Length);
        }
    }
}
