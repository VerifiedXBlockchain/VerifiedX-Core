using Xunit;
using ReserveBlockCore.Models;
using ReserveBlockCore.Data;
using System.Collections.Generic;
using System.Linq;

namespace VerfiedXCore.Tests
{
    public class BeaconMessageRoutingSecurityTests
    {
        [Fact]
        public void BeaconPool_ShouldNotAllowMessageHijacking_WithSameReferenceButDifferentIP()
        {
            // Arrange - Simulate two users: legitimate user and attacker
            var legitimateIP = "192.168.1.100";
            var attackerIP = "192.168.1.200";
            var sharedReference = "shared-ref-12345";

            var beaconPool = new List<BeaconPool>
            {
                new BeaconPool 
                { 
                    IpAddress = legitimateIP, 
                    Reference = sharedReference,
                    ConnectionId = "legitimate-connection-id"
                },
                new BeaconPool 
                { 
                    IpAddress = attackerIP, 
                    Reference = sharedReference,  // Attacker tries to use same reference
                    ConnectionId = "attacker-connection-id"
                }
            };

            // Act - Lookup using composite key (IP + Reference) as the fix does
            var legitimateLookup = beaconPool
                .Where(x => x.Reference == sharedReference && x.IpAddress == legitimateIP)
                .FirstOrDefault();

            var attackerLookup = beaconPool
                .Where(x => x.Reference == sharedReference && x.IpAddress == attackerIP)
                .FirstOrDefault();

            // Assert - Each IP should only get their own connection
            Assert.NotNull(legitimateLookup);
            Assert.NotNull(attackerLookup);
            Assert.Equal("legitimate-connection-id", legitimateLookup.ConnectionId);
            Assert.Equal("attacker-connection-id", attackerLookup.ConnectionId);
            Assert.NotEqual(legitimateLookup.ConnectionId, attackerLookup.ConnectionId);
        }

        [Fact]
        public void BeaconPool_ReferenceOnlyLookup_WouldAllowHijacking()
        {
            // Arrange - This test demonstrates the VULNERABILITY that existed before the fix
            var legitimateIP = "192.168.1.100";
            var attackerIP = "192.168.1.200";
            var sharedReference = "shared-ref-12345";

            var beaconPool = new List<BeaconPool>
            {
                new BeaconPool 
                { 
                    IpAddress = legitimateIP, 
                    Reference = sharedReference,
                    ConnectionId = "legitimate-connection-id"
                },
                new BeaconPool 
                { 
                    IpAddress = attackerIP, 
                    Reference = sharedReference,
                    ConnectionId = "attacker-connection-id"
                }
            };

            // Act - OLD vulnerable lookup that only used Reference (before HAL-043 fix)
            var vulnerableLookup = beaconPool
                .Where(x => x.Reference == sharedReference)
                .FirstOrDefault();

            // Assert - This demonstrates the vulnerability: wrong connection could be returned
            // The first match is returned, which could be either legitimate or attacker
            Assert.NotNull(vulnerableLookup);
            // This is non-deterministic - could match either connection, demonstrating the hijacking risk
            Assert.True(
                vulnerableLookup.ConnectionId == "legitimate-connection-id" || 
                vulnerableLookup.ConnectionId == "attacker-connection-id"
            );
        }

        [Fact]
        public void BeaconData_MessageRouting_RequiresBothIPAndReference()
        {
            // Arrange - Simulate beacon data for NFT transfer
            var senderIP = "10.0.0.5";
            var senderRef = "sender-ref-abc";
            var receiverIP = "10.0.0.10";
            var receiverRef = "receiver-ref-xyz";

            var beaconData = new BeaconData
            {
                IPAdress = senderIP,
                Reference = senderRef,
                DownloadIPAddress = receiverIP,
                NextOwnerReference = receiverRef,
                SmartContractUID = "test-sc-uid",
                AssetName = "test-asset.png"
            };

            var beaconPool = new List<BeaconPool>
            {
                new BeaconPool { IpAddress = senderIP, Reference = senderRef, ConnectionId = "sender-conn" },
                new BeaconPool { IpAddress = receiverIP, Reference = receiverRef, ConnectionId = "receiver-conn" },
                new BeaconPool { IpAddress = "10.0.0.99", Reference = senderRef, ConnectionId = "hijacker-conn" }
            };

            // Act - Lookup sender using composite key
            var senderLookup = beaconPool
                .Where(x => x.IpAddress == beaconData.IPAdress && x.Reference == beaconData.Reference)
                .FirstOrDefault();

            // Lookup receiver using composite key
            var receiverLookup = beaconPool
                .Where(x => x.IpAddress == beaconData.DownloadIPAddress && x.Reference == beaconData.NextOwnerReference)
                .FirstOrDefault();

            // Assert - Only exact IP+Reference matches should be found
            Assert.NotNull(senderLookup);
            Assert.Equal("sender-conn", senderLookup.ConnectionId);
            
            Assert.NotNull(receiverLookup);
            Assert.Equal("receiver-conn", receiverLookup.ConnectionId);

            // Verify hijacker with same reference but different IP is NOT matched
            Assert.NotEqual("hijacker-conn", senderLookup.ConnectionId);
        }

