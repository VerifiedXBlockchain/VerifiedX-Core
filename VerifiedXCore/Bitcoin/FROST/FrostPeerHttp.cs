namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// The HTTP clients this node uses to reach other validators' FROST servers. They share two connection pools, so
    /// that one node never opens more connections to one validator than <see cref="FrostConnectionLimits"/> gives a
    /// source: a request beyond a pool's size waits its turn here instead of being turned away there. Ceremony requests
    /// (key generation and signing rounds, which can take long) have one pool; short requests (reachability, pins,
    /// backups, Bitcoin reports and headers) have the other, so they never wait behind a ceremony. The wait counts
    /// against the request's timeout: a node leading more ceremonies at once than a pool has connections may see the
    /// requests at the back of the line time out, where the validator would otherwise have turned them away.
    /// </summary>
    internal static class FrostPeerHttp
    {
        /// <summary>Connections to one validator that ceremony requests share.</summary>
        public const int CeremonyConnectionsPerValidator = 10;
        /// <summary>Connections to one validator that short requests share.</summary>
        public const int ShortConnectionsPerValidator = 4;

        private static readonly SocketsHttpHandler Ceremonies = new() { MaxConnectionsPerServer = CeremonyConnectionsPerValidator };
        private static readonly SocketsHttpHandler Short = new() { MaxConnectionsPerServer = ShortConnectionsPerValidator };

        /// <summary>A client for ceremony requests. Its timeout covers the wait for a free connection.</summary>
        public static HttpClient ForCeremonies(TimeSpan timeout, long? maxResponseBytes = null)
        {
            var client = new HttpClient(Ceremonies, disposeHandler: false) { Timeout = timeout };
            if (maxResponseBytes != null) client.MaxResponseContentBufferSize = maxResponseBytes.Value;
            return client;
        }

        /// <summary>A client for short requests. Its timeout covers the wait for a free connection.</summary>
        public static HttpClient ForShortRequests(TimeSpan timeout) =>
            new HttpClient(Short, disposeHandler: false) { Timeout = timeout };
    }
}
