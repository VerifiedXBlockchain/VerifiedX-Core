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
    /// Coverage for the vBTC V2 multi-contract transfer (Data.Function "TransferVBTCMultiV2()",
    /// gated by <see cref="Globals.V2TransferMultiHeight"/>).
    ///
    /// Fork-safety invariant under test: BEFORE the gate, every node — old binary or new — must
    /// treat a VBTC_V2_TRANSFER by its single-shape top-level fields regardless of Function, so a
    /// pure multi Data (no ContractUID) is deterministically rejected with the single-shape error
    /// and a hybrid Data (multi Function plus valid single fields) validates as a single transfer.
    /// AFTER the gate, the multi branch validates shape + per-input balances with the exact
    /// semantics of N single transfers, and state apply writes the same credit/debit ledger pair
    /// per input contract.
    /// Mutates Globals.LastBlock and DbContext — serialized via the DbContextSequential collection.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VBTCV2MultiTransferTests : IDisposable
    {
        private const long ActivationHeight = 1000;
        private const string Sender = "RSenderMultiVbtcTest0000000000000001";
        private const string ReserveSender = "xRBXReserveMultiVbtcTest000000000001";
        // Valid base58check VFX address (canonical sample from AddressValidationTests) — the
        // recipient must survive VerifyTX's generic to-address validation.
        private const string Recipient = "RAjtW2uDSEDW9mPVkKp2K2AAu4uJD9Zrn7";
        private const string SingleShapeRejection = "ContractUID cannot be null for vBTC V2 transfer.";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly long _priorGateHeight;
        private readonly Block _priorLastBlock;

        public VBTCV2MultiTransferTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcmulti_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorGateHeight = Globals.V2TransferMultiHeight;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();

            // VFX balance for the fee checks ahead of the typed vBTC block.
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = Sender, Balance = 1000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = ReserveSender, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.V2TransferMultiHeight = _priorGateHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void ActivateGate() { Globals.V2TransferMultiHeight = ActivationHeight; Globals.LastBlock = new Block { Height = ActivationHeight + 1 }; }
        private static void GateInert() { Globals.V2TransferMultiHeight = 999_999_999_999L; Globals.LastBlock = new Block { Height = ActivationHeight + 1 }; }

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

        private static string MultiData(decimal total, (string ScUid, decimal Amount)[] inputs, string from = Sender, string to = Recipient)
            => JsonConvert.SerializeObject(new
            {
                Function = VBTCService.MultiTransferFunction,
                FromAddress = from,
                ToAddress = to,
                TotalAmount = total,
                Inputs = inputs.Select(i => new { SCUID = i.ScUid, Amount = i.Amount }).ToArray(),
            });

        private static Transaction BuildTx(string data, string from = Sender, string to = Recipient)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = from,
                ToAddress = to,
                Amount = 0.0M,
                Fee = 0.00001M,
                Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_TRANSFER,
                Data = data,
            };
            tx.Build();
            return tx;
        }

        // ── Gate behavior: pre-gate nodes must treat multi Data by single-shape rules ────────

        [Fact]
        public async Task PreGate_MultiShape_RejectedWithSingleShapeError()
        {
            GateInert();
            SeedContract("multigate:1", "xSomeOwner", ("+", Sender, 0.5M));

            var tx = BuildTx(MultiData(0.5M, new[] { ("multigate:1", 0.5M) }));
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Equal(SingleShapeRejection, message);
        }

        [Fact]
        public async Task BlockPath_UsesBlockHeightNotTip_HistoricalBlockStaysSingleShape()
        {
            // Tip far past the gate, but the block being (re)validated predates it: replay must
            // interpret the TX exactly as nodes did when it was mined — single-shape rejection.
            Globals.V2TransferMultiHeight = ActivationHeight;
            Globals.LastBlock = new Block { Height = ActivationHeight + 5000 };
            SeedContract("multigate:2", "xSomeOwner", ("+", Sender, 0.5M));

            var tx = BuildTx(MultiData(0.5M, new[] { ("multigate:2", 0.5M) }));
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx, blockHeight: ActivationHeight - 1);

            Assert.False(ok);
            Assert.Equal(SingleShapeRejection, message);
        }

        // ── Post-gate shape rules ────────────────────────────────────────────────────────────

        [Fact]
        public async Task PostGate_HybridDataWithContractUID_Rejected()
        {
            ActivateGate();
            var hybrid = JsonConvert.SerializeObject(new
            {
                Function = VBTCService.MultiTransferFunction,
                ContractUID = "multihybrid:1",
                FromAddress = Sender,
                ToAddress = Recipient,
                Amount = 0.1M,
                TotalAmount = 0.1M,
                Inputs = new[] { new { SCUID = "multihybrid:1", Amount = 0.1M } },
            });

            var (ok, message) = await TransactionValidatorService.VerifyTX(BuildTx(hybrid));

            Assert.False(ok);
            Assert.Contains("must not contain a top-level ContractUID", message);
        }

        [Fact]
        public async Task PostGate_EmptyInputs_Rejected()
        {
            ActivateGate();
            var tx = BuildTx(MultiData(0.5M, Array.Empty<(string, decimal)>()));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("Inputs cannot be empty", message);
        }

        [Fact]
        public async Task PostGate_DuplicateContracts_Rejected()
        {
            ActivateGate();
            SeedContract("multidup:1", "xSomeOwner", ("+", Sender, 1.0M));
            var tx = BuildTx(MultiData(0.4M, new[] { ("multidup:1", 0.2M), ("multidup:1", 0.2M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("distinct contracts", message);
        }

        [Fact]
        public async Task PostGate_TotalAmountMismatch_Rejected()
        {
            ActivateGate();
            SeedContract("multisum:1", "xSomeOwner", ("+", Sender, 1.0M));
            SeedContract("multisum:2", "xSomeOwner", ("+", Sender, 1.0M));
            var tx = BuildTx(MultiData(0.9M, new[] { ("multisum:1", 0.3M), ("multisum:2", 0.3M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("TotalAmount must equal the sum", message);
        }

        [Fact]
        public async Task PostGate_TooManyInputs_Rejected()
        {
            ActivateGate();
            var inputs = Enumerable.Range(1, VBTCService.MaxMultiTransferInputs + 1)
                .Select(i => ($"multimax:{i}", 0.01M)).ToArray();
            var tx = BuildTx(MultiData(inputs.Sum(x => x.Item2), inputs));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains($"maximum of {VBTCService.MaxMultiTransferInputs} inputs", message);
        }

        [Fact]
        public async Task PostGate_DataFromAddressMismatch_Rejected()
        {
            ActivateGate();
            var tx = BuildTx(MultiData(0.1M, new[] { ("multibind:1", 0.1M) }, from: "RSomeOtherSpoofedSenderAddress000001"));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("From address in data must match", message);
        }

        [Fact]
        public async Task PostGate_ReserveSender_Rejected()
        {
            ActivateGate();
            SeedContract("multirsrv:1", ReserveSender, ("+", ReserveSender, 1.0M));
            var tx = BuildTx(MultiData(0.1M, new[] { ("multirsrv:1", 0.1M) }, from: ReserveSender), from: ReserveSender);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("Reserve accounts cannot use multi-contract vBTC transfers", message);
        }

        [Fact]
        public async Task PostGate_UnknownContract_Rejected()
        {
            ActivateGate();
            var tx = BuildTx(MultiData(0.1M, new[] { ("multimissing:404", 0.1M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("not found in state trei", message);
        }

        [Fact]
        public async Task PostGate_InsufficientInputBalance_RejectedNamingTheContract()
        {
            ActivateGate();
            SeedContract("multibal:1", "xSomeOwner", ("+", Sender, 0.5M));
            SeedContract("multibal:2", "xSomeOwner", ("+", Sender, 0.1M));
            var tx = BuildTx(MultiData(0.7M, new[] { ("multibal:1", 0.5M), ("multibal:2", 0.2M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("Insufficient vBTC balance for transfer input multibal:2", message);
        }

        /// <summary>A fully valid multi TX (shape + balances) must clear the whole multi branch —
        /// proven by failing at the unrelated final signature check instead of any vBTC error.</summary>
        [Fact]
        public async Task PostGate_ValidMulti_PassesBranch_FailsOnlyAtSignature()
        {
            ActivateGate();
            SeedContract("multiok:1", "xSomeOwner", ("+", Sender, 0.5M));
            SeedContract("multiok:2", "xSomeOwner", ("+", Sender, 0.3M));
            var tx = BuildTx(MultiData(0.6M, new[] { ("multiok:1", 0.4M), ("multiok:2", 0.2M) }));

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Equal("Signature cannot be null.", message);
        }

        // ── Outflow parsing (shared by all overspend guards) ─────────────────────────────────

        [Fact]
        public void Outflows_SingleShape_OneEntry()
        {
            var tx = BuildTx(JsonConvert.SerializeObject(new
            {
                Function = "TransferVBTCV2()",
                ContractUID = "outflow:1",
                FromAddress = Sender,
                ToAddress = Recipient,
                Amount = 0.25M,
            }));

            var outflows = VBTCService.GetVbtcV2TransferOutflows(tx);

            Assert.Single(outflows);
            Assert.Equal(("outflow:1", 0.25M), outflows[0]);
        }

        [Fact]
        public void Outflows_MultiShape_OneEntryPerInput()
        {
            var tx = BuildTx(MultiData(0.6M, new[] { ("outflow:2", 0.4M), ("outflow:3", 0.2M) }));

            var outflows = VBTCService.GetVbtcV2TransferOutflows(tx);

            Assert.Equal(2, outflows.Count);
            Assert.Contains(("outflow:2", 0.4M), outflows);
            Assert.Contains(("outflow:3", 0.2M), outflows);
        }

        /// <summary>A hybrid Data (multi Function plus top-level ContractUID) must parse as the
        /// SINGLE shape — that is how pre-gate nodes validate and apply it, and the guards'
        /// accounting must always match state apply.</summary>
        [Fact]
        public void Outflows_HybridShape_ParsesAsSingle()
        {
            var tx = BuildTx(JsonConvert.SerializeObject(new
            {
                Function = VBTCService.MultiTransferFunction,
                ContractUID = "outflow:4",
                Amount = 0.1M,
                TotalAmount = 0.5M,
                Inputs = new[] { new { SCUID = "outflow:5", Amount = 0.5M } },
            }));

            var outflows = VBTCService.GetVbtcV2TransferOutflows(tx);

            Assert.Single(outflows);
            Assert.Equal(("outflow:4", 0.1M), outflows[0]);
        }

        [Fact]
        public void Outflows_GarbageOrWrongType_Empty()
        {
            Assert.Empty(VBTCService.GetVbtcV2TransferOutflows(BuildTx("not json at all")));
            Assert.Empty(VBTCService.GetVbtcV2TransferOutflows(new Transaction
            {
                TransactionType = TransactionType.TX,
                Data = MultiData(0.1M, new[] { ("outflow:6", 0.1M) }),
            }));
        }

        // ── State apply ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void StateApply_WritesCreditDebitPairPerInputContract()
        {
            SeedContract("multiapply:1", "xSomeOwner", ("+", Sender, 0.5M));
            SeedContract("multiapply:2", "xSomeOwner", ("+", Sender, 0.3M));
            var tx = BuildTx(MultiData(0.6M, new[] { ("multiapply:1", 0.4M), ("multiapply:2", 0.2M) }));

            var applyMulti = typeof(StateData).GetMethod("TransferVBTCV2Multi", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(applyMulti);
            applyMulti!.Invoke(null, new object[] { tx });

            foreach (var (scUid, amount, priorBalance) in new[] { ("multiapply:1", 0.4M, 0.5M), ("multiapply:2", 0.2M, 0.3M) })
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUid);
                Assert.NotNull(scState);
                var rows = scState!.SCStateTreiTokenizationTXes!;
                Assert.Equal(3, rows.Count); // 1 seed credit + credit/debit pair

                // Credit row goes to tx.ToAddress (signed, authoritative), debit row from tx.FromAddress.
                Assert.Contains(rows, r => r.FromAddress == "+" && r.ToAddress == Recipient && r.Amount == amount);
                Assert.Contains(rows, r => r.FromAddress == Sender && r.ToAddress == "-" && r.Amount == -amount);

                // Non-owner sender balance math (received + sent, debit rows negative) sees the spend.
                var senderRows = rows.Where(r => r.FromAddress == Sender || r.ToAddress == Sender).ToList();
                var received = senderRows.Where(r => r.ToAddress == Sender).Sum(r => r.Amount);
                var sent = senderRows.Where(r => r.FromAddress == Sender).Sum(r => r.Amount);
                Assert.Equal(priorBalance - amount, received + sent);

                // Recipient sees the credit.
                var recipientBalance = rows.Where(r => r.ToAddress == Recipient).Sum(r => r.Amount);
                Assert.Equal(amount, recipientBalance);
            }
        }

        // ── Joint single+multi overspend accounting ──────────────────────────────────────────

        [Fact]
        public async Task DoubleSpendCheck_SinglePlusMultiJointOverspend_Detected()
        {
            ActivateGate();
            SeedContract("multiguard:1", "xSomeOwner", ("+", Sender, 0.5M));
            SeedContract("multiguard:2", "xSomeOwner", ("+", Sender, 1.0M));

            // A single-shape transfer of 0.4 from multiguard:1 already sits in the mempool.
            var pendingSingle = BuildTx(JsonConvert.SerializeObject(new
            {
                Function = "TransferVBTCV2()",
                ContractUID = "multiguard:1",
                FromAddress = Sender,
                ToAddress = Recipient,
                Amount = 0.4M,
            }));
            TransactionData.GetPool().InsertSafe(pendingSingle);

            // Incoming multi drawing 0.2 more from multiguard:1 — jointly 0.6 > 0.5 held.
            var incomingMulti = BuildTx(MultiData(0.5M, new[] { ("multiguard:1", 0.2M), ("multiguard:2", 0.3M) }));
            Assert.True(await TransactionData.DoubleSpendReplayCheck(incomingMulti));

            // Same multi restricted to what multiguard:1 has left clears the guard.
            var okMulti = BuildTx(MultiData(0.4M, new[] { ("multiguard:1", 0.1M), ("multiguard:2", 0.3M) }));
            Assert.False(await TransactionData.DoubleSpendReplayCheck(okMulti));
        }

        // ── Live local-record prune on full consumption ──────────────────────────────────────

        [Fact]
        public void Prune_RemovesLocalRecords_WhenNoLocalClaimRemains()
        {
            const string scUid = "multiprune:1";
            // Chain says someone else owns it and nobody local holds a balance.
            SeedContract(scUid, "RSomeRemoteOwnerAddress0000000000001", ("+", "RSomeRemoteHolder0000000000000000001", 0.5M));
            VBTCContractV2.GetDb().InsertSafe(new VBTCContractV2 { SmartContractUID = scUid, OwnerAddress = "RSomeRemoteOwnerAddress0000000000001", DepositAddress = "tb1qtestdeposit" });

            Assert.True(VBTCContractV2.PruneLocalRecordsIfNoClaim(scUid));
            Assert.Null(VBTCContractV2.GetContract(scUid));
        }

        [Fact]
        public void Prune_KeepsRecords_WhenLocalAddressStillHoldsBalance()
        {
            const string scUid = "multiprune:2";
            const string localHolder = "RLocalHolderMultiVbtcTest00000000001";
            AccountData.GetAccounts().InsertSafe(new Account { Address = localHolder, Balance = 0M });
            SeedContract(scUid, "RSomeRemoteOwnerAddress0000000000002", ("+", localHolder, 0.25M));
            VBTCContractV2.GetDb().InsertSafe(new VBTCContractV2 { SmartContractUID = scUid, OwnerAddress = "RSomeRemoteOwnerAddress0000000000002", DepositAddress = "tb1qtestdeposit2" });

            Assert.False(VBTCContractV2.PruneLocalRecordsIfNoClaim(scUid));
            Assert.NotNull(VBTCContractV2.GetContract(scUid));
        }
    }
}
