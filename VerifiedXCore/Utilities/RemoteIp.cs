using System.Net;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// NEW-11: the text form of a caller's address. IPAddress.MapToIPv4 on a NATIVE IPv6 address keeps only its last 32
    /// bits (2001:db8:1234:5678::102:304 -> 1.2.3.4), and every host listens dual-stack, so any IPv6 caller could present
    /// itself as an arbitrary IPv4 address to IP-based authorization, trust, bans and throttles. Only IPv4-mapped IPv6
    /// addresses (::ffff:a.b.c.d) are converted; native IPv6 stays IPv6.
    /// </summary>
    public static class RemoteIp
    {
        public static string? Text(IPAddress? ip)
        {
            if (ip == null) return null;
            return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        }

        /// <summary>
        /// The network a caller's address is in, for limits that one party must not get around by using more addresses:
        /// the first <paramref name="v4Bits"/> bits of an IPv4 address, the first <paramref name="v6Bits"/> of an IPv6 one
        /// (a single IPv6 host is handed a /64 and can use every address in it). Written with its prefix length, so an IPv4
        /// group never equals an IPv6 one.
        /// </summary>
        public static string? Group(IPAddress? ip, int v4Bits = 24, int v6Bits = 64)
        {
            if (ip == null) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            var bits = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? v4Bits : v6Bits;
            bits = Math.Clamp(bits, 0, bytes.Length * 8);
            for (var i = 0; i < bytes.Length; i++)
            {
                var keep = Math.Clamp(bits - i * 8, 0, 8);
                bytes[i] &= (byte)(0xFF << (8 - keep));
            }
            return $"{new IPAddress(bytes)}/{bits}";
        }
    }
}
