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
    /// From BridgeLockPartMinimumHeight a vBTC V2 bridge lock is at least the smallest part a vault pays in an exit to
    /// Bitcoin (VBTCService.MinBridgeLockPartSats), so a fresh lock pays an exit part by itself. Admission and blocks refuse
    /// a smaller one from that height (admission: the block it would enter) and judge as before below it. Driven through
    /// VerifyTX, the path the local lock route, the raw route, peers and block validation use.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeLockPartMinimumTests : IDisposable
    {
        private const long Height = 5_000;
        private const string Contract = "lockpart:1";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight = Globals.BridgeLockPartMinimumHeight;
        private readonly PrivateKey _key;
        private readonly string _pub;
        private readonly string _holder;

        public BridgeLockPartMinimumTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"lockpart_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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
            Globals.BridgeLockPartMinimumHeight = Height;
        }

        public void Dispose()
        {
            Globals.BridgeLockPartMinimumHeight = _priorHeight;
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction Lock(long sats)
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
                    EvmDestination = "0x" + new string('1', 40),
                }),
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _key, _pub);
            return tx;
        }

        private static Task<(bool, string)> InBlock(Transaction tx, long height) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);

        private const string Refusal = "the smallest part of an exit to Bitcoin";

        [Fact]
        public void The_smallest_lock_is_the_smallest_exit_part()
        {
            Assert.Equal(VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.MinExitPartSats, VBTCService.MinBridgeLockPartSats);
            Assert.Equal(25_186, VBTCService.MinBridgeLockPartSats);
        }

        [Fact]
        public async Task Admission_takes_a_small_lock_until_the_block_it_would_enter_reaches_the_height()
        {
            Globals.BridgeLockPartMinimumHeight = Height + 2;                       // the next block is Height + 1
            var (ok, message) = await TransactionValidatorService.VerifyTX(Lock(10_000));
            Assert.True(ok, message);
            Globals.BridgeLockPartMinimumHeight = Height + 1;
            var (refused, why) = await TransactionValidatorService.VerifyTX(Lock(10_000));
            Assert.False(refused);
            Assert.Contains(Refusal, why);
            var (minimum, minimumWhy) = await TransactionValidatorService.VerifyTX(Lock(VBTCService.MinBridgeLockPartSats));
            Assert.True(minimum, minimumWhy);
        }

        [Fact]
        public async Task A_block_from_the_height_refuses_a_lock_below_an_exit_part_and_below_it_judges_as_before()
        {
            var small = Lock(VBTCService.MinBridgeLockPartSats - 1);

            var (atHeight, reason) = await InBlock(small, Height);
            Assert.False(atHeight);
            Assert.Contains(Refusal, reason);

            var (below, belowReason) = await InBlock(small, Height - 1);
            Assert.True(below, belowReason);

            var (minimum, minimumReason) = await InBlock(Lock(VBTCService.MinBridgeLockPartSats), Height);
            Assert.True(minimum, minimumReason);
        }
    }
}
