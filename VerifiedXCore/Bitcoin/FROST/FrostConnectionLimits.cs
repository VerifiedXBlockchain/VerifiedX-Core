using System.Net;
using Microsoft.AspNetCore.Connections;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// How many connections the FROST server holds open for one caller, decided when a connection arrives and before
    /// anything is read from it. Connections are counted in three separate budgets, so that no kind of caller can take
    /// another kind's room:
    /// <list type="bullet">
    /// <item>the addresses of the registered vBTC validators that hold the validator balance share
    /// <see cref="ValidatorTotal"/> connections, one address at most <see cref="PerValidator"/>, so validators reach
    /// each other for signing, key ceremonies, backups and Bitcoin reports whatever other callers do;</item>
    /// <item>every other source shares <see cref="OtherTotal"/> connections, one network
    /// (<see cref="RemoteIp.Group"/>) at most <see cref="PerOtherSource"/>;</item>
    /// <item>the node's own operator (loopback) has <see cref="Loopback"/>.</item>
    /// </list>
    /// A connection kept open with no request on it does not keep a caller out: when a source or a budget is full, a
    /// connection that has been idle for <see cref="IdleSecondsBeforeMakingRoom"/> is closed to make room (see
    /// <see cref="TryAdmit"/>). Only when nothing is idle is the new connection closed unanswered. A request counts as
    /// one only while its body arrives at <see cref="MinRequestBodyBytesPerSecond"/> or faster; the server drops a
    /// slower one.
    /// <see cref="FrostPeerHttp"/> keeps this node's own requests to a validator inside one source's limit.
    /// </summary>
    public sealed class FrostConnectionLimits
    {
        /// <summary>Connections one registered validator's address may hold.</summary>
        public const int PerValidator = 16;
        /// <summary>Connections all registered validators' addresses may hold together.</summary>
        public const int ValidatorTotal = 512;
        /// <summary>Connections one network that is not a registered validator's address may hold.</summary>
        public const int PerOtherSource = 16;
        /// <summary>Connections all sources that are not registered validators may hold together.</summary>
        public const int OtherTotal = 256;
        /// <summary>Connections the node's own operator (loopback) may hold.</summary>
        public const int Loopback = 32;
        /// <summary>
        /// Kestrel's own ceiling for a listener: every budget above plus room for connections that are being turned
        /// away, so that these limits decide before Kestrel's does.
        /// </summary>
        public const int ServerTotal = ValidatorTotal + OtherTotal + Loopback + 224;
        /// <summary>Seconds a connection has carried no request before it may be closed to make room for another.</summary>
        public const int IdleSecondsBeforeMakingRoom = 2;

        /// <summary>The slowest a request body may arrive, after the same seconds of grace, before the server drops the request.</summary>
        public const int MinRequestBodyBytesPerSecond = 16 * 1024;

        /// <summary>How long the set of validator addresses is used before it is read again.</summary>
        private const long ValidatorsFreshForMs = 15_000;
        /// <summary>How long one read of the validator addresses is waited for before another is started.</summary>
        private const long ValidatorsReadGivenUpAfterMs = 60_000;

        /// <summary>The limits the running FROST server enforces.</summary>
        public static readonly FrostConnectionLimits Server = new(FundedValidatorAddresses);

        internal sealed class Budget
        {
            public readonly int Total;
            public int Held;
            public Budget(int total) { Total = total; }
        }

        private readonly Func<IReadOnlyCollection<string>?> _readValidators;
        private readonly Func<long> _nowMs;
        private readonly object _gate = new();
        private readonly Dictionary<string, List<Place>> _bySource = new(StringComparer.Ordinal);
        private readonly Budget _validatorBudget = new(ValidatorTotal);
        private readonly Budget _otherBudget = new(OtherTotal);
        private IReadOnlyCollection<string> _validators = Array.Empty<string>();
        private long? _validatorsReadAt;
        private long? _readingValidatorsSince;

        /// <param name="readValidators">The addresses (<see cref="RemoteIp.Text"/> form) that have a validator's room; null when they cannot be known now.</param>
        /// <param name="nowMs">Tests only: the clock, in milliseconds.</param>
        internal FrostConnectionLimits(Func<IReadOnlyCollection<string>?> readValidators, Func<long>? nowMs = null)
        {
            _readValidators = readValidators;
            _nowMs = nowMs ?? (() => Environment.TickCount64);
        }

        /// <summary>
        /// The addresses of the registered vBTC validators that hold the validator balance now, as this node sees them;
        /// null while it is not synced, when the registry says nothing either way. Holding the balance is what makes an
        /// address cost something: a registration alone does not.
        /// </summary>
        internal static IReadOnlyCollection<string>? FundedValidatorAddresses()
        {
            if (!Globals.IsChainSynced) return null;
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var validator in VBTCValidatorRegistry.FundedOnly(VBTCValidatorRegistry.GetActiveValidators()))
            {
                if (IPAddress.TryParse(validator.IPAddress, out var ip))
                    addresses.Add(RemoteIp.Text(ip)!);
            }
            return addresses;
        }

        /// <summary>
        /// Kestrel connection middleware: a connection its source has no room for ends here, before TLS and before a
        /// byte of a request is read; any other holds its place until it closes.
        /// </summary>
        public ConnectionDelegate Guard(ConnectionDelegate next)
        {
            return async connection =>
            {
                var place = TryAdmit((connection.RemoteEndPoint as IPEndPoint)?.Address, connection);
                if (place == null) return;
                connection.Features.Set(place);
                try { await next(connection); }
                finally { place.Dispose(); }
            };
        }

        /// <summary>
        /// HTTP middleware: marks the connection's place as serving a request for as long as one is, so that only a
        /// connection with nothing on it is ever closed to make room.
        /// </summary>
        public static async Task TrackRequest(HttpContext context, Func<Task> next)
        {
            var place = context.Features.Get<Place>();
            if (place == null) { await next(); return; }
            place.RequestStarted();
            try { await next(); }
            finally { place.RequestEnded(); }
        }

        /// <summary>
        /// A place for a new connection from <paramref name="remote"/>, given back when disposed; null when it has none.
        /// When the source is at its own limit, its connection that has been idle longest gives up its place. When the
        /// source's budget is full, the place comes from the source holding the most, if that is more than this source
        /// holds: again the connection idle longest. A connection serving a request is not chosen; one whose request
        /// arrives in the instant after it was chosen is closed with it, and its caller connects again.
        /// </summary>
        internal Place? TryAdmit(IPAddress? remote, ConnectionContext? connection = null)
        {
            string source;
            int limit;
            Budget? budget;
            if (remote != null && IPAddress.IsLoopback(remote))
            {
                source = "loopback";
                limit = Loopback;
                budget = null;
            }
            else
            {
                var address = RemoteIp.Text(remote);
                if (address != null && Validators().Contains(address))
                {
                    source = "validator " + address;
                    limit = PerValidator;
                    budget = _validatorBudget;
                }
                else
                {
                    source = "other " + (RemoteIp.Group(remote) ?? "unknown");
                    limit = PerOtherSource;
                    budget = _otherBudget;
                }
            }

            Place? closed = null;
            Place admitted;
            lock (_gate)
            {
                var now = _nowMs();
                _bySource.TryGetValue(source, out var held);
                var holds = held?.Count ?? 0;
                if (holds >= limit)
                    closed = LongestIdle(held!, now);
                else if (budget != null && budget.Held >= budget.Total)
                    closed = LongestIdleOfLargestSource(budget, holds, now);
                if (closed == null && (holds >= limit || (budget != null && budget.Held >= budget.Total)))
                    return null;
                if (closed != null) ReleaseLocked(closed);

                admitted = new Place(this, source, budget, connection, now);
                if (!_bySource.TryGetValue(source, out held)) _bySource[source] = held = new List<Place>();
                held.Add(admitted);
                if (budget != null) budget.Held++;
            }
            // Outside the lock: closing runs the closed connection's own release, which takes it.
            closed?.Close();
            return admitted;
        }

        /// <summary>Tests only: connections holding a place now, and how many of them are serving a request.</summary>
        internal (int Held, int Serving) Counts()
        {
            lock (_gate)
            {
                var all = _bySource.Values.SelectMany(p => p).ToList();
                return (all.Count, all.Count(p => p.Requests > 0));
            }
        }

        /// <summary>Reads the validator addresses now. The server reads them in the background; tests call this.</summary>
        internal void ReadValidators()
        {
            IReadOnlyCollection<string>? read = null;
            try { read = _readValidators(); }
            catch (Exception ex)
            {
                // The registry cannot be read (at most one try per ValidatorsFreshForMs): the set already held stands.
                ErrorLogUtility.LogError($"Validator addresses could not be read: {ex.Message}", "FrostConnectionLimits.ReadValidators");
            }
            lock (_gate)
            {
                // No answer (not synced, or the read failed) keeps what is held; an answer is applied, an empty one too.
                if (read != null) _validators = read;
                _validatorsReadAt = _nowMs();
                _readingValidatorsSince = null;
            }
        }

        /// <summary>The validator addresses held now. A stale set is read again in the background, never by the connection that noticed.</summary>
        private IReadOnlyCollection<string> Validators()
        {
            lock (_gate)
            {
                var now = _nowMs();
                var stale = _validatorsReadAt == null || now - _validatorsReadAt.Value >= ValidatorsFreshForMs;
                var reading = _readingValidatorsSince != null && now - _readingValidatorsSince.Value < ValidatorsReadGivenUpAfterMs;
                if (stale && !reading)
                {
                    _readingValidatorsSince = now;
                    _ = Task.Run(ReadValidators);
                }
                return _validators;
            }
        }

        private static Place? LongestIdle(IEnumerable<Place> places, long now)
        {
            Place? longest = null;
            foreach (var place in places)
            {
                if (place.Requests > 0 || now - place.IdleSince < IdleSecondsBeforeMakingRoom * 1000L) continue;
                if (longest == null || place.IdleSince < longest.IdleSince) longest = place;
            }
            return longest;
        }

        private Place? LongestIdleOfLargestSource(Budget budget, int newcomerHolds, long now)
        {
            Place? chosen = null;
            var chosenHolds = newcomerHolds; // only a source holding more than the newcomer gives a place up
            foreach (var held in _bySource.Values)
            {
                if (held.Count <= chosenHolds || held[0].Budget != budget) continue;
                var idle = LongestIdle(held, now);
                if (idle == null) continue;
                chosen = idle;
                chosenHolds = held.Count;
            }
            return chosen;
        }

        private void ReleaseLocked(Place place)
        {
            if (place.Released) return;
            place.Released = true;
            if (_bySource.TryGetValue(place.Source, out var held))
            {
                held.Remove(place);
                if (held.Count == 0) _bySource.Remove(place.Source);
            }
            if (place.Budget != null) place.Budget.Held--;
        }

        /// <summary>One connection's place. Its fields are read and written under the limits' lock.</summary>
        internal sealed class Place : IDisposable
        {
            private readonly FrostConnectionLimits _limits;
            private readonly ConnectionContext? _connection;
            internal readonly string Source;
            internal readonly Budget? Budget;
            internal int Requests;
            internal long IdleSince;
            internal bool Released;

            internal Place(FrostConnectionLimits limits, string source, Budget? budget, ConnectionContext? connection, long now)
            {
                _limits = limits;
                Source = source;
                Budget = budget;
                _connection = connection;
                IdleSince = now;
            }

            internal void RequestStarted()
            {
                lock (_limits._gate) Requests++;
            }

            internal void RequestEnded()
            {
                lock (_limits._gate)
                {
                    if (Requests > 0) Requests--;
                    IdleSince = _limits._nowMs();
                }
            }

            /// <summary>Closes the connection whose place was given to another.</summary>
            internal void Close() => _connection?.Abort(new ConnectionAbortedException("The connection was idle and its place was needed."));

            public void Dispose()
            {
                lock (_limits._gate) _limits.ReleaseLocked(this);
            }
        }
    }
}
