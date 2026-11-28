using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A state apply that catches an error and goes on must flag a fault of this node's storage (StateData.NoteApplyStepFailed):
    /// the block is then reported as applied incompletely and the node repairs its state, where a silent catch left it holding
    /// other state than its peers after an earlier write of the same apply had landed. Every catch in StateData that neither
    /// rethrows nor flags fails here. A catch with no exception (a data format tried before another) writes nothing and is
    /// not checked; nor are the methods that only list or read.
    /// </summary>
    public class StateApplyFaultFlagTests
    {
        /// <summary>Methods that read or parse and write nothing: a caught error there has no write to leave half-done.</summary>
        private static readonly string[] ReadOnlyMethods = { "StateCollections", "ReserveRowCall" };

        private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

        [Fact]
        public void Every_state_apply_catch_rethrows_or_flags_a_fault_of_this_nodes_storage()
        {
            var file = Path.Combine(Root(), "VerifiedXCore", "Data", "StateData.cs");
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var silent = tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>()
                .Where(c => c.Declaration?.Identifier.Text is { Length: > 0 })
                .Where(c => !c.Block.DescendantNodes().OfType<ThrowStatementSyntax>().Any())
                .Where(c => !c.Block.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(n => n.Identifier.Text is "NoteApplyStepFailed" or "NoteWithdrawalStoreStepFailed" or "IsNodeLocalFault"))
                .Select(c => c.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "?")
                .Where(m => !ReadOnlyMethods.Contains(m))
                .ToList();
            Assert.True(silent.Count == 0, "A catch that neither rethrows nor flags a node-local fault (NoteApplyStepFailed): " + string.Join(", ", silent));
        }
    }
}
