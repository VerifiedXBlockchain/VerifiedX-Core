using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: the vBTCb contract verifies a mint message and its admin messages with the same signers and scheme. Every
    /// admin message begins with a text tag and a mint message begins with its destination, so validators refuse to sign a
    /// mint (and the chain refuses a lock) whose destination begins with one of those tags. This pins that list to the
    /// contract source: every signed message other than the mint begins with a tag, and every tag is refused. A contract
    /// change that adds a message fails here until the list covers it.
    /// </summary>
    public class Round37_BaseAdminTagCoverageTests
    {
        private static string ContractSource()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "deploy", "contracts", "vBTCb.sol");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            throw new FileNotFoundException("deploy/contracts/vBTCb.sol not found above the test directory");
        }

        [Fact]
        public void EveryAdminMessageTagOfTheContract_IsRefusedAsAMintDestination()
        {
            var source = ContractSource();
            var tags = Regex.Matches(source, @"abi\.encodePacked\(\s*""([A-Za-z_]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
            var signedMessages = Regex.Matches(source, @"\.toEthSignedMessageHash\(\)").Count;

            Assert.NotEmpty(tags);
            // One signed message is the mint (it begins with the destination); every other begins with a tag.
            Assert.Equal(tags.Count + 1, signedMessages);

            foreach (var tag in tags)
            {
                var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(tag)).ToLowerInvariant();
                var destination = "0x" + (hex + new string('1', 40)).Substring(0, 40);
                Assert.True(BaseBridgeAttestationService.DestinationStartsWithAdminTag(destination), $"a destination beginning with \"{tag}\" is not refused");
            }
            Assert.False(BaseBridgeAttestationService.DestinationStartsWithAdminTag("0x" + new string('a', 40)));
        }
    }
}
