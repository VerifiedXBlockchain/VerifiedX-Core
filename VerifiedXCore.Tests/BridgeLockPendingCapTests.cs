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

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// One sender has at most MaxPendingBridgeLocksPerSender bridge locks waiting in the mempool. Admission (VerifyTX, the
    /// local, raw and peer paths) refuses one more; the producer (ProcessTxPool) keeps a sender's earliest locks and drops
    /// the rest, so a backlog already in the pool, as one kept on disk across a restart, is cut down. Block validation never
    /// applies it: a block is judged by its height alone.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeLockPendingCapTests : IDisposable
    {
        private const long Height = 5_000;
        private const string Contract = "lockcap:1";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorFloorHeight = Globals.BridgeLockMinimumHeight;

        public BridgeLockPendingCapTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"lockcap_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Contract,
                OwnerAddress = "xContractOwnerNotTheHolder00000002",
                ContractData = VbtcTestContracts.VbtcV2ContractData,
                SCStateTreiTokenizationTXes = new(),
            });
            Globals.LastBlock = new Block { Height = Height };
        }

        public void Dispose()
        {
            Globals.BridgeLockMinimumHeight = _priorFloorHeight;
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private sealed record Holder(PrivateKey Key, string Pub, string Address);

        private static Holder NewHolder()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var holder = new Holder(key, pub, AccountData.GetHumanAddress(pub));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = holder.Address, Balance = 100M, Nonce = 0 });
            var contract = SmartContractStateTrei.GetSmartContractState(Contract)!;
            contract.SCStateTreiTokenizationTXes!.Add(new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = holder.Address, Amount = 1.0M });
            SmartContractStateTrei.UpdateSmartContract(contract);
            return holder;
        }

        private static Transaction Lock(Holder from, long nonce)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = from.Address,
                ToAddress = from.Address,
                Amount = 0.0M,
                Fee = 0.00001M,
                Nonce = nonce,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_LOCK,
                Data = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeLock()",
                    ContractUID = Contract,
                    LockId = Guid.NewGuid().ToString("N"),
                    Amount = VBTCService.MinBridgeLockSats / 100_000_000M,
                    AmountSats = VBTCService.MinBridgeLockSats,
                    EvmDestination = "0x" + new string('2', 40),
                }),
                TransactionRating = TransactionRating.A,
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, from.Key, from.Pub);
            return tx;
        }

        private static int Pooled(Holder from) =>
            TransactionData.GetPool().Find(x => x.FromAddress == from.Address).Count(x => x.TransactionType == TransactionType.VBTC_V2_BRIDGE_LOCK);

        [Fact]
        public async Task Admission_refuses_a_sender_one_lock_past_the_cap_and_no_one_else()
        {
            var cap = TransactionValidatorService.MaxPendingBridgeLocksPerSender;
            var flooder = NewHolder();
            for (var i = 0; i < cap; i++)
            {
                var tx = Lock(flooder, i);
                var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
                Assert.True(ok, message);
                Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(tx, TransactionData.GetPool(), out var reason), reason);
            }

            var (past, pastReason) = await TransactionValidatorService.VerifyTX(Lock(flooder, cap));
            Assert.False(past);
            Assert.Contains($"already has {cap} bridge locks waiting", pastReason);

            var (other, otherReason) = await TransactionValidatorService.VerifyTX(Lock(NewHolder(), 0));
            Assert.True(other, otherReason);

            // A block is judged by its height, never by this node's mempool.
            Globals.BridgeLockMinimumHeight = long.MaxValue;
            var (inBlock, inBlockReason) = await TransactionValidatorService.VerifyTX(Lock(flooder, cap), false, true, false, null, false, Height);
            Assert.True(inBlock, inBlockReason);
        }

        [Fact]
        public async Task Locks_arriving_together_cannot_take_a_sender_past_the_cap_on_either_insert_path()
        {
            var cap = TransactionValidatorService.MaxPendingBridgeLocksPerSender;

            var received = NewHolder(); // peers and the raw route
            await Task.WhenAll(Enumerable.Range(0, cap + 12).Select(i => Task.Run(() =>
                MempoolEvictionUtility.TryAdmitAndInsert(Lock(received, i), TransactionData.GetPool(), out _))));
            Assert.Equal(cap, Pooled(received));

            var local = NewHolder(); // this node's own routes
            await Task.WhenAll(Enumerable.Range(0, cap + 12).Select(i => Task.Run(() => TransactionData.AddToPool(Lock(local, i)))));
            Assert.Equal(cap, Pooled(local));
        }

        [Fact]
        public async Task A_local_lock_the_mempool_does_not_take_is_reported_as_failed_and_kept_out_of_the_wallet()
        {
            var cap = TransactionValidatorService.MaxPendingBridgeLocksPerSender;
            var holder = NewHolder();
            AccountData.GetAccounts().InsertSafe(new Account { Address = holder.Address, PublicKey = holder.Pub, PrivateKey = "0" + holder.Key.secret.ToString("x") });
            for (var i = 0; i < cap - 1; i++)
                Assert.True(MempoolEvictionUtility.TryAdmitAndInsert(Lock(holder, i), TransactionData.GetPool(), out var reason), reason);

            // One place left; several of this node's own lock requests at once.
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                VBTCService.CreateBridgeLockTx(Contract, holder.Address, VBTCService.MinBridgeLockSats / 100_000_000M, "0x" + new string('3', 40)))));

            var succeeded = results.Where(r => r.Success).Select(r => r.TxHashOrError).ToList();
            Assert.Equal(cap, Pooled(holder));
            var pooled = TransactionData.GetPool().Find(x => x.FromAddress == holder.Address).Select(x => x.Hash).ToHashSet();
            var inWallet = TransactionData.GetAll().Find(x => x.FromAddress == holder.Address).Select(x => x.Hash).ToHashSet();
            Assert.All(succeeded, hash => { Assert.Contains(hash, pooled); Assert.Contains(hash, inWallet); });
            Assert.Equal(succeeded.Count, inWallet.Count);
            Assert.All(results.Where(r => !r.Success), r => Assert.Contains("bridge locks waiting", r.TxHashOrError));
        }

        [Fact]
        public async Task The_producer_keeps_a_senders_earliest_locks_and_drops_a_backlog_past_the_cap()
        {
            var cap = TransactionValidatorService.MaxPendingBridgeLocksPerSender;
            var flooder = NewHolder();
            var backlog = Enumerable.Range(0, cap + 4).Select(i => Lock(flooder, i)).ToList();
            foreach (var tx in backlog)
                TransactionData.GetPool().InsertSafe(tx); // as a pool kept on disk across a restart holds them

            var proposed = await TransactionData.ProcessTxPool();

            Assert.Equal(cap, Pooled(flooder));
            var kept = TransactionData.GetPool().Find(x => x.FromAddress == flooder.Address).Select(x => x.Hash).ToHashSet();
            Assert.True(backlog.Take(cap).All(x => kept.Contains(x.Hash)), "the earliest locks are kept");
            Assert.True(proposed.Count(x => x.FromAddress == flooder.Address) <= cap);
        }
    }
}
