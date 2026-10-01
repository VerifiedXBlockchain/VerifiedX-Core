using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Mainnet report: a vBTC DKG with 162 validators never got past Round 2 ("0/162 validators received shares", every
    /// request at the 90 s timeout). The coordinator sent every validator all n×(n-1) sealed shares (~10 MB each, ~1.6 GB
    /// in total from the coordinator). Each validator now gets only the packages addressed to it, in the shape the
    /// deployed /frost/dkg/shares handler reads: per sender a map { ownIdentifier: package }.
    /// </summary>
    public class FrostShareDistributionTests
    {
        private static List<string> Participants(int n) =>
            Enumerable.Range(0, n).Select(i => "R" + i.ToString("D3") + new string('x', 30)).Reverse().ToList();

        /// <summary>Round 2 output per sender, as the FROST native layer returns it: { recipientIdentifier: sealedPackage }.</summary>
        private static Dictionary<string, string> GeneratedShares(List<string> participants)
        {
            var ids = FrostStartup.BuildAddressToIdentifierMap(participants);
            return participants.ToDictionary(sender => sender, sender => new JObject(
                participants.Where(r => r != sender).Select(r => new JProperty(ids[r], $"enc1:{sender}->{r}"))).ToString(Formatting.None));
        }

        /// <summary>What the validator handler does with one sender's entry: parse it and look up its own identifier.</summary>
        private static string? Extract(string senderEntry, string myIdentifier) => (string?)JObject.Parse(senderEntry)[myIdentifier];

        [Fact]
        public void EachRecipient_GetsExactlyItsOwnPackageFromEverySender()
        {
            var participants = Participants(12);
            var ids = FrostStartup.BuildAddressToIdentifierMap(participants);
            var batches = FrostMPCService.SharesForEachRecipient(participants, GeneratedShares(participants));

            foreach (var recipient in participants)
            {
                var batch = batches[recipient];
                Assert.Equal(participants.Count - 1, batch.Count);
                Assert.DoesNotContain(recipient, batch.Keys);
                foreach (var (sender, entry) in batch)
                {
                    Assert.Single(JObject.Parse(entry).Properties());
                    Assert.Equal($"enc1:{sender}->{recipient}", Extract(entry, ids[recipient]));
                }
            }
        }

        [Fact]
        public void PayloadGrowsLinearly_NotWithTheCube()
        {
            var participants = Participants(162);
            var shares = GeneratedShares(participants);
            var batches = FrostMPCService.SharesForEachRecipient(participants, shares);

            var before = JsonConvert.SerializeObject(shares).Length;                 // what every validator used to receive
            var after = batches.Values.Max(b => JsonConvert.SerializeObject(b).Length);
            Assert.True(after * 100 < before, $"per-recipient batch {after} B vs full set {before} B");
        }

        [Fact]
        public void ASenderMapThatCannotBeRead_IsPassedThroughWhole()
        {
            var participants = Participants(4);
            var shares = GeneratedShares(participants);
            shares[participants[0]] = "not json";
            var batches = FrostMPCService.SharesForEachRecipient(participants, shares);
            Assert.Equal("not json", batches[participants[1]][participants[0]]);
        }
    }
}
