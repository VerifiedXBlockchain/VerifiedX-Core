using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A contract's header is the document its full state read gives, without the ledger: the same document when one UID
    /// has more than one (the oldest), the same answer for a UID written in another letter case, and every header field as
    /// stored, the ownership fields the shared rules read among them.
    /// </summary>
    [Collection("DbContextSequential")]
    public class SmartContractHeaderTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public SmartContractHeaderTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"scheader_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void AssertSameContract(SmartContractStateTrei? full, SmartContractHeader? header)
        {
            if (full == null) { Assert.Null(header); return; }
            Assert.NotNull(header);
            Assert.Equal(full.Id, header!.Id);
            Assert.Equal(full.SmartContractUID, header.SmartContractUID);
            Assert.Equal(full.ContractData, header.ContractData);
            Assert.Equal(full.MinterAddress, header.MinterAddress);
            Assert.Equal(full.OwnerAddress, header.OwnerAddress);
            Assert.Equal(full.NextOwner, header.NextOwner);
            Assert.Equal(full.IsLocked, header.IsLocked);
        }

        [Fact]
        public void The_header_is_the_document_the_full_read_gives_for_every_spelling_and_for_duplicates()
        {
            var scs = SmartContractStateTrei.GetSCST();
            // Two documents under one UID, the older first; then the same UID spelled in another case.
            scs.Insert(new SmartContractStateTrei
            {
                SmartContractUID = "abc:1", ContractData = "older", MinterAddress = "xMinter", OwnerAddress = "xOwnerOld",
                NextOwner = "xBuyer", IsLocked = true,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX> { new() { FromAddress = "+", ToAddress = "xH", Amount = 1M } },
            });
            scs.Insert(new SmartContractStateTrei
            {
                SmartContractUID = "abc:1", ContractData = "newer", MinterAddress = "xMinter2", OwnerAddress = "xOwnerNew", NextOwner = null, IsLocked = false,
            });
            scs.Insert(new SmartContractStateTrei { SmartContractUID = "Mixed:9", ContractData = "code", OwnerAddress = "xMixed", IsLocked = false });

            foreach (var uid in new[] { "abc:1", "ABC:1", "Abc:1", "Mixed:9", "mixed:9", "MIXED:9", "no-such:1", "abc:2" })
                AssertSameContract(SmartContractStateTrei.GetSmartContractState(uid), SmartContractStateTrei.GetSmartContractHeader(uid));

            var older = SmartContractStateTrei.GetSmartContractHeader("abc:1")!;
            Assert.Equal("older", older.ContractData);          // the oldest document of the UID, as the full read picks
            Assert.Equal("xBuyer", older.NextOwner);
            Assert.True(older.IsLocked);
            Assert.Null(SmartContractStateTrei.GetSmartContractHeader(null!));
        }
    }
}
