using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A vault's withdrawals in one state are read through an index on the vault and the state together: the same rows
    /// as reading every row the vault has and keeping those in the state, without loading the others.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VaultWithdrawalsByStateTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public VaultWithdrawalsByStateTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vault_wd_state_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static LiteDB.ILiteCollection<VaultWithdrawalRow> Rows => BtcAttestStore.Db.GetCollection<VaultWithdrawalRow>(BtcAttestStore.Withdrawals);

        /// <summary>The lookup as it was: every row of the vault read, those in the state kept. It had no order of its own.</summary>
        private static List<string> FullRead(string scUID, VaultWithdrawalState state) =>
            Rows.Find(x => x.SmartContractUID == scUID && x.State == state).Select(r => r.Ref).OrderBy(r => r, StringComparer.Ordinal).ToList();

        private static List<string> Indexed(string scUID, VaultWithdrawalState state)
        {
            var refs = BtcAttestStore.WithdrawalsOf(scUID, state).Select(r => r.Ref).ToList();
            Assert.Equal(refs.Count, refs.Distinct(StringComparer.Ordinal).Count()); // no row twice
            return refs.OrderBy(r => r, StringComparer.Ordinal).ToList();
        }

        private static VaultWithdrawalRow Row(string reference, string vault, VaultWithdrawalState state) =>
            new VaultWithdrawalRow { Ref = reference, SmartContractUID = vault, State = state, RequestHash = reference, AmountSats = 1_000, FeeRate = 1, RequestVfxHeight = 100 };

        [Fact]
        public void EveryVaultAndState_FindsTheRowsTheFullReadFound()
        {
            var states = Enum.GetValues<VaultWithdrawalState>();
            var longVault = new string('v', 300) + ":1";
            var vaults = new[] { "vault:1", "VAULT:1", "vault:2", "vault:1|Pinned", "vault", "va\u200dult:1", longVault, longVault.ToUpperInvariant(), "" };
            var n = 0;
            foreach (var vault in vaults)
                foreach (var state in states)
                    for (var i = 0; i < 3; i++)
                        BtcAttestStore.SaveWithdrawal(Row($"r{n++}", vault, state));

            // Rows change state, as withdrawals do: each is found under its new state only.
            foreach (var row in Rows.FindAll().ToList().Where((_, i) => i % 4 == 0))
            {
                row.State = states[((int)row.State + 1) % states.Length];
                BtcAttestStore.SaveWithdrawal(row);
            }

            var found = 0;
            foreach (var vault in vaults.Concat(new[] { " vault:1", "vault:1 ", "vault:3", "Pinned", longVault + "x", "vault:1|", "\u200d", "\u00ad" }))
                foreach (var state in states)
                {
                    var expected = FullRead(vault, state);
                    Assert.Equal(expected, Indexed(vault, state));
                    found += expected.Count;
                }
            Assert.True(found >= n, $"{found} rows found of {n}");
            Assert.Equal(FullRead(null!, VaultWithdrawalState.Pinned), Indexed(null!, VaultWithdrawalState.Pinned));
        }

        [Fact]
        public void AVaultsStateIsReadThroughItsIndex()
        {
            var plan = BtcAttestStore.Db.GetCollection(BtcAttestStore.Withdrawals).Query()
                .Where(LiteDB.Query.EQ(BtcAttestStore.VaultStateIndexExpression, "vault:1|Pinned")).GetPlan();
            Assert.Equal(BtcAttestStore.VaultStateIndexName, plan["index"]["name"].AsString);
            Assert.StartsWith("INDEX SEEK", plan["index"]["mode"].AsString);
            Assert.False(plan.ContainsKey("filters")); // nothing left to check row by row: only the rows under the key are read
        }

        [Fact]
        public void ADatabaseWrittenWithoutTheIndex_GetsItWhenItIsOpened()
        {
            Assert.True(BtcAttestStore.Db.GetCollection(BtcAttestStore.Withdrawals).DropIndex(BtcAttestStore.VaultStateIndexName));
            for (var i = 0; i < 300; i++)
                BtcAttestStore.SaveWithdrawal(Row($"r{i}", i % 2 == 0 ? "vault:1" : "vault:2", (VaultWithdrawalState)(i % 3)));
            Assert.DoesNotContain(IndexNames(), name => name == BtcAttestStore.VaultStateIndexName);
            var before = FullRead("vault:1", VaultWithdrawalState.Pinned);
            Assert.NotEmpty(before);
            Assert.Equal(before, Indexed("vault:1", VaultWithdrawalState.Pinned)); // still right without it

            DbContext.CloseDB();
            DbContext.Initialize();

            Assert.Contains(IndexNames(), name => name == BtcAttestStore.VaultStateIndexName);
            Assert.Equal(before, Indexed("vault:1", VaultWithdrawalState.Pinned));
            Assert.Equal(FullRead("VAULT:2", VaultWithdrawalState.Waiting), Indexed("VAULT:2", VaultWithdrawalState.Waiting));
        }

        private static List<string> IndexNames() =>
            BtcAttestStore.Db.GetCollection("$indexes").Find(LiteDB.Query.EQ("collection", BtcAttestStore.Withdrawals)).Select(d => d["name"].AsString).ToList();
    }

    /// <summary>
    /// Three thousand requests waiting on a vault whose coin a withdrawal holds: the block that retries them all is
    /// applied without reading the vault's rows once per request. Applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VaultWithdrawalsByState_WaitingRequestsTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;

        public VaultWithdrawalsByState_WaitingRequestsTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
        }

        public override void Dispose()
        {
            Globals.BTCNetwork = _priorNetwork;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private VfxTx Request(string vault, decimal amount, V from, int feeRate) =>
            SignedBy(from, from.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = feeRate, UniqueId = Guid.NewGuid().ToString("N") });

        private async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        [Fact]
        public async Task ThreeThousandWaitingRequests_AreRetriedWithoutReadingTheVaultsRowsForEachOne()
        {
            // A vault with one coin, held by the holder's withdrawal; the minter's request for more than is free waits.
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            var deposit = Pay(vault.Script, 150_000);
            var height = _btc.Mine(deposit);
            _btc.MineEmpty(BtcAttestRules.Depth);
            var headers = _btc.HeaderHexes(BtcAttestStore.GetMeta()!.TipHeight + 1, height + BtcAttestRules.Depth);
            var report = Signed(Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(height), height, headers, BtcAttestRules.EpochOf(Tip + 1))));
            var (reported, whyNot) = await Verify(report, Tip + 1);
            Assert.True(reported, whyNot);
            await Apply(Tip + 1, report);
            await Mine(SignedBy(Minter, _holder.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault.Uid, FromAddress = Minter.Address, ToAddress = _holder.Address, Amount = 0.0001M }));
            var held = await Mine(Request(vault.Uid, 0.0001M, _holder, feeRate: 1));
            Assert.Equal(VaultWithdrawalState.Pinned, BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(held.Hash, vault.Uid))!.State);
            var waits = await Mine(Request(vault.Uid, 0.001M, Minter, feeRate: 10));
            var waiting = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(waits.Hash, vault.Uid))!;
            Assert.Equal(VaultWithdrawalState.Waiting, waiting.State);

            // The same request 2,999 more times, and as many closed rows the vault keeps for good (written straight to
            // the store: a block of requests is not what is measured).
            const int requests = 3_000;
            var rows = BtcAttestStore.Db.GetCollection<VaultWithdrawalRow>(BtcAttestStore.Withdrawals);
            var seeded = new List<VaultWithdrawalRow>();
            for (var i = 1; i < requests; i++)
            {
                var copy = rows.FindById(waiting.Ref);
                copy.RequestHash = $"{i:x64}";
                copy.Ref = BtcWithdrawals.RefOf(copy.RequestHash, vault.Uid);
                seeded.Add(copy);
                var closed = rows.FindById(waiting.Ref);
                closed.RequestHash = $"c{i:x63}";
                closed.Ref = BtcWithdrawals.RefOf(closed.RequestHash, vault.Uid);
                closed.State = VaultWithdrawalState.Expired;
                closed.ClosedAtVfxHeight = Tip;
                seeded.Add(closed);
            }
            rows.InsertBulk(seeded);
            Assert.Equal(requests, BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Waiting).Count);

            // The next block every waiting request is tried again in.
            var retry = (Tip / BtcAttestRules.TakeRetryBlocks + 1) * BtcAttestRules.TakeRetryBlocks;
            Assert.True(retry < waiting.RequestVfxHeight + 360, "the requests are still inside their window");
            if (retry - 1 > Tip) await Apply(retry - 1);
            // Measured by what the block allocates, against one read of every row the vault has.
            var before = GC.GetTotalAllocatedBytes(true);
            Assert.Equal(2 * requests, rows.Find(x => x.SmartContractUID == vault.Uid).Count());
            var oneRead = GC.GetTotalAllocatedBytes(true) - before;
            before = GC.GetTotalAllocatedBytes(true);
            await Apply(retry);
            var applied = GC.GetTotalAllocatedBytes(true) - before;

            // Nothing changed for them: the coin is still held by a withdrawal that is moving.
            Assert.Equal(requests, BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Waiting).Count);
            Assert.Equal(VaultWithdrawalState.Pinned, BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(held.Hash, vault.Uid))!.State);
            Assert.Single(BtcAttestStore.WithdrawalsOf(vault.Uid, VaultWithdrawalState.Pinned));
            // Each of the 3,000 retries read all of the vault's rows: 3,000 such reads. Now a retry reads the one pinned row.
            Assert.True(applied < 300 * oneRead, $"the retry block allocated {applied:N0} bytes; one read of the vault's rows allocates {oneRead:N0}");
        }
    }
}
