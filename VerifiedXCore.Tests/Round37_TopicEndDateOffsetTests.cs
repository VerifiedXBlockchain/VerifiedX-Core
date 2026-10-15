using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: from Globals.PathAuditRulesHeight a vote topic's end of voting carries a UTC offset ("Z" or "+hh:mm").
    /// A date without one is stored as an instant that depends on the storing node's time zone. Refused for every
    /// function (the apply stores the topic whatever the function); below the height a block is judged as before. The
    /// date is refused at admission at every height. The rule reads only how the date was written, so the tests do not depend on this machine's time zone.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_TopicEndDateOffsetTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _creator;

        public Round37_TopicEndDateOffsetTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37topic_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.PathAuditRulesHeight;
            Globals.PathAuditRulesHeight = Gate;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 10, Timestamp = TimeUtil.GetTime() }; // admission judges at tip + 1, below the gate

            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            _creator = (key, pub, AccountData.GetHumanAddress(pub));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _creator.Address, Balance = 10_000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction TopicTx(string function, string votingEndDate)
        {
            var uid = Guid.NewGuid().ToString("N");
            var tx = new Transaction
            {
                FromAddress = _creator.Address, ToAddress = "Topic_Base", Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.VOTE_TOPIC,
                Data = "{\"Function\":\"" + function + "\",\"Topic\":{\"TopicUID\":\"" + uid + "\",\"TopicName\":\"t\",\"TopicDescription\":\"d\",\"TopicOwnerAddress\":\""
                    + _creator.Address + "\",\"VotingEndDate\":\"" + votingEndDate + "\"}}",
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _creator.Key, _creator.Pub);
            return tx;
        }

        private static string In(TimeSpan from, string suffix) => DateTime.UtcNow.Add(from).ToString("yyyy-MM-ddTHH:mm:ss") + suffix;

        [Theory]
        [InlineData("X")]
        [InlineData("TopicAdd()")]
        public async Task AnEndDateWithoutAnOffset_IsRefused_AtAdmissionAndInBlocksFromTheHeight(string function)
        {
            var tx = TopicTx(function, In(TimeSpan.FromDays(2), ""));

            var (poolOk, poolMsg) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(poolOk);
            Assert.Equal(TransactionValidatorService.VotingEndDateNeedsOffsetReason, poolMsg);

            var (blockOk, blockMsg) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Gate);
            Assert.False(blockOk);
            Assert.Equal(TransactionValidatorService.VotingEndDateNeedsOffsetReason, blockMsg);

            // A block below the height is judged as before (replay).
            var (_, belowMsg) = await TransactionValidatorService.VerifyTX(tx, true, true, false, null, false, Gate - 1);
            Assert.NotEqual(TransactionValidatorService.VotingEndDateNeedsOffsetReason, belowMsg);
        }

        [Theory]
        [InlineData("Z")]
        [InlineData("+09:00")]
        [InlineData("-05:00")]
        public async Task AnEndDateWithAnOffset_PassesAtTheHeight(string offset)
        {
            var tx = TopicTx("X", In(TimeSpan.FromDays(2), offset));
            var (blockOk, blockMsg) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Gate);
            Assert.True(blockOk, blockMsg);
            var (poolOk, poolMsg) = await TransactionValidatorService.VerifyTX(tx);
            Assert.True(poolOk, poolMsg);
        }
    }
}
