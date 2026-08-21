using System;
using System.IO;
using System.Linq;
using NBitcoin;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Regression guards for three SILENT failures found in the pre-mainnet review of the
    /// caster-upgrade branch. Each shipped as working code, passed the existing suite, and was
    /// invisible on the happy path — the five clean testnet withdrawals could not have caught any
    /// of them, because none of them required a retry, a cancellation, or a rate-limited provider.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCWithdrawalReviewFindingsTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VBTCWithdrawalReviewFindingsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcfind_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        private static VBTCWithdrawalRequest NewRequest(string scUid, string requestor, decimal amount, string txHash) =>
            new VBTCWithdrawalRequest
            {
                SmartContractUID = scUid,
                RequestorAddress = requestor,
                BTCDestination = "tb1q066af78la3rqmnchc396keujllva6turs52749",
                Amount = amount,
                TransactionHash = txHash,
                Status = VBTCWithdrawalStatus.Requested,
                Timestamp = TimeUtil.GetTime(),
                RequestBlockHeight = 1000,
            };

        // ── The durable FIND-028 pin must actually reach the database ────────────────
        //
        // Save()'s update branch copies an explicit field whitelist onto a separately-loaded row.
        // PinnedUnsignedTxHex/PinnedCoinsJson were absent from that list, so PersistPinnedBuild was
        // a silent no-op: the pin never persisted, CompleteWithdrawal's reuse branch never fired,
        // and every retry rebuilt from scratch — the exact wedge the pin was written to prevent.

        [Fact]
        public void Save_Update_PersistsPinnedBuild()
        {
            const string sc = "pin:1";
            var req = NewRequest(sc, "xMjrTest", 0.001M, "hash-pin-1");
            Assert.True(VBTCWithdrawalRequest.Save(req));

            var stored = VBTCWithdrawalRequest.GetByTransactionHash("hash-pin-1");
            Assert.NotNull(stored);
            stored!.PinnedUnsignedTxHex = "0200000001deadbeef";
            stored.PinnedCoinsJson = "[{\"Outpoint\":\"abc:0\"}]";
            Assert.True(VBTCWithdrawalRequest.Save(stored, true));

            var reloaded = VBTCWithdrawalRequest.GetByTransactionHash("hash-pin-1");
            Assert.NotNull(reloaded);
            Assert.Equal("0200000001deadbeef", reloaded!.PinnedUnsignedTxHex);
            Assert.Equal("[{\"Outpoint\":\"abc:0\"}]", reloaded.PinnedCoinsJson);
        }

        [Fact]
        public void Save_Update_ClearsPinnedBuild_ForRebuildFresh()
        {
            // The RebuildFresh disposition clears the pin by saving nulls. A carry-forward
            // null-guard would make that clear a permanent no-op and strand the stale build.
            const string sc = "pin:2";
            var req = NewRequest(sc, "xMjrTest", 0.001M, "hash-pin-2");
            req.PinnedUnsignedTxHex = "0200000001stale";
            req.PinnedCoinsJson = "[{\"Outpoint\":\"stale:0\"}]";
            Assert.True(VBTCWithdrawalRequest.Save(req));

            var stored = VBTCWithdrawalRequest.GetByTransactionHash("hash-pin-2")!;
            stored.PinnedUnsignedTxHex = null;
            stored.PinnedCoinsJson = null;
            Assert.True(VBTCWithdrawalRequest.Save(stored, true));

            var reloaded = VBTCWithdrawalRequest.GetByTransactionHash("hash-pin-2")!;
            Assert.Null(reloaded.PinnedUnsignedTxHex);
            Assert.Null(reloaded.PinnedCoinsJson);
        }

        // ── The owner add-back must count only withdrawals that actually burned ──────
        //
        // GetCompletedWithdrawalAmount filtered on IsCompleted alone. Cancellation votes, Cancel(),
        // and the cleanup janitor all set IsCompleted=true WITHOUT writing the offsetting burn row
        // the add-back exists to cancel — so each one credited the owner vBTC that no BTC backs.

        [Fact]
        public void CompletedWithdrawalAmount_CountsOnlyTrulyCompleted()
        {
            const string sc = "addback:1";
            const string owner = "xOwnerTest";

            var completed = NewRequest(sc, owner, 0.5M, "hash-done");
            completed.Status = VBTCWithdrawalStatus.Completed;
            completed.IsCompleted = true;
            completed.BTCTxHash = "btc-tx-hash";
            Assert.True(VBTCWithdrawalRequest.Save(completed));

            var cancelled = NewRequest(sc, owner, 1.0M, "hash-cancelled");
            cancelled.Status = VBTCWithdrawalStatus.Cancelled;
            cancelled.IsCompleted = true;
            Assert.True(VBTCWithdrawalRequest.Save(cancelled));

            // Only the burn-backed 0.5 counts; the cancelled 1.0 must contribute nothing.
            Assert.Equal(0.5M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(owner, sc));
        }

        [Fact]
        public void CompletedWithdrawalAmount_JanitorRetiredRowContributesZero()
        {
            const string sc = "addback:2";
            const string owner = "xOwnerTest2";

            // Shape written by a janitor retirement / approved cancellation vote: flagged complete,
            // terminal status Cancelled, no BTC tx, no burn row.
            var retired = NewRequest(sc, owner, 2.5M, "hash-retired");
            retired.Status = VBTCWithdrawalStatus.Cancelled;
            retired.IsCompleted = true;
            Assert.True(VBTCWithdrawalRequest.Save(retired));

            Assert.Equal(0M, VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(owner, sc));
        }

        // ── Rebuilds of the same coin set must be byte-identical ─────────────────────
        //
        // NBitcoin shuffles inputs and outputs by default, so the deterministic UTXO sort was
        // undone by the builder: every rebuild produced fresh sighashes and a fresh txid, which
        // the validators' per-withdrawal sighash pin refuses as a different transaction.

        [Fact]
        public void DeterministicBuilder_SameCoins_ProduceIdenticalTransaction()
        {
            var priorNetwork = Globals.BTCNetwork;
            Globals.BTCNetwork = Network.TestNet;
            try
            {
                var key = new Key();
                var dest = key.PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.TestNet);
                var change = new Key().PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.TestNet);

                // Four equal-value coins: the pathological case from the original wedge.
                var coins = Enumerable.Range(0, 4)
                    .Select(i => new Coin(
                        new OutPoint(uint256.Parse(i.ToString("x2").PadLeft(64, '0')), 0),
                        new TxOut(Money.Satoshis(500_000), change)))
                    .ToArray();

                string BuildOnce()
                {
                    var b = BitcoinTransactionService.CreateDeterministicTransactionBuilder();
                    b.AddCoins(coins);
                    b.Send(dest, Money.Satoshis(1_200_000));
                    b.SetChange(change);
                    b.SendFees(Money.Satoshis(2_000));
                    var tx = b.BuildTransaction(false);
                    return tx.ToHex();
                }

                var first = BuildOnce();
                for (var attempt = 0; attempt < 15; attempt++)
                    Assert.Equal(first, BuildOnce());
            }
            finally
            {
                Globals.BTCNetwork = priorNetwork;
            }
        }
    }
}
