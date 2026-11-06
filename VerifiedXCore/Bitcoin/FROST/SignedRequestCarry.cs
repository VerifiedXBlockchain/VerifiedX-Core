using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// A withdrawal request this validator gave a share for, carried over a rewind that removes its block. The share is
    /// out: the Bitcoin transaction stays payable whatever this chain does, so the request must stand on the chain the
    /// node ends up on, or its escrow is given back as if nothing had been paid. The request is the requester's own
    /// signed transaction; it is put back in the mempool, as a node does with what a replaced block held, and mined
    /// again by the ordinary rules. A validator's rule, not a block's: a node that gave no share holds nothing.
    /// </summary>
    public static class SignedRequestCarry
    {
        public class Carried
        {
            public long Id { get; set; }
            public string TxHash { get; set; } = string.Empty;
            public string TxJson { get; set; } = string.Empty;
            public long RemovedAtHeight { get; set; }
            public long HeldAt { get; set; }
            public int Attempts { get; set; }
            public string LastRefusal { get; set; } = string.Empty;
        }

        public const string CollectionName = "rsrv_frost_signed_requests_carried";

        private static ILiteCollection<Carried>? Coll()
        {
            var db = DbContext.DB_vBTC;
            if (db == null) return null;
            var c = db.GetCollection<Carried>(CollectionName);
            c.EnsureIndex(x => x.TxHash, false);
            return c;
        }

        /// <summary>Whether this node may put a transaction back now: at the network's height, on a moving chain. Replaceable for tests.</summary>
        internal static Func<bool> AtNetworkHeight = () => Services.VBTCCancellationVoteService.NodeIsAtNetworkHeight().Ready;
        /// <summary>Whether this validator gave a share for a request; throws when its evidence cannot be read. Replaceable for tests.</summary>
        internal static Func<string, bool> SignedFor = FrostSignedWithdrawalEvidence.AnyFor;
        /// <summary>Sends a transaction this node put back in its mempool to its peers. Replaceable for tests.</summary>
        internal static Func<Transaction, Task> Broadcast = P2P.P2PClient.SendTXMempool;

        /// <summary>The requests held now.</summary>
        public static List<Carried> Held() => Coll()?.FindAll().ToList() ?? new List<Carried>();

        /// <summary>
        /// Holds the withdrawal requests in <paramref name="removed"/> that this validator gave a share for, before the
        /// blocks go. Returns how many were held. Throws when a request cannot be saved or the evidence cannot be read:
        /// the caller must not remove the blocks then, or a signed-for request would be lost with them.
        /// </summary>
        public static int Hold(IEnumerable<Block> removed)
        {
            var held = 0;
            var c = Coll() ?? throw new System.IO.IOException("the vBTC store is not open");
            foreach (var block in removed)
            {
                foreach (var tx in block.Transactions ?? new List<Transaction>())
                {
                    if (tx?.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_REQUEST || string.IsNullOrEmpty(tx.Hash)) continue;
                    if (!SignedFor(tx.Hash)) continue;
                    var row = c.FindOne(x => x.TxHash == tx.Hash) ?? new Carried { TxHash = tx.Hash, HeldAt = TimeUtil.GetTime() };
                    row.TxJson = JsonConvert.SerializeObject(tx);
                    row.RemovedAtHeight = block.Height;
                    if (row.Id == 0) c.Insert(row); else c.Update(row);
                    held++;
                    var line = $"Withdrawal request {tx.Hash} in block {block.Height}, which this validator signed for, goes with the block; it is held to be put back in the mempool.";
                    LogUtility.Log(line, "SignedRequestCarry.Hold()");
                    ConsoleWriterService.Output(line);
                }
            }
            return held;
        }

        /// <summary>
        /// One pass over the held requests: one not on this chain is put back in the mempool and sent on; one that stands
        /// on this chain again is let go; one the chain can no longer take (its timestamp is past the age a block accepts)
        /// is let go with the loss named, for the operator. Returns the hashes put back this pass.
        /// </summary>
        public static async Task<List<string>> ResubmitDueAsync()
        {
            var put = new List<string>();
            var c = Coll();
            if (c == null || c.Count() == 0 || !AtNetworkHeight()) return put;
            foreach (var row in c.FindAll().ToList())
            {
                Transaction? tx = null;
                try { tx = JsonConvert.DeserializeObject<Transaction>(row.TxJson); } catch { }
                if (tx == null || string.IsNullOrEmpty(tx.Hash))
                {
                    c.Delete(row.Id);
                    ErrorLogUtility.LogError($"Held withdrawal request {row.TxHash} could not be read back; it is dropped.", "SignedRequestCarry.ResubmitDueAsync()");
                    continue;
                }
                var standing = VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash);
                if (standing != null && standing.RequestBlockHeight > 0)
                {
                    c.Delete(row.Id);
                    LogUtility.Log($"Withdrawal request {tx.Hash} stands on this chain again at block {standing.RequestBlockHeight}; no longer held.", "SignedRequestCarry.ResubmitDueAsync()");
                    continue;
                }
                var pool = TransactionData.GetPool();
                if (pool.FindOne(x => x.Hash == tx.Hash) != null) continue;

                var (ok, why) = await TransactionValidatorService.VerifyTX(tx);
                if (ok)
                {
                    if (!MempoolEvictionUtility.TryAdmitAndInsert(tx, pool, out var reason)) { why = reason; ok = false; }
                }
                if (ok)
                {
                    try { await Broadcast(tx); } catch (Exception ex) { LogUtility.Log($"Sending held withdrawal request {tx.Hash} on failed: {ex.Message}", "SignedRequestCarry.ResubmitDueAsync()"); }
                    put.Add(tx.Hash);
                    LogUtility.Log($"Withdrawal request {tx.Hash} (removed with block {row.RemovedAtHeight}) is back in the mempool.", "SignedRequestCarry.ResubmitDueAsync()");
                    continue;
                }
                if (await TransactionData.IsTxTimestampStale(tx))
                {
                    c.Delete(row.Id);
                    var paid = string.Join(", ", FrostSignedWithdrawalEvidence.AllFor(tx.Hash).Select(e => $"{e.ScUID}: Bitcoin {e.BtcTxId}"));
                    var line = $"Withdrawal request {tx.Hash} (removed with block {row.RemovedAtHeight}) is too old for the chain to take back ({why}); it cannot be put back. " +
                               $"This validator signed its payment ({paid}), which may be paid while the request's escrow is not debited on this chain. Operator attention required.";
                    ErrorLogUtility.LogError(line, "SignedRequestCarry.ResubmitDueAsync()");
                    ConsoleWriterService.Output(line);
                    continue;
                }
                row.Attempts++;
                row.LastRefusal = why ?? string.Empty;
                c.Update(row);
                LogUtility.Log($"Withdrawal request {tx.Hash} not put back yet ({why}); attempt {row.Attempts}.", "SignedRequestCarry.ResubmitDueAsync()");
            }
            return put;
        }
    }
}
