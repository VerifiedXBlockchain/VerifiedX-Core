namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// VX-03: request-level protections for the wallet API host (Startup, API port).
    ///
    /// The API binds to loopback by default, but a loopback bind does not stop a web page in the
    /// operator's browser: a cross-site request (an &lt;img&gt;/form/fetch to http://127.0.0.1:port) or a
    /// DNS-rebinding page (attacker domain resolving to 127.0.0.1) reaches it. Many routes that sign and
    /// broadcast are plain GETs, so this guard refuses browser requests that do not come from the node's
    /// own origin, and — unless the operator deliberately opened the API — any Host that is not loopback.
    ///
    /// Why this cannot break a working client: the API serves no CORS headers, so a browser page on a
    /// different origin could never read responses; every client that works today is either the node's
    /// own same-origin pages (browser wallet, explorer, swagger) or a non-browser client, which sends
    /// neither Sec-Fetch-Site nor Origin.
    /// </summary>
    public static class ApiRequestGuard
    {
        private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", "127.0.0.1", "[::1]", "::1",
        };

        /// <summary>Strips an optional port from a Host header value ("127.0.0.1:7292" → "127.0.0.1",
        /// "[::1]:7292" → "[::1]").</summary>
        public static string HostWithoutPort(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            if (host.StartsWith("["))
            {
                var close = host.IndexOf(']');
                return close > 0 ? host[..(close + 1)] : host;
            }
            var colon = host.LastIndexOf(':');
            return colon > 0 && host.IndexOf(':') == colon ? host[..colon] : host;
        }

        /// <summary>
        /// Returns null when the request may proceed, else the reason it is refused (HTTP 403).
        /// </summary>
        /// <param name="host">The request Host header (may include a port).</param>
        /// <param name="origin">The Origin header, if any.</param>
        /// <param name="secFetchSite">The Sec-Fetch-Site header, if any.</param>
        /// <param name="openApi">True when the operator started the node with "openapi" (all interfaces).</param>
        /// <summary>
        /// VX-03 (follow-up): "openapi" exposes the wallet API on every interface. Without an API token or API password
        /// nothing authenticates a network caller (the audit's own setup: openapi, no token — /wallet/api/send/vfx still
        /// worked after the route fix). Returns the reason and switches OpenAPI off when no credential is configured, so
        /// the API stays on loopback. Call before the API host is built.
        /// </summary>
        public static string? EnforceOpenApiCredential()
        {
            if (!Globals.OpenAPI)
                return null;
            var hasToken = Globals.APIToken != null && Globals.APIToken.Length > 0;
            var hasPassword = !string.IsNullOrEmpty(Globals.APIPassword);
            if (hasToken || hasPassword)
                return null;
            Globals.OpenAPI = false;
            return "openapi refused: no apitoken or APIPassword is configured, so the wallet API would accept anyone on the network. The API stays on localhost. Set apitoken=<secret> (or APIPassword in config.txt) to expose it.";
        }

        public static string? GetRejection(string? host, string? origin, string? secFetchSite, bool openApi)
        {
            // DNS rebinding: an attacker hostname that resolves to 127.0.0.1 arrives with its own Host.
            if (!openApi && !LoopbackHosts.Contains(HostWithoutPort(host ?? "")))
                return "Host not allowed. The API only answers loopback hosts unless started with openapi.";

            // Browsers label every request with where it came from. Only the node's own pages
            // (same-origin) and user-typed navigations (none) are allowed. "same-site" is refused too:
            // another port on localhost is same-site but a different application.
            if (!string.IsNullOrEmpty(secFetchSite) &&
                !secFetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase) &&
                !secFetchSite.Equals("none", StringComparison.OrdinalIgnoreCase))
                return "Cross-origin browser requests are not allowed.";

            // Older browsers without Sec-Fetch-*: an Origin header that is not this host is cross-origin.
            if (!string.IsNullOrEmpty(origin))
            {
                if (origin.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return "Cross-origin browser requests are not allowed.";
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) ||
                    !string.Equals(o.Authority, host ?? "", StringComparison.OrdinalIgnoreCase))
                    return "Cross-origin browser requests are not allowed.";
            }

            return null;
        }

        /// <summary>
        /// The ONLY paths that skip the API-enabled / API-password gate: the read-only explorer and the
        /// browser-wallet HTML shell. Every /wallet/api/* route now goes through the normal gate (it used
        /// to be exempt as a prefix, which let an unauthenticated request sign and broadcast transfers).
        /// </summary>
        public static bool IsGateExemptPath(string method, string path)
        {
            if (!HttpMethodsIsGet(method)) return false;
            var p = (path ?? "").ToLowerInvariant();
            return p == "/wallet" || p == "/wallet/" || p == "/explorer" || p.StartsWith("/explorer/");
        }

        private static bool HttpMethodsIsGet(string method) =>
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
    }
}
