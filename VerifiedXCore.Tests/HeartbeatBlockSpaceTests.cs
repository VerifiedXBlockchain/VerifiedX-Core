using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A block takes at most 1 MB of the mempool, by rating and then by a timestamp the sender chooses. The priority lane
    /// (the Bitcoin report or rewind, vault re-keys, validator heartbeats and registrations, reserve Recover()) is taken
    /// first in its own 1 MB, one per sender, and the per-block transaction cap counts it first.
    /// </summary>
    [Collection("DbContextSequential")]
    public class HeartbeatBlockSpaceTests : BtcAttestTestBase
    {
        public HeartbeatBlockSpaceTests() : base(realMainnetRules: false) { }

        /// <summary>A heartbeat built and pooled as VBTCValidatorHeartbeatService.SendHeartbeatTransaction does.</summary>
        private static Transaction Heartbeat(V v)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = v.Address,
                ToAddress = v.Address,
                Amount = 0M,
                Fee = 0M,
                Nonce = StateData.GetSpecificAccountStateTrei(v.Address)!.Nonce,
                TransactionType = TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT,
                Data = JsonConvert.SerializeObject(new
                {
                    ValidatorAddress = v.Address,
                    IPAddress = "52.1.2.3",
                    FrostPublicKey = v.Pub,
                    ReactivationBlockHeight = Globals.LastBlock.Height,
                    PreviousIPAddress = "",
                    IsS3C = false,
                    Signature = SignatureService.CreateSignature(v.Address, v.Key, v.Pub),
                }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, v.Key, v.Pub);
            return tx;
        }

        [Fact]
        public async Task AHeartbeat_IsInTheBlock_WhenThePoolHoldsOverAMegabyteOfEarlierDatedTransfers()
        {
            await Start();
            var pool = TransactionData.GetPool();

            // Free vBTC transfers dated half an hour back (the rating a heartbeat has, and earlier) filling the block's 1 MB
            // to within less than a heartbeat, as a flood of small transfers does; more of them wait behind.
            Transaction Filler(int i, int dataChars) => new()
            {
                Hash = $"filler-{i}-{Guid.NewGuid():N}", Timestamp = TimeUtil.GetTime() - 1800 + i, FromAddress = Minter.Address,
                ToAddress = Signers[1].Address, Nonce = i, TransactionType = TransactionType.VBTC_V2_TRANSFER,
                TransactionRating = TransactionRating.A, Signature = "x", Data = new string('a', dataChars),
            };
            static long SizeOf(Transaction t) => JsonConvert.SerializeObject(t).Length; // as SizeMempoolDown measures
            for (var i = 0; i < 25; i++)
                pool.InsertSafe(Filler(i, 40_000));
            var last = Filler(25, 0);
            pool.InsertSafe(last);
            const long Full = 1_053_700; // the cut keeps a set while its size rounds to 1.00 MB
            last.Data = new string('a', (int)(Full - pool.FindAll().Sum(SizeOf)));
            pool.UpdateSafe(last);
            Assert.InRange(pool.FindAll().Sum(SizeOf), Full - 50, Full);
            for (var i = 26; i < 40; i++)
                pool.InsertSafe(Filler(i, 800));

            var heartbeat = Heartbeat(Signers[0]);
            var (ok, why) = await TransactionValidatorService.VerifyTX(heartbeat);
            Assert.True(ok, why);
            heartbeat.TransactionRating = await TransactionRatingService.GetTransactionRating(heartbeat);
            Assert.Equal(TransactionRating.A, heartbeat.TransactionRating);
            await TransactionData.AddToPool(heartbeat);

            var chosen = await TransactionData.ProcessTxPool();

            Assert.Contains(chosen, t => t.Hash == heartbeat.Hash);
            var (inBlock, inBlockWhy) = await Verify(chosen.Single(t => t.Hash == heartbeat.Hash), Globals.LastBlock.Height + 1);
            Assert.True(inBlock, inBlockWhy);
        }

        private static Transaction Tx(string hash, string from, long timestamp, TransactionType type, int dataChars, string? function = null) => new()
        {
            Hash = hash, FromAddress = from, Timestamp = timestamp, TransactionType = type, TransactionRating = TransactionRating.A,
            Data = function == null ? new string('d', dataChars) : JsonConvert.SerializeObject(new { Function = function, Pad = new string('d', dataChars) }),
        };

        [Fact]
        public void TheLane_IsTakenFirst_InItsOrder_AndTheBlockStaysWithinWhatPeersTake()
        {
            // 1.2 MB of transfers dated earlier than everything in the lane, so by time alone they come first.
            var pool = Enumerable.Range(0, 30).Select(i => Tx($"transfer-{i}", $"xT{i}", 10 + i, TransactionType.VBTC_V2_TRANSFER, 40_000)).ToList();
            pool.Add(Tx("recover", "xRBXowner", 900, TransactionType.RESERVE, 20_000, "Recover()"));
            pool.Add(Tx("callback", "xRBXother", 50, TransactionType.RESERVE, 1_000, "CallBack()"));
            pool.Add(Tx("heartbeat", "xVal", 800, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, 1_000));
            pool.Add(Tx("rekey", "BTC_Attestation", 700, TransactionType.VBTC_V2_VAULT_REKEY, 20_000));
            pool.Add(Tx("report", "BTC_Attestation", 600, TransactionType.VBTC_V2_BTC_REPORT, 400_000));

            var sized = MempoolSizeUtility.SizeMempoolDown(pool);

            Assert.Equal(new[] { "report", "rekey", "heartbeat", "recover" }, sized.Take(4).Select(t => t.Hash));
            // The rest fills what the lane left of the same 1 MB, so the block is within the size a peer takes a broadcast
            // block at (1,179,648 characters, P2PClient and NodeDataProcessor); a reserve CallBack() is not in the lane.
            Assert.InRange(sized.Sum(t => JsonConvert.SerializeObject(t).Length), 1_000_000, 1_053_818);
            Assert.True(sized.Skip(4).Any());
            Assert.False(MempoolSizeUtility.InPriorityLane(pool.Single(t => t.Hash == "callback")));
        }

        [Fact]
        public void TheLane_TakesOnePerSender_AndNoMoreThanItsSpace_TheRestWaitsInThePool()
        {
            var pool = new List<Transaction>
            {
                Tx("hb-early", "xVal", 100, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, 1_000),
                Tx("hb-later", "xVal", 200, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, 1_000),
                Tx("register", "xVal", 150, TransactionType.VBTC_V2_VALIDATOR_REGISTER, 1_000),
                Tx("report", "BTC_Attestation", 300, TransactionType.VBTC_V2_BTC_REPORT, 400_000),
            };
            // Many reserves' Recover() calls, more than the lane holds: they come after the report and the heartbeat.
            pool.AddRange(Enumerable.Range(0, 60).Select(i => Tx($"recover-{i}", $"xRBX{i}", 10 + i, TransactionType.RESERVE, 29_000, "Recover()")));
            pool.Add(Tx("recover-again", "xRBX0", 5, TransactionType.RESERVE, 29_000, "Recover()"));
            pool.Add(Tx("transfer", "xT", 1, TransactionType.VBTC_V2_TRANSFER, 1_000)); // first of the rest: marks where the lane ends

            var sized = MempoolSizeUtility.SizeMempoolDown(pool);
            var lane = sized.TakeWhile(MempoolSizeUtility.InPriorityLane).ToList();

            Assert.Equal("report", lane[0].Hash);
            Assert.Equal("hb-early", lane[1].Hash); // one of the validator's heartbeat and registration
            Assert.DoesNotContain(lane.Skip(2), t => t.FromAddress == "xVal");
            Assert.Single(lane, t => t.FromAddress == "xRBX0");
            Assert.InRange(lane.Sum(t => JsonConvert.SerializeObject(t).Length), 0, MempoolSizeUtility.PriorityLaneBytes);
            Assert.True(lane.Count(t => t.TransactionType == TransactionType.RESERVE) < 60);
            // What the lane had no room for is not dropped: it is sized with the rest of the pool.
            Assert.Contains(sized.Skip(lane.Count), t => t.Hash == "hb-later" || t.Hash == "register");
        }

        [Fact]
        public void TheTransactionCap_KeepsTheLane_AndCutsASendersLaterTransactionsWithItsFirst()
        {
            var priorCapHeight = Globals.BlockTransactionCapHeight;
            Globals.BlockTransactionCapHeight = 0;
            try
            {
                // In validation order (sender, then nonce): the validator sorts after the 3,100 senders of transfers.
                var txs = Enumerable.Range(0, 3_100).Select(i => Tx($"t-{i:D4}", $"xA{i:D4}", i, TransactionType.TX, 10)).ToList();
                txs.Add(Tx("t-z1", "xZZ", 1, TransactionType.TX, 10));
                txs.Add(Tx("hb", "xZZ", 2, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, 10));
                txs.Add(Tx("hb-other", "xZZother", 2, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, 10));

                var kept = LedgerIntegrityRules.WithinTransactionCap(txs, 1, 1);

                Assert.InRange(kept.Count, Globals.MaxTransactionsPerBlock - 3, Globals.MaxTransactionsPerBlock - 1);
                Assert.Contains(kept, t => t.Hash == "hb-other");
                // xZZ's transfer is past the cap, so its heartbeat after it goes too: no sender is left with a gap.
                Assert.DoesNotContain(kept, t => t.FromAddress == "xZZ");
                Assert.Equal(kept.Select(t => t.Hash), txs.Where(t => kept.Contains(t)).Select(t => t.Hash)); // order kept
            }
            finally
            {
                Globals.BlockTransactionCapHeight = priorCapHeight;
            }
        }
    }
}
