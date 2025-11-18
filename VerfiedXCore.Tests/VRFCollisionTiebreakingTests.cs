using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using Xunit;

namespace VerfiedXCore.Tests
{
    public class VRFCollisionTiebreakingTests
    {
        [Fact]
        public async Task SortProofs_WithIdenticalVRFNumbers_UsesDeterministicTiebreaking()
        {
            // Arrange - Create proofs with identical VRFNumbers but different ProofHashes
            var proofs = new List<Proof>
            {
                new Proof
                {
                    Address = "Address1",
                    PublicKey = "PubKey1",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 12345,
                    ProofHash = "ZZZZZ", // Lexicographically last
                    IPAddress = "192.168.1.1"
                },
                new Proof
                {
                    Address = "Address2",
                    PublicKey = "PubKey2",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 12345,
                    ProofHash = "AAAAA", // Lexicographically first - should win
                    IPAddress = "192.168.1.2"
                },
                new Proof
                {
                    Address = "Address3",
                    PublicKey = "PubKey3",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 12345,
                    ProofHash = "MMMMM", // Lexicographically middle
                    IPAddress = "192.168.1.3"
                }
            };

            // Mock Globals.LastBlock for the test
            ReserveBlockCore.Globals.LastBlock = new Block
            {
                Height = 1000,
                Hash = "PrevHash"
            };
            ReserveBlockCore.Globals.ABL = new List<string>();

            // Act - Run multiple times to ensure deterministic behavior
            var results = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                // Shuffle the list to simulate different collection orders
                var shuffledProofs = proofs.OrderBy(x => Guid.NewGuid()).ToList();
                var winner = await ProofUtility.SortProofs(shuffledProofs);
                results.Add(winner?.Address ?? "null");
            }

            // Assert - All results should be the same (deterministic)
            Assert.All(results, address => Assert.Equal("Address2", address));
        }

        [Fact]
        public async Task SortProofs_WithIdenticalVRFAndProofHash_UsesAddressTiebreaking()
        {
            // Arrange - Create proofs with identical VRFNumbers and ProofHashes
            var proofs = new List<Proof>
            {
                new Proof
                {
                    Address = "ZZZ_LastAddress",
                    PublicKey = "PubKey1",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 12345,
                    ProofHash = "SameHash",
                    IPAddress = "192.168.1.1"
                },
                new Proof
                {
                    Address = "AAA_FirstAddress", // Lexicographically first - should win
                    PublicKey = "PubKey2",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 12345,
                    ProofHash = "SameHash",
                    IPAddress = "192.168.1.2"
                },
                new Proof
                {
                    Address = "MMM_MiddleAddress",
                    PublicKey = "PubKey3",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 12345,
                    ProofHash = "SameHash",
                    IPAddress = "192.168.1.3"
                }
            };

            // Mock Globals.LastBlock for the test
            ReserveBlockCore.Globals.LastBlock = new Block
            {
                Height = 1000,
                Hash = "PrevHash"
            };
            ReserveBlockCore.Globals.ABL = new List<string>();

            // Act - Run multiple times to ensure deterministic behavior
            var results = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                // Shuffle the list to simulate different collection orders
                var shuffledProofs = proofs.OrderBy(x => Guid.NewGuid()).ToList();
                var winner = await ProofUtility.SortProofs(shuffledProofs);
                results.Add(winner?.Address ?? "null");
            }

