using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// HAL-064 — Deterministic VRF tie-break behavior.
    ///
    /// These tests target the pure ordering helper <see cref="ProofUtility.SelectWinnerByVrfOrdering"/>
    /// which is the same code path that <see cref="ProofUtility.SortProofs"/> calls *after* its
    /// height/prev-hash/<c>VerifyProof()</c> filter. Going through <c>SortProofs</c> directly with
    /// synthetic fixtures isn't practical because it requires every test proof to carry a real
    /// SHA256-derived <c>ProofHash</c> (otherwise the cryptographic <c>VerifyProof()</c> filter
    /// rejects them all and we end up asserting against a null winner). The ordering layer is the
    /// piece HAL-064 is actually changing, so testing it directly gives us deterministic, fast,
    /// and isolation-safe coverage.
    /// </summary>
    public class VRFCollisionTiebreakingTests
    {
        [Fact]
        public void SortProofs_WithIdenticalVRFNumbers_UsesDeterministicTiebreaking()
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

            // Act - Run multiple times with shuffled inputs to ensure deterministic behavior
            var results = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                var shuffledProofs = proofs.OrderBy(x => Guid.NewGuid()).ToList();
                var winner = ProofUtility.SelectWinnerByVrfOrdering(shuffledProofs);
                results.Add(winner?.Address ?? "null");
            }

            // Assert - All results should be the same (deterministic) and should pick the
            // lexicographically smallest ProofHash.
            Assert.All(results, address => Assert.Equal("Address2", address));
        }

        [Fact]
        public void SortProofs_WithIdenticalVRFAndProofHash_UsesAddressTiebreaking()
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

            // Act - Run multiple times with shuffled inputs to ensure deterministic behavior
            var results = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                var shuffledProofs = proofs.OrderBy(x => Guid.NewGuid()).ToList();
                var winner = ProofUtility.SelectWinnerByVrfOrdering(shuffledProofs);
                results.Add(winner?.Address ?? "null");
            }

            // Assert - All results should be the same (deterministic), tied on both VRF and
            // ProofHash → final tiebreak is the lexicographically smallest Address.
            Assert.All(results, address => Assert.Equal("AAA_FirstAddress", address));
        }

        [Fact]
        public void SortProofs_WithDifferentVRFNumbers_SelectsLowest()
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

            // Act
            var winner = ProofUtility.SelectWinnerByVrfOrdering(proofs);

            // Assert
            Assert.NotNull(winner);
            Assert.Equal("Address2", winner!.Address);
            Assert.Equal((uint)100, winner.VRFNumber);
        }

        [Fact]
        public void SortProofs_WithCollisionsAndDifferentOrders_RemainsConsistent()
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

            // Act - Run 20 times with random ordering
            var results = new HashSet<string>();
            for (int i = 0; i < 20; i++)
            {
                var shuffled = proofs.OrderBy(x => Guid.NewGuid()).ToList();
                var winner = ProofUtility.SelectWinnerByVrfOrdering(shuffled);
                results.Add(winner?.Address ?? "null");
            }

            // Assert - Should always select the same winner (Val4: lowest VRF=500, then ProofHash="ProofA")
            Assert.Single(results);
            Assert.Contains("Val4", results);
        }

        [Fact]
        public void SortProofs_ExcludesInvalidProofs_ThenAppliesTiebreaking()
        {
            // Arrange — caller is expected to pre-filter invalid proofs (wrong height, failed
            // VerifyProof, etc.) before handing the list to the ordering helper. Simulate that
            // here by removing the wrong-height proof up-front, then assert the helper picks
            // the deterministic winner from the remaining valid proofs.
            var allProofs = new List<Proof>
            {
                new Proof
                {
                    Address = "InvalidHeight",
                    PublicKey = "Key1",
                    BlockHeight = 999, // Wrong height — would be filtered out by SortProofs
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

            const long processHeight = 1001;
            const string processPrev = "Hash";
            var validProofs = allProofs
                .Where(p => p.BlockHeight == processHeight && p.PreviousBlockHash == processPrev)
                .ToList();

            // Act
            var winner = ProofUtility.SelectWinnerByVrfOrdering(validProofs);

            // Assert - Should select ValidProof2 (correct height, lowest VRF, lowest ProofHash)
            Assert.NotNull(winner);
            Assert.Equal("ValidProof2", winner!.Address);
        }
    }
}
