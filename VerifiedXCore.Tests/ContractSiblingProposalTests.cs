using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Block validation refuses a block in which a Transfer() or Burn() shares its contract with another contract
    /// transaction of the same sender, whatever their order. The proposal drops such a Transfer() or Burn() (and the
    /// sender's later transactions, so no nonce gap is left) instead of proposing a block every validator refuses.
    /// </summary>
    public class ContractSiblingProposalTests
    {
        private static Transaction Call(string from, string function, string uid, long nonce, TransactionType type = TransactionType.NFT_TX) => new()
        {
            Hash = $"{from}-{nonce}-{function}", FromAddress = from, ToAddress = "xTo", Nonce = nonce, TransactionType = type,
            Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = uid } }),
        };

        [Fact]
        public void ATransferThenAnUpdateOfTheSameContract_LeavesOnlyWhatValidationAccepts()
        {
            var transfer = Call("xA", "Transfer()", "uid:1", 1);
            var update = Call("xA", "Update()", "uid:1", 2, TransactionType.SC_TX);
            var proposal = new List<Transaction> { transfer, update };
            Assert.True(TransactionData.ConflictsWithContractSibling(transfer, proposal)); // validation's verdict on the pair

            var kept = TransactionData.DropContractSiblingConflicts(proposal);

            Assert.Empty(kept); // the Transfer() goes, and the sender's later Update() with it (no nonce gap)
        }

        [Fact]
        public void AnUpdateThenATransferOfTheSameContract_KeepsTheUpdate()
        {
            var update = Call("xA", "Update()", "uid:1", 1, TransactionType.SC_TX);
            var transfer = Call("xA", "Transfer()", "uid:1", 2);

            var kept = TransactionData.DropContractSiblingConflicts(new List<Transaction> { update, transfer });

            Assert.Equal(new[] { update }, kept);
            Assert.All(kept, tx => Assert.False(TransactionData.ConflictsWithContractSibling(tx, kept)));
        }

        [Theory]
        [InlineData("Burn()", TransactionType.NFT_BURN)]
        [InlineData("Transfer()", TransactionType.TKNZ_TX)]
        public void EveryFunctionAndTypeTheRuleCovers_IsDropped(string function, TransactionType type)
        {
            var first = Call("xA", function, "uid:1", 1, type);
            var sibling = Call("xA", "Evolve()", "uid:1", 2, TransactionType.NFT_TX);

            Assert.Empty(TransactionData.DropContractSiblingConflicts(new List<Transaction> { first, sibling }));
        }

        [Fact]
        public void TransactionsOfOtherContractsOrSenders_AreKept()
        {
            var proposal = new List<Transaction>
            {
                Call("xA", "Transfer()", "uid:1", 1),
                Call("xA", "Update()", "uid:2", 2, TransactionType.SC_TX), // another contract
                Call("xB", "Update()", "uid:1", 1, TransactionType.SC_TX), // another sender
                new Transaction { Hash = "xA-3-tx", FromAddress = "xA", ToAddress = "xTo", Nonce = 3, TransactionType = TransactionType.TX, Amount = 1M },
            };

            Assert.Equal(proposal, TransactionData.DropContractSiblingConflicts(proposal));
        }
    }
}
