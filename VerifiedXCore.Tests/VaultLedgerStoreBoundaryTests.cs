using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A contract's vBTC ledger is read and written only through <see cref="VaultLedgerStore"/>. Where the rows live is
    /// then decided in one place, and the ledger can move out of the contract's state document without any caller changing
    /// what it means. The node's code names the row list nowhere else, apart from the model that declares it and the
    /// compaction that rewrites it; a new reader or writer fails here with the store to use instead.
    /// </summary>
    public class VaultLedgerStoreBoundaryTests
    {
        private const string Field = nameof(SmartContractStateTrei.SCStateTreiTokenizationTXes);

        /// <summary>The files that may name the row list: the store, the model, and the compaction of a watched vault's rows.</summary>
        private static readonly string[] Allowed =
        {
            "Bitcoin/Attestation/VaultLedgerStore.cs",
            "Models/SmartContractStateTrei.cs",
            "Bitcoin/Attestation/BtcVaultLedger.cs",
        };

        private static string Root([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;

        [Fact]
        public void Only_the_store_reads_or_writes_a_contracts_ledger_rows()
        {
            var root = Path.Combine(Root(), "VerifiedXCore");
            var sep = Path.DirectorySeparatorChar;
            var outside = new List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(sep + "bin" + sep) || file.Contains(sep + "obj" + sep)) continue;
                var relative = Path.GetRelativePath(root, file).Replace(sep, '/');
                if (Allowed.Contains(relative)) continue;
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                var uses = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Count(n => n.Identifier.Text == Field);
                if (uses > 0) outside.Add($"{relative}: {uses}");
            }
            Assert.True(outside.Count == 0,
                $"Read or write a contract's ledger through VaultLedgerStore (Balance, HasLedger, HasRows, Rows, Names, Append), not {Field}: " + string.Join("; ", outside));
        }

        /// <summary>The store reads the rows as the callers did: balances in order, a missing ledger as none, an empty one as a ledger.</summary>
        [Fact]
        public void The_store_reads_the_rows_as_the_callers_did()
        {
            var contract = new SmartContractStateTrei { SmartContractUID = "v:1" };
            Assert.False(VaultLedgerStore.HasLedger(contract));
            Assert.False(VaultLedgerStore.HasRows(contract));
            Assert.Empty(VaultLedgerStore.Rows(contract));
            Assert.Equal(0M, VaultLedgerStore.Balance(contract, "xA"));
            Assert.Equal(0M, VaultLedgerStore.Balance(null, "xA"));

            contract.SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>();
            Assert.True(VaultLedgerStore.HasLedger(contract));
            Assert.False(VaultLedgerStore.HasRows(contract));

            VaultLedgerStore.Append(contract, new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = "xA", Amount = 1.5M });
            VaultLedgerStore.Append(contract, new[]
            {
                new SmartContractStateTreiTokenizationTX { FromAddress = "xA", ToAddress = "-", Amount = -0.25M },
                new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = "xB", Amount = 0.25M },
                new SmartContractStateTreiTokenizationTX { FromAddress = "xOld", ToAddress = "-", Amount = -0.1M },
            });
            Assert.True(VaultLedgerStore.HasRows(contract));
            Assert.Equal(4, VaultLedgerStore.Rows(contract).Count);
            Assert.Equal(1.25M, VaultLedgerStore.Balance(contract, "xA"));
            Assert.Equal(1.25M, VaultLedgerStore.ReceivedPlusSent(contract, "xA"));
            Assert.Equal(0.25M, VaultLedgerStore.Balance(contract, "xB"));
            Assert.Equal(0M, VaultLedgerStore.Balance(contract, "xa"));                       // ordinal: another address
            Assert.True(VaultLedgerStore.Names(contract, "xB"));
            Assert.False(VaultLedgerStore.Names(contract, "xC"));
            Assert.True(VaultLedgerStore.NamesAny(contract, new HashSet<string> { "xC", "xB" }));
            Assert.Equal(-0.1M, VaultLedgerStore.NegativePositionsExcept(contract, "xA"));
            Assert.Equal(-0.1M, Bitcoin.Services.VBTCService.NegativeNonOwnerPositions(contract, "xA"));
        }
    }
}
