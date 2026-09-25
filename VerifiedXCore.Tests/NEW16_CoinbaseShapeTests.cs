using System.Collections.Generic;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-16 (found by the fifth independent review; CRITICAL, pre-existing): coinbase transactions skip VerifyTX and
    /// every per-type rule, but UpdateTreis applied their type and Data. A winning producer set its coinbase to an
    /// FTKN_TX TokenTransfer() of a victim's tokens to itself (reviewer PoC: victim 1000 -> 0, validator +1000).
    /// </summary>
    public class NEW16_CoinbaseShapeTests
    {
        private static Block BlockWith(Transaction coinbase)
        {
            coinbase.Build(); // producers build coinbases (NEW-25 binds content to hash)
            return new Block
            {
            Height = 10_000, Version = 4, Validator = "xValidator",
                Transactions = new List<Transaction> { coinbase },
            };
        }

        [Fact]
        public void NEW16_PoC_CoinbaseCarryingATokenTransfer_Refused()
        {
            var coinbase = new Transaction
            {
                FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.FTKN_TX,
                Data = JsonConvert.SerializeObject(new { Function = "TokenTransfer()", ContractUID = "c:1", FromAddress = "xVictim", ToAddress = "xValidator", Amount = 1000M }),
            };
            Assert.False(BlockchainData.ValidateBlock(BlockWith(coinbase)));
        }

        [Fact]
        public void NEW16_PoC_CoinbaseWithDataOnATxType_Refused()
        {
            var coinbase = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX, Data = "{\"Function\":\"Transfer()\"}" };
            Assert.False(BlockchainData.ValidateBlock(BlockWith(coinbase)));
        }

        [Fact]
        public void NEW16_Control_PlainCoinbase_Accepted()
        {
            var coinbase = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX };
            Assert.True(BlockchainData.ValidateBlock(BlockWith(coinbase)));
        }

        // ── NEW-25 (sixth review): coinbase content bound to its hash ───────────────────────

        [Fact]
        public void NEW25_PoC_CoinbaseContentChangedUnderItsHash_Refused()
        {
            // Reviewer PoC R6_SpecialBlock_ArbitraryCoinbaseAccepted: a coinbase credited 50,000,000 to an attacker while
            // keeping the original Hash (and so the merkle root and block hash).
            var coinbase = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX, Timestamp = 1 };
            var block = BlockWith(coinbase);
            Assert.True(BlockchainData.ValidateBlock(block));                         // control: as built
            coinbase.Timestamp = 2;                                                   // content changed, Hash kept
            Assert.False(BlockchainData.ValidateBlock(block));                         // (the special block case below shows the impact)
        }

        [Fact]
        public void NEW25_SpecialBlockHeightNoLongerSkipsTheCoinbaseChecks()
        {
            var prior = Globals.SpecialBlockHeight;
            try
            {
                Globals.SpecialBlockHeight = 10_000;
                var coinbase = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX, Timestamp = 1 };
                var block = BlockWith(coinbase);
                coinbase.Amount = 50_000_000M; // Hash kept
                Assert.False(BlockchainData.ValidateBlock(block));
            }
            finally { Globals.SpecialBlockHeight = prior; }
        }
    }
}
