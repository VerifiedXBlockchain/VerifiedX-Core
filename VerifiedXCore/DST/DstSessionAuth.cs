using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VerifiedXCore.Models.DST;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.DST
{
    /// <summary>
    /// Proof that a DST datagram's source can receive at that address, before a node sends anything to an endpoint the
    /// datagram names or remembers its source as a client. UDP sources can be spoofed, so a string compare of the source
    /// let anyone aim a shop's packets at a third party.
    /// <list type="bullet">
    /// <item>A shop's registration with a STUN server: the server answers with a random 16-byte secret, sent to the
    /// registering endpoint, and forwards punches to that shop only once the shop has returned it.</item>
    /// <item>A punch the STUN server forwards carries an HMAC under that secret over the buyer endpoint it observed and its
    /// timestamp; the shop acks only a fresh, valid, unused one.</item>
    /// <item>A buyer's helo: the shop answers a helo without a valid cookie with a cookie (an HMAC under a key only the shop
    /// holds, over the source endpoint and a time bucket) and touches no client table; a helo that returns a fresh, unused
    /// cookie from that same endpoint is accepted.</item>
    /// </list>
    /// </summary>
    public static class DstSessionAuth
    {
        /// <summary>How long a punch or a cookie stays valid.</summary>
        public const long FreshSeconds = 120;

        private const long CookieBucketSeconds = FreshSeconds / 2;

        private static readonly byte[] CookieKey = RandomNumberGenerator.GetBytes(32);

        /// <summary>Shop side: the secret each STUN server issued at registration, by the server's endpoint.</summary>
        internal static readonly ConcurrentDictionary<string, string> StunSecrets = new();

        /// <summary>Punch MACs and cookies already used, with the time they were used (single use).</summary>
        private static readonly ConcurrentDictionary<string, long> Used = new();

        private const int MaxUsed = 100_000;

        public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        private static string Mac(byte[] key, string text)
        {
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        private static bool SameMac(string a, string b) =>
            a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

        private static bool FirstUse(string token, long now)
        {
            if (Used.Count >= MaxUsed)
                foreach (var kv in Used.Where(kv => now - kv.Value > 2 * FreshSeconds).ToList())
                    Used.TryRemove(kv.Key, out _);
            return Used.Count < MaxUsed && Used.TryAdd(token, now);
        }

        // ── Punches forwarded by a STUN server ─────────────────────────────────────────────────────────────────

        public static string PunchMac(string secretB64, string buyerEndpoint, long timestamp) =>
            Mac(Convert.FromBase64String(secretB64), $"punch|{buyerEndpoint}|{timestamp}");

        /// <summary>STUN side: the PunchClient message for a registered shop, signed with the shop's secret.</summary>
        public static Message SignedPunch(string secretB64, string buyerEndpoint)
        {
            var punch = new Message { Type = MessageType.PunchClient, Data = buyerEndpoint, ResponseMessage = true };
            punch.Build();
            punch.Auth = PunchMac(secretB64, buyerEndpoint, punch.SentTimestamp);
            return punch;
        }

        /// <summary>Shop side: whether a PunchClient from <paramref name="from"/> was signed by the STUN server it registered with, fresh and unused.</summary>
        public static bool PunchIsAuthentic(Message message, string from, long now)
        {
            if (string.IsNullOrEmpty(message?.Auth) || string.IsNullOrEmpty(message.Data)
                || !StunSecrets.TryGetValue(from, out var secret)
                || Math.Abs(now - message.SentTimestamp) > FreshSeconds)
                return false;
            return SameMac(PunchMac(secret, message.Data, message.SentTimestamp), message.Auth) && FirstUse("punch|" + message.Auth, now);
        }

        // ── Cookies for a buyer's helo ─────────────────────────────────────────────────────────────────────────

        public static string Cookie(string endpoint, long now) => $"{now / CookieBucketSeconds}.{Mac(CookieKey, $"helo|{endpoint}|{now / CookieBucketSeconds}")}";

        /// <summary>Shop side: whether <paramref name="cookie"/> was issued to <paramref name="endpoint"/> within the last two buckets and is used for the first time.</summary>
        public static bool CookieIsValid(string? cookie, string endpoint, long now)
        {
            if (string.IsNullOrEmpty(cookie)) return false;
            var dot = cookie.IndexOf('.');
            if (dot <= 0 || !long.TryParse(cookie.AsSpan(0, dot), out var bucket)) return false;
            var current = now / CookieBucketSeconds;
            if (bucket != current && bucket != current - 1) return false;
            var expected = $"{bucket}.{Mac(CookieKey, $"helo|{endpoint}|{bucket}")}";
            return SameMac(expected, cookie) && FirstUse("cookie|" + cookie, now);
        }

        /// <summary>
        /// Buyer side: when <paramref name="received"/> is the shop's cookie for a helo, sends the helo again carrying it
        /// and returns true.
        /// </summary>
        public static bool AnswerCookie(byte[] received, Message helo, UdpClient udp, IPEndPoint shop)
        {
            Message? reply;
            try { reply = JsonConvert.DeserializeObject<Message>(Encoding.UTF8.GetString(received)); }
            catch (JsonException) { return false; }
            if (reply?.Type != MessageType.ShopCookie || string.IsNullOrEmpty(reply.Data))
                return false;
            helo.Build();
            helo.Auth = reply.Data;
            udp.Send(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(helo)), shop);
            return true;
        }
    }
}
