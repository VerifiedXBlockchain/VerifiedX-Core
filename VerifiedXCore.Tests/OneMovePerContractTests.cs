using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.OneMovePerContractHeight a block moves a contract (Transfer() or Burn()) at most once, whatever the
    /// transaction types carrying the moves: each was judged against the owner before the block, so the second undid a
    /// confirmed transfer. The proposal keeps the second for a later block.
    /// </summary>
    [Collection("DbContextSequential")]
    public class OneMovePerContractTests : IDisposable
    {
        private const long Rule = 5_000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.OneMovePerContractHeight;

        public OneMovePerContractTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"one_move_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.OneMovePerContractHeight = Rule;
        }

        public void Dispose()
        {
            Globals.OneMovePerContractHeight = _priorRule;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Move(TransactionType type, string function, string uid, string hash) => new()
        {
            Hash = hash, FromAddress = "xOwner", ToAddress = "xBuyer", Nonce = 0, TransactionType = type,
            Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = uid, ToAddress = "xBuyer" } }),
        };

        [Theory]
        [InlineData(TransactionType.FTKN_TX, "Transfer()", TransactionType.FTKN_BURN, "Burn()")]
        [InlineData(TransactionType.NFT_TX, "Transfer()", TransactionType.NFT_TX, "Transfer()")]
        [InlineData(TransactionType.TKNZ_TX, "Transfer()", TransactionType.SC_BURN, "Burn()")]
        public void FromTheRule_ASecondMoveOfAContract_IsRefused_AndBelowItIsNot(TransactionType firstType, string firstFunction, TransactionType secondType, string secondFunction)
        {
            var first = Move(firstType, firstFunction, "uid:1", "a");
            var second = Move(secondType, secondFunction, "uid:1", "b");

            var atRule = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(first, atRule, Rule));
            Assert.StartsWith(LedgerIntegrityRules.NftMovedTwiceInBlock, LedgerIntegrityRules.RegisterNftStepInBlock(second, atRule, Rule));
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(LedgerIntegrityRules.RegisterNftStepInBlock(second, atRule, Rule)!));

            var below = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(first, below, Rule - 1));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(second, below, Rule - 1));
        }

        [Fact]
        public void MovesOfDifferentContracts_AreBothKept()
        {
            var steps = new LedgerIntegrityRules.NftStepState();
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Move(TransactionType.NFT_TX, "Transfer()", "uid:1", "a"), steps, Rule));
            Assert.Null(LedgerIntegrityRules.RegisterNftStepInBlock(Move(TransactionType.NFT_TX, "Transfer()", "uid:2", "b"), steps, Rule));
        }

        [Fact]
        public void TheProposal_KeepsTheFirstMoveAndHoldsTheSecond()
        {
            Globals.LastBlock = new Block { Height = Rule - 1 };
            var first = Move(TransactionType.FTKN_TX, "Transfer()", "uid:1", "a");
            var second = Move(TransactionType.FTKN_BURN, "Burn()", "uid:1", "b");

            var kept = TransactionData.DropDuplicateContractCreations(new List<Transaction> { first, second });

            Assert.Equal(new[] { "a" }, kept.Select(x => x.Hash));
        }
    }
}
