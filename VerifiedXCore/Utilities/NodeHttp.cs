using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Http;
using VerifiedXCore.Beacon;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// How this node's HTTP clients are built. A request ends at the address it was sent to: no client follows a
    /// redirect, so an answer cannot move the node's request to another address, and a hub connection stays at the
    /// host it was opened to. The one exception is the client for the fixed outside services this node reads over
    /// HTTPS (<see cref="OutsideService"/>), some of which serve their downloads through redirects: it follows them,
    /// and never connects to an address that reaches only this machine or its link.
    /// </summary>
    internal static class NodeHttp
    {
        /// <summary>
        /// The factory client for a fixed outside service at an https URL (release downloads, block explorers, the seed
        /// list). Every other request, a request to another node above all, uses the unnamed client.
        /// </summary>
        public const string OutsideService = "outside-service";

        /// <summary>
        /// Registers the client factory the node uses (<see cref="Globals.HttpClientFactory"/>). Every client it
        /// creates, named or not, leaves redirects unfollowed, except <see cref="OutsideService"/>.
        /// </summary>
        public static IServiceCollection AddNodeHttpClients(this IServiceCollection services) =>
            AddNodeHttpClients(services, NewOutsideServiceHandler);

        /// <summary><see cref="AddNodeHttpClients(IServiceCollection)"/> with the outside-service handler given.</summary>
        internal static IServiceCollection AddNodeHttpClients(IServiceCollection services, Func<HttpMessageHandler> outsideService)
        {
            services.AddHttpClient();
            services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.PrimaryHandler = builder.Name == OutsideService ? outsideService() : NewHandler()));
            return services;
        }

        /// <summary>The longest a request to another node for a short answer takes, body included.</summary>
        public static readonly TimeSpan PeerReplyTimeout = TimeSpan.FromSeconds(10);

        /// <summary>The most bytes of another node's short answer (a signature, a refusal with its reason) that are read.</summary>
        public const int MaxPeerReplyBytes = 16 * 1024;

        /// <summary>Tests only: the handler requests to other nodes go through.</summary>
        internal static HttpMessageHandler? PeerHandlerForTests;

        private static HttpClient PeerClient() =>
            PeerHandlerForTests != null ? new HttpClient(PeerHandlerForTests, disposeHandler: false)
            : Globals.HttpClientFactory?.CreateClient() ?? NewClient();

        /// <summary>
        /// Posts JSON to another node and returns its answer when that is a short, successful one; null when the node
        /// answers with another status or with more than <paramref name="maxBytes"/>. The status is looked at before any
        /// of the body is read, no more than the limit and one byte is read, and one deadline covers the whole exchange,
        /// body included: a node that does not answer, or sends its headers and then stalls, ends the wait with an
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        public static async Task<string?> PostForShortReplyAsync(string url, string json, CancellationToken cancel = default, TimeSpan? timeout = null, int maxBytes = MaxPeerReplyBytes)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(timeout ?? PeerReplyTimeout);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var client = PeerClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await Bitcoin.Attestation.Net.BtcValidatorSource.ReadBoundedAsync(response.Content, maxBytes, deadline.Token);
        }

        /// <summary>
        /// Posts JSON to another node whose answer is not used: none of its body is read. Within
        /// <see cref="PeerReplyTimeout"/>, as <see cref="PostForShortReplyAsync"/>.
        /// </summary>
        public static async Task PostWithoutReadingAsync(string url, string json, CancellationToken cancel = default, TimeSpan? timeout = null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(timeout ?? PeerReplyTimeout);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var client = PeerClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        }

        /// <summary>A handler that leaves redirects unfollowed.</summary>
        public static HttpClientHandler NewHandler() => new() { AllowAutoRedirect = false };

        /// <summary>A client that leaves redirects unfollowed, for a caller that does not use the factory.</summary>
        public static HttpClient NewClient() => new(NewHandler());

        /// <summary>A client for a fixed outside service (<see cref="OutsideService"/>), for a caller that does not use the factory.</summary>
        public static HttpClient NewOutsideServiceClient() => new(NewOutsideServiceHandler());

        /// <summary>
        /// The handler behind <see cref="OutsideService"/>: redirects are followed, and no connection is made to a
        /// local-only address (<see cref="BeaconClient.IsLocalOnly"/>), whether the URL names it, a name resolves to
        /// it, or a redirect points to it.
        /// </summary>
        public static SocketsHttpHandler NewOutsideServiceHandler() => NewOutsideServiceHandler(BeaconClient.IsLocalOnly);

        /// <summary>
        /// <see cref="NewOutsideServiceHandler()"/> with the addresses it refuses given. On a machine set to use a
        /// proxy the connection to that proxy is made wherever it is: the proxy, not this node, then reaches the
        /// service.
        /// </summary>
        internal static SocketsHttpHandler NewOutsideServiceHandler(Func<IPAddress, bool> refused)
        {
            var handler = new SocketsHttpHandler();
            handler.ConnectCallback = async (context, cancel) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? new[] { literal }
                    : await Dns.GetHostAddressesAsync(host, cancel);
                var targets = IsTheProxy(handler, context.InitialRequestMessage, host, port)
                    ? addresses.ToList()
                    : addresses.Where(a => !refused(a)).ToList();
                if (targets.Count == 0)
                    throw new HttpRequestException($"An outside service is not reached at a local-only address ({host}).");
                return await BeaconClient.ConnectInTurn(targets, port, cancel);
            };
            return handler;
        }

        /// <summary>
        /// Whether a connection to <paramref name="host"/> and <paramref name="port"/> is the one to the proxy this
        /// machine uses for <paramref name="request"/>: the handler's own CONNECT, which it sends only to a proxy to
        /// open a tunnel for an https request, or a request the machine's proxy settings send to that address.
        /// </summary>
        private static bool IsTheProxy(SocketsHttpHandler handler, HttpRequestMessage request, string host, int port)
        {
            if (!handler.UseProxy)
                return false;
            if (request.Method.Method == "CONNECT")
                return true;
            var proxies = handler.Proxy ?? HttpClient.DefaultProxy;
            var requested = request.RequestUri;
            if (proxies == null || requested == null || proxies.IsBypassed(requested))
                return false;
            var proxy = proxies.GetProxy(requested);
            return proxy != null && proxy.Port == port && string.Equals(proxy.IdnHost, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Keeps a hub connection at the host it is opened to (the URL given to WithUrl, which sets it before the
        /// connection's options are configured). Its HTTP requests and the request that opens its socket leave
        /// redirects unfollowed, and an answer that names another host to connect to is not taken up.
        /// With <paramref name="refuseLocalOnly"/> the connection is also never made to a local-only address
        /// (<see cref="BeaconClient.NewBeaconHandler"/>).
        /// </summary>
        public static void StayAtItsHost(this HttpConnectionOptions options, bool refuseLocalOnly = false)
        {
            var hub = options.Url ?? throw new InvalidOperationException("The hub's URL is set before its connection is kept there.");
            options.HttpMessageHandlerFactory = inner => new HubHostOnly(hub, refuseLocalOnly ? BeaconClient.NewBeaconHandler() : WithoutRedirects(inner));
            options.WebSocketFactory = (context, cancel) => OpenSocketAsync(context.Uri, hub, context.Options.Headers, refuseLocalOnly, cancel);
        }

        private static HttpMessageHandler WithoutRedirects(HttpMessageHandler handler)
        {
            if (handler is not HttpClientHandler client)
                return NewHandler();
            client.AllowAutoRedirect = false;
            return client;
        }

        private static bool SameHost(Uri? a, Uri b) =>
            a != null && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

        /// <summary>Sends a hub connection's HTTP requests to the hub's host and nowhere else.</summary>
        private sealed class HubHostOnly : DelegatingHandler
        {
            private readonly Uri _hub;

            public HubHostOnly(Uri hub, HttpMessageHandler inner) : base(inner) => _hub = hub;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                SameHost(request.RequestUri, _hub)
                    ? base.SendAsync(request, cancellationToken)
                    : throw new HttpRequestException($"A hub connection stays at {_hub.Authority}; it is not moved to {request.RequestUri?.Authority}.");
        }

        private const string SocketKeySuffix = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>
        /// Opens a hub connection's socket at <paramref name="uri"/> with the WebSocket opening handshake (RFC 6455,
        /// section 4.1) over a handler that leaves redirects unfollowed: any answer but the handshake's own ends the
        /// attempt. ClientWebSocket, which would otherwise make this request, follows a redirect.
        /// </summary>
        internal static async ValueTask<WebSocket> OpenSocketAsync(Uri uri, Uri hub, IDictionary<string, string> headers, bool refuseLocalOnly, CancellationToken cancel)
        {
            if (!SameHost(uri, hub))
                throw new WebSocketException(WebSocketError.Faulted, $"A hub connection stays at {hub.Authority}; it is not moved to {uri.Authority}.");

            var handler = refuseLocalOnly ? BeaconClient.NewBeaconHandler() : new SocketsHttpHandler { AllowAutoRedirect = false };
            handler.UseCookies = false;
            handler.PooledConnectionLifetime = TimeSpan.Zero;
            // The socket keeps the connection once it is open; the handler is not needed after the handshake.
            using var invoker = new HttpMessageInvoker(handler);

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version11 };
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            foreach (var header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", key);

            var response = await invoker.SendAsync(request, cancel);
            try
            {
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + SocketKeySuffix)));
                if (response.StatusCode != HttpStatusCode.SwitchingProtocols
                    || !HasToken(response, "Connection", "Upgrade")
                    || !HasToken(response, "Upgrade", "websocket")
                    || !response.Headers.TryGetValues("Sec-WebSocket-Accept", out var accepted)
                    || !string.Equals(string.Join(", ", accepted).Trim(), accept, StringComparison.Ordinal))
                    throw new WebSocketException(WebSocketError.NotAWebSocket, $"The hub at {hub.Authority} did not open a socket (answer {(int)response.StatusCode}).");

                var stream = await response.Content.ReadAsStreamAsync(cancel);
                return WebSocket.CreateFromStream(stream, isServer: false, subProtocol: null, keepAliveInterval: WebSocket.DefaultKeepAliveInterval);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        private static bool HasToken(HttpResponseMessage response, string header, string token) =>
            response.Headers.TryGetValues(header, out var values)
            && values.SelectMany(v => v.Split(',')).Any(v => string.Equals(v.Trim(), token, StringComparison.OrdinalIgnoreCase));
    }
}
