using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ReserveBlockCore.Models;
using ReserveBlockCore.Nodes;
using Xunit;

namespace VerfiedXCore.Tests
{
    /// <summary>
    /// CONSENSUS-V2 (Fix #5) — unit tests for proof-set commitment hashing & local
    /// commitment construction. The cross-network agreement loop (HTTP-driven) is
    /// covered by the integration smoke test; here we exercise the deterministic
    /// helpers that controllers and the consensus loop both depend on.
    /// </summary>
    public class ProofSetAgreementTests
    {
        [Fact]
        public void ComputeProofSetCommitmentHash_EmptyInput_ProducesSha256OfEmptyString()
        {
            var hash = BlockcasterNode.ComputeProofSetCommitmentHash(Array.Empty<string>());

            using var sha = SHA256.Create();
            var expected = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(""))).ToLowerInvariant();

            Assert.Equal(expected, hash);
        }

        [Fact]
        public void ComputeProofSetCommitmentHash_NullInput_ProducesSha256OfEmptyString()
        {
            var hash = BlockcasterNode.ComputeProofSetCommitmentHash(null!);

            using var sha = SHA256.Create();
            var expected = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(""))).ToLowerInvariant();

            Assert.Equal(expected, hash);
        }

        [Fact]
        public void ComputeProofSetCommitmentHash_IsDeterministic_ForIdenticalSortedInput()
        {
            var addresses = new[] { "xA1", "xB2", "xC3" };

            var h1 = BlockcasterNode.ComputeProofSetCommitmentHash(addresses);
            var h2 = BlockcasterNode.ComputeProofSetCommitmentHash(addresses.ToList());

            Assert.Equal(h1, h2);
            Assert.Equal(64, h1.Length); // hex SHA-256 length
            Assert.Equal(h1, h1.ToLowerInvariant()); // assert lower-case
        }

        [Fact]
        public void ComputeProofSetCommitmentHash_DiffersOnDifferentInput()
        {
            var h1 = BlockcasterNode.ComputeProofSetCommitmentHash(new[] { "xA1", "xB2" });
            var h2 = BlockcasterNode.ComputeProofSetCommitmentHash(new[] { "xA1", "xC3" });
            var h3 = BlockcasterNode.ComputeProofSetCommitmentHash(new[] { "xA1" });

            Assert.NotEqual(h1, h2);
            Assert.NotEqual(h1, h3);
            Assert.NotEqual(h2, h3);
        }

        [Fact]
        public void ComputeProofSetCommitmentHash_OrderSensitive_GuardsAgainstMismergeAtCaller()
        {
            // The hash function itself is order-sensitive — the convention is that
            // CALLERS sort with StringComparer.Ordinal before hashing. This test pins
            // that contract: if a caller forgets to sort, the hash diverges and
            // every well-behaved peer will reject the commitment.
            var sorted = new[] { "xA", "xB", "xC" };
            var unsorted = new[] { "xC", "xA", "xB" };

            var h1 = BlockcasterNode.ComputeProofSetCommitmentHash(sorted);
            var h2 = BlockcasterNode.ComputeProofSetCommitmentHash(unsorted);

            Assert.NotEqual(h1, h2);
        }

        [Fact]
        public void BuildLocalProofSetCommitment_EmptyProofs_ProducesValidCommitmentWithEmptyHash()
        {
            var commit = BlockcasterNode.BuildLocalProofSetCommitment(100, Array.Empty<Proof>());

            Assert.Equal(100, commit.BlockHeight);
            Assert.NotNull(commit.ProofAddressesSorted);
            Assert.Empty(commit.ProofAddressesSorted);

            var expectedEmpty = BlockcasterNode.ComputeProofSetCommitmentHash(Array.Empty<string>());
            Assert.Equal(expectedEmpty, commit.CommitmentHash);
        }

        [Fact]
        public void BuildLocalProofSetCommitment_FiltersOtherHeights()
        {
            // Only proofs matching the requested height should land in the commitment.
            // This is critical: a stale proof from height N-1 should never poison the
            // hash for height N.
            var proofs = new List<Proof>
            {
                new Proof { Address = "xA", BlockHeight = 100 },
                new Proof { Address = "xB", BlockHeight = 99 }, // should be filtered out
                new Proof { Address = "xC", BlockHeight = 100 },
                new Proof { Address = "xD", BlockHeight = 101 }, // should be filtered out
            };

            var commit = BlockcasterNode.BuildLocalProofSetCommitment(100, proofs);

            Assert.Equal(new[] { "xA", "xC" }, commit.ProofAddressesSorted);
            Assert.Equal(
                BlockcasterNode.ComputeProofSetCommitmentHash(new[] { "xA", "xC" }),
                commit.CommitmentHash);
        }

        [Fact]
        public void BuildLocalProofSetCommitment_DeduplicatesAddresses()
        {
            // A bag-of-proofs may contain duplicates if a peer broadcast races the local
            // generation. The commitment must collapse them so two casters with the same
            // logical proof set always produce the same hash.
            var proofs = new List<Proof>
            {
                new Proof { Address = "xA", BlockHeight = 100, VRFNumber = 1 },
                new Proof { Address = "xA", BlockHeight = 100, VRFNumber = 2 }, // duplicate addr
                new Proof { Address = "xB", BlockHeight = 100 },
            };

            var commit = BlockcasterNode.BuildLocalProofSetCommitment(100, proofs);

            Assert.Equal(2, commit.ProofAddressesSorted.Count);
            Assert.Equal(new[] { "xA", "xB" }, commit.ProofAddressesSorted);
        }

        [Fact]
        public void BuildLocalProofSetCommitment_SortsOrdinally_NotCultureSensitive()
        {
            // Use a mix of ASCII addresses that sort differently under InvariantCulture vs Ordinal
            // to lock in the comparer choice; if a future refactor switches to InvariantCulture
            // sort, the network would silently fork.
            var proofs = new List<Proof>
            {
                new Proof { Address = "xZ", BlockHeight = 100 },
                new Proof { Address = "xA", BlockHeight = 100 },
                new Proof { Address = "xa", BlockHeight = 100 }, // lowercase 'a' > uppercase 'Z' under Ordinal
                new Proof { Address = "xM", BlockHeight = 100 },
            };

            var commit = BlockcasterNode.BuildLocalProofSetCommitment(100, proofs);

            // Ordinal: A < M < Z < a
            Assert.Equal(new[] { "xA", "xM", "xZ", "xa" }, commit.ProofAddressesSorted);
        }

        [Fact]
        public void BuildLocalProofSetCommitment_HashMatchesIndependentRecompute()
        {
            // Property: anything BuildLocalProofSetCommitment emits must round-trip
            // through ComputeProofSetCommitmentHash on its own ProofAddressesSorted.
            // This is the exact verification the controller endpoint performs on inbound
            // commitments — so if it ever drifts, peers reject every local commitment.
            var proofs = new List<Proof>
            {
                new Proof { Address = "xC", BlockHeight = 100 },
                new Proof { Address = "xA", BlockHeight = 100 },
                new Proof { Address = "xB", BlockHeight = 100 },
            };

            var commit = BlockcasterNode.BuildLocalProofSetCommitment(100, proofs);
            var recomputed = BlockcasterNode.ComputeProofSetCommitmentHash(commit.ProofAddressesSorted);

            Assert.Equal(commit.CommitmentHash, recomputed);
        }

        [Fact]
        public void BuildLocalProofSetCommitment_IgnoresNullAndEmptyAddresses()
        {
            var proofs = new List<Proof>
            {
                new Proof { Address = "xA", BlockHeight = 100 },
                new Proof { Address = "",   BlockHeight = 100 }, // empty
                new Proof { Address = null!, BlockHeight = 100 }, // null
                new Proof { Address = "xB", BlockHeight = 100 },
            };

            var commit = BlockcasterNode.BuildLocalProofSetCommitment(100, proofs);

            Assert.Equal(new[] { "xA", "xB" }, commit.ProofAddressesSorted);
        }
    }
}