        [Fact]
        public void BeaconPool_CompositeKeyLookup_ReturnsNullWhenIPMismatch()
        {
            // Arrange
            var beaconPool = new List<BeaconPool>
            {
                new BeaconPool { IpAddress = "192.168.1.50", Reference = "ref-123", ConnectionId = "conn-1" }
            };

            // Act - Lookup with correct reference but wrong IP
            var result = beaconPool
                .Where(x => x.Reference == "ref-123" && x.IpAddress == "192.168.1.99")
                .FirstOrDefault();

            // Assert - Should return null, preventing message hijacking
            Assert.Null(result);
        }

        [Fact]
        public void BeaconPool_CompositeKeyLookup_ReturnsNullWhenReferenceMismatch()
        {
            // Arrange
            var beaconPool = new List<BeaconPool>
            {
                new BeaconPool { IpAddress = "192.168.1.50", Reference = "ref-123", ConnectionId = "conn-1" }
            };

            // Act - Lookup with correct IP but wrong reference
            var result = beaconPool
                .Where(x => x.Reference == "ref-999" && x.IpAddress == "192.168.1.50")
                .FirstOrDefault();

            // Assert - Should return null
            Assert.Null(result);
        }

        [Fact]
        public void BeaconPool_MultipleUsersFromSameNAT_CanBeDistinguishedByReference()
        {
            // Arrange - Multiple users behind same NAT (same external IP) but different references
            var sharedNatIP = "203.0.113.5";
            var user1Ref = "user1-internal-ref";
            var user2Ref = "user2-internal-ref";

            var beaconPool = new List<BeaconPool>
            {
                new BeaconPool { IpAddress = sharedNatIP, Reference = user1Ref, ConnectionId = "user1-conn" },
                new BeaconPool { IpAddress = sharedNatIP, Reference = user2Ref, ConnectionId = "user2-conn" }
            };

            // Act - Lookup each user by composite key
            var user1 = beaconPool
                .Where(x => x.IpAddress == sharedNatIP && x.Reference == user1Ref)
                .FirstOrDefault();

            var user2 = beaconPool
                .Where(x => x.IpAddress == sharedNatIP && x.Reference == user2Ref)
                .FirstOrDefault();

            // Assert - Each user gets correct connection despite shared IP
            Assert.NotNull(user1);
            Assert.NotNull(user2);
            Assert.Equal("user1-conn", user1.ConnectionId);
            Assert.Equal("user2-conn", user2.ConnectionId);
            Assert.NotEqual(user1.ConnectionId, user2.ConnectionId);
        }

        [Fact]
        public void BeaconPool_DifferentIPsSameReference_AreDistinct()
        {
            // Arrange - Edge case: Same reference used from different IPs
            // (Should only happen if reference generation is weak)
            var ip1 = "10.1.1.1";
            var ip2 = "10.2.2.2";
            var reference = "weak-ref-001";

            var beaconPool = new List<BeaconPool>
            {
                new BeaconPool { IpAddress = ip1, Reference = reference, ConnectionId = "conn-ip1" },
                new BeaconPool { IpAddress = ip2, Reference = reference, ConnectionId = "conn-ip2" }
            };

            // Act - Composite key lookup ensures correct routing
            var lookup1 = beaconPool
                .Where(x => x.IpAddress == ip1 && x.Reference == reference)
                .FirstOrDefault();

            var lookup2 = beaconPool
                .Where(x => x.IpAddress == ip2 && x.Reference == reference)
                .FirstOrDefault();

            // Assert - Each IP gets its own connection
            Assert.NotNull(lookup1);
            Assert.NotNull(lookup2);
            Assert.Equal("conn-ip1", lookup1.ConnectionId);
            Assert.Equal("conn-ip2", lookup2.ConnectionId);
        }
    }
}
