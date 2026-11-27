using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Services;
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
    /// A vBTC V2 bridge lock names an address Base can mint to: "0x" and 40 hex digits, not the zero address. Admission
    /// refuses any other at every height; a block carrying one is refused from BridgeLockEvmAddressHeight and judged as
    /// before below it. Driven through VerifyTX, the path the local lock route, the raw route, peers and block validation use.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeLockEvmAddressTests : IDisposable
    {
        private const long Height = 5_000;
        private const string Contract = "lockevm:1";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight = Globals.BridgeLockEvmAddressHeight;
        private readonly PrivateKey _key;
        private readonly string _pub;
        private readonly string _holder;

        public BridgeLockEvmAddressTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"lockevm_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();

            _key = new PrivateKey("secp256k1");
            _pub = "04" + Convert.ToHexString(_key.publicKey().toString()).ToLowerInvariant();
            _holder = AccountData.GetHumanAddress(_pub);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder, Balance = 100M, Nonce = 0 });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Contract,
                OwnerAddress = "xContractOwnerNotTheHolder00000001",
                ContractData = VbtcTestContracts.VbtcV2ContractData,
                SCStateTreiTokenizationTXes = new() { new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _holder, Amount = 1.0M } },
            });
            Globals.LastBlock = new Block { Height = Height };
            Globals.BridgeLockEvmAddressHeight = Height;
        }

        public void Dispose()
        {
            Globals.BridgeLockEvmAddressHeight = _priorHeight;
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction Lock(string evmDestination, long sats = 10_000)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _holder,
                ToAddress = _holder,
                Amount = 0.0M,
                Fee = 0.00001M,
                Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_LOCK,
                Data = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeLock()",
                    ContractUID = Contract,
                    LockId = Guid.NewGuid().ToString("N"),
                    Amount = sats / 100_000_000M,
                    AmountSats = sats,
                    EvmDestination = evmDestination,
                }),
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _key, _pub);
            return tx;
        }

        private static Task<(bool, string)> InBlock(Transaction tx, long height) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);

        private static readonly string Good = "0x" + "aB12cD34eF56aB12cD34eF56aB12cD34eF56aB12";
        private static readonly string Zero = "0x" + new string('0', 40);
        private static readonly string ShortPadded = "0x1234" + new string(' ', 36);
        private static readonly string NotHex = "0x" + new string('g', 40);

        [Theory]
        [InlineData("0x0000000000000000000000000000000000000000")]
        [InlineData("0x1234                                    ")]
        [InlineData("0xgggggggggggggggggggggggggggggggggggggggg")]
        public async Task Admission_refuses_a_destination_Base_cannot_mint_to_at_every_height(string destination)
        {
            Assert.Equal(42, destination.Length); // each passes the older length check
            Globals.BridgeLockEvmAddressHeight = long.MaxValue; // not yet active in blocks
            var (ok, message) = await TransactionValidatorService.VerifyTX(Lock(destination));
            Assert.False(ok);
            Assert.Equal(TransactionValidatorService.BridgeLockDestinationNotMintableReason, message);
        }

        [Fact]
        public async Task Admission_takes_a_mixed_case_hex_address()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(Lock(Good));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task A_block_from_the_height_refuses_an_unmintable_destination_and_below_it_judges_as_before()
        {
            foreach (var destination in new[] { Zero, ShortPadded, NotHex })
            {
                var tx = Lock(destination);
                var (atHeight, reason) = await InBlock(tx, Height);
                Assert.False(atHeight);
                Assert.Equal(TransactionValidatorService.BridgeLockDestinationNotMintableReason, reason);

                var (below, belowReason) = await InBlock(tx, Height - 1);
                Assert.True(below, belowReason);
            }

            var (good, goodReason) = await InBlock(Lock(Good), Height);
            Assert.True(good, goodReason);
        }
    }
}
