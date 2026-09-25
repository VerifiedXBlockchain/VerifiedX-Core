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
    }
}
