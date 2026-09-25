using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-11 (found by the fourth independent review): IPAddress.MapToIPv4() on a native IPv6 address keeps only its
    /// last 32 bits, and the hosts listen dual-stack, so an IPv6 caller could present itself as any IPv4 address to the
    /// IP-based beacon authorization, peer identity, bans and throttles.
    /// </summary>
    public class NEW11_RemoteIpTests
    {
        [Theory]
        [InlineData("2001:db8:1234:5678::102:304", "2001:db8:1234:5678::102:304")] // native IPv6 stays IPv6 (was 1.2.3.4)
        [InlineData("::ffff:1.2.3.4", "1.2.3.4")]                                  // IPv4-mapped -> IPv4
        [InlineData("1.2.3.4", "1.2.3.4")]
        public void NEW11_RemoteIpText(string input, string expected) =>
            Assert.Equal(expected, RemoteIp.Text(IPAddress.Parse(input)));

        [Fact]
        public void NEW11_PoC_OldMappingAliasedAnIPv6CallerToAnIPv4Address() =>
            Assert.Equal("1.2.3.4", IPAddress.Parse("2001:db8:1234:5678::102:304").MapToIPv4().ToString());

        [Fact]
        public void NEW11_NoUnconditionalMapToIPv4LeftInTheNode()
        {
            var repo = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var root = Path.Combine(repo, "VerifiedXCore");
            var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
                .Where(x => x.l.Contains("MapToIPv4()") && !x.l.Contains("IsIPv4MappedToIPv6"))
                .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
                .ToList();
            Assert.Empty(offenders);
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
