using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A chain snapshot carries the withdrawal store (requests, and the bridge locks and exits kept in the same file) but
    /// not the vBTC file the cancellations and votes are kept in (it holds key shares). The snapshot's copy of the
    /// withdrawal store is marked as holding an unfinished rebuild, so a node booted from it rebuilds requests,
    /// cancellations and votes from the chain before it judges anything against them.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ChainSnapshotWithdrawalStoreTests : IDisposable
    {
        private const string Vault = "snapshot:1";
        private readonly string _nodeA;
        private readonly string _nodeB;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ChainSnapshotWithdrawalStoreTests()
        {
            _nodeA = Path.Combine(Path.GetTempPath(), $"snapA_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            _nodeB = Path.Combine(Path.GetTempPath(), $"snapB_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_nodeA);
            Directory.CreateDirectory(_nodeB);
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            foreach (var d in new[] { _nodeA, _nodeB }) try { Directory.Delete(d, recursive: true); } catch { }
        }

        private static Transaction Tx(string hash, TransactionType type, long height, string data) => new()
        {
            Hash = hash, FromAddress = "xRequestorS", ToAddress = "TW_Base", TransactionType = type, Timestamp = 1_755_000_000L + height, Height = height, Data = data,
        };

        private static void InsertBlock(long height, params Transaction[] txs) =>
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = height, Hash = $"block-{height}", PrevHash = $"block-{height - 1}", Timestamp = 1_755_000_000L + height, Transactions = txs.ToList(), NumOfTx = txs.Length,
            });

        [Fact]
        public async Task ANodeBootedFromAChainSnapshot_RebuildsItsCancellationsFromTheChain()
        {
            // Node A: a request and its cancellation on chain, the store and the cancellation table written.
            Globals.CustomPath = _nodeA;
            DbContext.Initialize();
            InsertBlock(600, Tx("snap-req", TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, 600,
                $"{{\"Function\":\"VBTCWithdrawalRequest()\",\"ContractUID\":\"{Vault}\",\"BTCAddress\":\"tb1q066af78la3rqmnchc396keujllva6turs52749\",\"Amount\":0.2,\"FeeRate\":10}}"));
            InsertBlock(601, Tx("snap-cancel", TransactionType.VBTC_V2_WITHDRAWAL_CANCEL, 601,
                $"{{\"Function\":\"VBTCWithdrawalCancel()\",\"ContractUID\":\"{Vault}\",\"WithdrawalRequestHash\":\"snap-req\",\"FailureProof\":\"\"}}"));
            Assert.True((await VBTCWithdrawalStoreRebuildService.RebuildFromChainAsync("node A")).Success);
            Assert.NotNull(VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash("snap-req", Vault));
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            var dbA = GetPathUtility.GetDatabasePath();
            DbContext.CloseDB();

            await LocalDbSnapshotService.CreateChainSnapshotAsync();
            var snapshot = Directory.GetDirectories(Path.Combine(dbA, "snapshots")).Single();
            Assert.True(File.Exists(Path.Combine(snapshot, DbContext.RSRV_DB_VBTC_WITHDRAWAL_REQUESTS)));   // locks and exits travel with it
            Assert.False(File.Exists(Path.Combine(snapshot, DbContext.RSRV_DB_VBTC)));                     // key shares do not

            // Node B boots from the snapshot: no cancellation table yet, and the store reads as being rebuilt.
            Globals.CustomPath = _nodeB;
            var dbB = GetPathUtility.GetDatabasePath();
            foreach (var file in Directory.GetFiles(snapshot))
                File.Copy(file, Path.Combine(dbB, Path.GetFileName(file)), overwrite: true);
            DbContext.Initialize();
            Assert.Null(VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash("snap-req", Vault));
            Assert.True(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash("snap-req", Vault));

            await VBTCWithdrawalStoreRebuildService.MaybeAutoRebuildOnStartupAsync();
            Assert.False(VBTCWithdrawalStoreRebuildService.IsUnfinished);
            Assert.NotNull(VBTCWithdrawalCancellation.GetCancellationByWithdrawalHash("snap-req", Vault));
        }
    }
}
