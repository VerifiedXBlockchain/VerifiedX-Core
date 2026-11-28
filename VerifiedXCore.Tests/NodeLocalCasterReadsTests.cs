using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// What a block or a transaction is judged by, and what a block changes, comes from the chain. The caster membership
    /// record and the node's lists of casters do not: they reach a node apart from the blocks, so two nodes at the same
    /// tip can hold different ones, and a rule that reads them can give the same block two verdicts. The files that judge
    /// and apply blocks and transactions read them in the places listed here and nowhere else. A new read fails this test:
    /// it is either taken out (read the chain's state), or added to the list with the reason it cannot split nodes and how
    /// a node that is behind recovers (BlockValidatorService.NoteCommitteeRefusal).
    /// </summary>
    public class NodeLocalCasterReadsTests
    {
        /// <summary>The membership record store, the committee lookups over it, and the node's own lists of casters.</summary>
        private static readonly Regex Read = new(
            @"CasterMembershipStore\.\w+|\bGetCommitteeForHeight\b|Globals\.BlockCasters\b|\bKnownCasters\b|\bActiveCasterCount\b", RegexOptions.Compiled);

        /// <summary>
        /// File, what is read, how many times. Every entry is a place a node behind on its record can judge otherwise than
        /// its peers until the record arrives.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, int>> Allowed = new()
        {
            ["Services/TransactionValidatorService.cs"] = new()
            {
                // Who may send a pool unlock, an exit to Bitcoin, and an exit's completion or failure.
                ["GetCommitteeForHeight"] = 3,
            },
            ["Services/BlockValidatorService.cs"] = new()
            {
                // Which record is held, to tell whether a pull brought a newer one; no verdict reads it.
                ["CasterMembershipStore.GetCurrent"] = 1,
                // A node with no record waits for the one its peers hold.
                ["CasterMembershipStore.MissingWhilePeersHaveOne"] = 1,
            },
            ["Services/ConsensusCertificateVerifier.cs"] = new()
            {
                // Who may attest a block at a height, and how many must.
                ["CasterMembershipStore.GetCommitteeForHeight"] = 2,
                // Whether the committee's term has ended.
                ["CasterMembershipStore.GetForHeight"] = 1,
                ["CasterMembershipStore.GetBySeq"] = 1,
                // Before any record exists: the casters this node knows.
                ["Globals.BlockCasters"] = 2,
                ["KnownCasters"] = 1,
            },
            ["Services/ConsensusQuorum.cs"] = new()
            {
                ["CasterMembershipStore.GetCommitteeForHeight"] = 1,
            },
            ["Bitcoin/Services/BridgeCasterConsensus.cs"] = new()
            {
                // The lookup itself, and the two vote counts that call it.
                ["GetCommitteeForHeight"] = 3,
                ["CasterMembershipStore.GetCommitteeForHeight"] = 1,
                // The vote count of blocks below the height that bound votes to the committee.
                ["ActiveCasterCount"] = 1,
            },
            ["Services/MembershipChain.cs"] = new()
            {
                // The committee as the chain carried it. Only the store's pure checks of a record's own fields and
                // signatures (a run's records, and an election or seed reset the run that starts the chain keeps), and a
                // name: nothing this node holds.
                ["CasterMembershipStore.BootstrapResetChangeType"] = 2,
                ["CasterMembershipStore.ComputeRecordHash"] = 2,
                ["CasterMembershipStore.FieldHoldingTheSeparator"] = 2,
                ["CasterMembershipStore.ValidateSuccessor"] = 1,
                ["CasterMembershipStore.ValidateBootstrapReset"] = 1,
                // A producer offers the records it holds (AttachPending); each is judged as every verifier judges it, and a
                // block without them is taken, so what one node holds never decides a verdict. A caster declines to certify
                // a start that leaves out an election or reset it holds, or puts in force one it does not hold as in force
                // (ActivationSigningRefusal): its signature only.
                ["CasterMembershipStore.GetSince"] = 1,
                ["CasterMembershipStore.GetBySeq"] = 3,
                ["CasterMembershipStore.HeldForHeight"] = 1,
                // After a block is written this node's records are brought to the chain's (a write; no verdict reads it).
                ["CasterMembershipStore.FollowChain"] = 1,
            },
            // Read none.
            ["Data/StateData.cs"] = new(),
            ["Data/BlockchainData.cs"] = new(),
            ["Data/TransactionData.cs"] = new(),
            ["Services/LedgerIntegrityRules.cs"] = new(),
            ["Services/SameBlockDebitGuard.cs"] = new(),
            ["Utilities/BlockVersionUtility.cs"] = new(),
            ["Utilities/BlockVrf.cs"] = new(),
            ["Utilities/ConsensusData.cs"] = new(),
            ["Bitcoin/Services/BridgeTxRules.cs"] = new(),
            ["Bitcoin/Services/BridgeIntraBlockGuard.cs"] = new(),
            ["Bitcoin/Attestation/BtcAttestTxValidator.cs"] = new(),
        };

        /// <summary>The reads in a file's code, comments left out.</summary>
        internal static Dictionary<string, int> ReadsIn(string source)
        {
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in source.Split('\n'))
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                foreach (Match m in Read.Matches(comment < 0 ? line : line[..comment]))
                    found[m.Value] = found.TryGetValue(m.Value, out var n) ? n + 1 : 1;
            }
            return found;
        }

        [Fact]
        public void TheFilesThatJudgeAndApplyBlocks_ReadTheMembershipRecordAndCasterListsOnlyWhereListed()
        {
            var problems = new List<string>();
            foreach (var (file, allowed) in Allowed)
            {
                var path = Path.Combine(new[] { RepoRoot(), "VerifiedXCore" }.Concat(file.Split('/')).ToArray());
                Assert.True(File.Exists(path), $"{file} is not where this test reads it");
                var found = ReadsIn(File.ReadAllText(path));
                foreach (var name in found.Keys.Union(allowed.Keys).OrderBy(n => n, StringComparer.Ordinal))
                {
                    var (now, listed) = (found.GetValueOrDefault(name), allowed.GetValueOrDefault(name));
                    if (now != listed)
                        problems.Add($"{file}: {name} is read {now} time(s), the list has {listed}");
                }
            }
            Assert.True(problems.Count == 0,
                "The membership record and the node's caster lists reach a node apart from the blocks, so a rule that reads them can judge one block two ways. "
                + "Read the chain's state, or list the read here with why it cannot split nodes:\n" + string.Join("\n", problems));
        }

        [Fact]
        public void AReadInCodeIsCounted_AndOneInACommentIsNot()
        {
            var reads = ReadsIn(
                "var a = CasterMembershipStore.GetForHeight(h); // not CasterMembershipStore.GetCurrent()\n" +
                "/// <summary>Globals.BlockCasters</summary>\n" +
                "if (BridgeCasterConsensus.GetCommitteeForHeight(h).Count > Globals.ActiveCasterCount) x = Globals.KnownCasters.Count + Globals.BlockCasters.Count;\n" +
                "lock (Globals.KnownCastersLock) { }\n");

            Assert.Equal(new[] { "ActiveCasterCount", "CasterMembershipStore.GetForHeight", "GetCommitteeForHeight", "Globals.BlockCasters", "KnownCasters" },
                reads.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(reads.Values, n => Assert.Equal(1, n));
        }

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
    }
}
