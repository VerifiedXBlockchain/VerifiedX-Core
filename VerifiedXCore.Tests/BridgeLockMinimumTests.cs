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
    /// A vBTC V2 bridge lock is at least the smallest withdrawal (VBTCService.MinBridgeLockSats). Admission refuses a
    /// smaller one at every height; a block carrying one is refused from BridgeLockMinimumHeight and judged as before
    /// below it. Driven through VerifyTX, the path the local lock route, the raw route, peers and block validation use.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeLockMinimumTests : IDisposable
    {
        private const long Height = 5_000;
        private const string Contract = "lockfloor:1";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight = Globals.BridgeLockMinimumHeight;
        private readonly PrivateKey _key;
        private readonly string _pub;
        private readonly string _holder;

        public BridgeLockMinimumTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"lockfloor_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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
            Globals.BridgeLockMinimumHeight = Height;
        }

        public void Dispose()
        {
            Globals.BridgeLockMinimumHeight = _priorHeight;
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

        [Fact]
        public void The_smallest_lock_is_the_smallest_withdrawal()
        {
            Assert.Equal(VBTCService.MinWithdrawalTxVBytes + VerifiedXCore.Bitcoin.Attestation.BtcCanonicalWithdrawal.DustSats, VBTCService.MinBridgeLockSats);
            Assert.Equal(700, VBTCService.MinBridgeLockSats);
        }

        [Fact]
        public async Task Admission_refuses_a_lock_below_the_minimum_at_every_height()
        {
            Globals.BridgeLockMinimumHeight = long.MaxValue; // not yet active in blocks
            var (ok, message) = await TransactionValidatorService.VerifyTX(Lock(2));
            Assert.False(ok);
            Assert.Contains("at least 700 sats", message);
        }

        [Fact]
        public async Task Admission_takes_a_lock_of_the_minimum()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(Lock(VBTCService.MinBridgeLockSats));
            Assert.True(ok, message);
        }

        [Fact]
        public async Task A_block_from_the_height_refuses_a_lock_below_the_minimum_and_below_it_judges_as_before()
        {
            var dust = Lock(VBTCService.MinBridgeLockSats - 1);

            var (atHeight, reason) = await InBlock(dust, Height);
            Assert.False(atHeight);
            Assert.Contains("at least 700 sats", reason);

            var (below, belowReason) = await InBlock(dust, Height - 1);
            Assert.True(below, belowReason);

            var (minimum, minimumReason) = await InBlock(Lock(VBTCService.MinBridgeLockSats), Height);
            Assert.True(minimum, minimumReason);
        }
    }
}
