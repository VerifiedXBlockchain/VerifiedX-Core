using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using FeeCalcService = VerifiedXCore.Services.FeeCalcService;
using SignatureService = VerifiedXCore.Services.SignatureService;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A block's transactions are judged by the block's timestamp, not by the clock of the node reading the block
    /// (third pass, 14 October 2026).
    ///
    /// Block validation read the node's clock for a transaction's age, a reserve send's 24 hours, a call-back's
    /// deadline and a recovery signature's ten minutes. A transaction at the edge was valid on one node and not on the
    /// next, and a node reading the same block later (a download) applied none of these rules at all, so a block
    /// carrying a reserve send with no delay was refused by nodes that saw it live and accepted by nodes that came
    /// after. From Globals.PathAuditRulesHeight the block's own timestamp is the time, on every node, whenever it
    /// reads the block.
    ///
    /// Every case goes through VerifyTX as block validation calls it (height and timestamp of the block).
    /// </summary>
    [Collection("DbContextSequential")]
    public class PathAudit3_BlockTimeTests : IDisposable
    {
        private const long Rule = 300;
        private const long Hour = 3600;
        private const long Day = 24 * Hour;
        private const string Outside = "too far from its block";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.PathAuditRulesHeight;
        private readonly long _priorRecord = Globals.BitcoinAttestationRecordHeight;
        private readonly long _priorFreeze = Globals.BitcoinAttestationHeight;
        private readonly long _now = TimeUtil.GetTime();
        private readonly (PrivateKey Key, string Pub, string Address) _reserve;
        private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _recipient = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _recovery = NewKey();
        private long _height = Rule;

        public PathAudit3_BlockTimeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pa3time_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.BitcoinAttestationRecordHeight = long.MaxValue;
            Globals.BitcoinAttestationHeight = long.MaxValue;

            PrivateKey key; string pub, address;
            do
            {
                key = new PrivateKey("secp256k1");
                pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                address = ReserveAccount.GetHumanAddress(pub);
            } while (!address.StartsWith("xRBX", StringComparison.Ordinal));
            _reserve = (key, pub, address);

            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _reserve.Address, Balance = 100M, Nonce = 0, RecoveryAccount = _recovery.Address });
            foreach (var who in new[] { _sender, _recipient, _recovery })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = who.Address, Balance = 10M, Nonce = 0 });
            // The chain's last block is three days old: this node is reading old blocks, as a node that syncs does.
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = Rule, Hash = "rule-block", Timestamp = _now - 3 * Day, Transactions = new List<Transaction>() });
            Globals.LastBlock = new Block { Height = Rule, Timestamp = _now - 3 * Day };
            Globals.PathAuditRulesHeight = Rule;
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorRule;
            Globals.BitcoinAttestationRecordHeight = _priorRecord;
            Globals.BitcoinAttestationHeight = _priorFreeze;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static void BeforeTheRule() => Globals.PathAuditRulesHeight = 1_000_000;

        private Transaction Make((PrivateKey Key, string Pub, string Address) from, TransactionType type, string to, decimal amount, string? data, long timestamp, long? unlockTime)
        {
            var tx = new Transaction
            {
                Timestamp = timestamp, FromAddress = from.Address, ToAddress = to, Amount = amount, Fee = 0,
                Nonce = StateData.GetSpecificAccountStateTrei(from.Address)!.Nonce,
                TransactionType = type, Data = data, UnlockTime = unlockTime,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, from.Key, from.Pub);
            return tx;
        }

        private Transaction Send(long timestamp) => Make(_sender, TransactionType.TX, _recipient.Address, 1.0M, null, timestamp, null);
        private Transaction ReserveSend(long timestamp, long unlockTime) => Make(_reserve, TransactionType.TX, _recipient.Address, 1.0M, null, timestamp, unlockTime);
        private Transaction CallBack(Transaction pending, long timestamp) =>
            Make(_reserve, TransactionType.RESERVE, "Reserve_Base", 0M, JsonConvert.SerializeObject(new { Function = "CallBack()", Hash = pending.Hash }), timestamp, null);
        private Transaction Recover(long sigTime, long timestamp) =>
            Make(_reserve, TransactionType.RESERVE, "Reserve_Base", 0M, JsonConvert.SerializeObject(new
            {
                Function = "Recover()", RecoveryAddress = _recovery.Address,
                RecoverySigScript = SignatureService.CreateSignature($"{sigTime}{_recovery.Address}", _recovery.Key, _recovery.Pub), SignatureTime = sigTime,
            }), timestamp, null);

        /// <summary>As block validation judges a transaction of a block at this height and time; a download when the node is syncing.</summary>
        private Task<(bool Ok, string Why)> InBlock(Transaction tx, long blockTime, bool download = false) =>
            TransactionValidatorService.VerifyTX(tx, download, true, false, null, false, _height + 1, blockTime);

        /// <summary>As the mempool judges it: by the node's clock.</summary>
        private static Task<(bool Ok, string Why)> AtAdmission(Transaction tx) => TransactionValidatorService.VerifyTX(tx);

        private async Task Mine(long blockTime, params Transaction[] txs)
        {
            foreach (var tx in txs)
            {
                var (ok, why) = await InBlock(tx, blockTime);
                Assert.True(ok, $"{tx.TransactionType} refused at block {_height + 1}: {why}");
                tx.Height = _height + 1;
            }
            _height++;
            var block = new Block { Height = _height, Hash = $"b{_height}", StateRoot = "root" + _height, Timestamp = blockTime, Transactions = txs.ToList() };
            BlockchainData.GetBlocks().InsertSafe(block);
            Assert.True(await StateData.UpdateTreis(block));
            Globals.LastBlock = block;
            await ReserveService.Run();
        }

        [Fact]
        public async Task ATransactionsAge_IsMeasuredFromItsBlocksTimestamp_OnEveryNodeAlike()
        {
            var blockTime = _now - 2 * Day;                 // a block two days old, read today
            var margin = LedgerIntegrityRules.BlockTimeMarginSeconds;

            // Half an hour old when its block was made: valid in that block whenever the block is read, live or not.
            var fresh = Send(blockTime - 1800);
            Assert.True((await InBlock(fresh, blockTime)).Ok);
            Assert.True((await InBlock(fresh, blockTime, download: true)).Ok);

            // More than the hour (and the margin a producer's own clock is given) older than its block: refused, and
            // refused alike by a node downloading the block, which used to check nothing.
            var old = Send(blockTime - Globals.MaxTxAgeSeconds - margin - 1);
            var (ok, why) = await InBlock(old, blockTime);
            Assert.False(ok);
            Assert.Contains(Outside, why);
            (ok, why) = await InBlock(old, blockTime, download: true);
            Assert.False(ok);
            Assert.Contains(Outside, why);
            Assert.True((await InBlock(Send(blockTime - Globals.MaxTxAgeSeconds - margin), blockTime)).Ok);

            // Timed ahead of its block by more than the skew and the margin.
            (ok, why) = await InBlock(Send(blockTime + Globals.MaxFutureSkewSeconds + margin + 1), blockTime);
            Assert.False(ok);
            Assert.Contains(Outside, why);
            Assert.True((await InBlock(Send(blockTime + Globals.MaxFutureSkewSeconds + margin), blockTime)).Ok);

            // What a producer admits by its clock stands in the block it stamps a moment later.
            var chosen = Send(_now - Globals.MaxTxAgeSeconds + 5);
            Assert.True((await AtAdmission(chosen)).Ok);
            Assert.True((await InBlock(chosen, _now + 30)).Ok);
        }

        [Fact]
        public async Task BeforeTheRule_ABlocksTransactions_WereJudgedByTheReadersClock_AndNotAtAllInADownload()
        {
            BeforeTheRule();
            var blockTime = _now - 2 * Day;
            // The same transaction of the same block: refused by a node validating now, accepted by one downloading.
            var tx = Send(blockTime - 1800);
            Assert.False((await InBlock(tx, blockTime)).Ok);
            Assert.True((await InBlock(tx, blockTime, download: true)).Ok);
            // And a reserve send with no delay at all was accepted by a node while it was downloading blocks (that
            // rule looked at the download lock itself).
            Assert.True(Globals.BlocksDownloadSlim.Wait(0));
            try { Assert.True((await InBlock(ReserveSend(blockTime, blockTime), blockTime, download: true)).Ok); }
            finally { Globals.BlocksDownloadSlim.Release(); }
        }

        [Fact]
        public async Task AReserveSendsDelay_IsMeasuredFromItsBlocksTimestamp()
        {
            var blockTime = _now - 2 * Day;
            var margin = LedgerIntegrityRules.BlockTimeMarginSeconds;

            // No delay: refused in the block, and by a node downloading it.
            var (ok, why) = await InBlock(ReserveSend(blockTime, blockTime), blockTime, download: true);
            Assert.False(ok);
            Assert.Contains("24 hour requirement", why);
            (ok, why) = await InBlock(ReserveSend(blockTime, blockTime + 21 * Hour - margin - 1), blockTime);
            Assert.False(ok);
            Assert.Contains("24 hour requirement", why);

            // A day ahead of its block: valid in that block, though by this node's clock the unlock time is long past.
            Assert.True((await InBlock(ReserveSend(blockTime, blockTime + Day), blockTime)).Ok);
            Assert.True((await InBlock(ReserveSend(blockTime, blockTime + Day), blockTime, download: true)).Ok);

            // At admission the clock is the time, as before.
            Assert.False((await AtAdmission(ReserveSend(_now, _now + 20 * Hour))).Ok);
            Assert.True((await AtAdmission(ReserveSend(_now, _now + Day))).Ok);
        }

        [Fact]
        public async Task ACallBack_StandsInABlock_UntilTheBlockThatFinalizesItsTransfer()
        {
            var t0 = _now - 2 * Day;
            var send = ReserveSend(t0, t0 + Day);
            await Mine(t0, send);
            Assert.Equal(ReserveTransactionStatus.Pending, ReserveTransactions.GetTransactions(send.Hash)!.ReserveTransactionStatus);

            // By this node's clock the unlock time passed a day ago; by the chain's it has not: the block is timed
            // before it. The call-back stands in that block on every node, whenever the node reads it.
            var early = CallBack(send, t0 + Hour);
            Assert.True((await InBlock(early, t0 + Hour)).Ok);
            Assert.True((await InBlock(early, t0 + Hour, download: true)).Ok);
            // The mempool still goes by the clock, so a call-back past its time is not sent around.
            Assert.False((await AtAdmission(CallBack(send, _now))).Ok);

            // The chain halted across the unlock time: the first block after it is timed three hours past it and has
            // not yet finalized the transfer. It cannot carry the call-back (third pass review). Within the margin of
            // the unlock time it still can.
            var afterHalt = CallBack(send, t0 + Day + 3 * Hour);
            var (haltOk, haltWhy) = await InBlock(afterHalt, t0 + Day + 3 * Hour);
            Assert.False(haltOk);
            Assert.Contains("passed by its block's time", haltWhy);
            Assert.False((await InBlock(afterHalt, t0 + Day + 3 * Hour, download: true)).Ok);
            Assert.True((await InBlock(CallBack(send, t0 + Day - 60), t0 + Day + LedgerIntegrityRules.BlockTimeMarginSeconds - 1)).Ok);

            // A block timed after the unlock time finalizes the transfer; from then on a call-back is refused by the
            // row's status, which is chain state.
            await Mine(t0 + Day + 10);
            Assert.Equal(ReserveTransactionStatus.Confirmed, ReserveTransactions.GetTransactions(send.Hash)!.ReserveTransactionStatus);
            var late = CallBack(send, t0 + Day + 20);
            var (ok, why) = await InBlock(late, t0 + Day + 20);
            Assert.False(ok);
            Assert.Contains("already has a status", why);
        }

        [Fact]
        public async Task ARecoverySignaturesAge_IsMeasuredFromItsBlocksTimestamp()
        {
            var blockTime = _now - 2 * Day;
            var margin = LedgerIntegrityRules.BlockTimeMarginSeconds;

            // Signed five minutes before its block: valid in that block, though two days old by this node's clock.
            Assert.True((await InBlock(Recover(blockTime - 300, blockTime), blockTime)).Ok);
            // Signed more than ten minutes (and the margin) before its block: expired, on a downloading node too.
            var (ok, why) = await InBlock(Recover(blockTime - 600 - margin - 1, blockTime), blockTime, download: true);
            Assert.False(ok);
            Assert.Contains("expired", why);
        }
    }
}
