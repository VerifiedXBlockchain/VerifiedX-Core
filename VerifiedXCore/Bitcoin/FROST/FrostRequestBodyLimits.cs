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
            context.Request.Body = new CountedAsRead(context.Request.Body, max, counted);
            try { await next(); }
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
        /// there is no room.
        /// </summary>
        private sealed class CountedAsRead : Stream
        {
            private readonly Stream _body;
            private readonly long _max;
            private readonly CountedBody _counted;
            private long _read;

            public CountedAsRead(Stream body, long max, CountedBody counted)
            {
                _body = body;
                _max = max;
                _counted = counted;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _body.ReadAsync(buffer, cancellationToken);
                Count(read);
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _body.Read(buffer, offset, count);
                Count(read);
                return read;
            }

            private void Count(int read)
            {
                _read += read;
                if (_read > _max) throw new BadHttpRequestException(TooLong, StatusCodes.Status413PayloadTooLarge);
                if (_read <= SmallBodyBytes) return;
                if (!_counted.TryCount(_read - _counted.Bytes))
                    throw new BadHttpRequestException(Busy, StatusCodes.Status503ServiceUnavailable);
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
