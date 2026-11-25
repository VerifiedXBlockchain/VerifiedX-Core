using System;
using System.IO;
using System.Linq;
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
using Xunit.Abstractions;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Checking a bridge lock reads its vault's state document once in the lock's own rules (it read it four times): the
    /// document holds the vault's whole ledger, so each read costs as much as the ledger is long. The rules every contract
    /// call passes read it twice more, so a check costs about three reads. Measured as bytes allocated on the test's thread
    /// against one read of the same document, on a vault whose ledger names many holders: the count of bytes does not move
    /// with machine load or collections the way elapsed time does.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeLockReadOnceTests : IDisposable
    {
        private const long Height = 5_000;
        private const string Contract = "lockreadonce:1";
        private const int Holders = 60_000;

        private readonly ITestOutputHelper _output;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly PrivateKey _key;
        private readonly string _pub;
        private readonly string _holder;

        public BridgeLockReadOnceTests(ITestOutputHelper output)
        {
            _output = output;
            _tempRoot = Path.Combine(Path.GetTempPath(), $"lockreadonce_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();

            _key = new PrivateKey("secp256k1");
            _pub = "04" + Convert.ToHexString(_key.publicKey().toString()).ToLowerInvariant();
            _holder = AccountData.GetHumanAddress(_pub);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder, Balance = 100M, Nonce = 0 });
            var rows = Enumerable.Range(0, Holders)
                .Select(i => new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = $"xHolder{i:D27}", Amount = 0.00001M })
                .Append(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _holder, Amount = 1.0M })
                .ToList();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Contract,
                OwnerAddress = "xContractOwnerNotTheHolder00000003",
                ContractData = VbtcTestContracts.VbtcV2ContractData,
                SCStateTreiTokenizationTXes = rows,
            });
            Globals.LastBlock = new Block { Height = Height };
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction Lock()
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
                    Amount = VBTCService.MinBridgeLockSats / 100_000_000M,
                    AmountSats = VBTCService.MinBridgeLockSats,
                    EvmDestination = "0x" + new string('4', 40),
                }),
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _key, _pub);
            return tx;
        }

        /// <summary>Bytes the call allocates on this thread: a read of the vault's document allocates its whole ledger.</summary>
        private static long Allocated(Action run)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            run();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        [Fact]
        public async Task A_lock_check_reads_its_vaults_document_once_in_its_own_rules()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(Lock()); // warm, and the lock is valid
            Assert.True(ok, message);
            SmartContractStateTrei.GetSmartContractState(Contract);

            // Built and signed before counting: only the check is counted. The check runs to completion on this thread.
            var tx = Lock();
            var read = Allocated(() => SmartContractStateTrei.GetSmartContractState(Contract));
            var check = Allocated(() =>
            {
                var (valid, why) = TransactionValidatorService.VerifyTX(tx).GetAwaiter().GetResult();
                Assert.True(valid, why);
            });
            var reads = (double)check / read;
            _output.WriteLine($"one read {read:N0} bytes, one lock check {check:N0} bytes, {reads:F2} reads");
            Assert.True(reads < 4.5, // about 3 now; 6 or more before the read-once change
                $"a lock check allocated {check:N0} bytes, {reads:F2} reads of its vault's document ({read:N0} bytes each)");
        }
    }
}
