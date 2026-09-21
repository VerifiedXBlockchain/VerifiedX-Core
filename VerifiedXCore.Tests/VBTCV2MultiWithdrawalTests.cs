using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Coverage for the vBTC V2 multi-contract WITHDRAWAL request (Data.Function
    /// "VBTCWithdrawalRequestMultiV2()"). Dispatch keys on Function alone — no height gate, by
    /// decision — so there is no pre/post-activation split to test.
    ///
    /// Under test: the multi branch validates shape + per-contract gates + per-contract balances
    /// with the exact semantics of N single requests, and state apply mints one withdrawal row
    /// per input contract — all sharing the REQUEST tx hash, which is why per-contract readers
    /// must look rows up by (hash, contract).
    /// Mutates Globals.LastBlock and DbContext — serialized via the DbContextSequential collection.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCV2MultiWithdrawalTests : IDisposable
    {
        private const long TestHeight = 1000;
        // Valid base58check VFX address (canonical sample from AddressValidationTests) — the
        // request is a self-transaction, so it must survive VerifyTX's generic to-address check.
        private const string Requestor = "RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7";
        private const string ReserveRequestor = "xRBXReserveMultiWithdrawTest00000001";
        private const string BtcDestination = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorEscrowHeight;
        private readonly Block _priorLastBlock;

        public VBTCV2MultiWithdrawalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcmultiwd_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();

            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Requestor, Balance = 1000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = ReserveRequestor, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.WithdrawalEscrowHeight = _priorEscrowHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void SetTip() => Globals.LastBlock = new Block { Height = TestHeight + 1 };

        private static void SeedContract(string scUid, string owner, params (string From, string To, decimal Amount)[] rows)
        {
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = scUid,
                OwnerAddress = owner,
                SCStateTreiTokenizationTXes = rows
                    .Select(r => new SmartContractStateTreiTokenizationTX { FromAddress = r.From, ToAddress = r.To, Amount = r.Amount })
                    .ToList(),
            });
        }

        private static string MultiData(decimal total, (string ScUid, decimal Amount)[] inputs,
            string from = Requestor, int feeRate = 10)
            => JsonConvert.SerializeObject(new
            {
                Function = VBTCService.MultiWithdrawalFunction,
                RequestorAddress = from,
                BTCAddress = BtcDestination,
                TotalAmount = total,
                FeeRate = feeRate,
                Inputs = inputs.Select(i => new { SCUID = i.ScUid, Amount = i.Amount }).ToArray(),
            });

        private static Transaction BuildTx(string data, string from = Requestor, long height = 0, string? to = null)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = from,
                ToAddress = to ?? from,
                Amount = 0.0M,
                Fee = 0.00001M,
                Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = data,
                Height = height,
            };
            tx.Build();
            return tx;
        }

        // ── Shape rules ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Multi_HybridDataWithContractUID_Rejected()
        {
            SetTip();
            var hybrid = JsonConvert.SerializeObject(new
            {
                Function = VBTCService.MultiWithdrawalFunction,
                ContractUID = "wdhybrid:1",
                BTCAddress = BtcDestination,
                TotalAmount = 0.1M,
                FeeRate = 10,
                Inputs = new[] { new { SCUID = "wdhybrid:1", Amount = 0.1M } },
            });

            var (ok, message) = await TransactionValidatorService.VerifyTX(BuildTx(hybrid));

            Assert.False(ok);
            Assert.Contains("must not contain a top-level ContractUID", message);
        }

        [Fact]
        public async Task Multi_HybridDataWithAmount_Rejected()
        {
            SetTip();
            var hybrid = JsonConvert.SerializeObject(new
            {
                Function = VBTCService.MultiWithdrawalFunction,
                BTCAddress = BtcDestination,
                Amount = 0.1M,
                TotalAmount = 0.1M,
                FeeRate = 10,
                Inputs = new[] { new { SCUID = "wdhybrid:2", Amount = 0.1M } },
            });

            var (ok, message) = await TransactionValidatorService.VerifyTX(BuildTx(hybrid));

            Assert.False(ok);
            Assert.Contains("must not contain a top-level Amount", message);
        }

        [Fact]
        public async Task Multi_EmptyInputs_Rejected()
        {
            SetTip();
            var tx = BuildTx(MultiData(0.5M, Array.Empty<(string, decimal)>()));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("Inputs cannot be empty", message);
        }

        [Fact]
        public async Task Multi_DuplicateContracts_Rejected()
        {
            SetTip();
            SeedContract("wddup:1", "xSomeOwner", ("+", Requestor, 1.0M));
            var tx = BuildTx(MultiData(0.4M, new[] { ("wddup:1", 0.2M), ("wddup:1", 0.2M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("distinct contracts", message);
        }

        [Fact]
        public async Task Multi_TotalAmountMismatch_Rejected()
        {
            SetTip();
            SeedContract("wdsum:1", "xSomeOwner", ("+", Requestor, 1.0M));
            SeedContract("wdsum:2", "xSomeOwner", ("+", Requestor, 1.0M));
            var tx = BuildTx(MultiData(0.9M, new[] { ("wdsum:1", 0.3M), ("wdsum:2", 0.3M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("TotalAmount must equal the sum", message);
        }

        [Fact]
        public async Task Multi_TooManyInputs_Rejected()
        {
            SetTip();
            var inputs = Enumerable.Range(1, VBTCService.MaxMultiWithdrawalInputs + 1)
                .Select(i => ($"wdmax:{i}", 0.01M)).ToArray();
            var tx = BuildTx(MultiData(inputs.Sum(x => x.Item2), inputs));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains($"maximum of {VBTCService.MaxMultiWithdrawalInputs} inputs", message);
        }

        [Fact]
        public async Task Multi_ReserveRequestor_Rejected()
        {
            SetTip();
            SeedContract("wdrsrv:1", "xSomeOwner", ("+", ReserveRequestor, 1.0M));
            var tx = BuildTx(MultiData(0.1M, new[] { ("wdrsrv:1", 0.1M) }, from: ReserveRequestor),
                from: ReserveRequestor, to: Requestor);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("Reserve accounts cannot request BTC withdrawals", message);
        }

        [Fact]
        public async Task Multi_UnknownContract_Rejected()
        {
            SetTip();
            var tx = BuildTx(MultiData(0.1M, new[] { ("wdmissing:404", 0.1M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("not found in state trei", message);
        }

        [Fact]
        public async Task Multi_InsufficientInputBalance_RejectedNamingTheContract()
        {
            SetTip();
            SeedContract("wdbal:1", "xSomeOwner", ("+", Requestor, 0.5M));
            SeedContract("wdbal:2", "xSomeOwner", ("+", Requestor, 0.1M));
            var tx = BuildTx(MultiData(0.7M, new[] { ("wdbal:1", 0.5M), ("wdbal:2", 0.2M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("Insufficient vBTC balance for withdrawal input wdbal:2", message);
        }

        /// <summary>The per-CONTRACT active-withdrawal gate must apply to every input, so a multi
        /// request cannot re-open a vault that already has one in flight.</summary>
        [Fact]
        public async Task Multi_InputContractWithActiveRequest_Rejected()
        {
            SetTip();
            SeedContract("wdactive:1", "xSomeOwner", ("+", Requestor, 1.0M));
            SeedContract("wdactive:2", "xSomeOwner", ("+", Requestor, 1.0M));

            VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = "RSomeoneElse000000000000000000000001",
                SmartContractUID = "wdactive:2",
                Amount = 0.1M,
                BTCDestination = BtcDestination,
                FeeRate = 10,
                OriginalUniqueId = "prior-request",
                TransactionHash = "priorhash",
                Status = VBTCWithdrawalStatus.Requested,
                IsCompleted = false,
                RequestBlockHeight = TestHeight + 1,
            });

            var tx = BuildTx(MultiData(0.4M, new[] { ("wdactive:1", 0.2M), ("wdactive:2", 0.2M) }));
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("A withdrawal is already in progress for contract wdactive:2", message);
        }

        /// <summary>A fully valid multi TX (shape + gates + balances) must clear the whole multi
        /// branch — proven by failing at the unrelated final signature check.</summary>
        [Fact]
        public async Task Multi_ValidMulti_PassesBranch_FailsOnlyAtSignature()
        {
            SetTip();
            SeedContract("wdok:1", "xSomeOwner", ("+", Requestor, 0.5M));
            SeedContract("wdok:2", "xSomeOwner", ("+", Requestor, 0.3M));
            var tx = BuildTx(MultiData(0.6M, new[] { ("wdok:1", 0.4M), ("wdok:2", 0.2M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Equal("Signature cannot be null.", message);
        }

        // ── Outflow parsing (drives the per-contract mempool + intra-block guards) ───────────

        [Fact]
        public void Outflows_SingleShape_OneEntry()
        {
            var tx = BuildTx(JsonConvert.SerializeObject(new
            {
                Function = "VBTCWithdrawalRequest()",
                ContractUID = "wdflow:1",
                BTCAddress = BtcDestination,
                Amount = 0.25M,
                FeeRate = 10,
            }));

            var outflows = VBTCService.GetVbtcV2WithdrawalOutflows(tx);

            Assert.Single(outflows);
            Assert.Equal(("wdflow:1", 0.25M), outflows[0]);
        }

        [Fact]
        public void Outflows_MultiShape_OneEntryPerInput()
        {
            var tx = BuildTx(MultiData(0.6M, new[] { ("wdflow:2", 0.4M), ("wdflow:3", 0.2M) }));

            var outflows = VBTCService.GetVbtcV2WithdrawalOutflows(tx);

            Assert.Equal(2, outflows.Count);
            Assert.Contains(("wdflow:2", 0.4M), outflows);
            Assert.Contains(("wdflow:3", 0.2M), outflows);
        }

        /// <summary>A hybrid Data (multi Function plus top-level ContractUID) must parse as the
        /// SINGLE shape — that is how pre-gate nodes validate and apply it, and the guards'
        /// accounting must always match state apply.</summary>
        [Fact]
        public void Outflows_HybridShape_ParsesAsSingle()
        {
            var tx = BuildTx(JsonConvert.SerializeObject(new
            {
                Function = VBTCService.MultiWithdrawalFunction,
                ContractUID = "wdflow:4",
                Amount = 0.1M,
                TotalAmount = 0.5M,
                Inputs = new[] { new { SCUID = "wdflow:5", Amount = 0.5M } },
            }));

            var outflows = VBTCService.GetVbtcV2WithdrawalOutflows(tx);

            Assert.Single(outflows);
            Assert.Equal(("wdflow:4", 0.1M), outflows[0]);
        }

        [Fact]
        public void Outflows_GarbageOrWrongType_Empty()
        {
            Assert.Empty(VBTCService.GetVbtcV2WithdrawalOutflows(BuildTx("not json at all")));
            Assert.Empty(VBTCService.GetVbtcV2WithdrawalOutflows(new Transaction
            {
                TransactionType = TransactionType.TX,
                Data = MultiData(0.1M, new[] { ("wdflow:6", 0.1M) }),
            }));
        }

        // ── State apply ──────────────────────────────────────────────────────────────────────

        private static void ApplyMulti(Transaction tx)
        {
            var apply = typeof(StateData).GetMethod("RequestVBTCV2WithdrawalMulti", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(apply);
            apply!.Invoke(null, new object[] { tx });
        }

        [Fact]
        public void StateApply_MintsOneRowPerInputContract_SharingTheRequestHash()
        {
            Globals.WithdrawalEscrowHeight = 999_999_999_999L; // escrow off: rows only
            SeedContract("wdapply:1", "xSomeOwner", ("+", Requestor, 0.5M));
            SeedContract("wdapply:2", "xSomeOwner", ("+", Requestor, 0.3M));

            var tx = BuildTx(MultiData(0.6M, new[] { ("wdapply:1", 0.4M), ("wdapply:2", 0.2M) }), height: TestHeight + 1);
            ApplyMulti(tx);

            var rows = VBTCWithdrawalRequest.GetAllByTransactionHash(tx.Hash);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal(Requestor, r.RequestorAddress);
                Assert.Equal(BtcDestination, r.BTCDestination);
                Assert.Equal(tx.Hash, r.TransactionHash);
                Assert.Equal(TestHeight + 1, r.RequestBlockHeight);
                Assert.False(r.IsCompleted);
                Assert.Equal(VBTCWithdrawalStatus.Requested, r.Status);
            });

            // Per-contract lookup must return that contract's own row, never a sibling.
            Assert.Equal(0.4M, VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, "wdapply:1")!.Amount);
            Assert.Equal(0.2M, VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, "wdapply:2")!.Amount);
        }

        [Fact]
        public void StateApply_EscrowDebitsEveryInputContract()
        {
            Globals.WithdrawalEscrowHeight = TestHeight;
            SeedContract("wdescrow:1", "xSomeOwner", ("+", Requestor, 0.5M));
            SeedContract("wdescrow:2", "xSomeOwner", ("+", Requestor, 0.3M));

            var tx = BuildTx(MultiData(0.6M, new[] { ("wdescrow:1", 0.4M), ("wdescrow:2", 0.2M) }), height: TestHeight + 1);
            ApplyMulti(tx);

            foreach (var (scUid, amount, priorBalance) in new[] { ("wdescrow:1", 0.4M, 0.5M), ("wdescrow:2", 0.2M, 0.3M) })
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUid);
                Assert.NotNull(scState);
                var rows = scState!.SCStateTreiTokenizationTXes!;

                // Escrow writes the same debit row shape a single withdrawal request writes.
                Assert.Contains(rows, r => r.FromAddress == Requestor && r.ToAddress == "-" && r.Amount == -amount);

                var holderRows = rows.Where(r => r.FromAddress == Requestor || r.ToAddress == Requestor).ToList();
                var received = holderRows.Where(r => r.ToAddress == Requestor).Sum(r => r.Amount);
                var sent = holderRows.Where(r => r.FromAddress == Requestor).Sum(r => r.Amount);
                Assert.Equal(priorBalance - amount, received + sent);
            }
        }

        /// <summary>Rows of one multi request share a TX hash, so Save() must key on
        /// (hash, contract) — keying on the hash alone silently overwrote the first row.</summary>
        [Fact]
        public void Save_RowsSharingATxHash_DoNotOverwriteEachOther()
        {
            Globals.WithdrawalEscrowHeight = 999_999_999_999L;
            SeedContract("wdsave:1", "xSomeOwner", ("+", Requestor, 1.0M));
            SeedContract("wdsave:2", "xSomeOwner", ("+", Requestor, 1.0M));

            var tx = BuildTx(MultiData(0.3M, new[] { ("wdsave:1", 0.1M), ("wdsave:2", 0.2M) }), height: TestHeight + 1);
            ApplyMulti(tx);

            // Completing one share must not touch the other.
            var first = VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, "wdsave:1")!;
            first.IsCompleted = true;
            first.Status = VBTCWithdrawalStatus.Completed;
            first.BTCTxHash = new string('a', 64);
            Assert.True(VBTCWithdrawalRequest.Save(first, update: true));

            Assert.True(VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, "wdsave:1")!.IsCompleted);
            Assert.False(VBTCWithdrawalRequest.GetByTransactionHash(tx.Hash, "wdsave:2")!.IsCompleted);
            Assert.Equal(2, VBTCWithdrawalRequest.GetAllByTransactionHash(tx.Hash).Count);
        }

        /// <summary>Each share's contract gets its own active-request gate entry, so the whole
        /// multi request serializes every vault it touched.</summary>
        [Fact]
        public void StateApply_EveryInputContractBecomesActive()
        {
            Globals.WithdrawalEscrowHeight = 999_999_999_999L;
            SeedContract("wdgateall:1", "xSomeOwner", ("+", Requestor, 1.0M));
            SeedContract("wdgateall:2", "xSomeOwner", ("+", Requestor, 1.0M));

            var tx = BuildTx(MultiData(0.3M, new[] { ("wdgateall:1", 0.1M), ("wdgateall:2", 0.2M) }), height: TestHeight + 1);
            ApplyMulti(tx);

            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest("wdgateall:1", TestHeight + 2, includeLocalOnlyRows: false));
            Assert.True(VBTCWithdrawalRequest.HasActiveContractRequest("wdgateall:2", TestHeight + 2, includeLocalOnlyRows: false));
        }

        // ── Store rebuild / chain recovery ───────────────────────────────────────────────────

        /// <summary>A node whose local store lost the rows must rebuild ALL of them from the mined
        /// REQUEST tx — otherwise the missing contract's COMPLETE skips its consensus burn row and
        /// forks that node's state trei.</summary>
        [Fact]
        public void RebuildRows_MultiRequest_ProducesOneRowPerInput()
        {
            var tx = BuildTx(MultiData(0.6M, new[] { ("wdrebuild:1", 0.4M), ("wdrebuild:2", 0.2M) }), height: TestHeight + 1);

            Assert.True(VBTCWithdrawalStoreRebuildService.TryBuildRequestRows(tx, out var rows));
            Assert.NotNull(rows);
            Assert.Equal(2, rows!.Count);
            Assert.Contains(rows, r => r.SmartContractUID == "wdrebuild:1" && r.Amount == 0.4M);
            Assert.Contains(rows, r => r.SmartContractUID == "wdrebuild:2" && r.Amount == 0.2M);
            Assert.All(rows, r => Assert.Equal(tx.Hash, r.TransactionHash));
        }
    }
}