            // Assert - All results should be the same (deterministic)
            Assert.All(results, address => Assert.Equal("AAA_FirstAddress", address));
        }

        [Fact]
        public async Task SortProofs_WithDifferentVRFNumbers_SelectsLowest()
        {
            // Arrange
            var proofs = new List<Proof>
            {
                new Proof
                {
                    Address = "Address1",
                    PublicKey = "PubKey1",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 99999,
                    ProofHash = "Hash1",
                    IPAddress = "192.168.1.1"
                },
                new Proof
                {
                    Address = "Address2",
                    PublicKey = "PubKey2",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 100, // Lowest - should win
                    ProofHash = "Hash2",
                    IPAddress = "192.168.1.2"
                },
                new Proof
                {
                    Address = "Address3",
                    PublicKey = "PubKey3",
                    BlockHeight = 1001,
                    PreviousBlockHash = "PrevHash",
                    VRFNumber = 50000,
                    ProofHash = "Hash3",
                    IPAddress = "192.168.1.3"
                }
            };

            // Mock Globals.LastBlock for the test
            ReserveBlockCore.Globals.LastBlock = new Block
            {
                Height = 1000,
                Hash = "PrevHash"
            };
            ReserveBlockCore.Globals.ABL = new List<string>();

            // Act
            var winner = await ProofUtility.SortProofs(proofs);

            // Assert
            Assert.NotNull(winner);
            Assert.Equal("Address2", winner.Address);
            Assert.Equal((uint)100, winner.VRFNumber);
        }

        [Fact]
        public async Task SortProofs_WithCollisionsAndDifferentOrders_RemainsConsistent()
        {
            // Arrange - Simulate realistic scenario with multiple collisions
            var proofs = new List<Proof>
            {
                new Proof { Address = "Val1", PublicKey = "Key1", BlockHeight = 1001, PreviousBlockHash = "Hash", VRFNumber = 1000, ProofHash = "ProofB", IPAddress = "1.1.1.1" },
                new Proof { Address = "Val2", PublicKey = "Key2", BlockHeight = 1001, PreviousBlockHash = "Hash", VRFNumber = 1000, ProofHash = "ProofA", IPAddress = "1.1.1.2" },
                new Proof { Address = "Val3", PublicKey = "Key3", BlockHeight = 1001, PreviousBlockHash = "Hash", VRFNumber = 500, ProofHash = "ProofZ", IPAddress = "1.1.1.3" },
                new Proof { Address = "Val4", PublicKey = "Key4", BlockHeight = 1001, PreviousBlockHash = "Hash", VRFNumber = 500, ProofHash = "ProofA", IPAddress = "1.1.1.4" },
                new Proof { Address = "Val5", PublicKey = "Key5", BlockHeight = 1001, PreviousBlockHash = "Hash", VRFNumber = 2000, ProofHash = "ProofX", IPAddress = "1.1.1.5" }
            };

            // Mock Globals
            ReserveBlockCore.Globals.LastBlock = new Block { Height = 1000, Hash = "Hash" };
            ReserveBlockCore.Globals.ABL = new List<string>();

            // Act - Run 20 times with random ordering
            var results = new HashSet<string>();
            for (int i = 0; i < 20; i++)
            {
                var shuffled = proofs.OrderBy(x => Guid.NewGuid()).ToList();
                var winner = await ProofUtility.SortProofs(shuffled);
                results.Add(winner?.Address ?? "null");
            }

            // Assert - Should always select the same winner (Val4: lowest VRF=500, then ProofHash="ProofA")
            Assert.Single(results);
            Assert.Contains("Val4", results);
        }

        [Fact]
        public async Task SortProofs_ExcludesInvalidProofs_ThenAppliesTiebreaking()
        {
            // Arrange
            var proofs = new List<Proof>
            {
                new Proof
                {
                    Address = "InvalidHeight",
                    PublicKey = "Key1",
                    BlockHeight = 999, // Wrong height
                    PreviousBlockHash = "Hash",
                    VRFNumber = 1,
                    ProofHash = "Hash1",
                    IPAddress = "1.1.1.1"
                },
                new Proof
                {
                    Address = "ValidProof1",
                    PublicKey = "Key2",
                    BlockHeight = 1001,
                    PreviousBlockHash = "Hash",
                    VRFNumber = 100,
                    ProofHash = "HashZ",
                    IPAddress = "1.1.1.2"
                },
                new Proof
                {
                    Address = "ValidProof2",
                    PublicKey = "Key3",
                    BlockHeight = 1001,
                    PreviousBlockHash = "Hash",
                    VRFNumber = 100,
                    ProofHash = "HashA", // Should win - same VRF, lower ProofHash
                    IPAddress = "1.1.1.3"
                }
            };

            // Mock Globals
            ReserveBlockCore.Globals.LastBlock = new Block { Height = 1000, Hash = "Hash" };
            ReserveBlockCore.Globals.ABL = new List<string>();

            // Act
            var winner = await ProofUtility.SortProofs(proofs);

            // Assert - Should select ValidProof2 (correct height, lowest VRF, lowest ProofHash)
            Assert.NotNull(winner);
            Assert.Equal("ValidProof2", winner.Address);
        }
    }
}
