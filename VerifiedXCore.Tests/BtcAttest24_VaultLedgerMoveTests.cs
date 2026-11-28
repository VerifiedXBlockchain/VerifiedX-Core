using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.VaultLedgerHeight a contract's vBTC ledger is one entry per holder in a collection of its own, not a
    /// list of rows in the contract's state document. At the height every ledger moves, keeping every balance; from then on
    /// a block's transfers write the entries they change, with their contract, in one write; a balance is one read whatever
    /// the number of holders; and no ledger can outgrow a document.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest24_VaultLedgerMoveTests : BtcAttestTestBase
    {
        private readonly long _priorHeight = Globals.VaultLedgerHeight;
        private readonly V _a = NewKey(), _b = NewKey(), _c = NewKey();

        public BtcAttest24_VaultLedgerMoveTests() : base(realMainnetRules: false)
        {
            foreach (var v in new[] { _a, _b, _c })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = v.Address, Balance = 100M, Nonce = 0 });
        }

        public override void Dispose()
        {
            Globals.VaultLedgerHeight = _priorHeight;
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

        private VfxTx Transfer(string vault, decimal amount, V from, V to) =>
            SignedBy(from, to.Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = from.Address, ToAddress = to.Address, Amount = amount });

        private static SmartContractStateTrei State(string vault) => SmartContractStateTrei.GetSmartContractState(vault)!;

        /// <summary>Adds rows to a vault's ledger as a block's apply would, and saves it.</summary>
        private static void AddRows(string vault, params SmartContractStateTreiTokenizationTX[] rows)
        {
            var state = State(vault);
            VaultLedgerStore.Append(state, rows);
            SmartContractStateTrei.UpdateSmartContract(state);
        }

        private static SmartContractStateTreiTokenizationTX Credit(string to, decimal amount) => new() { FromAddress = "+", ToAddress = to, Amount = amount };
        private static SmartContractStateTreiTokenizationTX Debit(string from, decimal amount) => new() { FromAddress = from, ToAddress = "-", Amount = -amount };

        private async Task MoveAtTheNextBlock()
        {
            Globals.VaultLedgerHeight = Tip + 1;
            await Apply(Tip + 1);
        }

        [Fact]
        public async Task TheMove_KeepsEveryBalance_AndLeavesNoRowsInTheDocument()
        {
            await Start();
            var vault = (await CreateVault(H1 + 1)).Uid;
            await Apply(Tip + 1, Transfer(vault, 0.01M, Minter, _a));
            await Apply(Tip + 1, Transfer(vault, 0.004M, _a, _b));
            // A former owner below zero, a holder back at zero, and two addresses that differ only in letter case.
            AddRows(vault, Debit("xFormerOwner000000000000000000001", 0.002M), Credit("xZero00000000000000000000000000001", 0.001M),
                Debit("xZero00000000000000000000000000001", 0.001M), Credit("xCaseAbc00000000000000000000000001", 0.003M),
                Credit("xcaseabc00000000000000000000000001", 0.0005M));
            var addresses = new[] { Minter.Address, _a.Address, _b.Address, _c.Address, "xFormerOwner000000000000000000001",
                "xZero00000000000000000000000000001", "xCaseAbc00000000000000000000000001", "xcaseabc00000000000000000000000001" };
            var before = State(vault);
            var balances = addresses.ToDictionary(a => a, a => VaultLedgerStore.Balance(before, a));
            var negative = VaultLedgerStore.NegativePositionsExcept(before, before.OwnerAddress);
            var owner = VBTCService.GetOwnerLedgerBalance(before, before.OwnerAddress, Tip + 1);
            Assert.True(VaultLedgerStore.HasRows(before));

            await MoveAtTheNextBlock();

            var after = State(vault);
            Assert.True(after.LedgerMoved);
            Assert.Null(after.SCStateTreiTokenizationTXes);                    // the rows have left the document
            Assert.Equal(Tip, WorldTrei.GetWorldTrei().FindOne(x => true)!.VaultLedgerMovedHeight);
            foreach (var a in addresses)
                Assert.Equal(balances[a], VaultLedgerStore.Balance(after, a));
            Assert.Equal(0.003M, VaultLedgerStore.Balance(after, "xCaseAbc00000000000000000000000001"));   // case kept apart
            Assert.Equal(0.0005M, VaultLedgerStore.Balance(after, "xcaseabc00000000000000000000000001"));
            Assert.Equal(negative, VaultLedgerStore.NegativePositionsExcept(after, after.OwnerAddress));
            Assert.Equal(owner, VBTCService.GetOwnerLedgerBalance(after, after.OwnerAddress, Tip + 1));
            Assert.True(VaultLedgerStore.HasLedger(after) && VaultLedgerStore.HasRows(after));
            // The holder limit counts holders with a balance: the one back at zero, claimed by nothing, is not named.
            Assert.True(VaultLedgerStore.Names(after, _b.Address));
            Assert.False(VaultLedgerStore.Names(after, "xZero00000000000000000000000000001"));
            Assert.Equal(balances.Count(kv => kv.Value != 0M), VaultLedgerStore.HolderCount(after));

            // The move is done once: the next block moves nothing and changes no balance.
            Assert.Equal(0, VaultLedgerStore.MoveAll(Tip + 1));
            await Apply(Tip + 1);
            foreach (var a in addresses)
                Assert.Equal(balances[a], VaultLedgerStore.Balance(State(vault), a));
        }

        [Fact]
        public async Task TheMove_OfAUidWithTwoDocuments_KeepsTheLedgerEveryReaderReads()
        {
            await Start();
            var vault = (await CreateVault(H1 + 1)).Uid;
            await Apply(Tip + 1, Transfer(vault, 0.01M, Minter, _a));
            var read = State(vault);
            var addresses = new[] { Minter.Address, _a.Address, _c.Address };
            var balances = addresses.ToDictionary(a => a, a => VaultLedgerStore.Balance(read, a));
            // A younger document under the same UID with a different ledger. Readers read the oldest; the younger one is never read.
            var younger = new SmartContractStateTrei
            {
                SmartContractUID = vault, ContractData = read.ContractData, MinterAddress = read.MinterAddress, OwnerAddress = read.OwnerAddress,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX> { Credit(_a.Address, 1M), Credit(_c.Address, 5M) },
            };
            SmartContractStateTrei.GetSCST().Insert(younger);
            Assert.Equal(read.Id, State(vault).Id);

            await MoveAtTheNextBlock();

            var after = State(vault);
            Assert.Equal(read.Id, after.Id);
            Assert.True(after.LedgerMoved);
            foreach (var a in addresses)
                Assert.Equal(balances[a], VaultLedgerStore.Balance(after, a));   // the younger ledger wrote nothing over it
            Assert.Equal(0M, VaultLedgerStore.Balance(after, _c.Address));
            Assert.Equal(balances.Count(kv => kv.Value != 0M), VaultLedgerStore.HolderCount(after));
            var left = SmartContractStateTrei.GetSCST().FindById(younger.Id);
            Assert.NotEqual(true, left.LedgerMoved);                            // left as it was, unread as before
            Assert.Equal(2, left.SCStateTreiTokenizationTXes!.Count);
        }

        [Fact]
        public async Task AfterTheMove_ABlocksTransfersWriteTheEntries_AndAreJudgedOnThem()
        {
            await Start();
            var vault = (await CreateVault(H1 + 1)).Uid;
            await Apply(Tip + 1, Transfer(vault, 0.01M, Minter, _a));
            await MoveAtTheNextBlock();

            await Apply(Tip + 1, Transfer(vault, 0.004M, _a, _b));
            var state = State(vault);
            Assert.Null(state.SCStateTreiTokenizationTXes);
            Assert.Equal(0.006M, VaultLedgerStore.Balance(state, _a.Address));
            Assert.Equal(0.004M, VaultLedgerStore.Balance(state, _b.Address));
            Assert.Equal(0.006M, VaultLedgerStore.Entries().FindById(VaultLedgerStore.EntryId(vault, _a.Address))!.Balance);

            // Validation reads the entries: the holder can send what it holds and no more.
            Assert.True((await Verify(Transfer(vault, 0.004M, _b, _c), Tip + 1)).Ok);
            var (ok, why) = await Verify(Transfer(vault, 0.0041M, _b, _c), Tip + 1);
            Assert.False(ok);
            Assert.Contains("Insufficient", why);

            // A contract whose ledger began after the move starts in the collection.
            var later = (await CreateVault(Tip + 1)).Uid;
            await Apply(Tip + 1, Transfer(later, 0.002M, Minter, _c));
            var laterState = State(later);
            Assert.True(laterState.LedgerMoved);
            Assert.Null(laterState.SCStateTreiTokenizationTXes);
            Assert.Equal(0.002M, VaultLedgerStore.Balance(laterState, _c.Address));
        }

        [Fact]
        public async Task AfterTheMove_ABalanceIsOneRead_WhateverTheNumberOfHolders()
        {
            await Start();
            var vault = (await CreateVault(H1 + 1)).Uid;
            await Apply(Tip + 1, Transfer(vault, 0.01M, Minter, _a));
            AddRows(vault, Enumerable.Range(0, 20_000).Select(i => Credit($"xHolder{i:D27}", 0.00001M)).ToArray());
            await MoveAtTheNextBlock();

            static long Allocated(Action run)
            {
                var start = GC.GetAllocatedBytesForCurrentThread();
                run();
                return GC.GetAllocatedBytesForCurrentThread() - start;
            }
            var state = State(vault);
            var read = Allocated(() => VaultLedgerStore.Balance(state, _a.Address));
            var load = Allocated(() => SmartContractStateTrei.GetSmartContractState(vault));
            Assert.True(read < 200_000, $"one balance allocated {read:N0} bytes");
            Assert.True(load < 200_000, $"the contract's state allocated {load:N0} bytes: its rows are no longer in it");
            Assert.Equal(20_002, VaultLedgerStore.HolderCount(state)); // the 20,000, the holder _a, and the owner (below zero after paying _a)
        }

        [Fact]
        public async Task AContractAndItsUnsavedLedgerRows_AreWrittenTogetherOrNotAtAll()
        {
            await Start();
            var vault = (await CreateVault(H1 + 1)).Uid;
            await Apply(Tip + 1, Transfer(vault, 0.01M, Minter, _a));
            await MoveAtTheNextBlock();

            var state = State(vault);
            var ghost = new SmartContractStateTrei
            {
                Id = state.Id + 1_000_000, SmartContractUID = state.SmartContractUID, LedgerMoved = true, LedgerHolders = state.LedgerHolders,
                LedgerNegative = state.LedgerNegative, OwnerAddress = state.OwnerAddress,
            };
            VaultLedgerStore.Append(ghost, Credit(_c.Address, 0.5M));
            Assert.ThrowsAny<Exception>(() => SmartContractStateTrei.UpdateSmartContract(ghost)); // no such document: not written
            Assert.Null(VaultLedgerStore.Entries().FindById(VaultLedgerStore.EntryId(vault, _c.Address)));
            Assert.Equal(0M, VaultLedgerStore.Balance(State(vault), _c.Address));
        }

        /// <summary>
        /// The snapshot slots hold the entries, full and by their write height: a slot rolled forward over a transfer made
        /// after the move, restored onto a state whose entries were lost, gives every holder back its balance. Without the
        /// entries in the slot a restored node would read every holder at zero.
        /// </summary>
        [Fact]
        public async Task ASnapshotTakenAfterTheMove_RestoresTheEntries()
        {
            await Start();
            var vault = (await CreateVault(H1 + 1)).Uid;
            await Apply(Tip + 1, Transfer(vault, 0.01M, Minter, _a));
            await MoveAtTheNextBlock();
            await StateSnapshotService.UpdateCycleAsync(Tip);       // three full copies
            await Apply(Tip + 1);
            await StateSnapshotService.UpdateCycleAsync(Tip);
            await Apply(Tip + 1);
            await StateSnapshotService.UpdateCycleAsync(Tip);

            await Apply(Tip + 1, Transfer(vault, 0.004M, _a, _b));  // an entry written after the copies
            await StateSnapshotService.UpdateCycleAsync(Tip);       // the oldest slot rolls forward by diff
            var slot = StateSnapshotService.GetSlots().Single(x => x.Height == Tip);

            VaultLedgerStore.Entries().DeleteAll();                  // the live entries are lost
            Assert.Equal(0M, VaultLedgerStore.Balance(State(vault), _a.Address));
            VerifiedXCore.Utilities.BlockRollbackUtility.WipeChainDerivedState();   // as a restore does first
            Assert.True(StateSnapshotService.RestoreSlotToLive(slot.SlotId));

            var restored = State(vault);
            Assert.Equal(0.006M, VaultLedgerStore.Balance(restored, _a.Address));
            Assert.Equal(0.004M, VaultLedgerStore.Balance(restored, _b.Address));
            Assert.True(restored.LedgerMoved);
        }

        [Fact]
        public async Task AFastRollback_DoesNotUndoTheMove()
        {
            await Start();
            var vault = (await CreateVault(H1 + 1)).Uid;
            await Apply(Tip + 1, Transfer(vault, 0.01M, Minter, _a));
            await MoveAtTheNextBlock();
            var movedAt = Tip;

            Assert.Contains("ledgers moved", VerifiedXCore.Utilities.BlockRollbackUtility.WhyNotIncremental(Array.Empty<Block>(), movedAt - 1) ?? "");
            Assert.DoesNotContain("ledgers moved", VerifiedXCore.Utilities.BlockRollbackUtility.WhyNotIncremental(Array.Empty<Block>(), movedAt) ?? "");
        }
    }
}
