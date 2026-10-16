using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.BtcNameOneRecordPerAddressHeight a Bitcoin address holds at most one BTC name: a transfer to an
    /// address that already holds one (another transfer to it earlier in the block) is left at apply.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcNameOneRecordPerAddressTests : IDisposable
    {
        private const long Height = 5000;
        private const string Target = "bc1qtarget";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorHeight;

        public BtcNameOneRecordPerAddressTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"btc_name_one_record_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            _priorHeight = Globals.BtcNameOneRecordPerAddressHeight;
            Globals.BtcNameOneRecordPerAddressHeight = Height;
        }

        public void Dispose()
        {
            Globals.BtcNameOneRecordPerAddressHeight = _priorHeight;
            DbContext.CloseDB();
            Globals.CustomPath = _priorCustomPath;
            Directory.Delete(_tempRoot, recursive: true);
        }

        private static void Name(string name, string btcAddress, string owner) =>
            BitcoinAdnr.GetBitcoinAdnr()!.InsertSafe(new BitcoinAdnr { Name = name, BTCAddress = btcAddress, RBXAddress = owner, TxHash = name, Timestamp = 1 });

        private static Transaction Transfer(string from, string newOwner, string sender = "xSender") => new Transaction
        {
            Hash = Guid.NewGuid().ToString("N"),
            FromAddress = sender,
            ToAddress = newOwner,
            TransactionType = TransactionType.ADNR,
            Data = JsonConvert.SerializeObject(new { Function = "BTCAdnrTransfer()", BTCFromAddress = from, BTCToAddress = Target }),
        };

        private static int RecordsAt(string btcAddress) => BitcoinAdnr.GetBitcoinAdnr()!.Count(x => x.BTCAddress == btcAddress);

        [Fact]
        public async Task FromTheHeight_ASecondTransferToTheSameAddressInABlockIsLeft()
        {
            Name("alice.btc", "bc1qalice", "xAlice");
            Name("bob.btc", "bc1qbob", "xBob");

            await StateData.TransferBTCAdnr(Transfer("bc1qalice", "xCarol", "xAlice"), Height);
            await StateData.TransferBTCAdnr(Transfer("bc1qbob", "xCarol", "xBob"), Height);

            Assert.Equal(1, RecordsAt(Target));
            Assert.Equal("alice.btc", BitcoinAdnr.GetBitcoinAdnr()!.FindOne(x => x.BTCAddress == Target).Name);
            Assert.Equal(1, RecordsAt("bc1qbob")); // the second name stays where it was
        }

        [Fact]
        public async Task FromTheHeight_ATransferToAFreeAddressStillMovesTheName()
        {
            Name("alice.btc", "bc1qalice", "xAlice");

            await StateData.TransferBTCAdnr(Transfer("bc1qalice", "xCarol", "xAlice"), Height);

            var moved = BitcoinAdnr.GetBitcoinAdnr()!.FindOne(x => x.BTCAddress == Target);
            Assert.NotNull(moved);
            Assert.Equal("xCarol", moved.RBXAddress);
            Assert.Equal(0, RecordsAt("bc1qalice"));
        }

        [Fact]
        public async Task BeforeTheHeight_TheOlderApplyIsKept()
        {
            Name("alice.btc", "bc1qalice", "xAlice");
            Name("bob.btc", "bc1qbob", "xBob");

            await StateData.TransferBTCAdnr(Transfer("bc1qalice", "xCarol"), Height - 1);
            await StateData.TransferBTCAdnr(Transfer("bc1qbob", "xCarol"), Height - 1);

            Assert.Equal(2, RecordsAt(Target));
        }

        private static Transaction Delete(string from, string sender) => new Transaction
        {
            Hash = Guid.NewGuid().ToString("N"),
            FromAddress = sender,
            ToAddress = "Adnr_Base",
            TransactionType = TransactionType.ADNR,
            Data = JsonConvert.SerializeObject(new { Function = "BTCAdnrDelete()", BTCFromAddress = from }),
        };

        [Fact]
        public void FromTheHeight_ADeleteRemovesOnlyTheSendersRecordAtTheAddress()
        {
            // Two records on one address, as the older apply could leave.
            Name("alice.btc", Target, "xAlice");
            Name("mallory.btc", Target, "xMallory");

            StateData.DeleteBTCAdnr(Delete(Target, "xMallory"), Height);

            Assert.Equal(1, RecordsAt(Target));
            Assert.Equal("xAlice", BitcoinAdnr.GetBitcoinAdnr()!.FindOne(x => x.BTCAddress == Target).RBXAddress);
        }

        [Fact]
        public void BeforeTheHeight_ADeleteRemovesEveryRecordAtTheAddress()
        {
            Name("alice.btc", Target, "xAlice");
            Name("mallory.btc", Target, "xMallory");

            StateData.DeleteBTCAdnr(Delete(Target, "xMallory"), Height - 1);

            Assert.Equal(0, RecordsAt(Target));
        }

        [Fact]
        public async Task FromTheHeight_ATransferMovesTheSendersOwnRecord()
        {
            Name("alice.btc", "bc1qshared", "xAlice");
            Name("mallory.btc", "bc1qshared", "xMallory");

            await StateData.TransferBTCAdnr(Transfer("bc1qshared", "xMallory2", "xMallory"), Height);

            var moved = BitcoinAdnr.GetBitcoinAdnr()!.FindOne(x => x.BTCAddress == Target);
            Assert.Equal("mallory.btc", moved.Name);
            Assert.Equal("xAlice", BitcoinAdnr.GetBitcoinAdnr()!.FindOne(x => x.BTCAddress == "bc1qshared").RBXAddress);
        }

        [Theory]
        [InlineData("BTCAdnrCreate()", "BTCAddress")]
        [InlineData("BTCAdnrTransfer()", "BTCToAddress")]
        public void TheBlockCandidateList_KeysCreatesAndTransfersOnTheAddressTheyName(string function, string field)
        {
            var call = Newtonsoft.Json.Linq.JObject.FromObject(new Dictionary<string, string> { ["Function"] = function, [field] = Target });
            Assert.Equal(Target, TransactionData.BtcNameTargetAddress(function, call));
        }

        [Fact]
        public void TheBlockCandidateList_IgnoresOtherNameCalls()
        {
            var call = Newtonsoft.Json.Linq.JObject.FromObject(new { Function = "BTCAdnrDelete()", BTCFromAddress = Target });
            Assert.Null(TransactionData.BtcNameTargetAddress("BTCAdnrDelete()", call));
        }

        [Fact]
        public void TheRuleShipsNotActive_UntilAHeightIsChosen()
        {
            Assert.Equal(999_999_999_999, _priorHeight);
        }
    }
}
