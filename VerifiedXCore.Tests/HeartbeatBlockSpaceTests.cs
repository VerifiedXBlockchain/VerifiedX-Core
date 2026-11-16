using System;
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
    /// A block takes at most 1 MB of the mempool. Free vBTC transfers share a heartbeat's rating and may be dated up to the
    /// age limit back, so by time alone a pool full of them kept a validator's heartbeat out of every block; each sender's
    /// earliest heartbeat is taken first.
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

        [Fact]
        public void OnlyOneHeartbeatPerSender_GoesAheadOfTheRest()
        {
            var early = new Transaction { Hash = "hb-early", FromAddress = "xA", Timestamp = 100, TransactionType = TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, TransactionRating = TransactionRating.A, Data = new string('h', 400_000) };
            var later = new Transaction { Hash = "hb-later", FromAddress = "xA", Timestamp = 200, TransactionType = TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, TransactionRating = TransactionRating.A, Data = new string('h', 400_000) };
            var transfer = new Transaction { Hash = "transfer", FromAddress = "xB", Timestamp = 50, TransactionType = TransactionType.VBTC_V2_TRANSFER, TransactionRating = TransactionRating.A, Data = new string('t', 400_000) };

            var sized = MempoolSizeUtility.SizeMempoolDown(new() { later, transfer, early });

            // The sender's earliest heartbeat first, then the rest by rating and time: the later heartbeat has no place.
            Assert.Equal(new[] { "hb-early", "transfer" }, sized.Select(t => t.Hash));
        }
    }
}
