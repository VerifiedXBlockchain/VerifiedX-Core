using Microsoft.AspNetCore.Http.Features;
using Newtonsoft.Json;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// How much request body the FROST server takes into memory. A route reads its body before it can tell who sent
    /// it, so what it reads is bounded by what the route's own callers send, and by how much is being read at once:
    /// <list type="bullet">
    /// <item>a route takes a body of at most <see cref="MaxBytesFor"/>: <see cref="SmallBodyBytes"/>, except where a
    /// ceremony sends more (<see cref="SigningBodyBytes"/> to start a signing and for its nonce set,
    /// <see cref="LargestBodyBytes"/> for a key ceremony's commitment set and share batch). A request that announces
    /// more is refused unread, and reading any body ends at that size;</item>
    /// <item>a body over <see cref="SmallBodyBytes"/> is counted as it is read, for as long as its request is served:
    /// one source holds at most <see cref="CountedPerSourceBytes"/> of what has been read, and each kind of caller
    /// (the kinds <see cref="FrostConnectionLimits"/> keeps apart) <see cref="CountedPerKindBytes"/>. Reading ends
    /// where there is no room for more. A request that announces a length there is no room for now is answered
    /// "busy" before any of it is read.</item>
    /// </list>
    /// A request whose body is not read to its end for either reason is answered here, whatever its route does with
    /// the ended read: 413 for a body longer than the route takes, 503 ("busy") where there was no room, which a
    /// caller may ask again.
    /// With one request at a time on a connection (both listeners speak HTTP/1.1 only), the bodies held for one kind
    /// of caller are at most its connections times <see cref="SmallBodyBytes"/>, plus <see cref="CountedPerKindBytes"/>.
    /// </summary>
    public sealed class FrostRequestBodyLimits
    {
        /// <summary>The body any route takes; a body up to this size is not counted.</summary>
        public const int SmallBodyBytes = 64 * 1024;
        /// <summary>
        /// The body the signing routes take: a start request carries its transaction with every input's coin and hash
        /// (about 800 KB for the largest transaction Bitcoin relays), a nonce set one entry a signer.
        /// </summary>
        public const int SigningBodyBytes = 1024 * 1024;
        /// <summary>
        /// The body a key ceremony's commitment set and share batch may have, and the server's limit for any body. A
        /// commitment set for 200 participants is about 2 MB.
        /// </summary>
        public const int LargestBodyBytes = 8 * 1024 * 1024;
        /// <summary>
        /// Counted bodies one source may have in progress: eight commitment sets of 2 MB, a source leading at most
        /// eight key ceremonies at once.
        /// </summary>
        public const long CountedPerSourceBytes = 16L * 1024 * 1024;
        /// <summary>Counted bodies one kind of caller may have in progress.</summary>
        public const long CountedPerKindBytes = 64L * 1024 * 1024;

        private const string Busy = "Busy with other requests; ask again.";
        private const string TooLong = "The request body is longer than this route takes.";

        /// <summary>Counts for requests on connections that no <see cref="FrostConnectionLimits"/> placed: together they are one source.</summary>
        private static readonly FrostRequestBodyLimits Unplaced = new();

        private readonly object _gate = new();
        private readonly Dictionary<string, long> _bySource = new(StringComparer.Ordinal);
        private readonly Dictionary<object, long> _byKind = new();

        /// <summary>
        /// What a body may hold, as well as how long it may be (<see cref="ShapeFor"/>). Most routes take a request with
        /// a list of at most one entry per participant.
        /// </summary>
        public static readonly VerifiedXCore.Utilities.JsonShape SmallShape = new(MaxDepth: 16,
            MaxItems: Models.FrostSessionStorage.MAX_PARTICIPANTS + 16, MaxContainers: 1024, MaxStringBytes: SmallBodyBytes, ObjectRoot: true);
        /// <summary>A signing start and nonce set: an entry per input of the largest transaction Bitcoin relays.</summary>
        public static readonly VerifiedXCore.Utilities.JsonShape SigningShape = new(MaxDepth: 16,
            MaxItems: 16_384, MaxContainers: 16_448, MaxStringBytes: SigningBodyBytes, ObjectRoot: true);
        /// <summary>
        /// A key ceremony's commitment set and share batch: one text entry per participant, each a package that is
        /// itself held to <see cref="PackageShape"/> before it is read.
        /// </summary>
        public static readonly VerifiedXCore.Utilities.JsonShape CeremonyShape = new(MaxDepth: 8,
            MaxItems: Models.FrostSessionStorage.MAX_PARTICIPANTS + 16, MaxContainers: 64, MaxStringBytes: FrostDkgBlame.MaxDealerEntryLength, ObjectRoot: true);
        /// <summary>One FROST package from another participant: a header, a commitment with one entry per coefficient, a proof or a share.</summary>
        public static readonly VerifiedXCore.Utilities.JsonShape PackageShape = new(MaxDepth: 8,
            MaxItems: Models.FrostSessionStorage.MAX_PARTICIPANTS + 16, MaxContainers: 16, MaxStringBytes: Models.FrostSessionStorage.MAX_COMMITMENT_DATA_LENGTH);
        /// <summary>A dealer's shares: one package for each participant.</summary>
        public static readonly VerifiedXCore.Utilities.JsonShape PackageMapShape = new(MaxDepth: 8,
            MaxItems: Models.FrostSessionStorage.MAX_PARTICIPANTS + 16, MaxContainers: 3 * (Models.FrostSessionStorage.MAX_PARTICIPANTS + 16) + 8,
            MaxStringBytes: Models.FrostSessionStorage.MAX_COMMITMENT_DATA_LENGTH);

        /// <summary>What the body of the route at <paramref name="path"/> may hold.</summary>
        public static VerifiedXCore.Utilities.JsonShape ShapeFor(PathString path)
        {
            if (path.StartsWithSegments("/frost/dkg/round2") || path.StartsWithSegments("/frost/dkg/shares"))
                return CeremonyShape;
            if (path.StartsWithSegments("/frost/sign/start") || path.StartsWithSegments("/frost/sign/round2"))
                return SigningShape;
            return SmallShape;
        }

        /// <summary>
        /// Why a posted commitment set may not be read, or null: every entry is a package of <see cref="PackageShape"/>.
        /// A wide entry parses into a large object graph, so the whole set is judged before any entry is parsed.
        /// </summary>
        public static string? CommitmentSetRefusal(IReadOnlyDictionary<string, string>? set)
        {
            if (set == null) return null;
            foreach (var entry in set)
                if (VerifiedXCore.Utilities.BoundedJson.Refusal(entry.Value, PackageShape) is { } refusal)
                    return $"The commitment for {(entry.Key.Length > 64 ? entry.Key.Substring(0, 64) : entry.Key)} is refused: {refusal}.";
            return null;
        }

        /// <summary>The longest body the route at <paramref name="path"/> takes.</summary>
        public static int MaxBytesFor(PathString path)
        {
            if (path.StartsWithSegments("/frost/dkg/round2") || path.StartsWithSegments("/frost/dkg/shares"))
                return LargestBodyBytes;
            if (path.StartsWithSegments("/frost/sign/start") || path.StartsWithSegments("/frost/sign/round2"))
                return SigningBodyBytes;
            return SmallBodyBytes;
        }

        /// <summary>
        /// HTTP middleware, ahead of every route: refuses a request that announces a longer body than its route takes
        /// or than there is room for, and has every other body read through the route's size and the counts.
        /// </summary>
        public static async Task Bound(HttpContext context, Func<Task> next)
        {
            var max = MaxBytesFor(context.Request.Path);
            var serverLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (serverLimit != null && !serverLimit.IsReadOnly) serverLimit.MaxRequestBodySize = max;

            // A length is announced by a Content-Length that stands alone: sent in pieces (Transfer-Encoding), a body
            // is as long as its pieces, whatever length is written beside them.
            var announced = context.Request.Headers.ContainsKey("Transfer-Encoding") ? null : context.Request.ContentLength;
            if (announced > max)
            {
                await Refuse(context, StatusCodes.Status413PayloadTooLarge, TooLong);
                return;
            }

            var hasBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? true;
            if (!hasBody || announced <= SmallBodyBytes)
            {
                await next();
                return;
            }

            var place = context.Features.Get<FrostConnectionLimits.Place>();
            var counted = new CountedBody(place?.Bodies ?? Unplaced, place?.Source ?? "", place?.Budget);
            if (announced != null && !counted.HasRoomFor(announced.Value))
            {
                await Refuse(context, StatusCodes.Status503ServiceUnavailable, Busy);
                return;
            }
            var body = new CountedAsRead(context, max, counted);
            context.Request.Body = body;
            try { await next(); }
            catch (Exception) when (body.Answered)
            {
                // The request was answered where its body ended; what its route then made of the ended read (an
                // answer of its own, which cannot follow the one given) is not the request's outcome.
            }
            finally { counted.Uncount(); }
        }

        private static Task Refuse(HttpContext context, int status, string message)
        {
            context.Response.StatusCode = status;
            return context.Response.WriteAsync(JsonConvert.SerializeObject(new { Success = false, Message = message }));
        }

        /// <summary>
        /// Counts <paramref name="bytes"/> of body for <paramref name="source"/>, until <see cref="Uncount"/>; false
        /// when the source or its <paramref name="kind"/> (null: a source that shares with no other) has no room for
        /// them. With <paramref name="count"/> false nothing is counted: the answer is whether there is room now.
        /// </summary>
        internal bool TryCount(string source, object? kind, long bytes, bool count = true)
        {
            lock (_gate)
            {
                _bySource.TryGetValue(source, out var ofSource);
                if (ofSource + bytes > CountedPerSourceBytes) return false;
                long ofKind = 0;
                if (kind != null)
                {
                    _byKind.TryGetValue(kind, out ofKind);
                    if (ofKind + bytes > CountedPerKindBytes) return false;
                }
                if (!count) return true;
                if (kind != null) _byKind[kind] = ofKind + bytes;
                _bySource[source] = ofSource + bytes;
                return true;
            }
        }

        internal void Uncount(string source, object? kind, long bytes)
        {
            lock (_gate)
            {
                if (_bySource.TryGetValue(source, out var ofSource))
                {
                    if (ofSource > bytes) _bySource[source] = ofSource - bytes;
                    else _bySource.Remove(source);
                }
                if (kind != null && _byKind.TryGetValue(kind, out var ofKind))
                {
                    if (ofKind > bytes) _byKind[kind] = ofKind - bytes;
                    else _byKind.Remove(kind);
                }
            }
        }

        /// <summary>Tests only: the bytes of body counted now, over every source.</summary>
        internal long Counted()
        {
            lock (_gate) return _bySource.Values.Sum();
        }

        /// <summary>What one request's body is counted as, given back when the request has been served.</summary>
        private sealed class CountedBody
        {
            private readonly FrostRequestBodyLimits _bodies;
            private readonly string _source;
            private readonly object? _kind;
            public long Bytes { get; private set; }

            public CountedBody(FrostRequestBodyLimits bodies, string source, object? kind)
            {
                _bodies = bodies;
                _source = source;
                _kind = kind;
            }

            public bool HasRoomFor(long bytes) => _bodies.TryCount(_source, _kind, bytes, count: false);

            public bool TryCount(long bytes)
            {
                if (!_bodies.TryCount(_source, _kind, bytes)) return false;
                Bytes += bytes;
                return true;
            }

            public void Uncount()
            {
                if (Bytes > 0) _bodies.Uncount(_source, _kind, Bytes);
                Bytes = 0;
            }
        }

        /// <summary>
        /// A request body as its route reads it: reading ends past the route's size, and once more than
        /// <see cref="SmallBodyBytes"/> has been read, all that has been read is counted, and reading ends where
        /// there is no room. Where reading ends so, the request is answered before the route's read fails.
        /// </summary>
        private sealed class CountedAsRead : Stream
        {
            private readonly HttpContext _context;
            private readonly Stream _body;
            private readonly long _max;
            private readonly CountedBody _counted;
            private long _read;

            /// <summary>Whether the request was answered here, its body not read to the end.</summary>
            public bool Answered { get; private set; }

            public CountedAsRead(HttpContext context, long max, CountedBody counted)
            {
                _context = context;
                _body = context.Request.Body;
                _max = max;
                _counted = counted;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int read;
                try { read = await _body.ReadAsync(buffer, cancellationToken); }
                catch (BadHttpRequestException tooLong) when (tooLong.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    // The server's own limit for this request (the route's size, set in Bound) ended the read.
                    await AnswerAsync(tooLong);
                    throw;
                }
                var refusal = Count(read);
                if (refusal != null)
                {
                    await AnswerAsync(refusal);
                    throw refusal;
                }
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _body.Read(buffer, offset, count);
                return Count(read) is { } refusal ? throw refusal : read;
            }

            /// <summary>Counts what was read; the refusal when reading ends here, null when it goes on.</summary>
            private BadHttpRequestException? Count(int read)
            {
                _read += read;
                if (_read > _max) return new BadHttpRequestException(TooLong, StatusCodes.Status413PayloadTooLarge);
                if (_read <= SmallBodyBytes) return null;
                return _counted.TryCount(_read - _counted.Bytes) ? null : new BadHttpRequestException(Busy, StatusCodes.Status503ServiceUnavailable);
            }

            private async Task AnswerAsync(BadHttpRequestException refusal)
            {
                if (_context.Response.HasStarted) return;
                Answered = true;
                await Refuse(_context, refusal.StatusCode, refusal.StatusCode == StatusCodes.Status503ServiceUnavailable ? Busy : TooLong);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
